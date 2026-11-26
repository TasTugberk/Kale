using System.Net;
using AuthService.Domain;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Hosting;

/// <summary>
/// Behind a reverse proxy listed in ForwardedHeaders:KnownProxies/KnownNetworks, AuthService takes the scheme
/// (X-Forwarded-Proto) and the client's address (X-Forwarded-For) from the proxy's headers; from anyone else it
/// ignores them. Plain HTTP is refused here as in production (OpenIddict:AllowInsecureHttpDevOnly off).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ForwardedHeadersTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Proxy = "10.0.0.5";
    private const string Stranger = "203.0.113.9";

    // The in-memory test server has no connection address; this header gives each request one.
    private const string ConnectionAddressHeader = "X-Test-Connection-Address";

    private static readonly Uri Discovery = new("/.well-known/openid-configuration", UriKind.Relative);

    private AuthServiceApp _app = null!;

    public async Task InitializeAsync()
    {
        var baseApp = await AuthServiceApp.CreateAsync(postgres);
        _app = new AuthServiceApp(baseApp.ConnectionString,
            ("OpenIddict:AllowInsecureHttpDevOnly", "false"),
            ("ForwardedHeaders:KnownProxies:0", Proxy),
            ("SignIn:AttemptsPerAddress", "1"),
            ("SignIn:AttemptWindow", "00:10:00"))
        {
            ConfigureServices = services => services.AddSingleton<IStartupFilter>(new ConnectionAddressFromHeader()),
        };
        await baseApp.DisposeAsync();

        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        (await users.CreateAsync(new User { UserName = "deniz", DisplayName = "Deniz" }, "Correct-horse-9")).Succeeded.ShouldBeTrue();
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Plain_http_without_a_proxy_is_refused()
    {
        using var response = await Send(Discovery, from: Stranger);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Plain_http_from_the_trusted_proxy_that_received_https_is_accepted()
    {
        using var response = await Send(Discovery, from: Proxy, ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "198.51.100.1"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain(AuthServiceApp.Issuer);
    }

    [Fact]
    public async Task Forwarded_headers_from_anyone_else_are_ignored()
    {
        using var response = await Send(Discovery, from: Stranger, ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", "198.51.100.1"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Behind_the_trusted_proxy_the_sign_in_limit_counts_each_client_on_its_own()
    {
        using var first = BehindProxy("198.51.100.1");
        using var second = BehindProxy("198.51.100.2");

        (await WrongPassword(first)).ShouldBe(HttpStatusCode.OK);
        (await WrongPassword(second)).ShouldBe(HttpStatusCode.OK, "another client, though through the same proxy");
        (await WrongPassword(first)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Someone_who_is_not_the_proxy_cannot_get_a_new_budget_by_sending_another_address()
    {
        using var first = new Browser(_app, headers: Headers(Stranger, ("X-Forwarded-For", "198.51.100.1")));
        using var second = new Browser(_app, headers: Headers(Stranger, ("X-Forwarded-For", "198.51.100.2")));

        (await WrongPassword(first)).ShouldBe(HttpStatusCode.OK);
        (await WrongPassword(second)).ShouldBe(HttpStatusCode.TooManyRequests);
    }

    private Browser BehindProxy(string client) =>
        new(_app, headers: Headers(Proxy, ("X-Forwarded-Proto", "https"), ("X-Forwarded-For", client)));

    private static async Task<HttpStatusCode> WrongPassword(Browser browser)
    {
        using var loginPage = await browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var response = await browser.LogInAsync(loginPage, "deniz", "wrong-password-1");
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> Send(Uri uri, string from, params (string Name, string Value)[] headers)
    {
        using var client = _app.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        foreach (var (name, value) in Headers(from, headers))
        {
            request.Headers.Add(name, value);
        }

        return await client.SendAsync(request);
    }

    private static Dictionary<string, string> Headers(string from, params (string Name, string Value)[] headers)
    {
        var all = headers.ToDictionary(header => header.Name, header => header.Value);
        all[ConnectionAddressHeader] = from;
        return all;
    }

    // Runs before the app's own middleware, like the real connection that Kestrel would report.
    private sealed class ConnectionAddressFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ConnectionAddressHeader, out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }

                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
