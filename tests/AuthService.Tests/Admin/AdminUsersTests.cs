using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;

namespace AuthService.Tests.Admin;

/// <summary>The admin API's users: list, create, disable and enable.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminUsersTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri Users = new("/admin/api/users", UriKind.Relative);

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
    public async Task A_new_user_is_created_and_listed_without_their_password()
    {
        using var created = await _admin.PostAsJsonAsync(Users, new { userName = "deniz", displayName = "Deniz", password = AdminSteps.Password });

        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await created.Content.ReadAsStringAsync()).ShouldNotContain(AdminSteps.Password);
        var list = await _admin.GetFromJsonAsync<JsonElement[]>(Users);
        list!.Select(u => u.GetProperty("userName").GetString()).ShouldBe(["ayse", "deniz"]);
        list!.ShouldAllBe(u => u.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task An_invalid_user_is_a_400_that_says_why()
    {
        using var response = await _admin.PostAsJsonAsync(Users, new { userName = "deniz", displayName = "Deniz", password = "short" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("12");
    }

    [Fact]
    public async Task A_user_can_be_disabled_and_enabled_again()
    {
        var id = (await (await _admin.PostAsJsonAsync(Users, new { userName = "deniz", displayName = "Deniz", password = AdminSteps.Password }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var active = new Uri($"/admin/api/users/{id}/active", UriKind.Relative);

        (await _admin.PutAsJsonAsync(active, new { isActive = false })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await IsActive("deniz")).ShouldBeFalse();
        (await _admin.PutAsJsonAsync(active, new { isActive = true })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await IsActive("deniz")).ShouldBeTrue();
    }

    [Fact]
    public async Task The_last_admin_cannot_disable_themselves()
    {
        var ayse = (await _admin.GetFromJsonAsync<JsonElement[]>(Users))!.Single().GetProperty("id").GetGuid();

        using var response = await _admin.PutAsJsonAsync(new Uri($"/admin/api/users/{ayse}/active", UriKind.Relative), new { isActive = false });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("auth.Admin");
    }

    [Fact]
    public async Task An_unknown_user_is_a_404()
    {
        using var response = await _admin.PutAsJsonAsync(new Uri($"/admin/api/users/{Guid.CreateVersion7()}/active", UriKind.Relative), new { isActive = false });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<bool> IsActive(string userName) =>
        (await _admin.GetFromJsonAsync<JsonElement[]>(Users))!.Single(u => u.GetProperty("userName").GetString() == userName)
            .GetProperty("isActive").GetBoolean();
}
