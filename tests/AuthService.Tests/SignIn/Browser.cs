using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AuthService.Tests.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Drives the sign-in flow like a browser (cookies, no automatic redirects) together with the client app's side
/// (PKCE, code exchange), so tests read as the steps a user goes through.
/// </summary>
internal sealed class Browser : IDisposable
{
    private readonly HttpClient _http;

    public Browser(AuthServiceApp app, string? acceptLanguage = null)
    {
        _http = app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        if (acceptLanguage is not null)
        {
            _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd(acceptLanguage);
        }
    }

    public string CodeVerifier { get; } = Base64Url(RandomNumberGenerator.GetBytes(32));

    public static Uri RedirectUri(string clientId) => new($"https://{clientId}.example/signin-oidc");

    /// <summary>What a client app sends to start sign-in: authorization code + PKCE (S256).</summary>
    public Uri AuthorizeUri(string clientId, string? extra = null)
    {
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(CodeVerifier)));
        var query = $"client_id={clientId}&response_type=code&scope=openid%20offline_access" +
            $"&redirect_uri={Uri.EscapeDataString(RedirectUri(clientId).AbsoluteUri)}" +
            $"&code_challenge={challenge}&code_challenge_method=S256&state=abc{extra}";
        return new Uri($"/connect/authorize?{query}", UriKind.Relative);
    }

    public Task<HttpResponseMessage> GetAsync(Uri uri) => _http.GetAsync(uri);

    /// <summary>The same authorization request as <see cref="AuthorizeUri"/>, sent as a form post.</summary>
    public Task<HttpResponseMessage> PostAuthorizeAsync(string clientId)
    {
        var query = AuthorizeUri(clientId).OriginalString.Split('?', 2)[1];
        var form = query.Split('&').Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]));
        return _http.PostAsync(new Uri("/connect/authorize", UriKind.Relative), new FormUrlEncodedContent(form));
    }

    /// <summary>Follows redirects that stay on AuthService; stops at the first one leaving it (to the client app).</summary>
    public async Task<HttpResponseMessage> FollowAsync(HttpResponseMessage response)
    {
        while (response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found or HttpStatusCode.SeeOther)
        {
            var location = response.Headers.Location!;
            if (location.IsAbsoluteUri && location.Host != "localhost")
            {
                return response;
            }

            response.Dispose();
            response = await _http.GetAsync(location);
        }

        return response;
    }

    /// <summary>Fills in and submits the login form on the page <paramref name="loginPage"/> returned.</summary>
    public async Task<HttpResponseMessage> LogInAsync(HttpResponseMessage loginPage, string userName, string password)
    {
        var html = await loginPage.Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenField(html, "__RequestVerificationToken"),
            ["ReturnUrl"] = WebUtility.HtmlDecode(OptionalHiddenField(html, "ReturnUrl")),
            ["UserName"] = userName,
            ["Password"] = password,
        };
        return await _http.PostAsync(loginPage.RequestMessage!.RequestUri, new FormUrlEncodedContent(form));
    }

    /// <summary>The role choices (value = role id) on the role picker page.</summary>
    public static async Task<IReadOnlyList<Guid>> RoleChoicesAsync(HttpResponseMessage pickerPage)
    {
        var html = await pickerPage.Content.ReadAsStringAsync();
        return Regex.Matches(html, "name=\"RoleId\" value=\"([0-9a-f-]{36})\"")
            .Select(match => Guid.Parse(match.Groups[1].Value))
            .ToList();
    }

    /// <summary>Submits the role picker with <paramref name="roleId"/>.</summary>
    public async Task<HttpResponseMessage> ChooseRoleAsync(HttpResponseMessage pickerPage, Guid roleId, string? returnUrl = null)
    {
        var html = await pickerPage.Content.ReadAsStringAsync();
        var form = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = HiddenField(html, "__RequestVerificationToken"),
            ["ReturnUrl"] = returnUrl ?? WebUtility.HtmlDecode(HiddenField(html, "ReturnUrl")),
            ["RoleId"] = roleId.ToString(),
        };
        return await _http.PostAsync(pickerPage.RequestMessage!.RequestUri, new FormUrlEncodedContent(form));
    }

    /// <summary>The authorization code from the redirect back to the client app.</summary>
    public static string CodeFrom(HttpResponseMessage redirectToClient) =>
        QueryValue(redirectToClient, "code") ?? throw new InvalidOperationException(
            $"No code in {redirectToClient.Headers.Location} (error: {QueryValue(redirectToClient, "error")})");

    public static string? QueryValue(HttpResponseMessage redirect, string name)
    {
        var query = redirect.Headers.Location?.Query.TrimStart('?') ?? "";
        return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .Where(pair => pair[0] == name)
            .Select(pair => Uri.UnescapeDataString(pair[1]))
            .FirstOrDefault();
    }

    /// <summary>The client app exchanging its code (with its secret and the PKCE verifier) for tokens.</summary>
    public async Task<(HttpStatusCode Status, JsonDocument Body)> ExchangeCodeAsync(
        string clientId, string clientSecret, string code, string? codeVerifier = null)
    {
        using var response = await _http.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = RedirectUri(clientId).AbsoluteUri,
            ["code_verifier"] = codeVerifier ?? CodeVerifier,
        }));
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    public async Task<(HttpStatusCode Status, JsonDocument Body)> RefreshAsync(string clientId, string clientSecret, string refreshToken)
    {
        using var response = await _http.PostAsync(new Uri("/connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["refresh_token"] = refreshToken,
        }));
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync()));
    }

    public void Dispose() => _http.Dispose();

    private static string HiddenField(string html, string name)
    {
        var match = Regex.Match(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"|value=\"([^\"]*)\"[^>]*name=\"{Regex.Escape(name)}\"");
        match.Success.ShouldBeTrue($"hidden field {name} on the login page");
        return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
    }

    // Razor leaves out the value attribute when it's null, e.g. ReturnUrl when the page was opened directly.
    private static string OptionalHiddenField(string html, string name) =>
        Regex.IsMatch(html, $"name=\"{Regex.Escape(name)}\"[^>]*value=") ? HiddenField(html, name) : "";

    private static string Base64Url(byte[] bytes) => System.Buffers.Text.Base64Url.EncodeToString(bytes);
}
