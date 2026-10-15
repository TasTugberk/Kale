using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace AuthService.Keys;

/// <summary>
/// Keeps this instance's server keys current: loads them before the app serves requests, then refreshes them
/// every hour (creating and retiring keys through <see cref="KeyRing"/>), and makes OpenIddict pick up the new
/// set without a restart.
/// </summary>
public sealed partial class ServerKeyRefresher(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitorCache<OpenIddictServerOptions> openIddictOptionsCache,
    TimeProvider clock,
    ILogger<ServerKeyRefresher> logger) : BackgroundService
{
    // Well below KeyRotationOptions.PublishAhead (1 day), so every instance knows a new key before it's used.
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public ServerKeys? Current { get; private set; }

    /// <summary>
    /// Loads the keys before the app starts serving: OpenIddict needs them to build its options, and broken key
    /// storage should stop startup rather than fail the first sign-in.
    /// </summary>
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    /// <summary>Refreshes now and makes OpenIddict rebuild its options with the new keys.</summary>
    public async Task<ServerKeys> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var keys = await LoadAsync(cancellationToken);

        // OpenIddict reads its options (including keys) through IOptionsMonitor. Dropping the cached copy makes
        // the next request build them again, which runs ConfigureServerKeys with the new set.
        openIddictOptionsCache.TryRemove(Options.DefaultName);
        return keys;
    }

    /// <summary>
    /// One scheduled refresh. A failure (e.g. a short database outage) is logged and retried next hour; the
    /// current keys stay usable meanwhile, since a key is published a day before it's needed.
    /// </summary>
    public async Task<bool> TryRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RefreshNowAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRefreshFailed(logger, exception, RefreshInterval);
            return false;
        }
    }

    /// <summary>The keys loaded at startup or by the last refresh.</summary>
    public ServerKeys GetCurrent() =>
        Current ?? throw new InvalidOperationException("Server keys are not loaded yet; ServerKeyRefresher must start first.");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRefreshAsync(stoppingToken);
        }
    }

    public override void Dispose()
    {
        _refreshing.Dispose();
        base.Dispose();
    }

    private async Task<ServerKeys> LoadAsync(CancellationToken cancellationToken)
    {
        await _refreshing.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            Current = await scope.ServiceProvider.GetRequiredService<KeyRing>().RefreshAsync(cancellationToken);
            return Current;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing the server keys failed; retrying in {Interval}.")]
    private static partial void LogRefreshFailed(ILogger logger, Exception exception, TimeSpan interval);
}
