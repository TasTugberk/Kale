using System.Net;
using System.Text.Json;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Signing in with a second factor: after the password, a user who turned it on enters a code from their
/// authenticator app or one of their recovery codes. Wrong codes count toward the lockout like wrong passwords.
/// With SignIn:RequireTwoFactor, a user without it sets it up before any application gets a code.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class TwoFactorSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";
    private const string CodePagePath = "/account/login-two-factor";

    private AuthServiceApp _app = null!;
    private CreatedApplication _billing = null!;
    private Browser _browser = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            _billing = await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
        }
        _user = await CreateUserWithAccess("deniz");
        _browser = new Browser(_app, acceptLanguage: "en");
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task After_the_password_a_user_with_two_factor_is_asked_for_a_code_and_no_app_gets_one_yet()
    {
        await TurnOnTwoFactor(_user);

        using var codePage = await LogInWithPassword(_browser);

        codePage.StatusCode.ShouldBe(HttpStatusCode.OK);
        codePage.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe(CodePagePath);
        (await codePage.Content.ReadAsStringAsync()).ShouldContain("authenticator app");

        // The password alone gave no sign-in cookie: starting over still shows the login page.
        using var again = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        again.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task The_code_from_the_authenticator_app_completes_the_sign_in()
    {
        var key = await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);

        using var redirect = await _browser.FollowAsync(await EnterCode(codePage, Totp.Code(key)));

        var (status, _) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_code_typed_with_spaces_works_too()
    {
        var key = await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);
        var code = Totp.Code(key);

        using var redirect = await _browser.FollowAsync(await EnterCode(codePage, $" {code[..3]} {code[3..]} "));

        Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_wrong_code_is_refused_and_counts_toward_the_lockout()
    {
        await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);

        using var response = await EnterCode(codePage, "000000");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("The code is wrong.");
        (await FailedAttempts()).ShouldBe(1);
    }

    [Fact]
    public async Task After_five_wrong_codes_even_the_right_one_is_refused_for_a_while()
    {
        var key = await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var _ = await EnterCode(codePage, "000000");
        }

        using var response = await EnterCode(codePage, Totp.Code(key));

        (await response.Content.ReadAsStringAsync()).ShouldContain("Too many wrong attempts.");
        response.Headers.Location.ShouldBeNull();
    }

    [Fact]
    public async Task The_right_password_does_not_wipe_out_earlier_wrong_codes()
    {
        // Otherwise someone who knows the password could guess codes forever, four at a time.
        await TurnOnTwoFactor(_user);
        using (var codePage = await LogInWithPassword(_browser))
        {
            for (var attempt = 0; attempt < 4; attempt++)
            {
                using var _ = await EnterCode(codePage, "000000");
            }
        }

        using var newCodePage = await LogInWithPassword(_browser);

        (await FailedAttempts()).ShouldBe(4);
    }

    [Fact]
    public async Task A_recovery_code_works_once()
    {
        await TurnOnTwoFactor(_user);
        var recoveryCode = (await NewRecoveryCodes(_user))[0];

        using (var codePage = await LogInWithPassword(_browser))
        using (var redirect = await _browser.FollowAsync(await EnterCode(codePage, recoveryCode)))
        {
            Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
        }

        using var otherBrowser = new Browser(_app, acceptLanguage: "en");
        using var secondCodePage = await LogInWithPassword(otherBrowser);
        using var second = await EnterCode(secondCodePage, recoveryCode, otherBrowser);
        (await second.Content.ReadAsStringAsync()).ShouldContain("The code is wrong.");
    }

    [Fact]
    public async Task A_recovery_code_is_accepted_in_lowercase()
    {
        await TurnOnTwoFactor(_user);
        var recoveryCode = (await NewRecoveryCodes(_user))[0];
        using var codePage = await LogInWithPassword(_browser);

        using var redirect = await _browser.FollowAsync(await EnterCode(codePage, recoveryCode.ToLowerInvariant()));

        Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task A_wrong_recovery_code_counts_toward_the_lockout()
    {
        await TurnOnTwoFactor(_user);
        await NewRecoveryCodes(_user);
        using var codePage = await LogInWithPassword(_browser);

        using var _ = await EnterCode(codePage, "AAAAA-AAAAA");

        (await FailedAttempts()).ShouldBe(1);
    }

    [Fact]
    public async Task The_code_page_waits_only_five_minutes_after_the_password()
    {
        var key = await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);
        _app.Clock.Advance(TimeSpan.FromMinutes(6));

        using var response = await _browser.FollowAsync(await EnterCode(codePage, Totp.Code(key)));

        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/login");
    }

    [Fact]
    public async Task Without_the_password_first_the_code_page_sends_the_browser_to_the_login_page()
    {
        using var response = await _browser.GetAsync(new Uri(CodePagePath, UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldBe("/account/login");
    }

    [Fact]
    public async Task The_code_page_is_in_Turkish_by_default()
    {
        await TurnOnTwoFactor(_user);
        using var turkish = new Browser(_app);

        using var codePage = await LogInWithPassword(turkish);

        (await codePage.Content.ReadAsStringAsync()).ShouldContain("Doğrulama kodu");
    }

    [Fact]
    public async Task The_ID_token_says_a_code_was_used_and_when_the_sign_in_was_completed()
    {
        var key = await TurnOnTwoFactor(_user);
        using var codePage = await LogInWithPassword(_browser);
        _app.Clock.Advance(TimeSpan.FromMinutes(2));
        var completedAt = _app.Clock.GetUtcNow().ToUnixTimeSeconds();

        using var redirect = await _browser.FollowAsync(await EnterCode(codePage, Totp.Code(key)));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));

        var idToken = new JsonWebToken(body.RootElement.GetProperty("id_token").GetString());
        idToken.GetPayloadValue<long>("auth_time").ShouldBe(completedAt);
        AuthenticationMethods(idToken).ShouldBe(["pwd", "otp"]);
        new JsonWebToken(body.RootElement.GetProperty("access_token").GetString()).TryGetClaim("amr", out _)
            .ShouldBeFalse("DESIGN.md: nothing but sid, user, expiry and audience in the access token");
    }

    [Fact]
    public async Task Without_two_factor_the_ID_token_says_only_a_password_was_used_even_after_a_refresh()
    {
        using var loginPage = await _browser.FollowAsync(await _browser.GetAsync(_browser.AuthorizeUri("billing")));
        using var redirect = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", Password));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        var (_, refreshed) = await _browser.RefreshAsync(
            "billing", _billing.ClientSecret, body.RootElement.GetProperty("refresh_token").GetString()!);

        foreach (var response in new[] { body, refreshed })
        {
            var idToken = new JsonWebToken(response.RootElement.GetProperty("id_token").GetString());
            AuthenticationMethods(idToken).ShouldBe(["pwd"]);
        }
    }

    [Fact]
    public async Task The_code_is_still_counted_after_the_security_stamp_check_renewed_the_cookie()
    {
        // Identity's stamp check rebuilds the cookie's claims every 30 minutes; how the user signed in must stay.
        var key = await TurnOnTwoFactor(_user);
        using (var codePage = await LogInWithPassword(_browser))
        using (var _ = await EnterCode(codePage, Totp.Code(key)))
        {
        }
        _app.Clock.Advance(TimeSpan.FromMinutes(31));

        using var renewed = await _browser.GetAsync(_browser.AuthorizeUri("billing"));
        var (_, body) = await _browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(renewed));

        AuthenticationMethods(new JsonWebToken(body.RootElement.GetProperty("id_token").GetString())).ShouldBe(["pwd", "otp"]);
    }

    [Fact]
    public async Task When_required_a_user_without_two_factor_sets_it_up_before_the_app_gets_a_code()
    {
        await using var app = new AuthServiceApp(_app.ConnectionString, ("SignIn:RequireTwoFactor", "true"));
        using var browser = new Browser(app, acceptLanguage: "en");
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri("billing")));

        using var setupPage = await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));

        setupPage.StatusCode.ShouldBe(HttpStatusCode.OK);
        setupPage.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/two-factor/setup");
        var key = await TwoFactorSetupTests.KeyOn(setupPage);
        using var done = await browser.SubmitAsync(setupPage, new Dictionary<string, string> { ["Code"] = Totp.Code(key) });
        var continueLink = TwoFactorSetupTests.ContinueLink(await done.Content.ReadAsStringAsync());
        continueLink.ShouldStartWith("/connect/authorize?");

        using var redirect = await browser.FollowAsync(await browser.GetAsync(new Uri(continueLink, UriKind.Relative)));
        var (status, _) = await browser.ExchangeCodeAsync("billing", _billing.ClientSecret, Browser.CodeFrom(redirect));
        status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task When_required_a_silent_sign_in_without_two_factor_is_refused_as_needing_the_user()
    {
        await using var app = new AuthServiceApp(_app.ConnectionString, ("SignIn:RequireTwoFactor", "true"));
        using var browser = new Browser(app, acceptLanguage: "en");
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri("billing")));
        using var _ = await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));

        using var response = await browser.GetAsync(browser.AuthorizeUri("billing", "&prompt=none"));

        Browser.QueryValue(response, "error").ShouldBe("interaction_required");
        Browser.QueryValue(response, "code").ShouldBeNull();
    }

    [Fact]
    public async Task When_required_a_user_who_has_two_factor_signs_in_as_usual()
    {
        var key = await TurnOnTwoFactor(_user);
        await using var app = new AuthServiceApp(_app.ConnectionString, ("SignIn:RequireTwoFactor", "true"));
        using var browser = new Browser(app, acceptLanguage: "en");

        using var codePage = await LogInWithPassword(browser);
        using var redirect = await browser.FollowAsync(await EnterCode(codePage, Totp.Code(key), browser));

        Browser.CodeFrom(redirect).ShouldNotBeNullOrEmpty();
    }

    // Starts sign-in to billing and enters the password: the code page, for a user with two-factor sign-in.
    private static async Task<HttpResponseMessage> LogInWithPassword(Browser browser)
    {
        using var loginPage = await browser.FollowAsync(await browser.GetAsync(browser.AuthorizeUri("billing")));
        return await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));
    }

    private Task<HttpResponseMessage> EnterCode(HttpResponseMessage codePage, string code, Browser? browser = null) =>
        (browser ?? _browser).SubmitAsync(codePage, new Dictionary<string, string> { ["Code"] = code });

    private static string[] AuthenticationMethods(JsonWebToken idToken) =>
        idToken.GetPayloadValue<JsonElement>("amr").EnumerateArray().Select(method => method.GetString()!).ToArray();

    private async Task<User> CreateUserWithAccess(string userName)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var user = new User { UserName = userName, DisplayName = userName };
        (await users.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();

        var billing = await db.Applications.SingleAsync(a => a.Key == "billing");
        var role = await db.AddRole(billing, "Accountant");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Turns two-factor sign-in on as the setup page does; returns the authenticator key.</summary>
    internal static async Task<string> TurnOnTwoFactor(AuthServiceApp app, User user)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var current = (await users.FindByIdAsync(user.Id.ToString()))!;
        (await users.ResetAuthenticatorKeyAsync(current)).Succeeded.ShouldBeTrue();
        (await users.SetTwoFactorEnabledAsync(current, true)).Succeeded.ShouldBeTrue();
        return (await users.GetAuthenticatorKeyAsync(current))!;
    }

    private Task<string> TurnOnTwoFactor(User user) => TurnOnTwoFactor(_app, user);

    private async Task<List<string>> NewRecoveryCodes(User user)
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        return (await users.GenerateNewTwoFactorRecoveryCodesAsync((await users.FindByIdAsync(user.Id.ToString()))!, 10))!.ToList();
    }

    private async Task<int> FailedAttempts()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.Where(u => u.Id == _user.Id).Select(u => u.AccessFailedCount).SingleAsync();
    }
}
