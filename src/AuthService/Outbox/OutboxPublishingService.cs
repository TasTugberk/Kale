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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await new ConnectionFactory { Uri = settings.Uri }.CreateConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(
                    new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true),
                    stoppingToken);

                // Durable, so it survives a broker restart; client apps bind their own queues to it.
                await channel.ExchangeDeclareAsync(OutboxPublisher.Exchange, ExchangeType.Fanout, durable: true, cancellationToken: stoppingToken);
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
            await using (var scope = scopes.CreateAsyncScope())
            {
                published = await scope.ServiceProvider.GetRequiredService<OutboxPublisher>().PublishPendingAsync(channel, stoppingToken);
            }

            // A full batch means more may be waiting: go again right away.
            if (published < OutboxPublisher.BatchSize)
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Publishing invalidation events failed; retrying in {RetryDelay}.")]
    private partial void LogPublishingFailed(Exception exception, TimeSpan retryDelay);
}

/// <param name="Uri">RabbitMQ's address, e.g. amqp://user:password@host:5672/ (config RabbitMq:Uri).</param>
public sealed record RabbitMqSettings(Uri Uri);
