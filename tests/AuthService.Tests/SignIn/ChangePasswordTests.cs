using System.Net;
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
/// A signed-in user changes their own password on /account/change-password: the current one is checked like a
/// login (lockout), the new one by Identity's rules; their application sessions stop, this browser stays signed in.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ChangePasswordTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string OldPassword = "Correct-horse-9";
    private const string NewPassword = "Battery-staple-7";
    private static readonly Uri Page = new("/account/change-password", UriKind.Relative);

    private AuthServiceApp _app = null!;
    private Browser _browser = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            _user = await scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync("deniz", "Deniz", OldPassword);
        }

        _browser = new Browser(_app, acceptLanguage: "en");
    }

    public async Task DisposeAsync()
    {
        _browser.Dispose();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Without_signing_in_the_page_sends_the_browser_to_the_login_page_and_back()
    {
        using var response = await _browser.GetAsync(Page);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.PathAndQuery.ShouldStartWith("/account/login?ReturnUrl=%2Faccount%2Fchange-password");
    }

    [Fact]
    public async Task The_new_password_replaces_the_old_one_and_the_application_sessions_stop()
    {
        await SignInToKale();
        var session = await AddSession();

        using var response = await Change(OldPassword, NewPassword, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.ShouldStartWith("/account/change-password?changed=");
        var (oldWorks, newWorks) = await CheckPasswords();
        oldWorks.ShouldBeFalse();
        newWorks.ShouldBeTrue();
        await using var db = Fresh();
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_page_says_the_password_was_changed_and_that_application_sessions_ended()
    {
        await SignInToKale();
        using var changed = await _browser.FollowAsync(await Change(OldPassword, NewPassword, NewPassword));

        var html = await changed.Content.ReadAsStringAsync();
        html.ShouldContain("Your password was changed");
        html.ShouldContain("sign in to your applications again");
        html.ShouldNotContain(NewPassword);
    }

    [Fact]
    public async Task This_browser_stays_signed_in_after_Identitys_next_stamp_check()
    {
        await SignInToKale();
        using var _ = await Change(OldPassword, NewPassword, NewPassword);

        // Identity compares the cookie's security stamp with the user's every 30 minutes; the page renewed the cookie.
        _app.Clock.Advance(TimeSpan.FromMinutes(31));
        using var response = await _browser.GetAsync(Page);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_wrong_current_password_is_refused_and_counts_toward_the_lockout()
    {
        await SignInToKale();
        var session = await AddSession();

        using var response = await Change("Wrong-password-1", NewPassword, NewPassword);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).ShouldContain("The current password is wrong.");
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
        await using var db = Fresh();
        (await db.Users.SingleAsync(u => u.Id == _user.Id)).AccessFailedCount.ShouldBe(1);
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBeNull();
    }

    [Fact]
    public async Task After_five_wrong_current_passwords_even_the_right_one_is_refused_for_a_while()
    {
        await SignInToKale();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var _ = await Change("Wrong-password-1", NewPassword, NewPassword);
        }

        using var response = await Change(OldPassword, NewPassword, NewPassword);

        (await response.Content.ReadAsStringAsync()).ShouldContain("Too many wrong attempts.");
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
    }

    [Fact]
    public async Task A_repeat_that_differs_is_refused_without_counting_as_a_wrong_password()
    {
        await SignInToKale();

        using var response = await Change(OldPassword, NewPassword, NewPassword + "x");

        (await response.Content.ReadAsStringAsync()).ShouldContain("The new passwords differ.");
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
        await using var db = Fresh();
        (await db.Users.SingleAsync(u => u.Id == _user.Id)).AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task A_weak_new_password_is_refused_with_the_rules_and_without_echoing_any_password()
    {
        await SignInToKale();

        using var response = await Change(OldPassword, "weakpassword", "weakpassword");

        var html = await response.Content.ReadAsStringAsync();
        html.ShouldContain("does not meet the rules");
        html.ShouldNotContain("weakpassword");
        html.ShouldNotContain(OldPassword);
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
    }

    [Fact]
    public async Task A_user_disabled_since_signing_in_is_signed_out_instead()
    {
        await SignInToKale();
        using var page = await _browser.GetAsync(Page);
        await using (var scope = _app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<UserAccounts>().DisableAsync(_user.Id);
        }

        using var response = await _browser.SubmitAsync(page, new Dictionary<string, string>
        {
            ["CurrentPassword"] = OldPassword, ["NewPassword"] = NewPassword, ["RepeatedPassword"] = NewPassword,
        });

        response.Headers.Location!.OriginalString.ShouldBe("/account/login");
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
        using var again = await _browser.GetAsync(Page);
        again.StatusCode.ShouldBe(HttpStatusCode.Redirect, "the sign-in cookie is gone");
    }

    [Fact]
    public async Task The_page_is_in_Turkish_by_default()
    {
        using var turkish = new Browser(_app);
        using var loginPage = await turkish.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var _ = await turkish.LogInAsync(loginPage, "deniz", OldPassword);

        using var page = await turkish.GetAsync(Page);

        (await page.Content.ReadAsStringAsync()).ShouldContain("Parolayı değiştir");
    }

    [Fact]
    public async Task A_form_posted_without_the_antiforgery_token_is_refused()
    {
        await SignInToKale();

        using var response = await _browser.PostFormAsync(Page, new Dictionary<string, string>
        {
            ["CurrentPassword"] = OldPassword, ["NewPassword"] = NewPassword, ["RepeatedPassword"] = NewPassword,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
    }

    [Fact]
    public async Task The_signed_in_page_links_to_it()
    {
        using var loginPage = await _browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var signedIn = await _browser.FollowAsync(await _browser.LogInAsync(loginPage, "deniz", OldPassword));

        (await signedIn.Content.ReadAsStringAsync()).ShouldContain("href=\"/account/change-password\"");
    }

    private async Task SignInToKale()
    {
        using var loginPage = await _browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        using var loggedIn = await _browser.LogInAsync(loginPage, "deniz", OldPassword);
        loggedIn.StatusCode.ShouldBe(HttpStatusCode.Redirect);
    }

    private async Task<HttpResponseMessage> Change(string current, string newPassword, string repeated)
    {
        using var page = await _browser.GetAsync(Page);
        page.StatusCode.ShouldBe(HttpStatusCode.OK);
        return await _browser.SubmitAsync(page, new Dictionary<string, string>
        {
            ["CurrentPassword"] = current, ["NewPassword"] = newPassword, ["RepeatedPassword"] = repeated,
        });
    }

    // An open session of the user in a new application with one role.
    private async Task<Session> AddSession()
    {
        await using var db = Fresh();
        var application = await db.AddApplication("billing");
        var role = await db.AddRole(application, "Member");
        db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = application.Id });
        var session = new Session
        {
            UserId = _user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private async Task<(bool OldWorks, bool NewWorks)> CheckPasswords()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByIdAsync(_user.Id.ToString()))!;
        return (await users.CheckPasswordAsync(user, OldPassword), await users.CheckPasswordAsync(user, NewPassword));
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(
        new Npgsql.NpgsqlConnectionStringBuilder(_app.ConnectionString).Database!, _app.Clock);
}
