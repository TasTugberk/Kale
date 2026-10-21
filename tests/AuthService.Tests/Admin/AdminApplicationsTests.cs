using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;

namespace AuthService.Tests.Admin;

/// <summary>The admin API's applications: list, create (secret shown once), rotate the secret, (de)activate.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminApplicationsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri Applications = new("/admin/api/applications", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private HttpClient _admin = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        var authSecret = await AdminSteps.BootstrapAsync(_app);
        _admin = _app.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, authSecret));
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_new_application_gets_a_client_secret_shown_once_that_works()
    {
        using var response = await Create("billing");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        created.GetProperty("key").GetString().ShouldBe("billing");
        (await ClientCredentials("billing", created.GetProperty("clientSecret").GetString()!)).ShouldBe(HttpStatusCode.OK);

        // The list never shows secrets.
        var list = await _admin.GetStringAsync(Applications);
        list.ShouldContain("\"billing\"");
        list.ShouldNotContain("clientSecret");
    }

    [Fact]
    public async Task The_list_shows_every_application_with_its_state()
    {
        (await Create("billing")).Dispose();

        var list = await _admin.GetFromJsonAsync<JsonElement[]>(Applications);

        list!.Select(a => a.GetProperty("key").GetString()).ShouldBe(["auth", "billing"], ignoreOrder: true);
        list!.ShouldAllBe(a => a.GetProperty("isActive").GetBoolean());
    }

    [Theory]
    [InlineData("Billing", "not a valid key")]
    [InlineData("auth", "already exists")]
    public async Task Invalid_input_is_a_400_that_says_why(string key, string reason)
    {
        using var response = await Create(key);

        await ShouldBeBadRequest(response, reason);
    }

    [Theory]
    [InlineData("""{ "name": "No key", "redirectUris": ["https://x.example/cb"] }""", "not a valid key")]
    [InlineData("""{ "key": "billing", "name": "Billing", "redirectUris": ["https://billing.example/cb#part"] }""", "fragment")]
    [InlineData("""{ "key": "billing", "name": "Billing", "redirectUris": [null] }""", "redirect")]
    public async Task Malformed_requests_are_a_400_too_not_a_crash(string json, string reason)
    {
        using var response = await _admin.PostAsync(Applications, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        await ShouldBeBadRequest(response, reason);
    }

    [Fact]
    public async Task Responses_that_carry_a_secret_are_never_cached()
    {
        using var created = await Create("billing");
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var rotated = await _admin.PostAsync(new Uri($"/admin/api/applications/{id}/secret", UriKind.Relative), null);

        created.Headers.CacheControl!.NoStore.ShouldBeTrue();
        rotated.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Rotating_the_secret_replaces_the_old_one()
    {
        var created = await (await Create("billing")).Content.ReadFromJsonAsync<JsonElement>();
        var oldSecret = created.GetProperty("clientSecret").GetString()!;

        using var response = await _admin.PostAsync(new Uri($"/admin/api/applications/{created.GetProperty("id").GetGuid()}/secret", UriKind.Relative), null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var newSecret = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("clientSecret").GetString()!;
        (await ClientCredentials("billing", newSecret)).ShouldBe(HttpStatusCode.OK);
        (await ClientCredentials("billing", oldSecret)).ShouldNotBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_application_can_be_deactivated_and_activated_again()
    {
        var created = await (await Create("billing")).Content.ReadFromJsonAsync<JsonElement>();
        var active = new Uri($"/admin/api/applications/{created.GetProperty("id").GetGuid()}/active", UriKind.Relative);

        (await _admin.PutAsJsonAsync(active, new { isActive = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.GetFromJsonAsync<JsonElement[]>(Applications))!.Single(a => a.GetProperty("key").GetString() == "billing")
            .GetProperty("isActive").GetBoolean().ShouldBeFalse();

        (await _admin.PutAsJsonAsync(active, new { isActive = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.GetFromJsonAsync<JsonElement[]>(Applications))!.Single(a => a.GetProperty("key").GetString() == "billing")
            .GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task The_auth_application_itself_cannot_be_deactivated()
    {
        // That would lock every admin out, with no admin left to undo it.
        var auth = (await _admin.GetFromJsonAsync<JsonElement[]>(Applications))!.Single(a => a.GetProperty("key").GetString() == "auth");

        using var response = await _admin.PutAsJsonAsync(
            new Uri($"/admin/api/applications/{auth.GetProperty("id").GetGuid()}/active", UriKind.Relative), new { isActive = false });

        await ShouldBeBadRequest(response, "can't be deactivated");
        (await _admin.GetFromJsonAsync<JsonElement[]>(Applications))!.Single(a => a.GetProperty("key").GetString() == "auth")
            .GetProperty("isActive").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_unknown_application_is_a_404()
    {
        var unknown = Guid.CreateVersion7();

        (await _admin.PostAsync(new Uri($"/admin/api/applications/{unknown}/secret", UriKind.Relative), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(new Uri($"/admin/api/applications/{unknown}/active", UriKind.Relative), new { isActive = false }))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> Create(string key) => _admin.PostAsJsonAsync(Applications, new
    {
        key,
        name = key,
        redirectUris = new[] { $"https://{key.ToLowerInvariant()}.example/signin-oidc" },
        postLogoutRedirectUris = Array.Empty<string>(),
    });

    private static async Task ShouldBeBadRequest(HttpResponseMessage response, string reason)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain(reason);
    }

    private async Task<HttpStatusCode> ClientCredentials(string clientId, string secret)
    {
        using var anonymous = _app.CreateClient();
        using var response = await anonymous.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = clientId,
            ["client_secret"] = secret,
        }));
        return response.StatusCode;
    }
}
