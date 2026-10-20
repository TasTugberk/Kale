using Auth.Contracts;
using Auth.Contracts.V1;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Auth.Client;

/// <summary>
/// DESIGN.md cache invalidation: listens for AuthService's events (invalidation.proto) and evicts the matching
/// cache entries, so a stopped session or an edited role takes effect right away. Each app instance has its own
/// queue, which RabbitMQ deletes when the instance disconnects.
/// </summary>
public sealed partial class InvalidationListener : BackgroundService
{
    // Real time, not the app's TimeProvider: how long to wait before reconnecting isn't business time.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    private readonly KaleAuthOptions _options;
    private readonly SessionAccess _access;
    private readonly ILogger<InvalidationListener> _logger;

    public InvalidationListener(KaleAuthOptions options, SessionAccess access, IHostEnvironment environment, ILogger<InvalidationListener> logger)
    {
        // Checked here, when the app starts, because only the host knows its environment.
        var isDevOrTest = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        if (options.RabbitMqUri is null && !isDevOrTest)
        {
            throw new InvalidOperationException(
                "Set KaleAuthOptions.RabbitMqUri: without it, stopped sessions and edited roles are noticed only when the cache expires.");
        }

        // Plain amqp:// would send the password unencrypted, which only a local setup may do.
        if (options.RabbitMqUri is { Scheme: "amqp" } && !isDevOrTest)
        {
            throw new InvalidOperationException("KaleAuthOptions.RabbitMqUri must use amqps:// outside Development/Testing.");
        }

        _options = options;
        _access = access;
        _logger = logger;
    }

    /// <summary>Evicts what the event makes stale.</summary>
    internal static void Apply(InvalidationEvent invalidation, SessionAccess access)
    {
        switch (invalidation.EventCase)
        {
            case InvalidationEvent.EventOneofCase.SessionStopped:
                access.EvictSession(invalidation.SessionStopped.SessionId);
                break;

            case InvalidationEvent.EventOneofCase.RoleEdited:
                if (Guid.TryParse(invalidation.RoleEdited.RoleId, out var roleId))
                {
                    access.EvictRole(roleId);
                }

                break;

            // UserDisabled: the user's sessions were stopped too, each with its own SessionStopped event.
            // Events added to the contract later are ignored until this package knows them.
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RabbitMqUri is null)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenUntilDisconnectedAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogListeningFailed(exception, RetryDelay);
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    private async Task ListenUntilDisconnectedAsync(CancellationToken stoppingToken)
    {
        await using var connection = await CreateConnectionFactory().CreateConnectionAsync(stoppingToken);
        var disconnected = new TaskCompletionSource();
        connection.ConnectionShutdownAsync += (_, _) =>
        {
            disconnected.TrySetResult();
            return Task.CompletedTask;
        };

        await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);

        // Declared here too (same settings as AuthService), so the app can start before AuthService has.
        await channel.ExchangeDeclareAsync(InvalidationExchange.Name, ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);

        // A server-named queue for this instance only, deleted when it disconnects: every instance gets every event.
        var queue = await channel.QueueDeclareAsync(queue: "", durable: false, exclusive: true, autoDelete: true, cancellationToken: stoppingToken);
        await channel.QueueBindAsync(queue.QueueName, InvalidationExchange.Name, routingKey: "", cancellationToken: stoppingToken);

        // Events sent while this instance wasn't listening are lost: forget everything cached, so nothing that
        // was invalidated meanwhile survives.
        _access.EvictAll();

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            try
            {
                Apply(InvalidationEvent.Parser.ParseFrom(delivery.Body.Span), _access);
            }
            catch (Exception exception)
            {
                // A message this version can't read: skip it (the cache times still apply) rather than stop listening.
                LogUnreadableEvent(exception, delivery.BasicProperties.MessageId);
            }

            return Task.CompletedTask;
        };

        // autoAck: an event is only an eviction hint; redelivering it after a crash would gain nothing.
        await channel.BasicConsumeAsync(queue.QueueName, autoAck: true, consumer, stoppingToken);
        LogListening(queue.QueueName);

        await disconnected.Task.WaitAsync(stoppingToken);
    }

    private ConnectionFactory CreateConnectionFactory()
    {
        var factory = new ConnectionFactory
        {
            Uri = _options.RabbitMqUri!,

            // This service reconnects by itself (and declares its queue again, then clears the caches). The
            // library's own recovery would reconnect without clearing them.
            AutomaticRecoveryEnabled = false,

            // Shown in RabbitMQ's connection list.
            ClientProvidedName = $"Kale.Auth.Client {_options.ApplicationKey}",
        };

        // Set after Uri, which would otherwise reset it from the address.
        if (_options.RabbitMqPassword is not null)
        {
            factory.Password = _options.RabbitMqPassword;
        }

        return factory;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Listening for invalidation events on queue {Queue}.")]
    private partial void LogListening(string queue);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Listening for invalidation events failed; reconnecting in {RetryDelay}.")]
    private partial void LogListeningFailed(Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an invalidation event that couldn't be read (message id {MessageId}).")]
    private partial void LogUnreadableEvent(Exception exception, string? messageId);
}
