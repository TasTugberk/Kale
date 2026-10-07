using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

/// <summary>
/// The single place the auth database provider is configured, shared by the app, tests and design-time tooling,
/// so they can never drift apart.
/// </summary>
public static class AuthDatabaseOptions
{
    public const string ConnectionStringName = "AuthDb";

    public static void UseAuthDatabase(this DbContextOptionsBuilder builder, string connectionString)
    {
        builder.UseNpgsql(connectionString);
        builder.UseSnakeCaseNamingConvention();
    }

    /// <summary>Options for creating an <see cref="AuthDbContext"/> directly, outside dependency injection.</summary>
    public static DbContextOptions<AuthDbContext> Create(string connectionString)
    {
        var builder = new DbContextOptionsBuilder<AuthDbContext>();
        builder.UseAuthDatabase(connectionString);
        return builder.Options;
    }
}
