using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>The admin API's users: list, create, disable and enable, set a new password, reset two-step sign-in.</summary>
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
    public async Task An_invalid_user_is_refused_with_a_code_for_every_reason()
    {
        // Identity's own reasons too, so the admin UI can show each one in the admin's language.
        using var response = await _admin.PostAsJsonAsync(Users, new { userName = "deniz", displayName = " ", password = "short" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var reasons = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("reasons").EnumerateArray().ToList();
        reasons.Select(r => r.GetProperty("code").GetString()).ShouldBe(
            ["user.display-name-required", "password.too-short", "password.needs-symbol", "password.needs-digit", "password.needs-uppercase"],
            ignoreOrder: true);
        reasons.Single(r => r.GetProperty("code").GetString() == "password.too-short")
            .GetProperty("values").GetProperty("min").GetString().ShouldBe("12");
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

    [Fact]
    public async Task An_admin_sets_a_new_password_and_the_answer_has_no_body()
    {
        var id = await CreateUser("deniz");

        using var response = await _admin.PutAsJsonAsync(PasswordOf(id), new { password = NewPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await response.Content.ReadAsStringAsync()).ShouldBeEmpty();
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await users.CheckPasswordAsync((await users.FindByNameAsync("deniz"))!, NewPassword)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_weak_new_password_is_a_400_that_says_why_without_repeating_it()
    {
        var id = await CreateUser("deniz");
        const string weak = "weakpassword";

        using var response = await _admin.PutAsJsonAsync(PasswordOf(id), new { password = weak });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("digit");
        body.ShouldNotContain(weak);
    }

    [Fact]
    public async Task A_password_request_without_a_password_is_a_400()
    {
        var id = await CreateUser("deniz");

        using var response = await _admin.PutAsJsonAsync(PasswordOf(id), new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("password");
    }

    [Fact]
    public async Task A_password_for_an_unknown_user_is_a_404()
    {
        using var response = await _admin.PutAsJsonAsync(PasswordOf(Guid.CreateVersion7()), new { password = NewPassword });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_resets_two_step_sign_in_and_the_user_details_say_it_is_off()
    {
        var id = await CreateUser("deniz");
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            var user = (await users.FindByIdAsync(id.ToString()))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }
        (await TwoFactorEnabled(id)).ShouldBeTrue();

        using var response = await _admin.DeleteAsync(TwoFactorOf(id));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await TwoFactorEnabled(id)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_two_step_reset_for_an_unknown_user_is_a_404()
    {
        using var response = await _admin.DeleteAsync(TwoFactorOf(Guid.CreateVersion7()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_change_that_PostgreSQL_ends_in_a_deadlock_is_a_409_that_says_to_try_again()
    {
        var id = await CreateUser("deniz");
        Guid applicationId;
        Guid roleId;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var billing = await db.AddApplication("billing");
            (applicationId, roleId) = (billing.Id, (await db.AddRole(billing, "Member")).Id);
        }

        // Someone else holds the role and, once the change below holds the user and waits for the role, asks for
        // the user: a deadlock. Only the change looks for it (this side waits longer before checking), so it's
        // the change that PostgreSQL ends.
        await using var otherScope = _app.Services.CreateAsyncScope();
        var other = otherScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlAsync($"SET LOCAL deadlock_timeout = '30s'");
        await other.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {roleId} FOR UPDATE");
        var change = _admin.PutAsJsonAsync(
            new Uri($"/admin/api/users/{id}/applications/{applicationId}", UriKind.Relative), new { isActive = true, defaultRoleId = roleId });
        await LockWaits.UntilSomeoneWaitsAsync(other);
        await other.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {id} FOR UPDATE");
        await transaction.CommitAsync();

        using var response = await change;

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("reasons")[0].GetProperty("code").GetString().ShouldBe(RefusalCodes.ConcurrentChange);
    }

    [Fact]
    public async Task An_admin_corrects_a_users_names()
    {
        var id = await CreateUser("deniz");

        using var response = await _admin.PutAsJsonAsync(UserUri(id), new { userName = "deniz.yilmaz", displayName = "Deniz Yılmaz" });

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        var details = await _admin.GetFromJsonAsync<JsonElement>(UserUri(id));
        details.GetProperty("userName").GetString().ShouldBe("deniz.yilmaz");
        details.GetProperty("displayName").GetString().ShouldBe("Deniz Yılmaz");
    }

    [Fact]
    public async Task A_taken_user_name_is_a_400_that_says_why()
    {
        var id = await CreateUser("deniz");

        using var response = await _admin.PutAsJsonAsync(UserUri(id), new { userName = "ayse", displayName = "Deniz" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("already taken");
    }

    [Fact]
    public async Task An_admin_deletes_a_user()
    {
        var id = await CreateUser("deniz");

        using var response = await _admin.DeleteAsync(UserUri(id));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.GetAsync(UserUri(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _admin.DeleteAsync(UserUri(id))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_admin_cannot_delete_their_own_account_even_with_another_admin()
    {
        await CreateUser("deniz");
        (await AdminSteps.RunAsync(_app, "", "assign-role", "deniz", AuthApplication.Key, "Admin")).ExitCode.ShouldBe(0);
        var ayse = (await _admin.GetFromJsonAsync<JsonElement[]>(Users))!.First(u => u.GetProperty("userName").GetString() == "ayse")
            .GetProperty("id").GetGuid();

        using var response = await _admin.DeleteAsync(UserUri(ayse));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString()!.ShouldContain("own account");
    }

    private static Uri UserUri(Guid userId) => new($"/admin/api/users/{userId}", UriKind.Relative);

    private const string NewPassword = "Battery-staple-7";

    private static Uri TwoFactorOf(Guid userId) => new($"/admin/api/users/{userId}/two-factor", UriKind.Relative);

    private async Task<bool> TwoFactorEnabled(Guid userId) =>
        (await _admin.GetFromJsonAsync<JsonElement>(new Uri($"/admin/api/users/{userId}", UriKind.Relative)))
            .GetProperty("twoFactorEnabled").GetBoolean();

    private static Uri PasswordOf(Guid userId) => new($"/admin/api/users/{userId}/password", UriKind.Relative);

    private async Task<Guid> CreateUser(string userName)
    {
        using var created = await _admin.PostAsJsonAsync(Users, new { userName, displayName = userName, password = AdminSteps.Password });
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<bool> IsActive(string userName) =>
        (await _admin.GetFromJsonAsync<JsonElement[]>(Users))!.Single(u => u.GetProperty("userName").GetString() == userName)
            .GetProperty("isActive").GetBoolean();
}
