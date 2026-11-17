using System.Net;
using System.Net.Http.Headers;
using Auth.Client;
using Auth.Web;
using AuthService.Applications;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndToEnd.Tests;

/// <summary>
/// One project that is both the API (Auth.Client) and the sign-in host (Auth.Web): [Authorize(Operation)] works
/// for the browser's cookie as for a bearer token, with the same session and operation checks.
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class SignInHostApiTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly Uri Address = new("https://billing.test/");

    private AuthServiceApp _auth = null!;
    private CreatedApplication _billing = null!;
    private WebApplication _app = null!;

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            // The second return address is the one AuthServiceSteps' token-only sign-in uses.
            _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>().CreateAsync(new NewApplication(
                "billing", "Billing", [new Uri(Address, "signin-oidc"), Browser.RedirectUri("billing")], [new Uri(Address, "signout-callback-oidc")]));
        }

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        // Kale's issuer: the browser is sent to the addresses in its discovery document (see SampleWebHost).
        builder.Configuration["Kale:Authority"] = AuthServiceApp.Issuer;
        builder.Configuration["Kale:ApplicationKey"] = "billing";
        builder.Configuration["Kale:ClientSecret"] = _billing.ClientSecret;
        builder.Configuration["Kale:AllowInsecureHttpDevOnly"] = "true";

        // The order doesn't matter: AddKaleSignIn picks the default scheme after every registration.
        builder.AddKaleSignIn();
        builder.AddKaleAuth<BillingOperations>(options => options.BackchannelHandler = _auth.Server.CreateHandler());
        builder.Services.Configure<OpenIdConnectOptions>(KaleSignInSetup.OpenIdConnectScheme, oidc => oidc.BackchannelHttpHandler = _auth.Server.CreateHandler());

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapKaleSignIn();
        _app.MapGet("/invoices", [Auth.Client.Authorize(BillingOperations.InvoiceRead)] () => "invoices");
        _app.MapPost("/invoices", [Auth.Client.Authorize(BillingOperations.InvoiceManage)] () => "created");
        _app.MapGet("/own-challenge", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/invoices" }));
        await _app.StartAsync();
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _auth.DisposeAsync();
    }

    [Fact]
    public async Task The_browsers_cookie_is_checked_against_the_sessions_operations()
    {
        using var browser = await SignedInBrowser("billing.InvoiceRead");

        using var read = await browser.FetchAsync(HttpMethod.Get, "/invoices");
        using var manage = await browser.FetchAsync(HttpMethod.Post, "/invoices");

        read.StatusCode.ShouldBe(HttpStatusCode.OK);
        manage.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the role lacks InvoiceManage");
    }

    [Fact]
    public async Task A_bearer_token_still_works_and_a_request_with_neither_gets_401()
    {
        var tokens = await AuthServiceSteps.SignInAsync(_auth, _billing, "billing.InvoiceRead");
        using var http = _app.GetTestClient();
        using var withToken = new HttpRequestMessage(HttpMethod.Get, "/invoices");
        withToken.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());

        (await http.SendAsync(withToken)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await http.GetAsync(new Uri("/invoices", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_change_sent_with_the_cookie_from_another_origin_counts_as_not_signed_in()
    {
        using var browser = await SignedInBrowser("billing.InvoiceManage");

        using var fromElsewhere = await browser.FetchAsync(HttpMethod.Post, "/invoices", origin: "https://evil.example");
        using var fromHere = await browser.FetchAsync(HttpMethod.Post, "/invoices");

        fromElsewhere.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        fromHere.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task After_the_session_ended_a_fetch_gets_401_and_the_sign_in_page_asks_for_the_password()
    {
        // The guide's path: the front end opens /auth/login after a 401, to come back to its own page ("/", not an
        // [Authorize(Operation)] endpoint that would notice the ended session itself).
        var (browser, userName) = await BrowserWhoseSessionEndedAsync();
        using (browser)
        {
            using var signIn = await browser.OpenAsync("/auth/login?returnUrl=/");

            // Signing the user straight back in with Kale's own cookie would end their session on another device.
            WebBrowser.IsKaleLoginPage(signIn).ShouldBeTrue("Kale asks for the password");
            browser.HasKaleSignInCookie().ShouldBeTrue("only this application's sign-in ends; the user's Kale sign-in stays");
            using var back = await browser.LogInAsync(signIn, userName);
            back.RequestMessage!.RequestUri.ShouldBe(Address);
            (await browser.FetchAsync(HttpMethod.Get, "/invoices")).StatusCode.ShouldBe(HttpStatusCode.OK);
        }
    }

    [Fact]
    public async Task After_the_session_ended_opening_a_protected_page_asks_for_the_password()
    {
        var (browser, _) = await BrowserWhoseSessionEndedAsync();
        using (browser)
        {
            using var opened = await browser.OpenAsync("/invoices");

            WebBrowser.IsKaleLoginPage(opened).ShouldBeTrue("Kale asks for the password");
            browser.HasKaleSignInCookie().ShouldBeTrue("only this application's sign-in ends; the user's Kale sign-in stays");
        }
    }

    [Fact]
    public async Task A_challenge_the_application_makes_itself_does_not_sign_out_at_Kale()
    {
        // Only Kale.Auth.Client's challenge for an ended session (it says invalid_token) signs out at Kale; Kale's
        // own sign-in cookie keeps signing the user in to their other applications.
        using var browser = await SignedInBrowser("billing.InvoiceRead");

        using var landed = await browser.OpenAsync("/own-challenge");

        browser.HasKaleSignInCookie().ShouldBeTrue();
        landed.RequestMessage!.RequestUri.ShouldBe(new Uri(Address, "/invoices"));
        landed.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    // Signs a user in, stops their session at Kale, and waits until the API check refuses it (the event arrives).
    // The cookie's token is still fresh: no renewal has asked Kale yet.
    private async Task<(WebBrowser Browser, string UserName)> BrowserWhoseSessionEndedAsync()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _billing, "billing.InvoiceRead");
        var browser = new WebBrowser(_auth, _app.GetTestServer().CreateHandler(), Address);
        await browser.SignInAsync(userName);
        (await browser.FetchAsync(HttpMethod.Get, "/invoices")).StatusCode.ShouldBe(HttpStatusCode.OK);

        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
            var userId = await db.Users.Where(u => u.UserName == userName).Select(u => u.Id).SingleAsync();
            var sessionId = await db.Sessions.Where(s => s.UserId == userId && s.EndedAt == null).Select(s => s.Id).SingleAsync();
            await scope.ServiceProvider.GetRequiredService<SessionStopper>().StopAsync(sessionId);
        }

        await EventuallyAsync(async () =>
        {
            using var fetched = await browser.FetchAsync(HttpMethod.Get, "/invoices");
            return fetched.StatusCode == HttpStatusCode.Unauthorized;
        });
        return (browser, userName);
    }

    private static async Task EventuallyAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (!await condition())
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException("The ended session was never refused.");
            }

            await Task.Delay(100);
        }
    }

    private async Task<WebBrowser> SignedInBrowser(string operation)
    {
        var browser = new WebBrowser(_auth, _app.GetTestServer().CreateHandler(), Address);
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _billing, operation));
        return browser;
    }
}
