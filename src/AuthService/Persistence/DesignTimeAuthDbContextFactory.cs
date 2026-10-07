using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AuthService.Persistence;

/// <summary>
/// Lets `dotnet ef migrations add` build the model without starting the app or reaching a database.
/// </summary>
internal sealed class DesignTimeAuthDbContextFactory : IDesignTimeDbContextFactory<AuthDbContext>
{
    public AuthDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AuthDbContext>()
            .UseAuthDatabase("Host=design-time-only")
            .Options);
}
