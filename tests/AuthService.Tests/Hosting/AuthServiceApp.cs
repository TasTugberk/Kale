using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Hosting;

/// <summary>
/// The real AuthService (Program.cs) running in memory against its own migrated test database,
/// with a clock the test controls.
/// </summary>
internal sealed class AuthServiceApp(string connectionString, params (string Key, string Value)[] settings)
    : WebApplicationFactory<Program>
{
    /// <summary>For starting another instance against the same database.</summary>
    public string ConnectionString => connectionString;

    // Deliberately not the test server's own address, so tests prove the configured issuer is used.
    public const string Issuer = "https://auth.kale.test/";

    /// <summary>Optional test-only service replacements, applied after the app's own registrations.</summary>
    public Action<IServiceCollection>? ConfigureServices { get; init; }

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>Creates the app against a fresh migrated database; it starts on first use (e.g. CreateClient).</summary>
    public static async Task<AuthServiceApp> CreateAsync(PostgresFixture postgres, params (string Key, string Value)[] settings)
    {
        await using var db = postgres.CreateDbContext();
        await db.Database.MigrateAsync();
        return new AuthServiceApp(postgres.ConnectionString(db.Database.GetDbConnection().Database), settings);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Not "Development": that would load user secrets from this machine.
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:AuthDb", connectionString);

        // The in-memory test server speaks plain HTTP.
        builder.UseSetting("OpenIddict:AllowInsecureHttpDevOnly", "true");
        builder.UseSetting("Auth:Issuer", Issuer);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            ConfigureServices?.Invoke(services);
        });
    }
}
