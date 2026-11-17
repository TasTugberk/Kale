using System.Net;
using System.Net.Http.Headers;
using Auth.Client;
using Auth.Web;
using AuthService.Applications;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
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

    private async Task<WebBrowser> SignedInBrowser(string operation)
    {
        var browser = new WebBrowser(_auth, _app.GetTestServer().CreateHandler(), Address);
        await browser.SignInAsync(await AuthServiceSteps.CreateUserAsync(_auth, _billing, operation));
        return browser;
    }
}
