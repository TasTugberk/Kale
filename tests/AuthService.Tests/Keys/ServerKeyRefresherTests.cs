using AuthService.Keys;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace AuthService.Tests.Keys;

[Collection(UsesPostgres.Name)]
public sealed class ServerKeyRefresherTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Keys_are_loaded_before_startup_completes()
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.MigrateAsync();
        await using var services = Services(db.Database.GetDbConnection().Database);
        using var refresher = NewRefresher(services);

        await refresher.StartAsync(CancellationToken.None);

        // Already there for OpenIddict's first request, with no lazy first load racing it.
        refresher.GetCurrent().Signing.Current.ShouldNotBeNull();
        await refresher.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Startup_fails_when_the_keys_cannot_be_loaded()
    {
        await using var services = Services("database_that_does_not_exist");
        using var refresher = NewRefresher(services);

        await Should.ThrowAsync<Exception>(() => refresher.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_scheduled_refresh_is_reported_without_stopping_the_service()
    {
        await using var services = Services("database_that_does_not_exist");
        using var refresher = NewRefresher(services);

        var refreshed = await refresher.TryRefreshAsync(CancellationToken.None);

        refreshed.ShouldBeFalse();
    }

    private ServiceProvider Services(string databaseName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AuthDbContext>(options => options.UseAuthDatabase(postgres.ConnectionString(databaseName)));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton(new KeyRotationOptions());
        services.AddScoped<KeyRing>();
        services.AddOptions();
        return services.BuildServiceProvider();
    }

    private static ServerKeyRefresher NewRefresher(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        services.GetRequiredService<IOptionsMonitorCache<OpenIddictServerOptions>>(),
        TimeProvider.System,
        NullLogger<ServerKeyRefresher>.Instance);
}
