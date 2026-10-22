using System.Net;
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
/// DESIGN.md sign-in flow: the client app redirects to AuthService, the user signs in, a role is chosen,
/// a session is created, and the app gets a short-lived token that carries only sid, user id, expiry and audience.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SignInFlowTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _billing = await CreateApplication("billing");
        _browser = new Browser(_app);
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Starting_sign_in_without_being_signed_in_shows_the_login_page()
    {
        using var response = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task A_user_with_one_role_signs_in_and_the_app_gets_a_token_with_only_sid_user_and_audience()
    {
        var user = await CreateUserWithRoles("deniz", "Accountant");

        var code = await SignInAndGetCode("deniz");
        var (status, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        status.ShouldBe(HttpStatusCode.OK);
        var token = new JsonWebToken(body.RootElement.GetProperty("access_token").GetString());
        token.Audiences.ShouldBe(["billing"], "apps must reject tokens for other audiences");
        token.Subject.ShouldBe(user.Id.ToString());
        var sid = Guid.Parse(token.GetClaim("sid").Value);

        // Only these: the standard JWT claims, our sub/sid, and OpenIddict's own bookkeeping (oi_*).
        string[] allowed = ["iss", "exp", "iat", "aud", "scope", "jti", "sub", "sid", "client_id"];
        token.Claims.Select(c => c.Type).Where(type => !type.StartsWith("oi_", StringComparison.Ordinal))
            .ShouldBeSubsetOf(allowed, "no roles, operations, names or e-mail addresses in the token");

        await using var scope = _app.Services.CreateAsyncScope();
        var session = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.SingleAsync(s => s.Id == sid);
        session.UserId.ShouldBe(user.Id);
        session.ExpiresAt.ShouldBe(_app.Clock.GetUtcNow().AddHours(8));
        body.RootElement.TryGetProperty("refresh_token", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task A_configured_session_lifetime_is_used()
    {
        await using var app = new AuthServiceApp(_app.ConnectionString, ("Sessions:Lifetime", "04:00:00"));
        using var browser = new Browser(app);
        await CreateUserWithRoles("deniz", "Accountant");

        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri("billing")));
        var redirect = await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));
        var (_, body) = await browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));

        (await SessionOf(body)).ExpiresAt.ShouldBe(app.Clock.GetUtcNow().AddHours(4));
    }

    [Fact]
    public async Task The_default_role_is_used_when_it_is_one_of_the_users_roles()
    {
        await CreateUserWithRoles("deniz", "Accountant", "Auditor");
        var auditorId = await SetDefaultRole("deniz", "Auditor");

        var code = await SignInAndGetCode("deniz");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        (await SessionOf(body)).RoleId.ShouldBe(auditorId);
    }

    [Fact]
    public async Task A_default_role_the_user_no_longer_holds_is_ignored()
    {
        // The default stays set, but the role was taken away; the remaining role is used instead.
        await CreateUserWithRoles("deniz", "Accountant", "Auditor");
        var auditorId = await SetDefaultRole("deniz", "Auditor");
        await WithDb(async db => db.UserRoles.Remove(await db.UserRoles.SingleAsync(ur => ur.RoleId == auditorId)));

        var code = await SignInAndGetCode("deniz");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        (await SessionOf(body)).RoleId.ShouldNotBe(auditorId);
    }

    [Fact]
    public async Task A_wrong_password_shows_the_login_page_again_and_signs_nobody_in()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));

        using var response = await _browser.LogInAsync(loginPage, "deniz", "wrong-password");

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the form again, with an error");
        using var retry = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        retry.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task A_disabled_user_cannot_sign_in()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        await WithDb(async db => (await db.Users.SingleAsync(u => u.UserName == "deniz")).IsActive = false);

        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        using var response = await _browser.LogInAsync(loginPage, "deniz", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "the form again: disabled users get no cookie");
    }

    [Fact]
    public async Task A_user_without_access_to_the_application_is_refused()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        await WithDb(async db => (await db.UserApplications.SingleAsync()).IsActive = false);

        var redirect = await SignInUntilRedirectToClient("deniz");

        Browser.QueryValue(redirect, "error").ShouldBe("access_denied");
    }

    [Fact]
    public async Task A_user_with_access_but_no_role_is_refused()
    {
        await CreateUserWithRoles("deniz");

        var redirect = await SignInUntilRedirectToClient("deniz");

        Browser.QueryValue(redirect, "error").ShouldBe("access_denied");
    }

    [Fact]
    public async Task An_inactive_application_gets_no_sign_in()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        await WithDb(async db => (await db.Applications.SingleAsync(a => a.Key == "billing")).IsActive = false);

        var redirect = await SignInUntilRedirectToClient("deniz");

        Browser.QueryValue(redirect, "error").ShouldBe("access_denied");
    }

    [Fact]
    public async Task A_code_needs_the_matching_PKCE_verifier()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        var code = await SignInAndGetCode("deniz");

        var (status, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, code, codeVerifier: new string('x', 43));

        status.ShouldBe(HttpStatusCode.BadRequest);
        body.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Refreshing_works_while_the_session_is_active()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, await SignInAndGetCode("deniz"));

        var (status, refreshed) = await _browser.RefreshAsync("billing", _billing.ClientSecret, RefreshToken(body));

        status.ShouldBe(HttpStatusCode.OK);
        var token = new JsonWebToken(refreshed.RootElement.GetProperty("access_token").GetString());
        token.GetClaim("sid").Value.ShouldBe((await SessionOf(body)).Id.ToString(), "still the same session");
    }

    [Fact]
    public async Task Refreshing_fails_once_the_session_is_stopped()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, await SignInAndGetCode("deniz"));
        var sessionId = (await SessionOf(body)).Id;
        await WithDb(async db => (await db.Sessions.SingleAsync(s => s.Id == sessionId)).End(_app.Clock.GetUtcNow()));

        var (status, refreshed) = await _browser.RefreshAsync("billing", _billing.ClientSecret, RefreshToken(body));

        status.ShouldBe(HttpStatusCode.BadRequest);
        refreshed.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task Signing_in_again_from_another_browser_ends_the_first_session_and_its_refresh_token()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        var (_, first) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, await SignInAndGetCode("deniz"));

        // The same user on another device: its own cookies, its own sign-in.
        using var otherBrowser = new Browser(_app);
        using var loginPage = await otherBrowser.FollowAsync(await otherBrowser.GetAsync(otherBrowser.AuthorizeUri("billing")));
        var code = Browser.CodeFrom(await otherBrowser.FollowAsync(await otherBrowser.LogInAsync(loginPage, "deniz", Password)));
        var (_, second) = await otherBrowser.ExchangeCodeAsync("billing", _billing.ClientSecret, code);

        (await SessionOf(first)).EndedAt.ShouldNotBeNull("the first session ends when the user signs in again");
        var (firstStatus, firstRefresh) = await _browser.RefreshAsync("billing", _billing.ClientSecret, RefreshToken(first));
        firstStatus.ShouldBe(HttpStatusCode.BadRequest);
        firstRefresh.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
        var (secondStatus, _) = await otherBrowser.RefreshAsync("billing", _billing.ClientSecret, RefreshToken(second));
        secondStatus.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Refreshing_fails_once_the_role_is_taken_away()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, await SignInAndGetCode("deniz"));
        await WithDb(async db => db.UserRoles.RemoveRange(await db.UserRoles.ToListAsync()));

        var (status, _) = await _browser.RefreshAsync("billing", _billing.ClientSecret, RefreshToken(body));

        status.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A_users_access_token_cannot_call_AuthServices_app_API()
    {
        // The user token's audience is the application ("billing"), not urn:kale:auth-api (promised in #24).
        await CreateUserWithRoles("deniz", "Accountant");
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, await SignInAndGetCode("deniz"));
        using var channel = global::Grpc.Net.Client.GrpcChannel.ForAddress(
            _app.Server.BaseAddress, new global::Grpc.Net.Client.GrpcChannelOptions { HttpHandler = _app.Server.CreateHandler() });
        var sessions = new Auth.Contracts.V1.SessionService.SessionServiceClient(channel);
        var headers = new global::Grpc.Core.Metadata { { "Authorization", $"Bearer {body.RootElement.GetProperty("access_token").GetString()}" } };

        var error = await Should.ThrowAsync<global::Grpc.Core.RpcException>(async () =>
            await sessions.GetSessionAsync(new Auth.Contracts.V1.GetSessionRequest { SessionId = Guid.CreateVersion7().ToString() }, headers));

        error.StatusCode.ShouldBe(global::Grpc.Core.StatusCode.Unauthenticated);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var page = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
            using var _ = await _browser.LogInAsync(page, "deniz", "wrong-password");
        }

        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        using var response = await _browser.LogInAsync(loginPage, "deniz", Password);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, "locked: even the right password shows the form again");
    }

    [Fact]
    public async Task After_login_the_user_is_never_sent_to_another_site()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(
            new Uri("/account/login?ReturnUrl=https%3A%2F%2Fevil.example%2F", UriKind.Relative)));

        using var response = await _browser.LogInAsync(loginPage, "deniz", Password);

        response.Headers.Location!.OriginalString.ShouldBe("/");
    }

    [Fact]
    public async Task An_authorize_request_sent_as_a_form_post_survives_the_login_page()
    {
        await CreateUserWithRoles("deniz", "Accountant");

        using var post = await _browser.PostAuthorizeAsync("billing");
        using var loginPage = await _browser.FollowAsync(post);
        var redirect = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));

        Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Prompt_none_without_a_signed_in_user_returns_login_required()
    {
        using var response = await _browser.GetAsync(_browser.AuthorizeUri("billing", "&prompt=none"));

        Browser.QueryValue(response, "error").ShouldBe("login_required");
    }

    [Fact]
    public async Task Prompt_login_asks_for_the_password_again()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        await SignInAndGetCode("deniz");

        using var response = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing", "&prompt=login")));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task Max_age_asks_for_the_password_again_once_the_login_is_older()
    {
        await CreateUserWithRoles("deniz", "Accountant");
        await SignInAndGetCode("deniz");
        _app.Clock.Advance(TimeSpan.FromMinutes(10));

        using var response = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing", "&max_age=300")));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task The_login_page_speaks_Turkish_or_English()
    {
        using var turkish = new Browser(_app, acceptLanguage: "tr-TR");
        using var english = new Browser(_app, acceptLanguage: "en-US");

        using var turkishPage = await turkish.FollowAsync(await turkish.GetAsync(turkish.AuthorizeUri("billing")));
        using var englishPage = await english.FollowAsync(await english.GetAsync(english.AuthorizeUri("billing")));

        // Written as real characters, not escaped as &#x15F;.
        (await turkishPage.Content.ReadAsStringAsync()).ShouldContain("Giriş yap");
        (await englishPage.Content.ReadAsStringAsync()).ShouldContain("Sign in");
    }

    // ----- Helpers -----

    private async Task<string> SignInAndGetCode(string userName) =>
        Browser.CodeFrom(await SignInUntilRedirectToClient(userName));

    private async Task<HttpResponseMessage> SignInUntilRedirectToClient(string userName)
    {
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        var redirect = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, userName, Password));
        redirect.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(Browser.RedirectUri("billing").AbsoluteUri);
        return redirect;
    }

    private static string RefreshToken(System.Text.Json.JsonDocument body) =>
        body.RootElement.GetProperty("refresh_token").GetString()!;

    private async Task<AuthService.Domain.Session> SessionOf(System.Text.Json.JsonDocument tokenResponse)
    {
        var sid = Guid.Parse(new JsonWebToken(tokenResponse.RootElement.GetProperty("access_token").GetString()).GetClaim("sid").Value);
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions.SingleAsync(s => s.Id == sid);
    }

    private async Task<CreatedApplication> CreateApplication(string key)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, key, [Browser.RedirectUri(key)], []));
    }

    // A user with a password, access to billing, and the given billing roles assigned directly.
    private async Task<User> CreateUserWithRoles(string userName, params string[] roleNames)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

        var user = new User { UserName = userName, DisplayName = userName };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var billing = await db.Applications.SingleAsync(a => a.Key == "billing");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        foreach (var roleName in roleNames)
        {
            var role = await db.AddRole(billing, roleName);
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        }

        await db.SaveChangesAsync();
        return user;
    }

    private async Task<Guid> SetDefaultRole(string userName, string roleName)
    {
        Guid roleId = default;
        await WithDb(async db =>
        {
            var access = await db.UserApplications.SingleAsync(ua => db.Users.Any(u => u.Id == ua.UserId && u.UserName == userName));
            roleId = await db.Roles.Where(r => r.Name == roleName).Select(r => r.Id).SingleAsync();
            access.DefaultRoleId = roleId;
        });
        return roleId;
    }

    private async Task WithDb(Func<AuthDbContext, Task> change)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await change(db);
        await db.SaveChangesAsync();
    }
}
