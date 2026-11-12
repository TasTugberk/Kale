using System.Net;
using System.Text.RegularExpressions;
using AuthService.SignIn;
using AuthService.Tests.Hosting;
using Microsoft.AspNetCore.Mvc.Testing.Handlers;

namespace EndToEnd.Tests;

/// <summary>
/// A browser for a web application that signs its users in through Kale (Auth.Web): one cookie jar for both
/// sites, redirects followed between them, and each request marked the way browsers mark it, as a page
/// navigation or as a script's fetch. Kale is reached at its issuer's address, as in its discovery document.
/// </summary>
internal sealed class WebBrowser : IDisposable
{
    public static readonly Uri KaleAddress = new(AuthServiceApp.Issuer);

    private readonly CookieContainerHandler _cookies;
    private readonly HttpClient _http;

    public WebBrowser(AuthServiceApp kale, HttpMessageHandler web, Uri webAddress)
    {
        WebAddress = webAddress;
        _cookies = new CookieContainerHandler { InnerHandler = new SiteRouter(kale.Server.CreateHandler(), web) };
        _http = new HttpClient(_cookies) { BaseAddress = webAddress };
    }

    public Uri WebAddress { get; }

    public CookieContainer Cookies => _cookies.Container;

    public static string Origin(Uri address) => address.GetLeftPart(UriPartial.Authority);

    /// <summary>Opens a page (address bar, link) and follows every redirect; returns the last answer.</summary>
    public async Task<HttpResponseMessage> OpenAsync(string address)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, UriKind.RelativeOrAbsolute));
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Accept.ParseAdd("text/html");
        return await FollowAsync(await _http.SendAsync(request));
    }

    /// <summary>A fetch from a script on the application's page (from <paramref name="origin"/>, its own by default).</summary>
    public async Task<HttpResponseMessage> FetchAsync(HttpMethod method, string path, string? origin = null)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Mode", "cors");
        request.Headers.Accept.ParseAdd("application/json");
        if (method != HttpMethod.Get)
        {
            // Browsers send Origin with every request that isn't a GET or HEAD.
            request.Headers.Add("Origin", origin ?? Origin(WebAddress));
        }

        return await _http.SendAsync(request);
    }

    /// <summary>Submits the page's sign-out form (posted from <paramref name="origin"/>) and follows the redirects.</summary>
    public async Task<HttpResponseMessage> SignOutAsync(string? origin = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/auth/logout", UriKind.Relative));
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        request.Headers.Add("Origin", origin ?? Origin(WebAddress));
        return await FollowAsync(await _http.SendAsync(request));
    }

    /// <summary>Opens the application's sign-in, and logs in on Kale's page; returns where the browser ended up.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string userName, string returnUrl = "/")
    {
        using var loginPage = await OpenAsync($"/auth/login?returnUrl={Uri.EscapeDataString(returnUrl)}");
        IsKaleLoginPage(loginPage).ShouldBeTrue($"expected Kale's login page, got {loginPage.RequestMessage!.RequestUri} ({loginPage.StatusCode})");
        return await LogInAsync(loginPage, userName);
    }

    /// <summary>Fills in and submits Kale's login form, then follows the redirects back to the application.</summary>
    public async Task<HttpResponseMessage> LogInAsync(HttpResponseMessage loginPage, string userName)
    {
        var html = await loginPage.Content.ReadAsStringAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, loginPage.RequestMessage!.RequestUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = HiddenField(html, "__RequestVerificationToken"),
                ["ReturnUrl"] = WebUtility.HtmlDecode(HiddenField(html, "ReturnUrl")),
                ["UserName"] = userName,
                ["Password"] = AuthServiceSteps.Password,
            }),
        };
        request.Headers.Add("Sec-Fetch-Mode", "navigate");
        return await FollowAsync(await _http.SendAsync(request));
    }

    public static bool IsKaleLoginPage(HttpResponseMessage response) =>
        response.StatusCode == HttpStatusCode.OK
        && response.RequestMessage!.RequestUri!.Host == KaleAddress.Host
        && response.RequestMessage.RequestUri.AbsolutePath == SignInSetup.LoginPath;

    /// <summary>Whether this browser still holds Kale's own sign-in cookie (which would sign the user in without a password).</summary>
    public bool HasKaleSignInCookie() => Cookies.GetCookies(KaleAddress).Any(cookie => cookie.Name == "kale.signin");

    public void Dispose() => _http.Dispose();

    private async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response)
    {
        for (var hops = 0; hops < 20; hops++)
        {
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null)
            {
                return response;
            }

            var next = new Uri(response.RequestMessage!.RequestUri!, response.Headers.Location);
            response.Dispose();
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Add("Sec-Fetch-Mode", "navigate");
            request.Headers.Accept.ParseAdd("text/html");
            response = await _http.SendAsync(request);
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    private static string HiddenField(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"|value=\"([^\"]*)\"[^>]*name=\"{Regex.Escape(name)}\"");
        match.Success.ShouldBeTrue($"hidden field {name} on Kale's login page");
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    /// <summary>Sends requests for Kale's host to the in-memory AuthService, everything else to the web application.</summary>
    private sealed class SiteRouter(HttpMessageHandler kale, HttpMessageHandler web) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _kale = new(kale);
        private readonly HttpMessageInvoker _web = new(web);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            request.RequestUri!.Host == KaleAddress.Host
                ? _kale.SendAsync(request, cancellationToken)
                : _web.SendAsync(request, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _kale.Dispose();
                _web.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
