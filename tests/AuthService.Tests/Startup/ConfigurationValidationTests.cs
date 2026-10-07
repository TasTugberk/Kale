using AuthService.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Startup;

public sealed class ConfigurationValidationTests
{
    // Not "Development": that environment loads .NET user secrets, where scripts/dev-db.sh stores a real
    // connection string, so results would depend on the machine.
    private const string TestEnvironment = "Testing";

    [Fact]
    public void Startup_fails_when_the_AuthDb_connection_string_is_missing()
    {
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host.UseEnvironment(TestEnvironment));

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("AuthDb");
    }

    [Fact]
    public void App_can_create_AuthDbContext_from_its_services()
    {
        // Creating the context doesn't open a connection, so any well-formed connection string is enough.
        using var baseFactory = new WebApplicationFactory<Program>();
        using var factory = baseFactory.WithWebHostBuilder(host => host
            .UseEnvironment(TestEnvironment)
            .UseSetting("ConnectionStrings:AuthDb", "Host=not-used-by-this-test"));
        using var scope = factory.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<AuthDbContext>().ShouldNotBeNull();
    }
}
