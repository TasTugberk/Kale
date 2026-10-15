using System.Net;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AuthService.Tests.Tokens;

/// <summary>
/// A client app authenticates as itself (client credentials) to call AuthService: registration, session and
/// role lookups. DESIGN.md: "client credentials for app-to-service calls".
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ClientCredentialsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _client = null!;
    private CreatedApplication _billing = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _client = _app.CreateClient();
        _billing = await CreateApplication("billing");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_client_gets_a_short_lived_access_token_for_AuthService()
    {
        using var response = await RequestToken("billing", _billing.ClientSecret);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        using var body = await Json(response);
        body.RootElement.GetProperty("token_type").GetString().ShouldBe("Bearer");
        body.RootElement.GetProperty("expires_in").GetInt32().ShouldBe(600);

        var token = new JsonWebToken(body.RootElement.GetProperty("access_token").GetString());
        token.Issuer.ShouldBe(AuthServiceApp.Issuer);
        token.Audiences.ShouldBe(["urn:kale:auth-api"], "the token is for AuthService's API, not for an app");
        token.Subject.ShouldBe("billing");
        token.GetClaim("client_id").Value.ShouldBe("billing");
        token.TryGetClaim("sid", out _).ShouldBeFalse("an app token belongs to no user session");
    }

    [Fact]
    public async Task The_token_is_signed_with_a_published_key()
    {
        using var response = await RequestToken("billing", _billing.ClientSecret);
        using var body = await Json(response);
        var accessToken = body.RootElement.GetProperty("access_token").GetString()!;

        var keys = new JsonWebKeySet(await _client.GetStringAsync(await JwksUri()));
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(accessToken, new TokenValidationParameters
        {
            ValidIssuer = AuthServiceApp.Issuer,
            ValidAudience = "urn:kale:auth-api",
            IssuerSigningKeys = keys.GetSigningKeys(),
            ValidateLifetime = false, // the app's clock is fake
        });

        result.IsValid.ShouldBeTrue(result.Exception?.Message);
    }

    [Fact]
    public async Task Credentials_can_also_be_sent_as_HTTP_Basic()
    {
        // Many client libraries send client_id:client_secret in the Authorization header by default.
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative))
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials" }),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"billing:{_billing.ClientSecret}")));

        using var response = await _client.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_secret_is_refused()
    {
        using var response = await RequestToken("billing", "not-the-secret");

        (await Error(response, HttpStatusCode.Unauthorized)).ShouldBe("invalid_client");
    }

    [Fact]
    public async Task An_unknown_client_is_refused()
    {
        using var response = await RequestToken("nobody", _billing.ClientSecret);

        (await Error(response, HttpStatusCode.Unauthorized)).ShouldBe("invalid_client");
    }

    [Fact]
    public async Task An_inactive_application_gets_no_token()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .SetActiveAsync(_billing.Application.Id, isActive: false);
        }

        using var response = await RequestToken("billing", _billing.ClientSecret);

        // OAuth (RFC 6749 §5.2): invalid_client is answered with 401.
        (await Error(response, HttpStatusCode.Unauthorized)).ShouldBe("invalid_client");
    }

    private async Task<CreatedApplication> CreateApplication(string key)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, key, [new Uri($"https://{key}.example/signin-oidc")], []));
    }

    private Task<HttpResponseMessage> RequestToken(string clientId, string clientSecret) =>
        _client.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
        }));

    private static async Task<JsonDocument> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static async Task<string?> Error(HttpResponseMessage response, HttpStatusCode expectedStatus)
    {
        response.StatusCode.ShouldBe(expectedStatus);
        using var body = await Json(response);
        return body.RootElement.GetProperty("error").GetString();
    }

    private async Task<Uri> JwksUri()
    {
        using var discovery = JsonDocument.Parse(await _client.GetStringAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative)));
        return new Uri(discovery.RootElement.GetProperty("jwks_uri").GetString()!);
    }
}
