using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Tests.Startup;

public sealed class ConfigurationValidationTests
{
    [Fact]
    public void Startup_fails_when_the_AuthDb_connection_string_is_missing()
    {
        using var factory = new WebApplicationFactory<Program>();

        var error = Should.Throw<InvalidOperationException>(() => factory.CreateClient());

        error.Message.ShouldContain("AuthDb");
    }
}
