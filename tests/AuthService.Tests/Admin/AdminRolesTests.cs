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

/// <summary>The admin API's roles: an application's operations and roles, a role's operations, deleting a role.</summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminRolesTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private HttpClient _admin = null!;
    private Application _billing = null!;

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
        await db.AddOperation(_billing, "InvoiceRead");
        var obsolete = await db.AddOperation(_billing, "OldReport");
        obsolete.ObsoletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _admin.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task An_applications_operations_are_listed_with_obsolete_ones_marked()
    {
        var operations = await _admin.GetFromJsonAsync<JsonElement[]>(Url($"applications/{_billing.Id}/operations"));

        operations!.Select(o => (o.GetProperty("name").GetString(), o.GetProperty("isObsolete").GetBoolean()))
            .ShouldBe([("billing.InvoiceRead", false), ("billing.OldReport", true)], ignoreOrder: true);
    }

    [Fact]
    public async Task A_role_is_created_given_operations_renamed_and_listed()
    {
        using var created = await _admin.PostAsJsonAsync(Url($"applications/{_billing.Id}/roles"), new { name = "Accountant", description = "Books" });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var roleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await _admin.PutAsJsonAsync(Url($"roles/{roleId}/operations"), Operations("billing.InvoiceRead")))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.PutAsJsonAsync(Url($"roles/{roleId}"), new { name = "Bookkeeper", description = (string?)null }))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        var role = (await _admin.GetFromJsonAsync<JsonElement[]>(Url($"applications/{_billing.Id}/roles")))!.Single();
        role.GetProperty("name").GetString().ShouldBe("Bookkeeper");
        role.GetProperty("operations").EnumerateArray().Select(o => o.GetString()).ShouldBe(["billing.InvoiceRead"]);
    }

    [Fact]
    public async Task Deleting_a_role_removes_it()
    {
        using var created = await _admin.PostAsJsonAsync(Url($"applications/{_billing.Id}/roles"), new { name = "Accountant" });
        var roleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await _admin.DeleteAsync(Url($"roles/{roleId}"))).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await _admin.GetFromJsonAsync<JsonElement[]>(Url($"applications/{_billing.Id}/roles")))!.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("Accountant", "already has a role")]
    [InlineData("", "needs a name")]
    public async Task An_invalid_role_is_a_400_that_says_why(string name, string reason)
    {
        (await _admin.PostAsJsonAsync(Url($"applications/{_billing.Id}/roles"), new { name = "Accountant" })).Dispose();

        using var response = await _admin.PostAsJsonAsync(Url($"applications/{_billing.Id}/roles"), new { name });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain(reason);
    }

    [Fact]
    public async Task An_obsolete_or_foreign_operation_is_a_400()
    {
        using var created = await _admin.PostAsJsonAsync(Url($"applications/{_billing.Id}/roles"), new { name = "Accountant" });
        var roleId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        (await _admin.PutAsJsonAsync(Url($"roles/{roleId}/operations"), Operations("billing.OldReport")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _admin.PutAsJsonAsync(Url($"roles/{roleId}/operations"), Operations("auth.Admin")))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Unknown_applications_and_roles_are_404s()
    {
        var unknown = Guid.CreateVersion7();

        (await _admin.GetAsync(Url($"applications/{unknown}/operations"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.GetAsync(Url($"applications/{unknown}/roles"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PostAsJsonAsync(Url($"applications/{unknown}/roles"), new { name = "X" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(Url($"roles/{unknown}"), new { name = "X" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.PutAsJsonAsync(Url($"roles/{unknown}/operations"), Operations())).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(Url($"roles/{unknown}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static object Operations(params string[] names) => new { operations = names };

    private static Uri Url(string path) => new($"/admin/api/{path}", UriKind.Relative);
}
