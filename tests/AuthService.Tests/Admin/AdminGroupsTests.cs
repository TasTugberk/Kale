using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>The admin API's groups: create, rename, delete, members and roles.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminGroupsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri Groups = new("/admin/api/groups", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private HttpClient _admin = null!;
    private Guid _deniz;
    private Role _accountant = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        var authSecret = await AdminSteps.BootstrapAsync(_app);
        _admin = _app.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, authSecret));

        await using var scope = _app.Services.CreateAsyncScope();
        var billing = (await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []))).Application;
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _accountant = await db.AddRole(billing, "Accountant");
        _deniz = (await db.AddUser("deniz")).Id;
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_group_gets_members_and_roles_and_shows_them()
    {
        var id = await Create("Finance");

        (await _admin.PutAsync(Url($"groups/{id}/members/{_deniz}"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.PutAsync(Url($"groups/{id}/roles/{_accountant.Id}"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var details = await _admin.GetFromJsonAsync<JsonElement>(Url($"groups/{id}"));
        details.GetProperty("name").GetString().ShouldBe("Finance");
        details.GetProperty("members").EnumerateArray().Single().GetProperty("userName").GetString().ShouldBe("deniz");
        var role = details.GetProperty("roles").EnumerateArray().Single();
        role.GetProperty("name").GetString().ShouldBe("Accountant");
        role.GetProperty("applicationKey").GetString().ShouldBe("billing");
        (await _admin.GetFromJsonAsync<JsonElement[]>(Groups))!.Single().GetProperty("name").GetString().ShouldBe("Finance");
    }

    [Fact]
    public async Task Members_and_roles_can_be_taken_away_and_the_group_renamed_and_deleted()
    {
        var id = await Create("Finance");
        (await _admin.PutAsync(Url($"groups/{id}/members/{_deniz}"), null)).Dispose();
        (await _admin.PutAsync(Url($"groups/{id}/roles/{_accountant.Id}"), null)).Dispose();

        (await _admin.DeleteAsync(Url($"groups/{id}/members/{_deniz}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.DeleteAsync(Url($"groups/{id}/roles/{_accountant.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.PutAsJsonAsync(Url($"groups/{id}"), new { name = "Money", description = "€" })).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var details = await _admin.GetFromJsonAsync<JsonElement>(Url($"groups/{id}"));
        details.GetProperty("name").GetString().ShouldBe("Money");
        details.GetProperty("members").GetArrayLength().ShouldBe(0);
        details.GetProperty("roles").GetArrayLength().ShouldBe(0);

        (await _admin.DeleteAsync(Url($"groups/{id}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.GetFromJsonAsync<JsonElement[]>(Groups))!.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_duplicate_name_is_a_400()
    {
        await Create("Finance");

        using var response = await _admin.PostAsJsonAsync(Groups, new { name = "Finance" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("already");
    }

    [Fact]
    public async Task Unknown_groups_users_and_roles_are_404s()
    {
        var id = await Create("Finance");
        var unknown = Guid.CreateVersion7();

        (await _admin.GetAsync(Url($"groups/{unknown}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(Url($"groups/{unknown}"), new { name = "X" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(Url($"groups/{unknown}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsync(Url($"groups/{id}/members/{unknown}"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsync(Url($"groups/{id}/roles/{unknown}"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(Url($"groups/{unknown}/members/{_deniz}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(Url($"groups/{unknown}/roles/{_accountant.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private async Task<Guid> Create(string name)
    {
        using var response = await _admin.PostAsJsonAsync(Groups, new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static Uri Url(string path) => new($"/admin/api/{path}", UriKind.Relative);
}
