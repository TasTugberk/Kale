using System.Net;
using System.Text.RegularExpressions;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.SignIn;

/// <summary>
/// A signed-in user turns two-factor sign-in on for themselves (/account/two-factor/setup): the key as a QR code
/// and as text, confirmed with a code from the app, then recovery codes shown once. They turn it off again on
/// /account/two-factor with a code.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class TwoFactorSetupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";
    private static readonly Uri SetupPage = new("/account/two-factor/setup", UriKind.Relative);
    private static readonly Uri StatusPage = new("/account/two-factor", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private Browser _browser = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            _user = await scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync("deniz", "Deniz", Password);
        }

        _browser = new Browser(_app, acceptLanguage: "en");
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Theory]
    [InlineData("/account/two-factor")]
    [InlineData("/account/two-factor/setup")]
    public async Task Without_signing_in_the_pages_send_the_browser_to_the_login_page(string path)
    {
        using var response = await _browser.GetAsync(new Uri(path, UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.ShouldStartWith("/account/login?ReturnUrl=");
    }

    [Fact]
    public async Task The_setup_page_shows_the_key_as_a_QR_code_and_as_text()
    {
        await SignInToKale();

        using var page = await _browser.GetAsync(SetupPage);

        var html = await page.Content.ReadAsStringAsync();
        var key = await KeyOn(page);
        key.Length.ShouldBeGreaterThanOrEqualTo(16);
        html.ShouldContain("<svg");
        html.ShouldContain($"otpauth://totp/Kale:deniz?secret={key}&amp;issuer=Kale&amp;digits=6");
        html.ShouldNotContain("<script", Case.Insensitive, "the pages work on isolated networks, without scripts");
    }

    [Fact]
    public async Task Opening_the_setup_page_again_shows_the_same_key_until_it_is_confirmed()
    {
        await SignInToKale();
        using var first = await _browser.GetAsync(SetupPage);
        using var second = await _browser.GetAsync(SetupPage);

        (await KeyOn(second)).ShouldBe(await KeyOn(first));
    }

    [Fact]
    public async Task A_code_from_the_app_turns_it_on_and_ten_recovery_codes_are_shown_once()
    {
        await SignInToKale();
        using var page = await _browser.GetAsync(SetupPage);

        using var done = await Submit(page, Totp.Code(await KeyOn(page)));

        done.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = RecoveryCodesOn(await done.Content.ReadAsStringAsync());
        codes.Count.ShouldBe(10);
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByIdAsync(_user.Id.ToString()))!;
        user.TwoFactorEnabled.ShouldBeTrue();
        (await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[0])).Succeeded.ShouldBeTrue();

        using var status = await _browser.GetAsync(StatusPage);
        RecoveryCodesOn(await status.Content.ReadAsStringAsync()).ShouldBeEmpty("recovery codes are shown only once");
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_it_stays_off()
    {
        await SignInToKale();
        using var page = await _browser.GetAsync(SetupPage);

        using var response = await Submit(page, "000000");

        (await response.Content.ReadAsStringAsync()).ShouldContain("The code is wrong.");
        (await TwoFactorEnabled()).ShouldBeFalse();
    }

    [Fact]
    public async Task This_browser_stays_signed_in_after_turning_it_on()
    {
        // Turning it on renews the user's security stamp, which signs out their other browsers.
        await SignInToKale();
        using var page = await _browser.GetAsync(SetupPage);
        using var _ = await Submit(page, Totp.Code(await KeyOn(page)));

        _app.Clock.Advance(TimeSpan.FromMinutes(31));
        using var status = await _browser.GetAsync(StatusPage);

        status.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Once_on_the_setup_page_goes_to_the_status_page_and_keeps_the_key()
    {
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        await SignInWithCode(key);

        using var response = await _browser.GetAsync(SetupPage);

        response.Headers.Location!.OriginalString.ShouldBe("/account/two-factor");
        (await CurrentKey()).ShouldBe(key);
    }

    [Fact]
    public async Task The_status_page_says_whether_it_is_on()
    {
        await SignInToKale();
        using (var off = await _browser.GetAsync(StatusPage))
        {
            var html = await off.Content.ReadAsStringAsync();
            html.ShouldContain("Two-step sign-in is off.");
            html.ShouldContain("href=\"/account/two-factor/setup\"");
        }

        using var page = await _browser.GetAsync(SetupPage);
        using var _ = await Submit(page, Totp.Code(await KeyOn(page)));
        using var on = await _browser.GetAsync(StatusPage);

        var onHtml = await on.Content.ReadAsStringAsync();
        onHtml.ShouldContain("Two-step sign-in is on.");
        onHtml.ShouldContain("10 recovery codes left");
    }

    [Fact]
    public async Task A_code_turns_it_off_and_the_old_key_is_gone()
    {
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        await SignInWithCode(key);
        using var page = await _browser.GetAsync(StatusPage);

        using var response = await Submit(page, Totp.Code(key));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await TwoFactorEnabled()).ShouldBeFalse();
        (await CurrentKey()).ShouldNotBe(key, "a later setup starts with a new key");
    }

    [Fact]
    public async Task A_recovery_code_turns_it_off_too()
    {
        // For a user who lost their phone: they signed in with a recovery code and set it up again.
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        await SignInWithCode(key);
        List<string> codes;
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(_user.Id.ToString()))!, 10))!.ToList();
        }
        using var page = await _browser.GetAsync(StatusPage);

        using var _ = await Submit(page, codes[0]);

        (await TwoFactorEnabled()).ShouldBeFalse();
    }

    [Fact]
    public async Task A_wrong_code_does_not_turn_it_off_and_counts_toward_the_lockout()
    {
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        await SignInWithCode(key);
        using var page = await _browser.GetAsync(StatusPage);

        using var response = await Submit(page, "000000");

        (await response.Content.ReadAsStringAsync()).ShouldContain("The code is wrong.");
        (await TwoFactorEnabled()).ShouldBeTrue();
        await using var db = Fresh();
        (await db.Users.SingleAsync(u => u.Id == _user.Id)).AccessFailedCount.ShouldBe(1);
    }

    [Fact]
    public async Task The_signed_in_page_links_to_it()
    {
        await SignInToKale();
        using var signedIn = await _browser.GetAsync(new Uri("/account/signed-in", UriKind.Relative));

        (await signedIn.Content.ReadAsStringAsync()).ShouldContain("href=\"/account/two-factor\"");
    }

    [Fact]
    public async Task The_setup_page_is_in_Turkish_by_default()
    {
        using var turkish = new Browser(_app);
        using var loginPage = await turkish.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var _ = await turkish.LogInAsync(loginPage, "deniz", Password);

        using var page = await turkish.GetAsync(SetupPage);

        (await page.Content.ReadAsStringAsync()).ShouldContain("İki adımlı giriş");
    }

    /// <summary>The authenticator key as the setup page shows it, without the spaces between its groups.</summary>
    internal static async Task<string> KeyOn(HttpResponseMessage setupPage)
    {
        var match = Regex.Match(await setupPage.Content.ReadAsStringAsync(), "id=\"authenticator-key\">([A-Z2-7 ]+)<");
        match.Success.ShouldBeTrue("the key on the setup page");
        return match.Groups[1].Value.Replace(" ", "", StringComparison.Ordinal);
    }

    /// <summary>Where the page after the recovery codes goes on (back to the sign-in, or the status page).</summary>
    internal static string ContinueLink(string html)
    {
        var match = Regex.Match(html, "id=\"continue\" href=\"([^\"]*)\"");
        match.Success.ShouldBeTrue("a continue link after the recovery codes");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private static List<string> RecoveryCodesOn(string html) =>
        Regex.Matches(html, "<li><code>([A-Z0-9]{5}-[A-Z0-9]{5})</code></li>").Select(m => m.Groups[1].Value).ToList();

    private async Task SignInToKale()
    {
        using var loginPage = await _browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var loggedIn = await _browser.LogInAsync(loginPage, "deniz", Password);
        loggedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    private async Task SignInWithCode(string key)
    {
        using var loginPage = await _browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var codePage = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));
        using var signedIn = await _browser.FollowAsync(await Submit(codePage, Totp.Code(key)));
        signedIn.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/signed-in");
    }

    private Task<HttpResponseMessage> Submit(HttpResponseMessage page, string code) =>
        _browser.SubmitAsync(page, new Dictionary<string, string> { ["Code"] = code });

    private async Task<bool> TwoFactorEnabled()
    {
        await using var db = Fresh();
        return (await db.Users.SingleAsync(u => u.Id == _user.Id)).TwoFactorEnabled;
    }

    private async Task<string?> CurrentKey()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(_user.Id.ToString()))!);
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(
        new Npgsql.NpgsqlConnectionStringBuilder(_app.ConnectionString).Database!, _app.Clock);
}
