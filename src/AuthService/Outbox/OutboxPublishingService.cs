using Auth.Contracts;
using RabbitMQ.Client;

namespace AuthService.Outbox;

/// <summary>
/// Keeps a connection to RabbitMQ and publishes the outbox about once a second. When RabbitMQ or the database is
/// unavailable, the events wait in the outbox and it tries again with a fresh connection.
/// </summary>
public sealed partial class OutboxPublishingService(
    IServiceScopeFactory scopes, RabbitMqSettings settings, ILogger<OutboxPublishingService> logger) : BackgroundService
{
    // Real time, not the app's TimeProvider: this is how often to look, not business time, and tests that
    // control the app's clock still need their events delivered.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // A broker that stops confirming (e.g. it blocks publishers when its disk is full) must not keep a batch's
    // transaction and row locks open forever: give up, and try again on a fresh connection.
    private static readonly TimeSpan BatchTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await CreateConnectionFactory().CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    stoppingToken);

                // Durable, so it survives a broker restart; client apps bind their own queues to it.
                await channel.ExchangeDeclareAsync(InvalidationExchange.Name, ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
                await PublishUntilStoppedAsync(channel, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogPublishingFailed(exception, RetryDelay);
                await Task.Delay(RetryDelay, stoppingToken);
            }
        }
    }

    private async Task PublishUntilStoppedAsync(IChannel channel, CancellationToken stoppingToken)
    {
        while (true)
        {
            int published;
            using (var batchTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
            await using (var scope = scopes.CreateAsyncScope())
            {
                batchTimeout.CancelAfter(BatchTimeout);
                published = await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().PublishPendingAsync(channel, batchTimeout.Token);
            }

            // A full batch means more may be waiting: go again right away.
            if (published < OutboxPublisher.BatchSize)
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private ConnectionFactory CreateConnectionFactory()
    {
        var factory = new ConnectionFactory
        {
            Uri = settings.Uri,

            // This service reconnects by itself (and declares the exchange again). The library's own recovery
            // would do the same work behind its back.
            AutomaticRecoveryEnabled = false,

            // Shown in RabbitMQ's connection list.
            ClientProvidedName = "Kale.AuthService outbox",
        };

        // Set after Uri, which would otherwise reset it from the (password-less) address.
        if (settings.Password is not null)
        {
            factory.Password = settings.Password;
        }

        return factory;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing invalidation events failed; retrying in {RetryDelay}.")]
    private partial void LogPublishingFailed(Exception exception, TimeSpan retryDelay);
}

/// <param name="Uri">RabbitMQ's address with the user name, e.g. amqps://kale@rabbitmq:5671/ (config RabbitMq:Uri).</param>
/// <param name="Password">From RabbitMq:PasswordFile (a Podman secret); null to use the address's own.</param>
public sealed record RabbitMqSettings(Uri Uri, string? Password)
{
    // A record prints all its members by default; logging this must never leak the password.
    public override string ToString() => $"RabbitMqSettings {{ Uri = {Uri} }}";
}
