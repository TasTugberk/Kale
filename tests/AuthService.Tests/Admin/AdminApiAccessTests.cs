using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Auth.Contracts.V1;
using AuthService.Admin;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Admin;

/// <summary>
/// Decision: AuthService is itself the application "auth" with the operation auth.Admin, and its admin API
/// requires it, the same way client apps protect their endpoints. `admin bootstrap` creates all of it.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class AdminApiAccessTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = AdminSteps.Password;
    private static readonly Uri Me = new("/admin/api/me", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private string _authSecret = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _authSecret = await AdminSteps.BootstrapAsync(_app);
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Bootstrap_creates_the_auth_application_its_Admin_role_and_the_first_admin()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var auth = await db.Applications.SingleAsync(a => a.Key == "auth");
        (await db.Operations.Where(o => o.ApplicationId == auth.Id).Select(o => o.Name).ToListAsync()).ShouldBe(["auth.Admin"]);
        var admin = await db.Roles.SingleAsync(r => r.ApplicationId == auth.Id);
        admin.Name.ShouldBe("Admin");
        var user = await db.Users.SingleAsync();
        (await db.UserRoles.CountAsync(ur => ur.UserId == user.Id && ur.RoleId == admin.Id)).ShouldBe(1);
    }

    [Fact]
    public async Task Bootstrapping_again_is_refused()
    {
        var again = await Run(Password + "\n", "bootstrap", "other", "Other", Browser.RedirectUri("auth").AbsoluteUri);

        again.ExitCode.ShouldBe(1);
        again.Errors.ShouldContain("already exists");
    }

    [Fact]
    public async Task An_admin_can_call_the_admin_API()
    {
        using var http = Client(await SignInAsync("ayse", "auth", _authSecret));

        using var response = await http.GetAsync(Me);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var me = await response.Content.ReadFromJsonAsync<JsonElement>();
        me.GetProperty("userName").GetString().ShouldBe("ayse");
        me.GetProperty("displayName").GetString().ShouldBe("Ayşe Admin");
    }

    [Fact]
    public async Task Without_a_token_the_admin_API_is_unauthenticated()
    {
        using var http = _app.CreateClient();

        (await http.GetAsync(Me)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_sign_in_cookie_alone_does_not_reach_the_admin_API()
    {
        // A browser signed in to AuthService carries its cookie on every request; if that were enough, any site
        // could make an admin's browser call the admin API (CSRF). Only a bearer token counts.
        using var browser = new Browser(_app);
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri(AuthService.Admin.AuthApplication.Key)));
        (await browser.FollowAsync(await browser.LogInAsync(loginPage, "ayse", Password))).Dispose();

        using var response = await browser.GetAsync(Me);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_user_of_the_auth_application_without_auth_Admin_is_forbidden()
    {
        await AddUserWithRole("deniz", "auth", "Viewer");

        using var http = Client(await SignInAsync("deniz", "auth", _authSecret));

        (await http.GetAsync(Me)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_token_for_another_application_is_refused()
    {
        // The same admin user, signed in to another application: its token's audience isn't "auth".
        var billing = await CreateApplication("billing");
        await AddUserWithRole("ayse", "billing", "Member");

        using var http = Client(await SignInAsync("ayse", "billing", billing.ClientSecret));

        (await http.GetAsync(Me)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_applications_own_token_is_refused()
    {
        // Client credentials: AuthService's gRPC API accepts these, the admin API must not (no user, no session).
        var billing = await CreateApplication("billing");
        using var anonymous = _app.CreateClient();
        using var tokenResponse = await anonymous.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "billing",
            ["client_secret"] = billing.ClientSecret,
        }));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        using var http = Client(token);

        (await http.GetAsync(Me)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admins_token_is_refused_by_the_apps_gRPC_API()
    {
        // OpenIddict validation now accepts the audience "auth" too; the gRPC API's own policy must still refuse
        // user tokens (it only takes applications' client-credentials tokens).
        var token = await SignInAsync("ayse", "auth", _authSecret);
        using var channel = GrpcChannel.ForAddress(_app.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _app.Server.CreateHandler() });
        var registration = new OperationRegistration.OperationRegistrationClient(channel);

        var error = await Should.ThrowAsync<RpcException>(() => registration.RegisterOperationsAsync(
            new RegisterOperationsRequest { Operations = { new OperationDefinition { Name = "Anything" } } },
            new Metadata { { "Authorization", $"Bearer {token}" } }).ResponseAsync);

        error.StatusCode.ShouldBe(StatusCode.PermissionDenied);
    }

    [Fact]
    public async Task The_admin_UIs_client_secret_cannot_rewrite_AuthServices_own_operations()
    {
        // "auth" is a confidential client like any other, so its secret gets a client-credentials token for the
        // gRPC API. Registering operations for "auth" through it could make auth.Admin obsolete (locking every
        // admin out) or add new auth.* operations; only AuthService itself registers them.
        using var anonymous = _app.CreateClient();
        using var tokenResponse = await anonymous.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "auth",
            ["client_secret"] = _authSecret,
        }));
        var token = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;
        using var channel = GrpcChannel.ForAddress(_app.Server.BaseAddress, new GrpcChannelOptions { HttpHandler = _app.Server.CreateHandler() });

        var error = await Should.ThrowAsync<RpcException>(() => new OperationRegistration.OperationRegistrationClient(channel).RegisterOperationsAsync(
            new RegisterOperationsRequest { Operations = { new OperationDefinition { Name = "Other" } } },
            new Metadata { { "Authorization", $"Bearer {token}" } }).ResponseAsync);

        error.StatusCode.ShouldBe(StatusCode.PermissionDenied);
        await using var scope = _app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Operations.SingleAsync()).IsObsolete.ShouldBeFalse();
    }

    [Fact]
    public async Task An_admin_whose_session_was_stopped_is_forbidden()
    {
        var token = await SignInAsync("ayse", "auth", _authSecret);
        var sessionId = Guid.Parse(new Microsoft.IdentityModel.JsonWebTokens.JsonWebToken(token).GetClaim("sid").Value);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AuthService.Sessions.SessionStopper>().StopAsync(sessionId);
        }

        using var http = Client(token);

        (await http.GetAsync(Me)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_lookups_only_answer_for_sessions_and_roles_of_auth()
    {
        // The admin API checks sessions in-process; a session or role of another application must not count,
        // just as the gRPC API answers each application only about its own.
        await CreateApplication("billing");
        await AddUserWithRole("deniz", "billing", "Member");
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var billingRole = await db.Roles.SingleAsync(r => r.Name == "Member");
        var deniz = await db.Users.SingleAsync(u => u.UserName == "deniz");
        var billingSession = new Session
        {
            UserId = deniz.Id, ApplicationId = billingRole.ApplicationId, RoleId = billingRole.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(1),
        };
        db.Sessions.Add(billingSession);
        await db.SaveChangesAsync();
        var lookups = scope.ServiceProvider.GetRequiredService<AdminSessionLookups>();

        (await lookups.GetAsync(billingSession.Id.ToString(), CancellationToken.None)).ShouldBeNull();
        (await lookups.GetAsync(billingRole.Id, CancellationToken.None)).ShouldBeNull();
    }

    private HttpClient Client(string accessToken)
    {
        var http = _app.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return http;
    }

    private Task<string> SignInAsync(string userName, string clientId, string clientSecret) =>
        AdminSteps.SignInAsync(_app, userName, clientId, clientSecret);

    private async Task<CreatedApplication> CreateApplication(string key)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, key, [Browser.RedirectUri(key)], []));
    }

    // Creates the user if needed, and gives them access to the application with a new role (no operations).
    private async Task AddUserWithRole(string userName, string applicationKey, string roleName)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await users.FindByNameAsync(userName);
        if (user is null)
        {
            user = new User { UserName = userName, DisplayName = userName };
            (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        }

        var application = await db.Applications.SingleAsync(a => a.Key == applicationKey);
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        var role = await db.AddRole(application, roleName);
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    private Task<(int ExitCode, string Output, string Errors)> Run(string input, params string[] args) =>
        AdminSteps.RunAsync(_app, input, args);
}
