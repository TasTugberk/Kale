using AuthService.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AuthService.Tests.Persistence;

/// <summary>
/// One PostgreSQL container shared by every test in the <see cref="UsesPostgres"/> collection.
/// Each test asks for its own database, so tests stay isolated without paying for a container each.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Official images are multi-arch (arm64 + amd64). Use the same image when compose.yaml is added (Phase 6).
    public const string Image = "docker.io/library/postgres:18-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image)
        .Build();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    /// <summary>
    /// Creates a context for a fresh, uniquely named database (created on first migrate).
    /// Pass a clock to control the CreatedAt/ModifiedAt timestamps; otherwise the real clock is used.
    /// </summary>
    public AuthDbContext CreateDbContext(TimeProvider? clock = null)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = $"test_{Guid.CreateVersion7():N}",
        }.ConnectionString;

        return new AuthDbContext(AuthDatabaseOptions.Create(connectionString), clock ?? TimeProvider.System);
    }
}

[CollectionDefinition(Name)]
public sealed class UsesPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
