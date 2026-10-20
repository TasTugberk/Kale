using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Persistence;

/// <summary>
/// The single place the auth database provider is configured, shared by the app, tests and design-time tooling,
/// so they can never drift apart.
/// </summary>
public static class AuthDatabaseOptions
{
    public const string ConnectionStringName = "AuthDb";

    /// <summary>
    /// ConnectionStrings:AuthDb, with the password from AuthDb:PasswordFile when that's set: in a container the
    /// password is a mounted Podman secret, never part of the connection string in configuration.
    /// </summary>
    public static string ConnectionString(IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");
        }

        var passwordFile = configuration["AuthDb:PasswordFile"];
        if (string.IsNullOrEmpty(passwordFile))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { Password = File.ReadAllText(passwordFile).Trim() }.ConnectionString;
    }

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
