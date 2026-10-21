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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>The admin API's access: a user's details, their access to applications, their roles.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _admin = null!;
    private Guid _deniz;
    private Application _billing = null!;
    private Role _accountant = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        var authSecret = await AdminSteps.BootstrapAsync(_app);
        _admin = _app.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, authSecret));

        await using var scope = _app.Services.CreateAsyncScope();
        _billing = (await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []))).Application;
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _accountant = await db.AddRole(_billing, "Accountant");
        _deniz = (await db.AddUser("deniz")).Id;
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Access_and_a_role_show_up_in_the_users_details()
    {
        (await _admin.PutAsJsonAsync(Url($"users/{_deniz}/applications/{_billing.Id}"), new { isActive = true, defaultRoleId = _accountant.Id }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.PutAsync(Url($"users/{_deniz}/roles/{_accountant.Id}"), null)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var details = await _admin.GetFromJsonAsync<JsonElement>(Url($"users/{_deniz}"));

        details.GetProperty("userName").GetString().ShouldBe("deniz");
        var access = details.GetProperty("applications").EnumerateArray().Single();
        access.GetProperty("applicationKey").GetString().ShouldBe("billing");
        access.GetProperty("isActive").GetBoolean().ShouldBeTrue();
        access.GetProperty("defaultRoleId").GetGuid().ShouldBe(_accountant.Id);
        var role = details.GetProperty("roles").EnumerateArray().Single();
        role.GetProperty("name").GetString().ShouldBe("Accountant");
        role.GetProperty("applicationKey").GetString().ShouldBe("billing");
    }

    [Fact]
    public async Task A_role_can_be_taken_away()
    {
        (await _admin.PutAsync(Url($"users/{_deniz}/roles/{_accountant.Id}"), null)).Dispose();

        (await _admin.DeleteAsync(Url($"users/{_deniz}/roles/{_accountant.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _admin.GetFromJsonAsync<JsonElement>(Url($"users/{_deniz}"))).GetProperty("roles").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_default_role_of_another_application_is_a_400()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var adminRole = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Roles.SingleAsync(r => r.Name == "Admin");

        using var response = await _admin.PutAsJsonAsync(Url($"users/{_deniz}/applications/{_billing.Id}"), new { isActive = true, defaultRoleId = adminRole.Id });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_users_applications_and_roles_are_404s()
    {
        var unknown = Guid.CreateVersion7();

        (await _admin.GetAsync(Url($"users/{unknown}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(Url($"users/{unknown}/applications/{_billing.Id}"), new { isActive = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(Url($"users/{_deniz}/applications/{unknown}"), new { isActive = true })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsync(Url($"users/{_deniz}/roles/{unknown}"), null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(Url($"users/{unknown}/roles/{_accountant.Id}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static Uri Url(string path) => new($"/admin/api/{path}", UriKind.Relative);
}
