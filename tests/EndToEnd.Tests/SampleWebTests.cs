using System.Net;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Outbox;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace EndToEnd.Tests;

/// <summary>
/// samples/SampleWeb, a web application's server using Auth.Web, against the real AuthService and SampleApp: the
/// browser signs in at Kale, the page calls SampleApp through SampleWeb with the user's token, the token is
/// renewed, and an ended Kale session signs the user out here (Kale then asks for the password; the user's Kale
/// sign-in for their other applications stays).
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class SampleWebTests(PostgresFixture postgres, ITestOutputHelper output) : IAsyncLifetime
{
    // Access tokens last 10 minutes and are renewed when a minute or less is left.
    private static readonly TimeSpan PastRenewalTime = TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(1);

    private AuthServiceApp _auth = null!;
    private CreatedApplication _sample = null!;
    private SampleAppHost _api = null!;
    private Backchannel _kale = null!;
    private SampleWebHost _web = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            _sample = await scope.ServiceProvider.GetRequiredService<ApplicationService>().CreateAsync(new NewApplication(
                "sample", "Sample", [SampleWebHost.SignInReturnAddress], [SampleWebHost.SignOutReturnAddress]));
        }

        _api = new SampleAppHost(_auth, _sample.ClientSecret);
        _api.StartServer();
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);

        _kale = new Backchannel(_auth.Server.CreateHandler());
        _web = new SampleWebHost(_auth, _sample.ClientSecret, _api, _kale);
        _web.StartServer();
    }

    public async Task DisposeAsync()
    {
        await _web.DisposeAsync();
        await _api.DisposeAsync();
        await _auth.DisposeAsync();
    }

    [Fact]
    public async Task Signing_in_brings_the_user_back_and_the_page_can_ask_who_is_signed_in()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        using var landed = await browser.SignInAsync(userName, returnUrl: "/index.html?tab=notes");

        landed.StatusCode.ShouldBe(HttpStatusCode.OK);
        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(SampleWebHost.Address, "/index.html?tab=notes"));
        var me = await MeAsync(browser);
        me.GetProperty("sub").GetString().ShouldBe((await KaleUserIdAsync(userName)).ToString());
        me.GetProperty("name").GetString().ShouldBe(userName, "AuthServiceSteps makes the display name the user name");
        me.GetProperty("preferred_username").GetString().ShouldBe(userName);
        me.TryGetProperty("sid", out _).ShouldBeFalse("the page's script needs nothing to do with the Kale session");

        // The tokens stay on the server: the cookie only holds a key (with the tokens in it, it was about 7 KB).
        var cookie = browser.Cookies.GetCookies(SampleWebHost.Address).ShouldHaveSingleItem();
        output.WriteLine($"Sign-in cookie: {cookie.Name}, {cookie.Value.Length} chars");
        cookie.Name.ShouldBe("kale.sample");
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Value.Length.ShouldBeLessThan(1000);
    }

    [Fact]
    public async Task The_page_calls_the_api_with_the_users_token_and_gets_what_the_role_allows()
    {
        using var reader = _web.CreateBrowser();
        await reader.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        using var nobody = _web.CreateBrowser();
        await nobody.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, operation: null));

        using var allowed = await reader.FetchAsync(HttpMethod.Get, "/api/notes");
        using var refused = await nobody.FetchAsync(HttpMethod.Get, "/api/notes");

        allowed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).ShouldBe("[]");
        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "SampleApp checked the role, which lacks NoteRead");
    }

    [Fact]
    public async Task Without_sign_in_a_fetch_gets_401_and_a_page_is_sent_to_Kales_sign_in()
    {
        using var browser = _web.CreateBrowser();

        using var fetched = await browser.FetchAsync(HttpMethod.Get, "/api/notes");
        using var opened = await browser.OpenAsync("/api/notes");

        fetched.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        WebBrowser.IsKaleLoginPage(opened).ShouldBeTrue();
    }

    [Fact]
    public async Task Sign_in_accepts_only_a_return_address_on_this_site()
    {
        using var browser = _web.CreateBrowser();

        using var response = await browser.OpenAsync("/auth/login?returnUrl=https%3A%2F%2Fevil.example%2F");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task The_access_token_is_renewed_a_minute_before_it_expires_once_for_parallel_requests()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);
        var afterSignIn = _kale.Calls(TokenPath);

        _web.Clock.Advance(PastRenewalTime);
        var parallel = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => browser.FetchAsync(HttpMethod.Get, "/api/notes")));

        parallel.Select(r => r.StatusCode).ShouldAllBe(status => status == HttpStatusCode.OK);
        _kale.Calls(TokenPath).ShouldBe(afterSignIn + 1, "the five requests shared one renewal");
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);
        _kale.Calls(TokenPath).ShouldBe(afterSignIn + 1, "the renewed token's expiry was kept in the cookie");
    }

    [Fact]
    public async Task The_newest_refresh_token_is_kept()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        _web.Clock.Advance(PastRenewalTime);
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Kale accepts a replaced refresh token for 30 seconds only. (Under a minute, so SampleApp still accepts
        // the tokens Kale's clock now stamps.)
        _auth.Clock.Advance(TimeSpan.FromSeconds(40));
        _web.Clock.Advance(PastRenewalTime);
        var renewals = _kale.Calls(TokenPath);

        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);
        _kale.Calls(TokenPath).ShouldBe(renewals + 1);
    }

    [Fact]
    public async Task When_Kale_cannot_be_reached_the_user_stays_signed_in_and_renewal_resumes_later()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        _web.Clock.Advance(PastRenewalTime);

        _kale.Unreachable = true;
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK, "the old token is still good");

        _kale.Unreachable = false;
        var before = _kale.Calls(TokenPath);
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);
        _kale.Calls(TokenPath).ShouldBe(before + 1, "it tried again instead of remembering the failure");
    }

    [Fact]
    public async Task A_stopped_session_signs_the_user_out_here_and_the_next_sign_in_asks_for_the_password()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);

        // The page's fetch: 401, so the front end can react (it opens the sign-in page).
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var signIn = await browser.OpenAsync("/auth/login?returnUrl=/");

        WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue("Kale asks for the password instead of signing the user straight back in");
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var back = await browser.LogInAsync(signIn, userName);
        back.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
        (await OpenSessionIdAsync(browser)).ShouldNotBe(sessionId);

        // The completed sign-in forgets "ask for the password": signed out here only (the cookie dropped, as after a
        // restart), the next sign-in uses Kale's own cookie again.
        browser.Cookies.GetCookies(SampleWebHost.Address).Select(c => c.Name).ShouldNotContain("kale.sample.ask-password");
        browser.Cookies.GetCookies(SampleWebHost.Address)["kale.sample"]!.Expired = true;
        using var later = await browser.OpenAsync("/auth/login?returnUrl=/");
        WebBrowser.IsKaleLoginPage(later).ShouldBeFalse("no password again after a completed sign-in");
        later.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
    }

    [Fact]
    public async Task An_ended_session_ends_only_this_applications_sign_in_and_the_user_stays_signed_in_to_Kale()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        await GiveAccessToAnotherApplicationAsync(userName, "other");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using (var signIn = await browser.OpenAsync("/auth/login?returnUrl=/"))
        {
            WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue("this application asks for the password");
        }

        browser.HasKaleSignInCookie().ShouldBeTrue("the user's Kale sign-in stays");

        // Leaving Kale's page and coming back still asks for the password: this application's sign-in is gone, but
        // Kale's own cookie must not sign the user back in here (that would end their session on the other device).
        using (var again = await browser.OpenAsync("/auth/login?returnUrl=/"))
        {
            WebBrowser.IsKaleLoginPage(again).ShouldBeTrue("still asks for the password");
        }

        // Another application signs the user in through Kale without a password: Kale sends the browser straight back
        // to that application with a code (an error, e.g. login_required, would come back there too).
        using var kale = new Browser(_auth);
        var sentTo = await browser.FollowOnKaleAsync(new Uri(WebBrowser.KaleAddress, kale.AuthorizeUri("other")));
        sentTo.Host.ShouldBe(Browser.RedirectUri("other").Host);
        sentTo.Query.ShouldContain("code=");
        sentTo.Query.ShouldNotContain("error=");
    }

    [Fact]
    public async Task Signing_out_after_the_session_ended_still_signs_out_at_Kale()
    {
        // A shared computer: the user whose session ended clicks "Sign out"; Kale's own sign-in must end too.
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);
        using (var signIn = await browser.OpenAsync("/auth/login?returnUrl=/"))
        {
            WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue();
        }

        using (await browser.SignOutAsync())
        {
        }

        browser.HasKaleSignInCookie().ShouldBeFalse("signed out at Kale too");
        using var next = await browser.OpenAsync("/auth/login?returnUrl=/");
        WebBrowser.IsKaleLoginPage(next).ShouldBeTrue("Kale asks for the password next time");
    }

    [Fact]
    public async Task When_the_API_refuses_an_ended_session_before_any_renewal_the_sign_in_page_asks_for_the_password()
    {
        // SampleApp (another project) finds the session ended at once, through its event; SampleWeb's cookie and its
        // token still look fine. The front end does what index.html does: 401 → /auth/login.
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        for (var attempt = 0; ; attempt++)
        {
            using var fetched = await browser.FetchAsync(HttpMethod.Get, "/api/notes");
            if (fetched.StatusCode == HttpStatusCode.Unauthorized)
            {
                break;
            }

            attempt.ShouldBeLessThan(100, "SampleApp kept accepting the stopped session");
            await Task.Delay(100);
        }

        using var signIn = await browser.OpenAsync("/auth/login?returnUrl=/");

        WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue("Kale asks for the password instead of sending the browser back to the 401");
        using var back = await browser.LogInAsync(signIn, userName);
        back.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Opening_a_protected_page_after_the_session_ended_asks_for_the_password_and_keeps_the_Kale_sign_in()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);
        using var opened = await browser.OpenAsync("/api/notes");

        WebBrowser.IsKaleLoginPage(opened).ShouldBeTrue();
        browser.HasKaleSignInCookie().ShouldBeTrue();
    }

    [Fact]
    public async Task After_the_session_ended_an_address_that_is_another_host_to_a_browser_is_not_where_the_browser_comes_back_to()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);
        // The path is "//evil.example/x", which a browser reads as https://evil.example/x.
        using var signIn = await browser.OpenAsync(new Uri(SampleWebHost.Address, "/").AbsoluteUri + "/evil.example/x");

        WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue();
        using var landed = await browser.LogInAsync(signIn, userName);
        landed.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
    }

    // Registers another application ("a second client") and gives the user access to it, with a role.
    private async Task GiveAccessToAnotherApplicationAsync(string userName, string key)
    {
        await using var scope = _auth.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication(key, key, [Browser.RedirectUri(key)], []));
        var db = scope.ServiceProvider.GetRequiredService<AuthService.Persistence.AuthDbContext>();
        var userId = await KaleUserIdAsync(userName);
        db.UserApplications.Add(new UserApplication { UserId = userId, ApplicationId = created.Application.Id });
        var role = await db.AddRole(created.Application, "Member");
        db.UserRoles.Add(new UserRole { UserId = userId, RoleId = role.Id });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Signing_out_ends_the_session_here_and_at_Kale()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = await OpenSessionIdAsync(browser);

        using var landed = await browser.SignOutAsync();

        landed.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
        (await KaleSessionAsync(sessionId)).EndedAt.ShouldNotBeNull();
        browser.HasKaleSignInCookie().ShouldBeFalse();
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        WebBrowser.IsKaleLoginPage(await browser.OpenAsync("/auth/login?returnUrl=/")).ShouldBeTrue();
    }

    [Fact]
    public async Task Signing_out_ends_the_Kale_session_from_the_server_even_if_the_browser_never_reaches_Kale()
    {
        // E.g. the tab is closed during the redirect, or Kale's own cookie ran out first (its sign-out page then has
        // no one to sign out): the application ends its session itself.
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = await OpenSessionIdAsync(browser);

        using var answer = await browser.SubmitSignOutAsync();

        answer.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        answer.Headers.Location!.AbsoluteUri.ShouldStartWith(new Uri(WebBrowser.KaleAddress, "connect/endsession").AbsoluteUri, Case.Insensitive,
            "the browser still goes to Kale, to end Kale's own sign-in cookie");
        (await KaleSessionAsync(sessionId)).EndedAt.ShouldNotBeNull();
        browser.HasKaleSignInCookie().ShouldBeTrue("the browser didn't get to Kale");
    }

    [Fact]
    public async Task When_Kale_cannot_be_reached_the_sign_out_still_works_and_says_so_in_the_log()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        _kale.Unreachable = true;

        using var answer = await browser.SubmitSignOutAsync();

        answer.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        answer.Headers.Location!.AbsoluteUri.ShouldStartWith(new Uri(WebBrowser.KaleAddress, "connect/endsession").AbsoluteUri, Case.Insensitive);
        _web.Logs.Lines.ShouldContain(line => line.StartsWith("Warning: Couldn't end the user's Kale session from the server", StringComparison.Ordinal));
        _kale.Unreachable = false;
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized, "signed out here");
    }

    [Fact]
    public async Task A_Kale_that_never_answers_holds_the_sign_out_for_at_most_a_few_seconds()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        _kale.Hangs = true;
        var started = System.Diagnostics.Stopwatch.StartNew();

        using var answer = await browser.SubmitSignOutAsync();

        // The token request, the discovery lookup and the gRPC call share one 5-second deadline.
        started.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        answer.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        _kale.Hangs = false;
    }

    [Fact]
    public async Task Signing_out_after_the_session_ended_does_not_fail()
    {
        // Kale answers "ended already" as a success; the sign-out goes on as usual.
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = await OpenSessionIdAsync(browser);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        using var landed = await browser.SignOutAsync();

        landed.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
    }

    [Fact]
    public async Task A_sign_out_posted_from_another_site_is_refused()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));

        using var response = await browser.SignOutAsync(origin: "https://evil.example");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_sign_in_ends_8_hours_after_it_started_however_often_the_token_was_renewed()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        _web.Clock.Advance(PastRenewalTime);
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Kale's own clock stays put, so only this application's cookie can end the sign-in here.
        _web.Clock.Advance(TimeSpan.FromHours(8) - PastRenewalTime + TimeSpan.FromMinutes(1));

        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_sign_in_Kale_refuses_comes_back_to_the_page_with_the_error()
    {
        using var browser = _web.CreateBrowser();
        using var landed = await browser.SignInAsync(await CreateUserWithoutAccessAsync(), returnUrl: "/index.html");

        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(SampleWebHost.Address, "/index.html?kaleError=access_denied"));
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Opening_a_protected_page_without_sign_in_comes_back_to_that_page_after_sign_in()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();

        using var loginPage = await browser.OpenAsync("/api/notes?page=2");
        using var landed = await browser.LogInAsync(loginPage, userName);

        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(SampleWebHost.Address, "/api/notes?page=2"));
        landed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_protected_page_Kale_refuses_to_sign_in_to_answers_401_instead_of_asking_Kale_again()
    {
        using var browser = _web.CreateBrowser();

        using var loginPage = await browser.OpenAsync("/api/notes");
        // Kale's own cookie now signs the user in without a password, and refuses again at once: without the
        // ?kaleError check the browser would go back and forth between SampleWeb and Kale.
        using var landed = await browser.LogInAsync(loginPage, await CreateUserWithoutAccessAsync());

        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(SampleWebHost.Address, "/api/notes?kaleError=access_denied"));
        landed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private const string TokenPath = "/connect/token";

    // A user without access to the application: Kale refuses their sign-in (access_denied).
    private async Task<string> CreateUserWithoutAccessAsync()
    {
        var userName = $"user-{Guid.CreateVersion7():N}";
        await using var scope = _auth.Services.CreateAsyncScope();
        var user = new User { UserName = userName, DisplayName = userName };
        (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthServiceSteps.Password)).Succeeded.ShouldBeTrue();
        return userName;
    }

    private static async Task<JsonElement> MeAsync(WebBrowser browser)
    {
        using var response = await browser.FetchAsync(HttpMethod.Get, "/auth/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<Session> KaleSessionAsync(Guid sessionId)
    {
        await using var scope = _auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthService.Persistence.AuthDbContext>();
        return await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == sessionId);
    }

    // The signed-in user's open Kale session (one per user and application), found by the "sub" /auth/me answers.
    private async Task<Guid> OpenSessionIdAsync(WebBrowser browser)
    {
        var userId = Guid.Parse((await MeAsync(browser)).GetProperty("sub").GetString()!);
        await using var scope = _auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthService.Persistence.AuthDbContext>();
        return await db.Sessions.Where(s => s.UserId == userId && s.EndedAt == null).Select(s => s.Id).SingleAsync();
    }

    private async Task<Guid> KaleUserIdAsync(string userName)
    {
        await using var scope = _auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthService.Persistence.AuthDbContext>();
        return await db.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
    }
}
