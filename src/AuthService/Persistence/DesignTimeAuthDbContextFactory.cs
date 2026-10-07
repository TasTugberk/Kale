using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add` build the model without starting the app or reaching a database.
/// </summary>
internal sealed class DesignTimeAuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    // Building the model doesn't need a real database, so a placeholder host is enough.
    public AuthDbContext CreateDbContext(string[] args) =>
        new(AuthDatabaseOptions.Create("Host=design-time-only"), TimeProvider.System);
}
