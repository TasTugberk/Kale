using System.Net;
using Auth.Contracts.V1;
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

/// <summary>Signing out of one application: its session ends, the browser is signed out of AuthService.</summary>
[Collection(UsesPostgres.Name)]
public sealed class SignOutTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";
    private static readonly Uri SignedOut = new("https://billing.example/signed-out");
    private static readonly Uri HrSignedOut = new("https://hr.example/signed-out");

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;
    private Guid _accountantId;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], [SignedOut]));
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            _accountantId = (await db.AddRole(_billing.Application, "Accountant")).Id;
            await db.SaveChangesAsync();
        }

        await CreateUser("deniz");
        _browser = new Browser(_app);
    }

    // A user with a password, access to billing and its Accountant role.
    private async Task CreateUser(string userName)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = new User { UserName = userName, DisplayName = userName };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Application.Id });
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = _accountantId });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Signing_out_ends_the_session_and_returns_to_the_app()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(EndSessionUri(idToken));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        Browser.QueryValue(response, "state").ShouldBe("bye");
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Signing_out_tells_client_apps_that_the_session_stopped()
    {
        var (idToken, sessionId) = await SignIn();

        using var _ = await _browser.GetAsync(EndSessionUri(idToken));

        await using var scope = _app.Services.CreateAsyncScope();
        var message = await scope.ServiceProvider.GetRequiredService<AuthDbContext>().OutboxMessages.SingleAsync();
        InvalidationEvent.Parser.ParseFrom(message.Payload).SessionStopped.SessionId.ShouldBe(sessionId.ToString());
    }

    [Fact]
    public async Task After_signing_out_the_browser_must_log_in_again()
    {
        var (idToken, _) = await SignIn();
        using var _ = await _browser.GetAsync(EndSessionUri(idToken));

        using var next = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));

        next.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task An_unregistered_post_logout_address_is_refused()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(new Uri(
            $"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString("https://evil.example/")}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull("nothing happens on a refused request");
    }

    [Fact]
    public async Task An_id_token_older_than_its_own_lifetime_still_signs_out()
    {
        // Apps keep the ID token from sign-in for the whole session (up to 8 h), far longer than the ID token's
        // own lifetime; sign-out must still accept it as the hint (docs/integration relies on this).
        var (idToken, sessionId) = await SignIn();
        _app.Clock.Advance(TimeSpan.FromHours(2));

        using var response = await _browser.GetAsync(EndSessionUri(idToken));

        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_an_id_token_hint_nothing_happens()
    {
        // Otherwise any site could sign users out with a plain link (logout CSRF).
        var (_, sessionId) = await SignIn();

        using var response = await _browser.GetAsync(new Uri("/connect/endsession", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
        using var next = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        next.RequestMessage!.RequestUri!.AbsolutePath.ShouldNotBe("/account/login", "still signed in");
    }

    [Fact]
    public async Task Without_the_Kale_cookie_a_valid_hint_still_ends_the_session_and_its_refresh_token()
    {
        // The review's case: password at 09:00, sign-in to billing at 12:00, sign-out at 17:30. The kale.signin
        // cookie ended at 17:00 (8 h from the password), the session runs until 20:00.
        await SignIn();
        _app.Clock.Advance(TimeSpan.FromHours(3));
        var redirect = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        var idToken = body.RootElement.GetProperty("id_token").GetString()!;
        var sessionId = Guid.Parse(new JsonWebToken(idToken).GetClaim("sid").Value);
        _app.Clock.Advance(TimeSpan.FromHours(5.5));
        using (var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing"))))
        {
            loginPage.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login", "the Kale cookie has expired");
        }

        using var response = await _browser.GetAsync(EndSessionUri(idToken));

        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
        var (status, refreshed) = await _browser.RefreshAsync("billing", _billing.ClientSecret, body.RootElement.GetProperty("refresh_token").GetString()!);
        status.ShouldBe(HttpStatusCode.BadRequest);
        refreshed.RootElement.GetProperty("error").GetString().ShouldBe("invalid_grant");
    }

    [Fact]
    public async Task A_hint_from_another_browser_ends_the_session_but_not_that_browsers_own_sign_in()
    {
        // Decision: a valid id_token_hint is enough to end the session it names. The kale.signin cookie is cleared
        // only when it belongs to the hint's user: here another user is signed in to Kale in the other browser.
        var (idToken, sessionId) = await SignIn();
        await CreateUser("ayse");
        using var otherBrowser = new Browser(_app);
        using (var loginPage = await otherBrowser.FollowAsync(await otherBrowser.GetAsync(otherBrowser.AuthorizeUri("billing"))))
        {
            using var _ = await otherBrowser.FollowAsync(await otherBrowser.LogInAsync(loginPage, "ayse", Password));
        }

        using var response = await otherBrowser.GetAsync(EndSessionUri(idToken));

        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
        using var next = await otherBrowser.FollowAsync(await otherBrowser.GetAsync(otherBrowser.AuthorizeUri("billing")));
        next.RequestMessage!.RequestUri!.AbsolutePath.ShouldNotBe("/account/login", "ayse is still signed in to Kale");
    }

    [Fact]
    public async Task A_hint_issued_to_another_application_is_refused()
    {
        // OpenIddict ties the hint to the client: client_id must be one of its audiences or presenters.
        var (idToken, sessionId) = await SignIn();
        await SignInToHrToo();

        using var response = await _browser.GetAsync(new Uri($"/connect/endsession?id_token_hint={idToken}&client_id=hr", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_post_logout_address_of_another_application_is_refused()
    {
        // ...and without client_id, the post-logout address must be registered for one of the hint's audiences.
        var (idToken, sessionId) = await SignIn();
        await SignInToHrToo(postLogoutRedirectUri: HrSignedOut);

        using var response = await _browser.GetAsync(new Uri(
            $"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString(HrSignedOut.AbsoluteUri)}",
            UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task A_tampered_id_token_hint_is_refused()
    {
        var (idToken, sessionId) = await SignIn();
        var tampered = idToken[..^4] + (idToken[^4..] == "AAAA" ? "BBBB" : "AAAA");

        using var response = await _browser.GetAsync(EndSessionUri(tampered));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await SessionEndedAt(sessionId)).ShouldBeNull();
    }

    [Fact]
    public async Task Signing_out_of_one_application_keeps_the_session_in_another()
    {
        var (billingIdToken, _) = await SignIn();
        var hrSessionId = await SignInToHrToo();

        using var _ = await _browser.GetAsync(EndSessionUri(billingIdToken));

        (await SessionEndedAt(hrSessionId)).ShouldBeNull("sessions of different applications are independent");
    }

    [Fact]
    public async Task Sign_out_also_works_as_a_form_post()
    {
        var (idToken, sessionId) = await SignIn();

        using var response = await _browser.PostEndSessionAsync(idToken, SignedOut);

        response.Headers.Location!.GetLeftPart(UriPartial.Path).ShouldBe(SignedOut.AbsoluteUri);
        (await SessionEndedAt(sessionId)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Without_a_return_address_the_user_sees_a_signed_out_page()
    {
        var (idToken, _) = await SignIn();

        using var response = await _browser.FollowAsync(await _browser.GetAsync(
            new Uri($"/connect/endsession?id_token_hint={idToken}", UriKind.Relative)));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/signed-out");
        (await response.Content.ReadAsStringAsync()).ShouldContain("Çıkış yapıldı");
    }

    private async Task<Guid> SignInToHrToo(Uri? postLogoutRedirectUri = null)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var hr = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("hr", "HR", [Browser.RedirectUri("hr")], postLogoutRedirectUri is null ? [] : [postLogoutRedirectUri]));
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == "deniz");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = hr.Application.Id });
        var payroll = await db.AddRole(hr.Application, "Payroll");
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = payroll.Id });
        await db.SaveChangesAsync();

        // Already signed in to AuthService, so this goes straight to the code.
        var redirect = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("hr")));
        var (_, body) = await _browser.ExchangeCodeAsync("hr", hr.ClientSecret, Browser.CodeFrom(redirect));
        return Guid.Parse(new JsonWebToken(body.RootElement.GetProperty("access_token").GetString()).GetClaim("sid").Value);
    }

    private async Task<(string IdToken, Guid SessionId)> SignIn()
    {
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        var redirect = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        var idToken = body.RootElement.GetProperty("id_token").GetString()!;
        return (idToken, Guid.Parse(new JsonWebToken(idToken).GetClaim("sid").Value));
    }

    private static Uri EndSessionUri(string idToken) => new(
        $"/connect/endsession?id_token_hint={idToken}&post_logout_redirect_uri={Uri.EscapeDataString(SignedOut.AbsoluteUri)}&state=bye",
        UriKind.Relative);

    private async Task<DateTimeOffset?> SessionEndedAt(Guid sessionId)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<AuthDbContext>().Sessions
            .Where(s => s.Id == sessionId).Select(s => s.EndedAt).SingleAsync();
    }
}
