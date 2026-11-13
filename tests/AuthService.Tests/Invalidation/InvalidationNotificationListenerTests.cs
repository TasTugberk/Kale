using AuthService.Invalidation;
using AuthService.Tests.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace AuthService.Tests.Invalidation;

[Collection(UsesPostgres.Name)]
public sealed class InvalidationNotificationListenerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Open_streams_end_as_soon_as_the_app_starts_stopping()
    {
        // Under Kestrel the web server stops before hosted services do, and first waits (up to the shutdown
        // timeout) for open requests: a stream never ends on its own, so it must end when stopping begins, not when
        // this service is finally stopped.
        var subscribers = new InvalidationSubscribers();
        var lifetime = new FakeLifetime();
        using var listener = new InvalidationNotificationListener(
            subscribers, new InvalidationListenerSettings(postgres.ConnectionString("postgres")), lifetime,
            NullLogger<InvalidationNotificationListener>.Instance);
        await listener.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => subscribers.IsListening);
            var subscription = subscribers.TryAdd(Guid.CreateVersion7())!;

            lifetime.Stopping.Cancel();

            await WaitUntilAsync(() => subscription.EndReason is not null);
            subscribers.TryAdd(Guid.CreateVersion7()).ShouldBeNull();
        }
        finally
        {
            await listener.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            DateTimeOffset.UtcNow.ShouldBeLessThan(deadline, "the condition never became true");
            await Task.Delay(50);
        }
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationTokenSource Stopping { get; } = new();

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => Stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Stopping.Cancel();
    }
}
