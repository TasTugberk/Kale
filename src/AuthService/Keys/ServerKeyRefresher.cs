using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace AuthService.Keys;

/// <summary>
/// Keeps this instance's server keys current: loads them on first use, then refreshes them every hour (creating
/// and retiring keys through <see cref="KeyRing"/>), and makes OpenIddict pick up the new set without a restart.
/// </summary>
public sealed class ServerKeyRefresher(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitorCache<OpenIddictServerOptions> openIddictOptionsCache,
    TimeProvider clock) : BackgroundService
{
    // Well below KeyRotationOptions.PublishAhead (1 day), so every instance knows a new key before it's used.
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _refreshing = new(1, 1);

    public ServerKeys? Current { get; private set; }

    /// <summary>Refreshes now and makes OpenIddict rebuild its options with the new keys.</summary>
    public async Task<ServerKeys> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        var keys = await LoadAsync(cancellationToken);

        // OpenIddict reads its options (including keys) through IOptionsMonitor. Dropping the cached copy makes
        // the next request build them again, which runs ConfigureServerKeys with the new set.
        openIddictOptionsCache.TryRemove(Options.DefaultName);
        return keys;
    }

    /// <summary>The current keys, loading them first if nothing has been loaded yet.</summary>
    public ServerKeys GetOrLoad() =>
        Current ?? LoadAsync(CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshNowAsync(stoppingToken);

        using var timer = new PeriodicTimer(RefreshInterval, clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshNowAsync(stoppingToken);
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
}
