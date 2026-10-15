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

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero));

    public static async Task<AuthServiceApp> StartAsync(PostgresFixture postgres, params (string Key, string Value)[] settings)
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
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}
