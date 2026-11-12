using Auth.Web;
using AuthService.Tests.Hosting;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using SampleWeb;

namespace EndToEnd.Tests;

/// <summary>
/// The real SampleWeb (its Program.cs and appsettings.json) running in memory: it signs users in through the
/// in-memory AuthService and calls the in-memory SampleApp with their tokens. Its client secret comes from a file,
/// as a mounted Podman secret reaches it in a container. It has its own clock, so tests can make its tokens look
/// old without changing what Kale or SampleApp think the time is.
/// </summary>
// The type argument only points at SampleWeb's assembly (Program would be ambiguous with AuthService's).
internal sealed class SampleWebHost : WebApplicationFactory<NotesApi>
{
    public static readonly Uri Address = new("https://web.sample.test/");

    private readonly AuthServiceApp _authService;
    private readonly SampleAppHost _sampleApp;
    private readonly HttpMessageHandler _backchannel;
    private readonly string _secretFile = Path.GetTempFileName();

    /// <param name="backchannel">How SampleWeb reaches Kale (the in-memory AuthService, maybe wrapped to count or fail calls).</param>
    public SampleWebHost(AuthServiceApp authService, string clientSecret, SampleAppHost sampleApp, HttpMessageHandler backchannel)
    {
        _authService = authService;
        _sampleApp = sampleApp;
        _backchannel = backchannel;
        File.WriteAllText(_secretFile, clientSecret + "\n");
    }

    public FakeTimeProvider Clock { get; } = new(DateTimeOffset.UtcNow);

    public static Uri SignInReturnAddress => new(Address, "signin-oidc");

    public static Uri SignOutReturnAddress => new(Address, "signout-callback-oidc");

    public WebBrowser CreateBrowser() => new(_authService, Server.CreateHandler(), Address);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        // Kale's own address (its issuer): its discovery document builds the browser's addresses from the request's
        // host, and the in-memory server answers on any host.
        builder.UseSetting("Kale:Authority", AuthServiceApp.Issuer);
        builder.UseSetting("Kale:ClientSecretFile", _secretFile);
        builder.UseSetting("Kale:AllowInsecureHttpDevOnly", "true");
        builder.UseSetting("SampleApi", "http://sampleapp.test/");

        builder.ConfigureTestServices(services =>
        {
            // Only the ways out change: to the in-memory AuthService and SampleApp instead of the network.
            services.Configure<OpenIdConnectOptions>(KaleSignInSetup.OpenIdConnectScheme, oidc => oidc.BackchannelHttpHandler = _backchannel);
            services.AddHttpClient<NotesApi>().ConfigurePrimaryHttpMessageHandler(() => _sampleApp.Server.CreateHandler());
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        File.Delete(_secretFile);
    }
}
