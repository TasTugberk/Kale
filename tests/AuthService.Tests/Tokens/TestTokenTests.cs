using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Admin;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using AuthService.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.Tests.Tokens;

/// <summary>
/// Test tokens (Development/Testing only): the admin UI asks connect/token, with its own client secret and the
/// admin's access token, for a user's access token to an application, so developers can try their API without
/// building a sign-in first. It's a real session, started under the normal sign-in rules.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class TestTokenTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private string _authSecret = null!;
    private string _adminToken = null!;
    private CreatedApplication _billing = null!;
    private Guid _denizId;
    private Guid _accountantId;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _authSecret = await AdminSteps.BootstrapAsync(_app);
        _adminToken = await AdminSteps.SignInAsync(_app, "ayse", AuthApplication.Key, _authSecret);

        await using var scope = _app.Services.CreateAsyncScope();
        _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
        (await AdminSteps.RunAsync(_app, AdminSteps.Password + "\n", "create-user", "deniz", "Deniz")).ExitCode.ShouldBe(0);
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var deniz = await db.Users.SingleAsync(u => u.UserName == "deniz");
        var accountant = await db.AddRole(_billing.Application, "Accountant");
        db.UserApplications.Add(new UserApplication { UserId = deniz.Id, ApplicationId = _billing.Application.Id });
        db.UserRoles.Add(new UserRole { UserId = deniz.Id, RoleId = accountant.Id });
        await db.SaveChangesAsync();
        (_denizId, _accountantId) = (deniz.Id, accountant.Id);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task An_admin_gets_a_users_access_token_for_an_application_backed_by_a_real_session()
    {
        var (status, body) = await AskAsync(_adminToken, "billing", _denizId, _accountantId);

        status.ShouldBe(HttpStatusCode.OK, body.ToString());
        var token = new JsonWebToken(body.GetProperty("access_token").GetString());
        token.Subject.ShouldBe(_denizId.ToString());
        token.GetPayloadValue<string>("aud").ShouldBe("billing");
        token.Typ.ShouldBe("at+jwt");
        body.GetProperty("expires_in").GetInt32().ShouldBe(600);
        body.TryGetProperty("refresh_token", out _).ShouldBeFalse("a test token is only an access token");
        body.TryGetProperty("id_token", out _).ShouldBeFalse();

        var sessionId = Guid.Parse(token.GetClaim("sid").Value);
        await using var scope = _app.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.SingleAsync(s => s.Id == sessionId);
        session.UserId.ShouldBe(_denizId);
        session.RoleId.ShouldBe(_accountantId);
        session.EndedAt.ShouldBeNull();
    }

    [Fact]
    public async Task Without_a_role_the_users_only_or_default_role_is_used()
    {
        var (status, body) = await AskAsync(_adminToken, "billing", _denizId, roleId: null);

        status.ShouldBe(HttpStatusCode.OK, body.ToString());
    }

    [Fact]
    public async Task Without_the_admins_token_it_is_refused()
    {
        var (status, body) = await AskAsync(adminToken: null, "billing", _denizId, _accountantId);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("access_denied");
    }

    [Fact]
    public async Task Someone_signed_in_to_the_admin_UI_without_auth_Admin_is_refused()
    {
        (await AdminSteps.RunAsync(_app, AdminSteps.Password + "\n", "create-user", "omer", "Ömer")).ExitCode.ShouldBe(0);
        (await AdminSteps.RunAsync(_app, "", "create-role", AuthApplication.Key, "Viewer")).ExitCode.ShouldBe(0);
        (await AdminSteps.RunAsync(_app, "", "assign-role", "omer", AuthApplication.Key, "Viewer")).ExitCode.ShouldBe(0);
        var notAdmin = await AdminSteps.SignInAsync(_app, "omer", AuthApplication.Key, _authSecret);

        var (status, body) = await AskAsync(notAdmin, "billing", _denizId, _accountantId);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("access_denied");
    }

    [Fact]
    public async Task Only_the_admin_UI_may_ask()
    {
        // Even a client given the grant's permission (OpenIddict would refuse others before Kale's code runs):
        // Kale itself accepts only the admin UI.
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            var client = (await clients.FindByClientIdAsync("billing"))!;
            var descriptor = new OpenIddict.Abstractions.OpenIddictApplicationDescriptor();
            await clients.PopulateAsync(descriptor, client);
            descriptor.Permissions.Add(TestTokens.AdminUiPermission);
            await clients.UpdateAsync(client, descriptor);
        }

        var (status, body) = await AskAsync(_adminToken, "billing", _denizId, _accountantId, clientId: "billing", clientSecret: _billing.ClientSecret);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("unauthorized_client");
        body.GetProperty("error_description").GetString()!.ShouldContain("Only the admin UI");
    }

    [Fact]
    public async Task A_chosen_role_is_used_and_one_the_user_does_not_hold_is_refused()
    {
        Guid reviewer, auditor;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var reviewerRole = await db.AddRole(_billing.Application, "Reviewer");
            var auditorRole = await db.AddRole(_billing.Application, "Auditor");
            db.UserRoles.Add(new UserRole { UserId = _denizId, RoleId = reviewerRole.Id });
            await db.SaveChangesAsync();
            (reviewer, auditor) = (reviewerRole.Id, auditorRole.Id);
        }

        var (chosenStatus, chosen) = await AskAsync(_adminToken, "billing", _denizId, reviewer);
        var (notHeldStatus, notHeld) = await AskAsync(_adminToken, "billing", _denizId, auditor);

        chosenStatus.ShouldBe(HttpStatusCode.OK, chosen.ToString());
        var sessionId = Guid.Parse(new JsonWebToken(chosen.GetProperty("access_token").GetString()).GetClaim("sid").Value);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.SingleAsync(s => s.Id == sessionId)).RoleId.ShouldBe(reviewer);
        }

        notHeldStatus.ShouldBe(HttpStatusCode.BadRequest);
        notHeld.GetProperty("error").GetString().ShouldBe("invalid_request");
        notHeld.GetProperty("error_description").GetString()!.ShouldContain("doesn't hold");
    }

    [Fact]
    public async Task No_test_tokens_for_the_admin_UI_itself()
    {
        // An admin-API token for another admin would let one admin act as another, and end their admin UI session.
        await using var scope = _app.Services.CreateAsyncScope();
        var ayse = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.SingleAsync(u => u.UserName == "ayse");

        var (status, body) = await AskAsync(_adminToken, AuthApplication.Key, ayse.Id, roleId: null);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("invalid_request");
        body.GetProperty("error_description").GetString()!.ShouldContain("auth");
    }

    [Fact]
    public async Task The_normal_sign_in_rules_apply_to_the_user()
    {
        // No access to the application: the same refusal as at a real sign-in.
        (await AdminSteps.RunAsync(_app, AdminSteps.Password + "\n", "create-user", "zeynep", "Zeynep")).ExitCode.ShouldBe(0);
        await using var scope = _app.Services.CreateAsyncScope();
        var zeynep = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Users.SingleAsync(u => u.UserName == "zeynep");

        var (status, body) = await AskAsync(_adminToken, "billing", zeynep.Id, roleId: null);

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.GetProperty("error").GetString().ShouldBe("invalid_grant");
        body.GetProperty("error_description").GetString()!.ShouldContain("access");
    }

    [Fact]
    public async Task A_missing_or_malformed_user_is_an_invalid_request()
    {
        using var http = _app.CreateClient();
        using var request = TokenRequest(_adminToken, AuthApplication.Key, _authSecret, new()
        {
            ["application"] = "billing",
            ["user_id"] = "not-a-guid",
        });
        using var response = await http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString().ShouldBe("invalid_request");
    }

    [Fact]
    public async Task A_stack_set_up_before_test_tokens_gets_the_admin_UIs_permission_at_startup()
    {
        // Take the permission away, as on a database created before test tokens existed, then start again.
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var clients = scope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();
            var client = (await clients.FindByClientIdAsync(AuthApplication.Key))!;
            var descriptor = new OpenIddict.Abstractions.OpenIddictApplicationDescriptor();
            await clients.PopulateAsync(descriptor, client);
            descriptor.Permissions.Remove(TestTokens.AdminUiPermission);
            await clients.UpdateAsync(client, descriptor);
        }

        await using var restarted = new AuthServiceApp(_app.ConnectionString);
        await using var restartedScope = restarted.Services.CreateAsyncScope();
        var restartedClients = restartedScope.ServiceProvider.GetRequiredService<OpenIddict.Abstractions.IOpenIddictApplicationManager>();

        (await restartedClients.HasPermissionAsync((await restartedClients.FindByClientIdAsync(AuthApplication.Key))!, TestTokens.AdminUiPermission))
            .ShouldBeTrue();
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> AskAsync(
        string? adminToken, string application, Guid userId, Guid? roleId, string? clientId = null, string? clientSecret = null)
    {
        var parameters = new Dictionary<string, string> { ["application"] = application, ["user_id"] = userId.ToString() };
        if (roleId is { } role)
        {
            parameters["role_id"] = role.ToString();
        }

        using var http = _app.CreateClient();
        using var request = TokenRequest(adminToken, clientId ?? AuthApplication.Key, clientSecret ?? _authSecret, parameters);
        using var response = await http.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadFromJsonAsync<JsonElement>());
    }

    // The request the admin UI sends: its own client credentials in the body, the admin's token as the bearer.
    private static HttpRequestMessage TokenRequest(string? adminToken, string clientId, string clientSecret, Dictionary<string, string> parameters)
    {
        parameters["grant_type"] = TestTokens.GrantType;
        parameters["client_id"] = clientId;
        parameters["client_secret"] = clientSecret;
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/connect/token", UriKind.Relative)) { Content = new FormUrlEncodedContent(parameters) };
        if (adminToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        }

        return request;
    }
}
