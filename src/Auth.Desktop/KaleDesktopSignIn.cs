using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Auth.Desktop;

/// <summary>Who signed in, from Kale's ID token.</summary>
public sealed record KaleUser(string Id, string? Name, string? UserName);

/// <summary>
/// Signs the user of a desktop application in through Kale (owner's decision): in the system browser, back to a
/// loopback address on a free port, authorization code + PKCE (S256) as the public client "{key}.desktop", with no
/// client secret (an installed app can't keep one). Keeps the tokens in memory (or the refresh token in
/// <see cref="KaleDesktopOptions.RefreshTokenStore"/>) and renews the access token a minute before it expires.
/// </summary>
/// <remarks>
/// Awaits use ConfigureAwait(false): in a WPF app the continuations then don't queue on the UI thread, and an app
/// that blocks on a call by mistake doesn't deadlock.
/// </remarks>
public sealed class KaleDesktopSignIn : IDisposable
{
    /// <summary>The access token is renewed this long before it expires, so a call never leaves with an expiring one.</summary>
    public static readonly TimeSpan RenewBefore = TimeSpan.FromMinutes(1);

    private const string CallbackPath = "/callback";

    private readonly KaleDesktopOptions _options;
    private readonly Uri _authority;
    private readonly HttpClient _http;

    // One renewal at a time: Kale gives a new refresh token at every renewal, so parallel calls must share one.
    // Sign-in and sign-out take it too before they replace or forget the tokens, so a renewal that was already
    // running can't undo them with its answer.
    private readonly SemaphoreSlim _renewal = new(1, 1);

    // Copied from the options once they're checked: a later change to the options mustn't name another client.
    private readonly string _clientId;

    // Replaced as a whole (never changed in place), so a reader on another thread sees one consistent set.
    private volatile TokenSet? _tokens;

    public KaleDesktopSignIn(KaleDesktopOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _authority = options.Authority is { IsAbsoluteUri: true } authority
            ? authority
            : throw new ArgumentException("Set Authority to Kale's address, e.g. https://auth.example.com/.", nameof(options));

        // Tokens travel to and from this address.
        if (_authority.Scheme != Uri.UriSchemeHttps && !(_authority.Scheme == Uri.UriSchemeHttp && options.AllowInsecureHttpDevOnly))
        {
            throw new ArgumentException(
                $"Kale's address must use https ({_authority}). Plain http only for local development, with AllowInsecureHttpDevOnly.", nameof(options));
        }

        // Keys are lowercase letters, digits and dashes: no '.', which would name another client.
        if (string.IsNullOrWhiteSpace(options.ApplicationKey) || options.ApplicationKey.Contains('.', StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{options.ApplicationKey}' is not an application key: set ApplicationKey to the application's key in Kale, e.g. orders.", nameof(options));
        }

        _options = options;
        _clientId = options.ApplicationKey + ".desktop";

        // A handler from the options belongs to the caller (it may serve several instances): not disposed here.
        _http = options.BackchannelHandler is { } handler ? new HttpClient(handler, disposeHandler: false) : new HttpClient();
    }

    /// <summary>The client id the app signs in with: "{key}.desktop".</summary>
    public string ClientId => _clientId;

    /// <summary>Whether tokens are at hand (they may still turn out to be refused, e.g. after an admin stopped the session).</summary>
    public bool IsSignedIn => _tokens is not null;

    /// <summary>Who is signed in, or null.</summary>
    public KaleUser? User => _tokens?.User;

    /// <summary>
    /// Signs the user in: opens Kale's sign-in page in the browser and waits (up to
    /// <see cref="KaleDesktopOptions.SignInTimeout"/>) for it to come back. A user still signed in to Kale in that
    /// browser comes back at once. Kale keeps one session per user and application, so this ends the user's other
    /// session in the application (e.g. in its web front end).
    /// </summary>
    /// <exception cref="KaleSignInException">Kale refused (e.g. no access to the application), or its answer was wrong.</exception>
    /// <exception cref="TimeoutException">The user didn't finish in the browser in time.</exception>
    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        using var listener = new LoopbackListener();
        var redirectUri = listener.AddressOf(CallbackPath);
        var state = RandomValue();
        var codeVerifier = RandomValue();

        await _options.Browser.OpenAsync(AuthorizeUri(redirectUri, state, codeVerifier), cancellationToken).ConfigureAwait(false);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.SignInTimeout);
        LoopbackRequest answer;
        try
        {
            answer = await listener.WaitForAsync(CallbackPath, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The sign-in wasn't finished in the browser within {_options.SignInTimeout}.");
        }

        using (answer)
        {
            var culture = CultureInfo.CurrentUICulture;
            try
            {
                // The state proves the answer belongs to this sign-in: another site or program could send its own
                // code to this port, to sign the app in as someone else.
                if (answer.Query["state"] != state)
                {
                    throw new KaleSignInException("The answer that came back to the app doesn't belong to its sign-in (state).");
                }

                if (answer.Query["error"] is { } error)
                {
                    throw new KaleSignInException($"Kale refused the sign-in: {error}.");
                }

                var code = answer.Query["code"] ?? throw new KaleSignInException("Kale's answer has no authorization code.");
                var tokens = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = redirectUri.AbsoluteUri,
                    ["code_verifier"] = codeVerifier,
                }, previous: null, cancellationToken).ConfigureAwait(false);

                // After a renewal still running with the old refresh token, so its answer can't overwrite these.
                await _renewal.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await KeepAsync(tokens, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _renewal.Release();
                }
            }
            catch (Exception exception) when (exception is KaleSignInException or KaleSignInRequiredException or HttpRequestException)
            {
                await answer.RespondAsync(HttpStatusCode.OK, BrowserPages.Failed(culture), cancellationToken).ConfigureAwait(false);
                throw exception is KaleSignInRequiredException ? new KaleSignInException("Kale refused the authorization code.", exception) : exception;
            }

            await answer.RespondAsync(HttpStatusCode.OK, BrowserPages.SignedIn(culture), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The user's access token for calls to the application's own API, renewed with the refresh token a minute
    /// before it expires (one renewal for parallel calls).
    /// </summary>
    /// <exception cref="KaleSignInRequiredException">Not signed in, or Kale refused the renewal (the session ended).</exception>
    /// <exception cref="HttpRequestException">Kale couldn't be reached; the tokens stay for the next try.</exception>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_tokens is { } current && IsFresh(current))
        {
            return current.AccessToken;
        }

        await _renewal.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another call may have renewed while this one waited.
            var tokens = _tokens;
            if (tokens is not null && IsFresh(tokens))
            {
                return tokens.AccessToken;
            }

            var refreshToken = tokens?.RefreshToken;
            if (refreshToken is null && tokens is null && _options.RefreshTokenStore is { } store)
            {
                refreshToken = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            if (refreshToken is null)
            {
                throw new KaleSignInRequiredException();
            }

            TokenSet renewed;
            try
            {
                renewed = await RequestTokensAsync(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken,
                }, tokens, cancellationToken).ConfigureAwait(false);
            }
            catch (KaleSignInRequiredException)
            {
                await ForgetAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }

            await KeepAsync(renewed, cancellationToken).ConfigureAwait(false);
            return renewed.AccessToken;
        }
        finally
        {
            _renewal.Release();
        }
    }

    /// <summary>
    /// Signs the user out: forgets the tokens here, then opens Kale's sign-out in the browser, which ends the
    /// session (its tokens stop working everywhere) and the browser's sign-in to Kale.
    /// </summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        // After a renewal still running, so its answer can't sign the user back in.
        string? idToken;
        await _renewal.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            idToken = _tokens?.IdToken;
            await ForgetAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _renewal.Release();
        }

        if (idToken is not null)
        {
            // Kale needs the ID token to know which session to end; without a return address the browser stays on
            // Kale's own "signed out" page.
            await _options.Browser.OpenAsync(EndpointUri("connect/endsession", new Dictionary<string, string>
            {
                ["id_token_hint"] = idToken,
                ["client_id"] = ClientId,
            }), cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _renewal.Dispose();
    }

    private bool IsFresh(TokenSet tokens) => _options.Clock.GetUtcNow() < tokens.ExpiresAt - RenewBefore;

    private Uri AuthorizeUri(Uri redirectUri, string state, string codeVerifier)
    {
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        return EndpointUri("connect/authorize", new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["response_type"] = "code",

            // openid: an ID token (who signed in, and for signing out); offline_access: a refresh token.
            ["scope"] = "openid offline_access",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        });
    }

    private Uri EndpointUri(string path, Dictionary<string, string> parameters)
    {
        var query = string.Join('&', parameters.Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value)}"));
        return new Uri(new Uri(_authority, path), "?" + query);
    }

    // Kale's token endpoint. A refused grant (invalid_grant) means the user must sign in again; anything else that
    // isn't tokens is an HttpRequestException, which leaves the tokens as they are.
    private async Task<TokenSet> RequestTokensAsync(
        Dictionary<string, string> form, TokenSet? previous, CancellationToken cancellationToken)
    {
        form["client_id"] = ClientId;
        using var content = new FormUrlEncodedContent(form);
        using var response = await _http.PostAsync(new Uri(_authority, "connect/token"), content, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.BadRequest && ErrorOf(body) == "invalid_grant")
        {
            throw new KaleSignInRequiredException("Kale ended the session: sign in again.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Kale's token endpoint answered {(int)response.StatusCode} ({ErrorOf(body)}).", null, response.StatusCode);
        }

        try
        {
            return TokenSet.FromAnswer(body, _options.Clock.GetUtcNow(), previous);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // E.g. a proxy's own page with status 200: no usable answer, like an unreachable Kale.
            throw new HttpRequestException("Kale's token endpoint answered with something that isn't tokens.", exception);
        }
    }

    private async Task KeepAsync(TokenSet tokens, CancellationToken cancellationToken)
    {
        _tokens = tokens;
        if (_options.RefreshTokenStore is { } store)
        {
            await store.SaveAsync(tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        _tokens = null;
        if (_options.RefreshTokenStore is { } store)
        {
            await store.SaveAsync(null, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ErrorOf(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // 32 random bytes, base64url: the PKCE verifier (43 characters, RFC 7636) and the state.
    private static string RandomValue() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    private sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAt, string? RefreshToken, string? IdToken, KaleUser? User)
    {
        // A renewal's answer may leave out what didn't change (the refresh or ID token): the previous ones stay.
        public static TokenSet FromAnswer(string body, DateTimeOffset now, TokenSet? previous)
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var accessToken = root.GetProperty("access_token").GetString()!;
            var expiresIn = root.TryGetProperty("expires_in", out var seconds) ? seconds.GetInt32() : 0;
            var refreshToken = Text(root, "refresh_token") ?? previous?.RefreshToken;
            var idToken = Text(root, "id_token");
            return idToken is null
                ? new TokenSet(accessToken, now.AddSeconds(expiresIn), refreshToken, previous?.IdToken, previous?.User)
                : new TokenSet(accessToken, now.AddSeconds(expiresIn), refreshToken, idToken, UserFrom(idToken));
        }

        private static string? Text(JsonElement root, string name) =>
            root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        // The ID token came straight from Kale's token endpoint over https, so its claims are read without checking
        // the signature (OpenID Connect allows that for the token endpoint's answer). Only for showing who is signed
        // in: the application's API checks every access token itself.
        private static KaleUser? UserFrom(string idToken)
        {
            var parts = idToken.Split('.');
            if (parts.Length != 3)
            {
                return null;
            }

            using var payload = JsonDocument.Parse(Base64Url.DecodeFromChars(parts[1]));
            var claims = payload.RootElement;
            return Text(claims, "sub") is { } subject
                ? new KaleUser(subject, Text(claims, "name"), Text(claims, "preferred_username"))
                : null;
        }
    }
}

/// <summary>
/// Adds the signed-in user's access token to calls to the application's own API:
/// <c>new HttpClient(new KaleBearerHandler(signIn) { InnerHandler = new HttpClientHandler() })</c>. Only for the
/// application's API: the token is the user's, for this application.
/// </summary>
public sealed class KaleBearerHandler(KaleDesktopSignIn signIn) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var accessToken = await signIn.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
