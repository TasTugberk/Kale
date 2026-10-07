using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AuthService.Tests.Persistence;

/// <summary>
/// One PostgreSQL container shared by every test in the <see cref="UsesPostgres"/> collection.
/// Each test asks for its own database, so tests stay isolated without paying for a container each.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Keep in sync with the PostgreSQL image in compose.yaml. Official images are multi-arch (arm64 + amd64).
    public const string Image = "docker.io/library/postgres:18-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>Creates a context for a fresh, uniquely named database (created on first migrate).</summary>
    public AuthDbContext CreateDbContext()
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"test_{Guid.CreateVersion7():N}",
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<AuthDbContext>()
            .UseAuthDatabase(connectionString)
            .Options;

        return new AuthDbContext(options);
    }
}

[CollectionDefinition(Name)]
public sealed class UsesPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
