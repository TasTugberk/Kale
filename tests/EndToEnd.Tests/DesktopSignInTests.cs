using System.Net;
using Auth.Desktop;
using AuthService.Applications;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace EndToEnd.Tests;

/// <summary>
/// Kale.Auth.Desktop against the real AuthService and SampleApp: a desktop app signs its user in through the
/// browser (here the test browser, which logs in on Kale's real pages and comes back to the package's real
/// loopback listener) and calls the application's own API with the user's token.
/// </summary>
[Collection(UsesContainers.Name)]
public sealed class DesktopSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _auth = null!;
    private CreatedApplication _sample = null!;
    private SampleAppHost _app = null!;
    private TestBrowser _browser = null!;
    private readonly FakeTimeProvider _desktopClock = new(DateTimeOffset.UtcNow);

    public async Task InitializeAsync()
    {
        _auth = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _auth.Services.CreateAsyncScope())
        {
            var applications = scope.ServiceProvider.GetRequiredService<ApplicationService>();
            _sample = await applications.CreateAsync(new NewApplication("sample", "Sample", [Browser.RedirectUri("sample")], []));
            await applications.SetDesktopSignInAsync(_sample.Application.Id, enabled: true);
        }

        _app = new SampleAppHost(_auth, _sample.ClientSecret);
        _app.StartServer();
        await AuthServiceSteps.WaitUntilAsync(_auth, async db => await db.Operations.CountAsync() == 2);
        _browser = new TestBrowser(_auth, await AuthServiceSteps.CreateUserAsync(_auth, _sample, "sample.NoteRead"));
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
        await _auth.DisposeAsync();
    }

    [Fact]
    public async Task A_desktop_app_signs_in_through_the_browser_and_calls_its_API_with_the_users_token()
    {
        using var signIn = new KaleDesktopSignIn(Options());

        await signIn.SignInAsync();
        using var api = new HttpClient(new KaleBearerHandler(signIn) { InnerHandler = _app.Server.CreateHandler() })
        {
            BaseAddress = _app.Server.BaseAddress,
        };
        using var response = await api.GetAsync(new Uri("/notes", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        new JsonWebToken(await signIn.GetAccessTokenAsync()).Audiences.ShouldBe(["sample"]);
        signIn.User!.UserName.ShouldBe(_browser.UserName);
        var (status, page) = await _browser.Comeback!;
        status.ShouldBe(HttpStatusCode.OK);
        page.ShouldContain("<h1");
    }

    [Fact]
    public async Task The_access_token_is_renewed_at_Kale_without_the_browser()
    {
        using var signIn = new KaleDesktopSignIn(Options());
        await signIn.SignInAsync();
        var first = await signIn.GetAccessTokenAsync();

        _desktopClock.Advance(TimeSpan.FromMinutes(10));
        var renewed = await signIn.GetAccessTokenAsync();

        renewed.ShouldNotBe(first);
        new JsonWebToken(renewed).GetClaim("sid").Value.ShouldBe(new JsonWebToken(first).GetClaim("sid").Value, "the same session");
        _browser.Opened.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Signing_out_ends_the_session_at_Kale()
    {
        using var signIn = new KaleDesktopSignIn(Options());
        await signIn.SignInAsync();
        var sessionId = Guid.Parse(new JsonWebToken(await signIn.GetAccessTokenAsync()).GetClaim("sid").Value);

        await signIn.SignOutAsync();
        (await _browser.SignedOut!).ShouldBe(HttpStatusCode.Redirect);

        await AuthServiceSteps.WithAuthDbAsync(_auth, async db =>
            (await db.Sessions.SingleAsync(s => s.Id == sessionId)).EndedAt.ShouldNotBeNull());
        await Should.ThrowAsync<KaleSignInRequiredException>(() => signIn.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_user_without_access_is_refused()
    {
        await AuthServiceSteps.WithAuthDbAsync(_auth, async db => (await db.UserApplications.SingleAsync()).IsActive = false);
        using var signIn = new KaleDesktopSignIn(Options());

        var error = await Should.ThrowAsync<KaleSignInException>(() => signIn.SignInAsync());

        error.Message.ShouldContain("access_denied");
    }

    private KaleDesktopOptions Options() => new()
    {
        Authority = _auth.Server.BaseAddress,
        ApplicationKey = "sample",
        AllowInsecureHttpDevOnly = true,
        Browser = _browser,
        BackchannelHandler = _auth.Server.CreateHandler(),
        Clock = _desktopClock,
        SignInTimeout = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// The system browser: logs the user in on Kale's real pages, then follows Kale's redirect to the desktop app's
    /// loopback address over the real network.
    /// </summary>
    private sealed class TestBrowser(AuthServiceApp auth, string userName) : IBrowser, IDisposable
    {
        private readonly Browser _browser = new(auth);

        public string UserName => userName;

        public List<Uri> Opened { get; } = [];

        public Task<(HttpStatusCode Status, string Page)>? Comeback { get; private set; }

        public Task<HttpStatusCode>? SignedOut { get; private set; }

        public Task OpenAsync(Uri address, CancellationToken cancellationToken)
        {
            Opened.Add(address);
            if (address.AbsolutePath == "/connect/endsession")
            {
                SignedOut = Task.Run(async () =>
                {
                    using var response = await _browser.GetAsync(address);
                    return response.StatusCode;
                }, cancellationToken);
            }
            else
            {
                Comeback = Task.Run(() => SignInAsync(address), cancellationToken);
            }

            // Like the system browser: returns at once.
            return Task.CompletedTask;
        }

        public void Dispose() => _browser.Dispose();

        private async Task<(HttpStatusCode Status, string Page)> SignInAsync(Uri authorize)
        {
            using var page = await _browser.FollowAsync(await _browser.GetAsync(authorize));
            using var redirect = page.RequestMessage!.RequestUri!.AbsolutePath == "/account/login"
                ? await _browser.FollowAsync(await _browser.LogInAsync(page, userName, AuthServiceSteps.Password))
                : null;
            var toApp = (redirect ?? page).Headers.Location!;

            using var http = new HttpClient();
            using var answer = await http.GetAsync(toApp);
            return (answer.StatusCode, await answer.Content.ReadAsStringAsync());
        }
    }
}
