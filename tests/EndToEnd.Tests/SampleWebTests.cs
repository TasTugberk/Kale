using System.Net;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Outbox;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace EndToEnd.Tests;

/// <summary>
/// samples/SampleWeb, a web application's server using Auth.Web, against the real AuthService and SampleApp: the
/// browser signs in at Kale, the page calls SampleApp through SampleWeb with the user's token, the token is
/// renewed, and an ended Kale session signs the user out at Kale too.
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
        var session = await KaleSessionAsync(me.GetProperty("sid").GetString()!);
        me.GetProperty("sub").GetString().ShouldBe(session.UserId.ToString());

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
    public async Task A_stopped_session_signs_the_user_out_here_and_at_Kale_so_the_next_sign_in_asks_for_the_password()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead");
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(userName);
        var sessionId = Guid.Parse((await MeAsync(browser)).GetProperty("sid").GetString()!);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);

        // The page's fetch: 401, so the front end can react (it opens the sign-in page).
        (await browser.FetchAsync(HttpMethod.Get, "/api/notes")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        browser.HasKaleSignInCookie().ShouldBeTrue("a fetch can't take the browser to Kale");
        using var signIn = await browser.OpenAsync("/auth/login?returnUrl=/");

        WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue("Kale asks for the password instead of signing the user straight back in");
        browser.HasKaleSignInCookie().ShouldBeFalse();
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        using var back = await browser.LogInAsync(signIn, userName);
        back.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
        (await MeAsync(browser)).GetProperty("sid").GetString().ShouldNotBe(sessionId.ToString());
    }

    [Fact]
    public async Task Opening_a_protected_page_after_the_session_ended_also_signs_out_at_Kale()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = Guid.Parse((await MeAsync(browser)).GetProperty("sid").GetString()!);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        _web.Clock.Advance(PastRenewalTime);
        using var opened = await browser.OpenAsync("/api/notes");

        WebBrowser.IsKaleLoginPage(opened).ShouldBeTrue();
        browser.HasKaleSignInCookie().ShouldBeFalse();
    }

    [Fact]
    public async Task Signing_out_ends_the_session_here_and_at_Kale()
    {
        using var browser = _web.CreateBrowser();
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
        var sessionId = (await MeAsync(browser)).GetProperty("sid").GetString()!;

        using var landed = await browser.SignOutAsync();

        landed.RequestMessage!.RequestUri.ShouldBe(SampleWebHost.Address);
        (await KaleSessionAsync(sessionId)).EndedAt.ShouldNotBeNull();
        browser.HasKaleSignInCookie().ShouldBeFalse();
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        WebBrowser.IsKaleLoginPage(await browser.OpenAsync("/auth/login?returnUrl=/")).ShouldBeTrue();
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
        // A user without access to the application: Kale refuses the sign-in (access_denied).
        var userName = $"user-{Guid.CreateVersion7():N}";
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            var user = new User { UserName = userName, DisplayName = userName };
            (await scope.ServiceProvider.GetRequiredService<UserManager<User>>().CreateAsync(user, AuthServiceSteps.Password)).Succeeded.ShouldBeTrue();
        }

        using var browser = _web.CreateBrowser();
        using var landed = await browser.SignInAsync(userName, returnUrl: "/index.html");

        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(SampleWebHost.Address, "/index.html?kaleError=access_denied"));
        (await browser.FetchAsync(HttpMethod.Get, "/auth/me")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private const string TokenPath = "/connect/token";

    private static async Task<JsonElement> MeAsync(WebBrowser browser)
    {
        using var response = await browser.FetchAsync(HttpMethod.Get, "/auth/me");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private async Task<Session> KaleSessionAsync(string sessionId)
    {
        await using var scope = _auth.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthService.Persistence.AuthDbContext>();
        return await db.Sessions.AsNoTracking().SingleAsync(s => s.Id == Guid.Parse(sessionId));
    }
}
