using System.Net;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Sign-in attempts (passwords on the login page, codes on the two-step page) are limited per client address, so
/// one address can't guess passwords across many accounts. A refused attempt shows the page again with a message,
/// and checks nothing: it doesn't count toward the account's lockout either.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SignInAttemptLimitTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string Password = "Correct-horse-9";

    private AuthServiceApp _app = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres,
            ("SignIn:AttemptsPerAddress", "3"), ("SignIn:AttemptWindow", "00:10:00"));
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ApplicationService>()
                .CreateAsync(new NewApplication("billing", "Billing", [Browser.RedirectUri("billing")], []));
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            _user = new User { UserName = "deniz", DisplayName = "Deniz" };
            (await users.CreateAsync(_user, Password)).Succeeded.ShouldBeTrue();
        }
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task Past_the_limit_the_login_page_says_so_and_checks_nothing()
    {
        using var browser = new Browser(_app, acceptLanguage: "en");
        using var loginPage = await OpenLoginPage(browser);
        using var first = await browser.LogInAsync(loginPage, "someone", "wrong-password-1");
        using var second = await browser.LogInAsync(loginPage, "someone-else", "wrong-password-2");
        using var third = await browser.LogInAsync(loginPage, "deniz", "wrong-password-3");

        // The right password, but from an address that used up its attempts.
        using var fourth = await browser.LogInAsync(loginPage, "deniz", Password);

        third.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await third.Content.ReadAsStringAsync()).ShouldContain("Wrong user name or password.");
        fourth.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        fourth.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldBe(TimeSpan.FromMinutes(10), "the window (SignIn:AttemptWindow)");
        fourth.Headers.Location.ShouldBeNull("no sign-in");
        var html = await fourth.Content.ReadAsStringAsync();
        html.ShouldContain("Too many sign-in attempts");
        html.ShouldContain("name=\"Password\"", customMessage: "the normal login page, not a bare error");
        (await FailedAttempts()).ShouldBe(1, "only the third attempt reached the account");
    }

    [Fact]
    public async Task The_message_is_in_turkish_by_default()
    {
        using var browser = new Browser(_app);
        using var loginPage = await OpenLoginPage(browser);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var _ = await browser.LogInAsync(loginPage, "someone", "wrong-password");
        }

        using var limited = await browser.LogInAsync(loginPage, "someone", "wrong-password");

        (await limited.Content.ReadAsStringAsync()).ShouldContain("Çok fazla giriş denemesi");
    }

    [Fact]
    public async Task Two_step_codes_count_toward_the_same_limit()
    {
        var key = await TwoFactorSignInTests.TurnOnTwoFactor(_app, _user);
        using var browser = new Browser(_app, acceptLanguage: "en");
        using var loginPage = await OpenLoginPage(browser);
        using var codePage = await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));
        using var _ = await browser.SubmitAsync(codePage, new Dictionary<string, string> { ["Code"] = "000000" });
        using var __ = await browser.SubmitAsync(codePage, new Dictionary<string, string> { ["Code"] = "000001" });

        using var limited = await browser.SubmitAsync(codePage, new Dictionary<string, string> { ["Code"] = Totp.Code(key) });

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.Location.ShouldBeNull("no sign-in");
        (await limited.Content.ReadAsStringAsync()).ShouldContain("Too many sign-in attempts");
    }

    [Fact]
    public async Task Opening_the_login_page_is_not_an_attempt()
    {
        using var browser = new Browser(_app, acceptLanguage: "en");
        for (var visit = 0; visit < 5; visit++)
        {
            using var _ = await OpenLoginPage(browser);
        }

        using var loginPage = await OpenLoginPage(browser);
        using var response = await browser.FollowAsync(await browser.LogInAsync(loginPage, "deniz", Password));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.RequestMessage!.RequestUri!.AbsolutePath.ShouldBe("/account/signed-in");
    }

    private static async Task<HttpResponseMessage> OpenLoginPage(Browser browser) =>
        await browser.GetAsync(new Uri("/account/login", UriKind.Relative));

    private async Task<int> FailedAttempts()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        return await db.Users.Where(u => u.Id == _user.Id).Select(u => u.AccessFailedCount).SingleAsync();
    }
}
