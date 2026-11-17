using System.Net;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Owner's decision: a desktop app (WPF) signs users in to its application through the system browser, as the
/// public client "{key}.desktop": code + PKCE, no secret, a loopback redirect on any port (RFC 8252).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class DesktopSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";
    private const string DesktopClient = "billing.desktop";

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;

    // A desktop app listens on whatever port is free at the moment; tests pick one at random too.
    private readonly Uri _callback = new($"http://127.0.0.1:{Random.Shared.Next(49152, 65535)}/callback");

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<ApplicationService>();
            _billing = await applications.CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
            await applications.SetDesktopSignInAsync(_billing.Application.Id, enabled: true);
        }

        await CreateUser("deniz");
        _browser = new Browser(_app);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task A_desktop_app_signs_in_without_a_secret_on_any_loopback_port_and_gets_a_token_for_its_application()
    {
        var redirect = await SignInUntilRedirect(_callback);
        redirect.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(_callback.AbsoluteUri, "back to the port the app listens on");

        var (status, body) = await _browser.ExchangeCodeAsync(DesktopClient, clientSecret: null, Browser.CodeFrom(redirect), redirectUri: _callback);

        status.ShouldBe(HttpStatusCode.OK, body.RootElement.ToString());
        var token = new JsonWebToken(body.RootElement.GetProperty("access_token").GetString());
        token.Audiences.ShouldBe(["billing"], "the application's own API accepts it like a web sign-in's token");
        var session = await SessionOf(body);
        session.ApplicationId.ShouldBe(_billing.Application.Id);
        body.RootElement.TryGetProperty("refresh_token", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Another_loopback_port_works_too()
    {
        var otherPort = new Uri($"http://127.0.0.1:{_callback.Port + 1}/callback");

        var redirect = await SignInUntilRedirect(otherPort);

        Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refreshing_works_without_a_secret()
    {
        var body = await SignIn();

        var (status, refreshed) = await _browser.RefreshAsync(DesktopClient, clientSecret: null, RefreshToken(body));

        status.ShouldBe(HttpStatusCode.OK, refreshed.RootElement.ToString());
        new JsonWebToken(refreshed.RootElement.GetProperty("access_token").GetString()).Audiences.ShouldBe(["billing"]);
    }

    [Theory]
    [InlineData("http://desktop.example:5000/callback")]
    [InlineData("https://desktop.example/callback")]
    [InlineData("http://127.0.0.1:5000/other-path")]
    public async Task A_redirect_that_is_not_the_loopback_callback_is_refused(string redirectUri)
    {
        using var response = await _browser.GetAsync(_browser.AuthorizeUri(DesktopClient, redirectUri: new Uri(redirectUri)));

        // OpenIddict shows its own error and sends nobody anywhere: the address isn't trusted.
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Without_desktop_sign_in_the_desktop_client_does_not_exist()
    {
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>().SetDesktopSignInAsync(_billing.Application.Id, enabled: false);
        }

        using var response = await _browser.GetAsync(_browser.AuthorizeUri(DesktopClient, redirectUri: _callback));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task Switching_desktop_sign_in_off_ends_refreshing()
    {
        var body = await SignIn();
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>().SetDesktopSignInAsync(_billing.Application.Id, enabled: false);
        }

        var (status, _) = await _browser.RefreshAsync(DesktopClient, clientSecret: null, RefreshToken(body));

        status.ShouldNotBe(HttpStatusCode.OK);
        await using var check = _app.Services.CreateAsyncScope();
        (await check.ServiceProvider.GetRequiredService<AuthDbContext>()
            .Set<OpenIddict.EntityFrameworkCore.Models.OpenIddictEntityFrameworkCoreToken<Guid>>().CountAsync())
            .ShouldBe(0, "the desktop client's refresh tokens are deleted with it");
    }

    [Fact]
    public async Task A_deactivated_application_gets_no_desktop_sign_in_and_no_refresh()
    {
        var body = await SignIn();
        await WithDb(async db => (await db.Applications.SingleAsync()).IsActive = false);

        var redirect = await SignInUntilRedirect(_callback);
        var (status, _) = await _browser.RefreshAsync(DesktopClient, clientSecret: null, RefreshToken(body));

        Browser.QueryValue(redirect, "error").ShouldBe("access_denied");
        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_desktop_sign_in_ends_the_users_web_session_in_the_same_application()
    {
        // Decision: one open session per user and application, whichever client started it.
        using var web = new Browser(_app);
        using var loginPage = await web.FollowAsync(await web.GetAsync(web.AuthorizeUri("billing")));
        var code = Browser.CodeFrom(await web.FollowAsync(await web.LogInAsync(loginPage, "deniz", Password)));
        var (_, webTokens) = await web.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        await SignIn();

        (await SessionOf(webTokens)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_web_sign_in_ends_the_users_desktop_session_in_the_same_application()
    {
        var desktopTokens = await SignIn();

        using var web = new Browser(_app);
        using var loginPage = await web.FollowAsync(await web.GetAsync(web.AuthorizeUri("billing")));
        var code = Browser.CodeFrom(await web.FollowAsync(await web.LogInAsync(loginPage, "deniz", Password)));
        await web.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        (await SessionOf(desktopTokens)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_user_with_several_roles_picks_one_on_the_role_picker()
    {
        await WithDb(async db =>
        {
            var auditor = await db.AddRole(_billing.Application, "Auditor");
            var user = await db.Users.SingleAsync(u => u.UserName == "deniz");
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = auditor.Id });
        });

        using var picker = await SignInUntilRedirect(_callback);
        picker.StatusCode.ShouldBe(HttpStatusCode.OK, "the role picker for the desktop client's application");
        var choices = await Browser.RoleChoicesAsync(picker);
        var redirect = await _browser.FollowAsync(await _browser.ChooseRoleAsync(picker, choices[1]));
        var (status, body) = await _browser.ExchangeCodeAsync(DesktopClient, clientSecret: null, Browser.CodeFrom(redirect), redirectUri: _callback);

        status.ShouldBe(HttpStatusCode.OK, body.RootElement.ToString());
        (await SessionOf(body)).RoleId.ShouldBe(choices[1]);
    }

    [Fact]
    public async Task Signing_out_returns_to_the_desktop_apps_loopback_port()
    {
        var body = await SignIn();
        var signedOut = new Uri($"http://127.0.0.1:{_callback.Port}/signed-out");

        using var response = await _browser.PostEndSessionAsync(body.RootElement.GetProperty("id_token").GetString()!, signedOut);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(signedOut.AbsoluteUri);
        (await SessionOf(body)).EndedAt.ShouldNotBeNull();
    }

    // ----- Helpers -----

    private async Task<JsonDocument> SignIn()
    {
        var code = Browser.CodeFrom(await SignInUntilRedirect(_callback));
        var (status, body) = await _browser.ExchangeCodeAsync(DesktopClient, clientSecret: null, code, redirectUri: _callback);
        status.ShouldBe(HttpStatusCode.OK, body.RootElement.ToString());
        return body;
    }

    // The system browser: the login page (or the existing Kale sign-in), then back to the desktop app's port.
    private async Task<HttpResponseMessage> SignInUntilRedirect(Uri callback)
    {
        using var first = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri(DesktopClient, redirectUri: callback)));
        if (first.RequestMessage!.RequestUri!.AbsolutePath != "/account/login")
        {
            return first;
        }

        return await _browser.FollowAsync(await _browser.LogInAsync(first, "deniz", Password));
    }

    private static string RefreshToken(JsonDocument body) => body.RootElement.GetProperty("refresh_token").GetString()!;

    private async Task<Session> SessionOf(JsonDocument tokenResponse)
    {
        var sid = Guid.Parse(new JsonWebToken(tokenResponse.RootElement.GetProperty("access_token").GetString()).GetClaim("sid").Value);
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.SingleAsync(s => s.Id == sid);
    }

    // A user with a password, access to billing and one billing role.
    private async Task CreateUser(string userName)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = new User { UserName = userName, DisplayName = userName };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Application.Id });
        var role = await db.AddRole(_billing.Application, "Accountant");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    private async Task WithDb(Func<AuthDbContext, Task> change)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }
}
