using System.Net;
using AuthService.Tests.Persistence;

namespace AuthService.Tests.Hosting;

/// <summary>The container's healthcheck (compose) asks /health; it must answer without signing in.</summary>
[Collection(UsesPostgres.Name)]
public sealed class HealthTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_health_endpoint_answers_without_signing_in()
    {
        await using var app = await AuthServiceApp.CreateAsync(postgres);
        using var http = app.CreateClient();

        using var response = await http.GetAsync(new Uri("/health", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }
}
