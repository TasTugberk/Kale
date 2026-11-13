using System.Text.Json;
using System.Text.Json.Serialization;
using Auth.Contracts.V1;
using Npgsql;

namespace AuthService.Invalidation;

/// <summary>
/// Listens for the invalidation events every AuthService instance saves (the outbox trigger's NOTIFY) on one
/// dedicated connection, and hands each to this instance's streams of that application. When the connection is
/// lost, events may have been missed: every stream ends (its client reconnects and clears its caches), and this
/// tries again with a fresh connection.
/// </summary>
public sealed partial class InvalidationNotificationListener(
    InvalidationSubscribers subscribers,
    InvalidationListenerSettings settings,
    IHostApplicationLifetime lifetime,
    ILogger<InvalidationNotificationListener> logger)
    : BackgroundService
{
    /// <summary>The NOTIFY channel; the trigger in the InvalidationNotify migration uses the same name.</summary>
    public const string Channel = "kale_invalidation";

    /// <summary>The listening connection's application_name, shown in pg_stat_activity.</summary>
    public const string ConnectionName = "Kale.AuthService invalidation listener";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // A connection dropped without a word (e.g. a firewall forgetting it) would otherwise wait for notifications
    // forever. After this long without any, a query proves the connection still works, within a short timeout:
    // until then, streams look connected and get nothing.
    private static readonly TimeSpan KeepAliveInterval = TimeSpan.FromSeconds(30);
    private const int PingTimeoutSeconds = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stop as soon as the app starts stopping, not when this service is stopped: the web server stops first,
        // and first waits (up to the shutdown timeout) for open requests to finish. A stream never finishes on its
        // own, so without this every shutdown would wait the whole timeout and then abort the streams.
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, lifetime.ApplicationStopping);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await ListenAsync(stopping.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    subscribers.StoppedListening("AuthService lost its connection for invalidation events.");
                    LogListeningFailed(exception, RetryDelay);
                    await Task.Delay(RetryDelay, stopping.Token);
                }
            }
        }
        finally
        {
            // Open streams end now (UNAVAILABLE), and no new one starts: their clients reconnect to another instance.
            subscribers.StoppedListening("AuthService is stopping.");
        }
    }

    private async Task ListenAsync(CancellationToken stoppingToken)
    {
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        connection.Notification += (_, notification) => Deliver(notification.Payload);
        await connection.OpenAsync(stoppingToken);
        await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
        {
            await listen.ExecuteNonQueryAsync(stoppingToken);
        }

        // Only now, so no stream starts before events can reach it.
        subscribers.StartedListening();
        LogListening();

        while (true)
        {
            // Notifications are handled (by the event above) while this waits.
            if (!await connection.WaitAsync(KeepAliveInterval, stoppingToken))
            {
                await using var ping = new NpgsqlCommand("SELECT 1", connection) { CommandTimeout = PingTimeoutSeconds };
                await ping.ExecuteNonQueryAsync(stoppingToken);
            }
        }
    }

    private void Deliver(string payload)
    {
        // An unreadable event is logged with its payload and skipped (the clients' caches still expire on their own).
        // Npgsql would catch an exception thrown here as well (the connection stays open), but its log wouldn't say
        // which event it was.
        try
        {
            var notification = JsonSerializer.Deserialize<Notification>(payload)!;
            var invalidation = InvalidationEvent.Parser.ParseFrom(Convert.FromHexString(notification.Event));
            subscribers.Deliver(notification.ApplicationId, invalidation);
        }
        catch (Exception exception)
        {
            LogUnreadable(exception, payload);
        }
    }

    // What the trigger sends: the row's application and its payload (protobuf InvalidationEvent) in hex.
    private sealed record Notification(
        [property: JsonPropertyName("application_id")] Guid ApplicationId,
        [property: JsonPropertyName("event")] string Event);

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for invalidation events.")]
    private partial void LogListening();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listening for invalidation events failed; open streams ended, retrying in {RetryDelay}.")]
    private partial void LogListeningFailed(Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Error, Message = "Skipped an unreadable invalidation event: {Payload}")]
    private partial void LogUnreadable(Exception exception, string payload);
}

/// <param name="ConnectionString">For the listener's own connection (see <see cref="InvalidationStreamSetup"/>).</param>
public sealed record InvalidationListenerSettings(string ConnectionString)
{
    // A record prints all its members by default; logging this must never leak the database password.
    public override string ToString() => nameof(InvalidationListenerSettings);
}
