using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;

namespace Auth.Desktop.Tests;

/// <summary>
/// The browser: records what it was asked to open and, like Kale after a sign-in, comes back to the app's loopback
/// address with what <see cref="Comeback"/> returns for the authorize address (nothing when it returns null).
/// </summary>
internal sealed class FakeBrowser : IBrowser
{
    public List<Uri> Opened { get; } = [];

    public Func<Uri, string?>? Comeback { get; set; } = authorize => $"code=the-code&state={QueryOf(authorize)["state"]}";

    /// <summary>The visit to the loopback address: its status and the page the app answered with.</summary>
    public Task<(HttpStatusCode Status, string Page)>? Visit { get; private set; }

    public Task OpenAsync(Uri address, CancellationToken cancellationToken)
    {
        Opened.Add(address);
        if (address.AbsolutePath == "/connect/authorize" && Comeback?.Invoke(address) is { } query)
        {
            var redirectUri = QueryOf(address)["redirect_uri"]!;
            Visit = Task.Run(() => GetAsync(new Uri($"{redirectUri}?{query}")));
        }

        // Like the system browser: returns at once, the user isn't done yet.
        return Task.CompletedTask;
    }

    public static async Task<(HttpStatusCode Status, string Page)> GetAsync(Uri address)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(address);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    public static System.Collections.Specialized.NameValueCollection QueryOf(Uri address) => HttpUtility.ParseQueryString(address.Query);
}

/// <summary>Kale's token endpoint: records each request and answers with <see cref="Answer"/>.</summary>
internal sealed class FakeKale : HttpMessageHandler
{
    private readonly Lock _gate = new();

    public List<Dictionary<string, string>> TokenRequests { get; } = [];

    public Func<Dictionary<string, string>, Task<HttpResponseMessage>> Answer { get; set; } =
        _ => Task.FromResult(Tokens("access-1", "refresh-1"));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.RequestUri!.AbsoluteUri.ShouldBe("https://auth.example/connect/token");
        var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
        var fields = form.AllKeys.ToDictionary(key => key!, key => form[key]!);
        lock (_gate)
        {
            TokenRequests.Add(fields);
        }

        return await Answer(fields);
    }

    public static HttpResponseMessage Tokens(string accessToken, string? refreshToken, int expiresIn = 600, string? idToken = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["access_token"] = accessToken,
            ["token_type"] = "Bearer",
            ["expires_in"] = expiresIn,
            ["refresh_token"] = refreshToken,
            ["id_token"] = idToken ?? IdToken("user-1", "Deniz Yılmaz", "deniz"),
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
    }

    public static HttpResponseMessage Error(string error, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json") };

    /// <summary>An ID token as Kale sends it (the signature doesn't matter: the app got it straight from Kale).</summary>
    public static string IdToken(string subject, string name, string userName)
    {
        static string Part(object value) => System.Buffers.Text.Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(value));
        return $"{Part(new { alg = "RS256", typ = "JWT" })}.{Part(new { sub = subject, name, preferred_username = userName })}.c2ln";
    }
}

/// <summary>Remembers the refresh token in memory, like an app's own store would on disk.</summary>
internal sealed class MemoryRefreshTokenStore : IRefreshTokenStore
{
    public string? Stored { get; set; }

    public Task<string?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Stored);

    public Task SaveAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        Stored = refreshToken;
        return Task.CompletedTask;
    }
}
