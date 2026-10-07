using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

public static class AuthDatabaseOptions
{
    public const string ConnectionStringName = "AuthDb";

    /// <summary>
    /// The single place the auth database provider is configured, shared by the app, tests and design-time tooling.
    /// </summary>
    public static DbContextOptionsBuilder UseAuthDatabase(this DbContextOptionsBuilder builder, string connectionString) =>
        builder
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention();

    public static DbContextOptionsBuilder<TContext> UseAuthDatabase<TContext>(
        this DbContextOptionsBuilder<TContext> builder, string connectionString)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)((DbContextOptionsBuilder)builder).UseAuthDatabase(connectionString);
}
