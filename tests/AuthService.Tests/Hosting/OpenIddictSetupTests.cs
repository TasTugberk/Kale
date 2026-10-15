using System.Text.Json;
using AuthService.Domain;
using AuthService.Keys;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenIddict.Server;

namespace AuthService.Tests.Hosting;

[Collection(UsesPostgres.Name)]
public sealed class OpenIddictSetupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
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

        // Day 30: the successor is created and must be published right away (it's used from day 31).
        _app.Clock.Advance(TimeSpan.FromDays(30));
        await _app.Services.GetRequiredService<ServerKeyRefresher>().RefreshNowAsync();
        var after = await PublishedKeyIds();

        after.Count.ShouldBe(2);
        after.ShouldContain(before.Single());
    }

    [Fact]
    public async Task The_issuer_comes_from_configuration_not_from_the_request()
    {
        using var discovery = await GetJson("/.well-known/openid-configuration");

        discovery.RootElement.GetProperty("issuer").GetString().ShouldBe(AuthServiceApp.Issuer);
    }

    [Fact]
    public async Task Plain_http_is_refused_unless_the_dev_only_flag_is_set()
    {
        await using var strictApp = await AuthServiceApp.CreateAsync(postgres, ("OpenIddict:AllowInsecureHttpDevOnly", "false"));
        using var client = strictApp.CreateClient();

        using var response = await client.GetAsync(new Uri("/.well-known/openid-configuration", UriKind.Relative));

        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest);
    }

    [Fact]
    public void Refresh_tokens_never_outlive_the_keys_that_encrypt_them()
    {
        // A refresh token is encrypted with the encryption key of its day. That key is kept for Retention after
        // its successor takes over, and instances may notice the takeover up to one refresh interval late.
        var server = _app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        var rotation = _app.Services.GetRequiredService<KeyRotationOptions>();

        server.RefreshTokenLifetime.ShouldBe(TimeSpan.FromHours(8), "as long as a session");
        (server.RefreshTokenLifetime!.Value + ServerKeyRefresher.RefreshInterval).ShouldBeLessThanOrEqualTo(rotation.Retention);
    }

    [Fact]
    public async Task OpenIddict_signs_and_encrypts_with_the_key_ring_current_keys_after_rotation()
    {
        var refresher = _app.Services.GetRequiredService<ServerKeyRefresher>();

        // Day 31: two keys of each kind exist and the successor has taken over.
        _app.Clock.Advance(TimeSpan.FromDays(30));
        await refresher.RefreshNowAsync();
        _app.Clock.Advance(TimeSpan.FromDays(1));
        var keys = await refresher.RefreshNowAsync();

        var server = _app.Services.GetRequiredService<IOptionsMonitor<OpenIddictServerOptions>>().CurrentValue;
        server.SigningCredentials.Count.ShouldBe(2);
        // OpenIddict sorts its credentials but treats RSA keys as equal, and .NET's sort keeps the given order
        // for lists this small. This test is what notices if an OpenIddict upgrade changes that.
        server.SigningCredentials[0].Kid.ShouldBe(keys.Signing.Current.KeyId, "OpenIddict signs with the first one");
        server.EncryptionCredentials[0].Key.KeyId.ShouldBe(keys.Encryption.Current.KeyId);
    }

    private Task<List<string>> PublishedKeyIds() => Jwks.KeyIds(_client);

    private async Task<JsonDocument> GetJson(string url)
    {
        using var response = await _client.GetAsync(new Uri(url, UriKind.RelativeOrAbsolute));
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static List<string> Strings(JsonElement element, string property) =>
        element.GetProperty(property).EnumerateArray().Select(e => e.GetString()!).ToList();
}
