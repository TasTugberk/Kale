using System.Text.Json;
using AuthService.Domain;
using AuthService.Keys;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Hosting;

[Collection(UsesPostgres.Name)]
public sealed class OpenIddictSetupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.StartAsync(postgres);
        _client = _app.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Discovery_document_advertises_the_endpoints_and_flows_from_the_design()
    {
        using var discovery = await GetJson("/.well-known/openid-configuration");
        var root = discovery.RootElement;

        root.GetProperty("authorization_endpoint").GetString().ShouldEndWith("/connect/authorize");
        root.GetProperty("token_endpoint").GetString().ShouldEndWith("/connect/token");
        Strings(root, "grant_types_supported").ShouldBe(
            ["authorization_code", "client_credentials", "refresh_token"], ignoreOrder: true);

        // PKCE with S256 only: "plain" would defeat its purpose.
        Strings(root, "code_challenge_methods_supported").ShouldBe(["S256"]);
    }

    [Fact]
    public async Task JWKS_publishes_the_current_signing_key_and_not_the_encryption_key()
    {
        var keyIds = await PublishedKeyIds();

        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var signing = await db.ServerKeys.SingleAsync(k => k.Use == KeyUse.Signing);
        var encryption = await db.ServerKeys.SingleAsync(k => k.Use == KeyUse.Encryption);

        keyIds.ShouldContain(signing.Id.ToString("N"));
        keyIds.ShouldNotContain(encryption.Id.ToString("N"));
    }

    [Fact]
    public async Task A_rotated_key_is_published_without_a_restart()
    {
        var before = await PublishedKeyIds();

        // Day 29: the successor is created and must be published right away (it's used from day 30).
        _app.Clock.Advance(TimeSpan.FromDays(29));
        await _app.Services.GetRequiredService<ServerKeyRefresher>().RefreshNowAsync();
        var after = await PublishedKeyIds();

        after.Count.ShouldBe(2);
        after.ShouldContain(before.Single());
    }

    private async Task<List<string>> PublishedKeyIds()
    {
        using var discovery = await GetJson("/.well-known/openid-configuration");
        using var jwks = await GetJson(discovery.RootElement.GetProperty("jwks_uri").GetString()!);
        return jwks.RootElement.GetProperty("keys").EnumerateArray()
            .Select(key => key.GetProperty("kid").GetString()!)
            .ToList();
    }

    private async Task<JsonDocument> GetJson(string url)
    {
        using var response = await _client.GetAsync(new Uri(url, UriKind.RelativeOrAbsolute));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<string> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();
}
