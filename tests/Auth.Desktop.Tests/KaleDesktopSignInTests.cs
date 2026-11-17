using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Time.Testing;

namespace Auth.Desktop.Tests;

/// <summary>
/// The desktop sign-in against a fake Kale (token endpoint) and a fake browser that comes back to the real loopback
/// listener. EndToEnd.Tests runs it against the real AuthService.
/// </summary>
public sealed class KaleDesktopSignInTests : IDisposable
{
    private readonly FakeBrowser _browser = new();
    private readonly FakeKale _kale = new();
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly KaleDesktopSignIn _signIn;

    public KaleDesktopSignInTests() => _signIn = new KaleDesktopSignIn(Options());

    public void Dispose() => _signIn.Dispose();

    [Fact]
    public async Task Sign_in_opens_Kale_in_the_browser_as_the_desktop_client_with_PKCE_and_a_loopback_address()
    {
        await _signIn.SignInAsync();

        var authorize = _browser.Opened.ShouldHaveSingleItem();
        authorize.GetLeftPart(UriPartial.Path).ShouldBe("https://auth.example/connect/authorize");
        var query = FakeBrowser.QueryOf(authorize);
        query["client_id"].ShouldBe("orders.desktop");
        query["response_type"].ShouldBe("code");
        query["scope"].ShouldBe("openid offline_access");
        query["code_challenge_method"].ShouldBe("S256");
        query["state"].ShouldNotBeNullOrEmpty();
        var redirectUri = new Uri(query["redirect_uri"]!);
        redirectUri.Host.ShouldBe("127.0.0.1");
        redirectUri.AbsolutePath.ShouldBe("/callback");

        // The code goes back with the verifier whose hash the browser carried, and no secret.
        var exchange = _kale.TokenRequests.ShouldHaveSingleItem();
        exchange["grant_type"].ShouldBe("authorization_code");
        exchange["client_id"].ShouldBe("orders.desktop");
        exchange["code"].ShouldBe("the-code");
        exchange["redirect_uri"].ShouldBe(redirectUri.AbsoluteUri);
        exchange.ShouldNotContainKey("client_secret");
        System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(exchange["code_verifier"])))
            .ShouldBe(query["code_challenge"]);

        _signIn.IsSignedIn.ShouldBeTrue();
        (await _signIn.GetAccessTokenAsync()).ShouldBe("access-1");
        _signIn.User.ShouldBe(new KaleUser("user-1", "Deniz Yılmaz", "deniz"));
    }

    [Fact]
    public async Task The_browser_is_told_it_can_close_the_window_in_the_computers_language()
    {
        var before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("tr-TR");
            await _signIn.SignInAsync();
            (await _browser.Visit!).Page.ShouldContain("Bu pencereyi kapatıp");

            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            await _signIn.SignInAsync();
            var (status, page) = await _browser.Visit!;
            status.ShouldBe(HttpStatusCode.OK);
            page.ShouldContain("You can close this window");
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public async Task An_answer_with_another_state_is_refused_and_no_code_is_exchanged()
    {
        // Someone else's code sent to the app's port (login CSRF) must not sign the app in.
        _browser.Comeback = _ => "code=the-code&state=forged";

        await Should.ThrowAsync<KaleSignInException>(() => _signIn.SignInAsync());

        _kale.TokenRequests.ShouldBeEmpty();
        _signIn.IsSignedIn.ShouldBeFalse();
        (await _browser.Visit!).Page.ShouldContain("try again");
    }

    [Fact]
    public async Task A_refusal_from_Kale_is_a_sign_in_exception_with_its_error()
    {
        _browser.Comeback = authorize => $"error=access_denied&state={FakeBrowser.QueryOf(authorize)["state"]}";

        var error = await Should.ThrowAsync<KaleSignInException>(() => _signIn.SignInAsync());

        error.Message.ShouldContain("access_denied");
        _kale.TokenRequests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Other_requests_to_the_loopback_port_are_ignored()
    {
        // The browser may ask for /favicon.ico first; the sign-in keeps waiting for /callback.
        var waiting = new TaskCompletionSource<Uri>();
        _browser.Comeback = authorize =>
        {
            waiting.SetResult(new Uri(FakeBrowser.QueryOf(authorize)["redirect_uri"]!));
            return null;
        };
        var signIn = _signIn.SignInAsync();
        var callback = await waiting.Task;

        var (status, _) = await FakeBrowser.GetAsync(new Uri(callback, "/favicon.ico"));
        var state = FakeBrowser.QueryOf(_browser.Opened[0])["state"];
        await FakeBrowser.GetAsync(new Uri($"{callback}?code=the-code&state={state}"));
        await signIn;

        status.ShouldBe(HttpStatusCode.NotFound);
        _signIn.IsSignedIn.ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_who_never_finishes_in_the_browser_gets_a_timeout()
    {
        _browser.Comeback = _ => null;
        using var signIn = new KaleDesktopSignIn(Options(signInTimeout: TimeSpan.FromMilliseconds(300)));

        await Should.ThrowAsync<TimeoutException>(() => signIn.SignInAsync());
    }

    [Fact]
    public async Task The_access_token_is_reused_until_a_minute_before_it_expires_then_renewed_with_the_refresh_token()
    {
        await _signIn.SignInAsync();
        _kale.Answer = _ => Task.FromResult(FakeKale.Tokens("access-2", "refresh-2"));

        _clock.Advance(TimeSpan.FromMinutes(8));
        (await _signIn.GetAccessTokenAsync()).ShouldBe("access-1", "still 2 minutes left");
        _clock.Advance(TimeSpan.FromSeconds(61));
        (await _signIn.GetAccessTokenAsync()).ShouldBe("access-2");

        var refresh = _kale.TokenRequests[1];
        refresh["grant_type"].ShouldBe("refresh_token");
        refresh["refresh_token"].ShouldBe("refresh-1");
        refresh["client_id"].ShouldBe("orders.desktop");
        refresh.ShouldNotContainKey("client_secret");
    }

    [Fact]
    public async Task Parallel_calls_renew_once()
    {
        // Kale gives a new refresh token at each renewal: the old one must not be spent twice.
        await _signIn.SignInAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        _kale.Answer = async _ =>
        {
            await Task.Delay(100);
            return FakeKale.Tokens("access-2", "refresh-2");
        };

        var tokens = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _signIn.GetAccessTokenAsync()));

        tokens.ShouldAllBe(token => token == "access-2");
        _kale.TokenRequests.Count.ShouldBe(2, "the code exchange and one renewal");
    }

    [Fact]
    public async Task A_refused_renewal_means_signing_in_again_and_forgets_the_tokens()
    {
        var store = new MemoryRefreshTokenStore();
        using var signIn = new KaleDesktopSignIn(Options(store: store));
        await signIn.SignInAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        _kale.Answer = _ => Task.FromResult(FakeKale.Error("invalid_grant"));

        await Should.ThrowAsync<KaleSignInRequiredException>(() => signIn.GetAccessTokenAsync());

        signIn.IsSignedIn.ShouldBeFalse();
        store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task An_unreachable_Kale_keeps_the_tokens_for_the_next_try()
    {
        await _signIn.SignInAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        _kale.Answer = _ => Task.FromResult(FakeKale.Error("server_error", HttpStatusCode.ServiceUnavailable));

        await Should.ThrowAsync<HttpRequestException>(() => _signIn.GetAccessTokenAsync());

        _signIn.IsSignedIn.ShouldBeTrue();
        _kale.Answer = _ => Task.FromResult(FakeKale.Tokens("access-2", "refresh-2"));
        (await _signIn.GetAccessTokenAsync()).ShouldBe("access-2");
    }

    [Fact]
    public async Task A_renewal_that_finishes_after_signing_out_does_not_sign_the_user_back_in()
    {
        var store = new MemoryRefreshTokenStore();
        using var signIn = new KaleDesktopSignIn(Options(store: store));
        await signIn.SignInAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var kaleAnswers = new TaskCompletionSource<HttpResponseMessage>();
        var renewalAsked = new TaskCompletionSource();
        _kale.Answer = _ =>
        {
            renewalAsked.TrySetResult();
            return kaleAnswers.Task;
        };

        var renewal = signIn.GetAccessTokenAsync();
        await renewalAsked.Task;
        var signOut = signIn.SignOutAsync();
        kaleAnswers.SetResult(FakeKale.Tokens("access-2", "refresh-2"));
        await renewal;
        await signOut;

        signIn.IsSignedIn.ShouldBeFalse();
        store.Stored.ShouldBeNull();
    }

    [Fact]
    public async Task A_refused_renewal_that_finishes_after_a_new_sign_in_keeps_the_new_tokens()
    {
        // The new sign-in ended the old session at Kale, so the old refresh token is refused: that refusal
        // mustn't throw away the new sign-in's tokens.
        await _signIn.SignInAsync();
        _clock.Advance(TimeSpan.FromMinutes(10));
        var kaleAnswers = new TaskCompletionSource<HttpResponseMessage>();
        var renewalAsked = new TaskCompletionSource();
        _kale.Answer = form =>
        {
            if (form["grant_type"] == "refresh_token")
            {
                renewalAsked.TrySetResult();
                return kaleAnswers.Task;
            }

            return Task.FromResult(FakeKale.Tokens("access-new", "refresh-new"));
        };

        var renewal = _signIn.GetAccessTokenAsync();
        await renewalAsked.Task;
        var newSignIn = _signIn.SignInAsync();

        // Give the new sign-in time to finish before the refusal arrives (the browser's page comes last).
        await Task.WhenAny(_browser.Visit!, Task.Delay(500));
        kaleAnswers.SetResult(FakeKale.Error("invalid_grant"));
        await Should.ThrowAsync<KaleSignInRequiredException>(() => renewal);
        await newSignIn;

        (await _signIn.GetAccessTokenAsync()).ShouldBe("access-new");
    }

    [Fact]
    public async Task An_answer_without_a_code_is_a_sign_in_exception()
    {
        _browser.Comeback = authorize => $"state={FakeBrowser.QueryOf(authorize)["state"]}";

        await Should.ThrowAsync<KaleSignInException>(() => _signIn.SignInAsync());

        (await _browser.Visit!).Page.ShouldContain("try again");
    }

    [Fact]
    public async Task A_token_answer_that_is_not_tokens_is_an_HttpRequestException_and_the_browser_still_gets_a_page()
    {
        _kale.Answer = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>proxy login</html>") });

        await Should.ThrowAsync<HttpRequestException>(() => _signIn.SignInAsync());

        _signIn.IsSignedIn.ShouldBeFalse();
        (await _browser.Visit!).Page.ShouldContain("try again");
    }

    [Fact]
    public async Task Without_a_sign_in_there_is_no_token()
    {
        await Should.ThrowAsync<KaleSignInRequiredException>(() => _signIn.GetAccessTokenAsync());
    }

    [Fact]
    public async Task A_refresh_token_store_keeps_the_sign_in_for_the_next_start()
    {
        var store = new MemoryRefreshTokenStore();
        using (var firstRun = new KaleDesktopSignIn(Options(store: store)))
        {
            await firstRun.SignInAsync();
        }

        store.Stored.ShouldBe("refresh-1");
        _kale.Answer = _ => Task.FromResult(FakeKale.Tokens("access-2", "refresh-2"));
        using var nextRun = new KaleDesktopSignIn(Options(store: store));

        (await nextRun.GetAccessTokenAsync()).ShouldBe("access-2", "renewed with the stored refresh token, no browser");
        store.Stored.ShouldBe("refresh-2");
        _browser.Opened.Count.ShouldBe(1);
        nextRun.User!.UserName.ShouldBe("deniz");
    }

    [Fact]
    public async Task Signing_out_forgets_the_tokens_and_ends_the_session_at_Kale_in_the_browser()
    {
        var store = new MemoryRefreshTokenStore();
        using var signIn = new KaleDesktopSignIn(Options(store: store));
        await signIn.SignInAsync();

        await signIn.SignOutAsync();

        signIn.IsSignedIn.ShouldBeFalse();
        signIn.User.ShouldBeNull();
        store.Stored.ShouldBeNull();
        var endSession = _browser.Opened[^1];
        endSession.GetLeftPart(UriPartial.Path).ShouldBe("https://auth.example/connect/endsession");
        FakeBrowser.QueryOf(endSession)["id_token_hint"].ShouldBe(FakeKale.IdToken("user-1", "Deniz Yılmaz", "deniz"));
        FakeBrowser.QueryOf(endSession)["client_id"].ShouldBe("orders.desktop");
        await Should.ThrowAsync<KaleSignInRequiredException>(() => signIn.GetAccessTokenAsync());
    }

    [Fact]
    public async Task The_bearer_handler_adds_the_users_access_token()
    {
        await _signIn.SignInAsync();
        var api = new RecordingHandler();
        using var http = new HttpClient(new KaleBearerHandler(_signIn) { InnerHandler = api });

        await http.GetAsync(new Uri("https://orders.example/api/orders"));

        api.Authorization.ShouldBe("Bearer access-1");
    }

    [Theory]
    [InlineData("http://auth.example/", "orders", "https")]
    [InlineData("https://auth.example/", "orders.desktop", "key")]
    [InlineData("https://auth.example/", "", "key")]
    public void Wrong_options_are_refused_at_once(string authority, string key, string reason)
    {
        var error = Should.Throw<ArgumentException>(() => new KaleDesktopSignIn(new KaleDesktopOptions
        {
            Authority = new Uri(authority),
            ApplicationKey = key,
        }));

        error.Message.ShouldContain(reason);
    }

    [Fact]
    public void Plain_http_is_allowed_for_local_development_when_asked()
    {
        using var signIn = new KaleDesktopSignIn(new KaleDesktopOptions
        {
            Authority = new Uri("http://auth.kale.localhost:5080/"),
            ApplicationKey = "orders",
            AllowInsecureHttpDevOnly = true,
        });

        signIn.ClientId.ShouldBe("orders.desktop");
    }

    private KaleDesktopOptions Options(TimeSpan? signInTimeout = null, IRefreshTokenStore? store = null) => new()
    {
        Authority = new Uri("https://auth.example/"),
        ApplicationKey = "orders",
        Browser = _browser,
        BackchannelHandler = _kale,
        Clock = _clock,
        RefreshTokenStore = store,
        SignInTimeout = signInTimeout ?? TimeSpan.FromSeconds(10),
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
