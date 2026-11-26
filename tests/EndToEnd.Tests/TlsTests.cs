using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Tests;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EndToEnd.Tests;

/// <summary>
/// The production path, over real TLS: AuthService on Kestrel with an https endpoint and a certificate from files
/// (as in production), an https issuer, and plain http refused (no AllowInsecureHttpDevOnly, no
/// Grpc:AllowInsecureDevOnly). The certificate is self-signed, standing for an internal CA: clients trust it
/// explicitly, never by switching certificate checks off.
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class TlsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly TestCertificate _certificate = TestCertificate.CreateForLocalhost();
    private AuthServiceApp _auth = null!;
    private Uri _address = null!;
    private CreatedApplication _billing = null!;

    public async Task InitializeAsync()
    {
        _address = new Uri($"https://localhost:{FreePort()}/");
        _auth = await AuthServiceApp.CreateAsync(postgres,
            ("Auth:Issuer", _address.AbsoluteUri),
            ("OpenIddict:AllowInsecureHttpDevOnly", "false"),
            ("Kestrel:Endpoints:Https:Url", _address.AbsoluteUri),
            ("Kestrel:Endpoints:Https:Certificate:Path", _certificate.CertificatePath),
            ("Kestrel:Endpoints:Https:Certificate:KeyPath", _certificate.PrivateKeyPath));
        _auth.UseKestrel();
        _auth.StartServer();

        await using var scope = _auth.Services.CreateAsyncScope();
        _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
            .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
    }

    public async Task DisposeAsync()
    {
        await _auth.DisposeAsync();
        _certificate.Dispose();
    }

    [Fact]
    public async Task Discovery_and_the_signing_keys_are_served_over_https()
    {
        using var http = new HttpClient(_certificate.TrustingHandler()) { BaseAddress = _address };

        using var discovery = JsonDocument.Parse(await http.GetStringAsync(new Uri(".well-known/openid-configuration", UriKind.Relative)));
        var jwksUri = discovery.RootElement.GetProperty("jwks_uri").GetString()!;
        using var jwks = JsonDocument.Parse(await http.GetStringAsync(new Uri(jwksUri)));

        discovery.RootElement.GetProperty("issuer").GetString().ShouldBe(_address.AbsoluteUri);
        jwksUri.ShouldStartWith(_address.AbsoluteUri);
        jwks.RootElement.GetProperty("keys").GetArrayLength().ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task A_client_that_doesnt_trust_the_certificate_is_refused()
    {
        using var http = new HttpClient { BaseAddress = _address };

        await Should.ThrowAsync<HttpRequestException>(() => http.GetAsync(new Uri(".well-known/openid-configuration", UriKind.Relative)));
    }

    [Fact]
    public async Task The_sign_in_cookie_is_secure()
    {
        var userName = await AuthServiceSteps.CreateUserAsync(_auth, _billing, operation: null);
        using var browser = NewBrowser();
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri("billing")));

        using var signedIn = await browser.LogInAsync(loginPage, userName, AuthServiceSteps.Password);

        var signInCookie = signedIn.Headers.GetValues("Set-Cookie").Single(cookie => cookie.StartsWith("kale.signin=", StringComparison.Ordinal));
        signInCookie.ShouldContain("secure", Case.Insensitive);
        signInCookie.ShouldContain("httponly", Case.Insensitive);
    }

    [Fact]
    public async Task A_client_app_registers_validates_tokens_and_checks_sessions_over_tls()
    {
        // Auth.Client over TLS: its client-credentials token and the signing keys (https), registration and the
        // session check (gRPC, HTTP/2 negotiated by TLS).
        await using var client = await ClientApp.StartAsync(_auth, _billing.ClientSecret,
            overTls: (_address, _certificate.TrustingHandler()));
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => (await db.Operations.CountAsync()) == 2);
        using var browser = NewBrowser();
        var tokens = await AuthServiceSteps.SignInAsync(_auth, _billing, "billing.InvoiceRead", browser);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/invoices");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.GetProperty("access_token").GetString());
        using var response = await client.Http.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private Browser NewBrowser() => new(new HttpClient(_certificate.TrustingHandler()) { BaseAddress = _address });

    // A port nobody listens on now: the issuer has to name it before AuthService starts.
    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
