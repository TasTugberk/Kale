using System.ComponentModel.DataAnnotations;
using System.Net;
using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using AuthService.Tests.SignIn;
using AuthService.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Users;

/// <summary>
/// Setting a new password (an admin's reset): Identity's password rules, a fresh lockout and security stamp, and
/// the user's open sessions in every application stop in the same save, so a stolen session doesn't outlive it.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserPasswordsTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string OldPassword = "Correct-horse-9";
    private const string NewPassword = "Battery-staple-7";

    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private UserPasswords _passwords = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _scope = _app.Services.CreateAsyncScope();
        _passwords = _scope.ServiceProvider.GetRequiredService<UserPasswords>();
        _user = await _scope.ServiceProvider.GetRequiredService<UserCreator>().CreateAsync("deniz", "Deniz", OldPassword);
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task The_new_password_replaces_the_old_one()
    {
        await _passwords.SetAsync(_user.Id, NewPassword);

        var (oldWorks, newWorks) = await CheckPasswords();
        oldWorks.ShouldBeFalse();
        newWorks.ShouldBeTrue();
    }

    [Fact]
    public async Task The_users_open_sessions_stop_in_every_application_with_their_events()
    {
        var billing = await AddSession(_user, "billing");
        var hr = await AddSession(_user, "hr");
        var someoneElse = await AddSession(await AddUser("ayse"), "crm");

        await _passwords.SetAsync(_user.Id, NewPassword);

        await using var db = Fresh();
        (await db.Sessions.SingleAsync(s => s.Id == billing.Id)).EndedAt.ShouldBe(_app.Clock.GetUtcNow());
        (await db.Sessions.SingleAsync(s => s.Id == hr.Id)).EndedAt.ShouldBe(_app.Clock.GetUtcNow());
        (await db.Sessions.SingleAsync(s => s.Id == someoneElse.Id)).EndedAt.ShouldBeNull("another user's session");
        (await QueuedEvents(db)).Select(e => e.SessionStopped.SessionId)
            .ShouldBe([billing.Id.ToString(), hr.Id.ToString()], ignoreOrder: true);
    }

    [Fact]
    public async Task A_weak_password_is_refused_with_every_reason_and_nothing_changes()
    {
        var session = await AddSession(_user, "billing");

        var error = await Should.ThrowAsync<ValidationException>(() => _passwords.SetAsync(_user.Id, "short"));

        error.Message.ShouldContain("12");
        error.Message.ShouldContain("digit");
        (await CheckPasswords()).OldWorks.ShouldBeTrue();
        await using var db = Fresh();
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldBeNull();
        (await db.OutboxMessages.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task An_empty_password_is_refused()
    {
        var error = await Should.ThrowAsync<ValidationException>(() => _passwords.SetAsync(_user.Id, ""));

        error.Message.ShouldContain("password");
    }

    [Fact]
    public async Task A_locked_out_user_can_sign_in_again_with_the_new_password()
    {
        var users = _scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await users.AccessFailedAsync(_user);
        }

        (await users.IsLockedOutAsync(_user)).ShouldBeTrue();

        await _passwords.SetAsync(_user.Id, NewPassword);

        await using var db = Fresh();
        var stored = await db.Users.SingleAsync(u => u.Id == _user.Id);
        stored.LockoutEnd.ShouldBeNull();
        stored.AccessFailedCount.ShouldBe(0);
    }

    [Fact]
    public async Task The_security_stamp_is_renewed()
    {
        await using var db = Fresh();
        var before = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == _user.Id)).SecurityStamp;

        await _passwords.SetAsync(_user.Id, NewPassword);

        (await db.Users.AsNoTracking().SingleAsync(u => u.Id == _user.Id)).SecurityStamp.ShouldNotBe(before);
    }

    [Fact]
    public async Task A_browser_signed_in_to_Kale_with_the_old_password_is_signed_out_at_the_next_stamp_check()
    {
        using var browser = new Browser(_app);
        using var loginPage = await browser.GetAsync(new Uri("/account/login", UriKind.Relative));
        (await browser.LogInAsync(loginPage, "deniz", OldPassword)).StatusCode.ShouldBe(HttpStatusCode.Redirect);
        (await SignedInToKale(browser)).ShouldBeTrue();

        await _passwords.SetAsync(_user.Id, NewPassword);

        // Identity compares the cookie's security stamp with the user's every 30 minutes (its default).
        _app.Clock.Advance(TimeSpan.FromMinutes(31));
        (await SignedInToKale(browser)).ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_in_still_in_progress_finishes_first_and_its_session_is_stopped_too()
    {
        var billing = await AddAccess(_user, "billing");

        // A sign-in holds a shared lock on the user from its checks until its session is saved.
        await using var signIn = Fresh();
        await using var transaction = await signIn.Database.BeginTransactionAsync();
        await signIn.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {_user.Id} FOR SHARE");

        var reset = _passwords.SetAsync(_user.Id, NewPassword);
        await Task.Delay(500);
        reset.IsCompleted.ShouldBeFalse("the reset must wait for the sign-in");

        var lateSession = NewSession(_user, billing);
        signIn.Sessions.Add(lateSession);
        await signIn.SaveChangesAsync();
        await transaction.CommitAsync();
        await reset;

        await using var db = Fresh();
        (await db.Sessions.SingleAsync(s => s.Id == lateSession.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_copy_of_the_user_loaded_earlier_in_the_same_scope_is_not_written_back()
    {
        // The caller (e.g. a page checking the current password) loaded the user before; then an admin disabled them.
        var db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Users.SingleAsync(u => u.Id == _user.Id)).IsActive.ShouldBeTrue();
        await using (var admin = Fresh())
        {
            (await admin.Users.SingleAsync(u => u.Id == _user.Id)).IsActive = false;
            await admin.SaveChangesAsync();
        }

        await _passwords.SetAsync(_user.Id, NewPassword);

        await using var fresh = Fresh();
        (await fresh.Users.SingleAsync(u => u.Id == _user.Id)).IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task An_unknown_user_is_an_error()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _passwords.SetAsync(Guid.CreateVersion7(), NewPassword));
    }

    // The role picker needs the sign-in cookie: without it, it sends the browser to the login page; with it, a
    // missing sign-in request is a 400.
    private static async Task<bool> SignedInToKale(Browser browser)
    {
        using var response = await browser.GetAsync(new Uri("/account/select-role", UriKind.Relative));
        return response.StatusCode == HttpStatusCode.BadRequest;
    }

    private async Task<(bool OldWorks, bool NewWorks)> CheckPasswords()
    {
        await using var scope = _app.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByIdAsync(_user.Id.ToString()))!;
        return (await users.CheckPasswordAsync(user, OldPassword), await users.CheckPasswordAsync(user, NewPassword));
    }

    private async Task<User> AddUser(string userName)
    {
        await using var db = Fresh();
        return await db.AddUser(userName);
    }

    // The user's active access to a new application that has one role; returns that role.
    private async Task<Role> AddAccess(User user, string applicationKey)
    {
        await using var db = Fresh();
        var application = await db.AddApplication(applicationKey);
        var role = await db.AddRole(application, "Member");
        db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        await db.SaveChangesAsync();
        return role;
    }

    private async Task<Session> AddSession(User user, string applicationKey)
    {
        var role = await AddAccess(user, applicationKey);
        await using var db = Fresh();
        var session = NewSession(user, role);
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private Session NewSession(User user, Role role) => new()
    {
        UserId = user.Id, ApplicationId = role.ApplicationId, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
    };

    private AuthDbContext Fresh() => postgres.CreateDbContext(
        new Npgsql.NpgsqlConnectionStringBuilder(_app.ConnectionString).Database!, _app.Clock);

    private static async Task<List<InvalidationEvent>> QueuedEvents(AuthDbContext db) =>
        (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload)).ToList();
}
