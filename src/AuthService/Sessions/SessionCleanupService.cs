using OpenIddict.Abstractions;

namespace AuthService.Sessions;

/// <summary>
/// Every hour: deletes old finished sessions (<see cref="SessionCleanup"/>) and lets OpenIddict prune its own
/// old tokens and authorizations (refresh tokens and codes are stored too, and pile up the same way). Several
/// instances may run it at once: deleting what's already deleted is harmless.
/// </summary>
public sealed partial class SessionCleanupService(
    IServiceScopeFactory scopeFactory,
    SessionCleanupSettings settings,
    TimeProvider clock,
    ILogger<SessionCleanupService> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryCleanUpAsync(stoppingToken);
        }
    }

    /// <summary>One run. A failure (e.g. the database is briefly down) is logged and retried next hour.</summary>
    private async Task TryCleanUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            // One cutoff for everything. OpenIddict removes what was created before it and is no longer usable
            // (expired, redeemed, revoked): the same retention as sessions, since a refresh token lives as long
            // as its session.
            var cutoff = clock.GetUtcNow() - settings.KeepFinishedFor;
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            var sessions = await services.GetRequiredService<SessionCleanup>().DeleteFinishedBeforeAsync(cutoff, cancellationToken);
            var tokens = await services.GetRequiredService<IOpenIddictTokenManager>().PruneAsync(cutoff, cancellationToken);
            var authorizations = await services.GetRequiredService<IOpenIddictAuthorizationManager>().PruneAsync(cutoff, cancellationToken);
            LogCleanedUp(logger, sessions, tokens, authorizations);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogCleanupFailed(logger, exception, Interval);
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Cleanup: deleted {Sessions} finished sessions, {Tokens} tokens and {Authorizations} authorizations.")]
    private static partial void LogCleanedUp(ILogger logger, int sessions, long tokens, long authorizations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cleanup failed; trying again in {Interval}.")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception, TimeSpan interval);
}
