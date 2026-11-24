using System.Net;
using Auth.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Auth.Web.Tests;

public sealed class KaleSignInSetupTests
{
    [Fact]
    public void Settings_come_from_the_Kale_section_and_the_secret_from_its_file()
    {
        var secretFile = Path.GetTempFileName();
        try
        {
            // With the newline `podman secret create` leaves in a mounted secret.
            File.WriteAllText(secretFile, "the-secret\n");
            var builder = Builder("Production", ("Kale:Authority", "https://auth.example.com/"), ("Kale:ApplicationKey", "orders"),
                ("Kale:ClientSecretFile", secretFile));

            builder.AddKaleSignIn();

            using var app = builder.Build();
            var oidc = app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(KaleSignInSetup.OpenIdConnectScheme);
            oidc.Authority.ShouldBe("https://auth.example.com/");
            oidc.ClientId.ShouldBe("orders");
            oidc.ClientSecret.ShouldBe("the-secret");
            oidc.Scope.ShouldBe(["openid", "offline_access"], ignoreOrder: true);
            oidc.UsePkce.ShouldBeTrue();
            oidc.MapInboundClaims.ShouldBeFalse();
            oidc.SaveTokens.ShouldBeTrue();
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public void The_cookie_lasts_as_long_as_a_Kale_session_is_httpOnly_and_Secure_outside_development()
    {
        var builder = Builder("Production", ("Kale:Authority", "https://auth.example.com/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"));

        builder.AddKaleSignIn();

        using var app = builder.Build();
        var cookie = app.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(KaleSignInSetup.CookieScheme);
        cookie.ExpireTimeSpan.ShouldBe(TimeSpan.FromHours(8));
        cookie.SlidingExpiration.ShouldBeFalse();
        cookie.Cookie.HttpOnly.ShouldBeTrue();
        cookie.Cookie.SecurePolicy.ShouldBe(CookieSecurePolicy.Always);
    }

    [Fact]
    public async Task Without_Kale_Auth_the_cookie_is_the_default_scheme()
    {
        var builder = Builder("Production", ("Kale:Authority", "https://auth.example.com/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"));

        builder.AddKaleSignIn();

        using var app = builder.Build();
        var scheme = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        scheme!.Name.ShouldBe(KaleSignInSetup.CookieScheme);
    }

    [Fact]
    public async Task A_default_scheme_the_application_chose_itself_stays()
    {
        var builder = Builder("Production", ("Kale:Authority", "https://auth.example.com/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"));
        builder.Services.AddAuthentication("Own").AddCookie("Own");

        builder.AddKaleSignIn();

        using var app = builder.Build();
        var scheme = await app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync();
        scheme!.Name.ShouldBe("Own");
    }

    [Fact]
    public void Missing_settings_stop_startup_with_what_to_set()
    {
        var builder = Builder("Production", ("Kale:Authority", "https://auth.example.com/"));

        var error = Should.Throw<InvalidOperationException>(() => builder.AddKaleSignIn());

        error.Message.ShouldContain("Kale:ApplicationKey");
    }

    [Fact]
    public void Plain_http_needs_AllowInsecureHttpDevOnly()
    {
        var builder = Builder("Development", ("Kale:Authority", "http://auth.kale.localhost:8080/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"));

        Should.Throw<InvalidOperationException>(() => builder.AddKaleSignIn()).Message.ShouldContain("https");
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void AllowInsecureHttpDevOnly_stops_startup_outside_Development_and_Testing(string environment)
    {
        var builder = Builder(environment, ("Kale:Authority", "http://auth.kale.localhost:8080/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"), ("Kale:AllowInsecureHttpDevOnly", "true"));

        Should.Throw<InvalidOperationException>(() => builder.AddKaleSignIn()).Message.ShouldContain("Development and Testing");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public void AllowInsecureHttpDevOnly_works_in_Development_and_Testing(string environment)
    {
        var builder = Builder(environment, ("Kale:Authority", "http://auth.kale.localhost:8080/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"), ("Kale:AllowInsecureHttpDevOnly", "true"));

        builder.AddKaleSignIn();

        using var app = builder.Build();
        app.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(KaleSignInSetup.OpenIdConnectScheme)
            .RequireHttpsMetadata.ShouldBeFalse();
    }

    [Fact]
    public async Task The_sign_in_endpoints_stay_open_under_the_applications_fallback_policy()
    {
        // An application that requires sign-in everywhere by default. Signing out from a tab whose sign-in has already
        // expired must still sign out and go back, not be challenged (a fetch would get 401, a form Kale's sign-in).
        var builder = Builder("Testing", ("Kale:Authority", "https://auth.example.com/"), ("Kale:ApplicationKey", "orders"),
            ("Kale:ClientSecret", "secret"));
        builder.WebHost.UseTestServer();
        builder.AddKaleSignIn();
        builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        await using var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        // An endpoint the application adds to the returned group keeps its own rule.
        app.MapKaleSignIn().MapGet("/extra", () => "extra").RequireAuthorization();
        await app.StartAsync();
        using var http = app.GetTestClient();

        using var logout = new HttpRequestMessage(HttpMethod.Post, new Uri("/auth/logout?returnUrl=/bye", UriKind.Relative));
        logout.Headers.Add("Origin", "http://localhost");
        logout.Headers.Add("Sec-Fetch-Mode", "cors");
        using var signedOut = await http.SendAsync(logout);

        signedOut.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        signedOut.Headers.Location.ShouldBe(new Uri("/bye", UriKind.Relative));
        using var extra = new HttpRequestMessage(HttpMethod.Get, new Uri("/auth/extra", UriKind.Relative));
        extra.Headers.Add("Sec-Fetch-Mode", "cors");
        (await http.SendAsync(extra)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static WebApplicationBuilder Builder(string environment, params (string Key, string Value)[] settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        foreach (var (key, value) in settings)
        {
            builder.Configuration[key] = value;
        }

        return builder;
    }
}
