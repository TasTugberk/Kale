using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using AuthService.Sessions;
using AuthService.SignIn;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.SignIn;

/// <summary>
/// Signing in: one open session per user and application (a new sign-in ends the previous one, so its tokens
/// stop working), and no session after the user was disabled at the same moment (it would come back to life if
/// the user were enabled again).
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserSignInTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthDbContext _db = null!;
    private UserSignIn _signIn = null!;
    private User _user = null!;
    private Application _billing = null!;
    private Role _member = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext();
        await _db.Database.MigrateAsync();
        _signIn = NewSignIn(_db);

        _billing = await _db.AddApplication("billing");
        _member = await _db.AddRole(_billing, "Member");
        _user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = _billing.Id });
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = _member.Id });
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task A_sign_in_waits_for_a_disable_in_progress_and_is_then_denied()
    {
        // An admin's DisableAsync holds the user's row lock until it commits.
        await using var admin = postgres.CreateDbContext(_db.Database.GetDbConnection().Database);
        await using var transaction = await admin.Database.BeginTransactionAsync();
        await admin.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {_user.Id} FOR UPDATE");
        await admin.Database.ExecuteSqlAsync($"UPDATE users SET is_active = false WHERE id = {_user.Id}");

        var signIn = _signIn.StartAsync(_user.Id, "billing", chosenRoleId: null);
        await Task.Delay(500);
        signIn.IsCompleted.ShouldBeFalse("the sign-in must wait for the disable");

        await transaction.CommitAsync();

        (await signIn).ShouldBeOfType<SignInOutcome.Denied>();
        (await _db.Sessions.CountAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task A_new_sign_in_ends_the_users_open_session_in_that_application_and_tells_client_apps()
    {
        var first = await SignInAsync();

        var second = await SignInAsync();

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == first.Id)).EndedAt.ShouldNotBeNull("the earlier sign-in's session ends");
        (await fresh.Sessions.SingleAsync(s => s.Id == second.Id)).EndedAt.ShouldBeNull();
        (await QueuedEvents(fresh)).Select(e => e.SessionStopped.SessionId).ShouldBe([first.Id.ToString()]);
    }

    [Fact]
    public async Task Sessions_in_other_applications_and_already_finished_ones_are_left_alone()
    {
        var hr = await _db.AddApplication("hr");
        var hrMember = await _db.AddRole(hr, "Member");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = hr.Id });
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = hrMember.Id });
        await _db.SaveChangesAsync();
        var hrSession = await SignInAsync("hr");
        // Whole seconds: the database keeps microseconds, so the end time reads back exactly.
        var fiveMinutesAgo = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 300);
        var ended = await AddSession(expiresAt: DateTimeOffset.UtcNow.AddHours(4), endedAt: fiveMinutesAgo);
        var expired = await AddSession(expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        await SignInAsync();

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == hrSession.Id)).EndedAt.ShouldBeNull("another application's session stays");
        (await fresh.Sessions.SingleAsync(s => s.Id == ended.Id)).EndedAt.ShouldBe(ended.EndedAt, "ending again keeps the first end");
        (await fresh.Sessions.SingleAsync(s => s.Id == expired.Id)).EndedAt.ShouldBeNull("an expired session isn't stopped again");
        (await QueuedEvents(fresh)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Asking_which_role_to_use_ends_nothing_yet()
    {
        var reader = await _db.AddRole(_billing, "Reader");
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = reader.Id });
        await _db.SaveChangesAsync();
        var first = await SignInAsync(chosenRoleId: _member.Id);

        var outcome = await _signIn.StartAsync(_user.Id, "billing", chosenRoleId: null);

        outcome.ShouldBeOfType<SignInOutcome.RoleChoiceRequired>();
        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == first.Id)).EndedAt.ShouldBeNull("no new session yet, so the old one stays");
    }

    [Fact]
    public async Task Two_sign_ins_at_once_take_turns_and_leave_one_open_session()
    {
        // Another sign-in of the same user to the same application, still in progress: it holds the access row.
        await using var other = Fresh();
        await using var transaction = await other.Database.BeginTransactionAsync();
        await other.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM user_applications WHERE user_id = {_user.Id} AND application_id = {_billing.Id} FOR UPDATE");

        var signIn = _signIn.StartAsync(_user.Id, "billing", chosenRoleId: null);
        await Task.Delay(500);
        signIn.IsCompleted.ShouldBeFalse("a sign-in must wait for another sign-in of the same user to the same application");

        var otherSession = new Session { UserId = _user.Id, ApplicationId = _billing.Id, RoleId = _member.Id, ExpiresAt = DateTimeOffset.UtcNow.AddHours(8) };
        other.Sessions.Add(otherSession);
        await other.SaveChangesAsync();
        await transaction.CommitAsync();
        await signIn;

        await using var fresh = Fresh();
        (await fresh.Sessions.CountAsync(s => s.EndedAt == null)).ShouldBe(1);
        (await fresh.Sessions.SingleAsync(s => s.Id == otherSession.Id)).EndedAt.ShouldNotBeNull("the later sign-in ends the earlier one's session");
    }

    [Fact]
    public async Task A_role_switch_and_a_delete_of_the_earlier_role_take_turns_instead_of_deadlocking()
    {
        // Signed in with Member (also the default role); now switching to Reader while an admin deletes Member.
        var reader = await _db.AddRole(_billing, "Reader");
        _db.UserRoles.Add(new UserRole { UserId = _user.Id, RoleId = reader.Id });
        (await _db.UserApplications.SingleAsync()).DefaultRoleId = _member.Id;
        await _db.SaveChangesAsync();
        var earlier = await SignInAsync(chosenRoleId: _member.Id);

        // RoleEditor.DeleteAsync's steps, in its order: lock the role, remove its sessions, then clear it as a
        // default role on the access rows.
        await using var admin = Fresh();
        await using var transaction = await admin.Database.BeginTransactionAsync();
        await admin.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {_member.Id} FOR UPDATE");
        await admin.Database.ExecuteSqlAsync($"DELETE FROM sessions WHERE role_id = {_member.Id}");

        await using var switching = Fresh();
        var signIn = NewSignIn(switching).StartAsync(_user.Id, "billing", chosenRoleId: reader.Id);
        await Task.Delay(500);

        // Before the fix, the sign-in held the access row here and waited for the session above: a deadlock.
        await admin.Database.ExecuteSqlAsync($"UPDATE user_applications SET default_role_id = NULL WHERE default_role_id = {_member.Id}");
        await transaction.CommitAsync();

        (await signIn).ShouldBeOfType<SignInOutcome.SessionStarted>().Session.RoleId.ShouldBe(reader.Id);
        await using var fresh = Fresh();
        (await fresh.Sessions.AnyAsync(s => s.Id == earlier.Id)).ShouldBeFalse("the delete removed the earlier session");
        (await fresh.Sessions.CountAsync(s => s.EndedAt == null)).ShouldBe(1);
    }

    private static UserSignIn NewSignIn(AuthDbContext db) =>
        new(db, new EligibleRoles(db), new SessionSettings(TimeSpan.FromHours(8)), new SessionStopper(db, TimeProvider.System), TimeProvider.System);

    private async Task<Session> SignInAsync(string applicationKey = "billing", Guid? chosenRoleId = null)
    {
        // A fresh context per sign-in, like a request each.
        await using var db = Fresh();
        var outcome = await NewSignIn(db).StartAsync(_user.Id, applicationKey, chosenRoleId);
        return outcome.ShouldBeOfType<SignInOutcome.SessionStarted>().Session;
    }

    private async Task<Session> AddSession(DateTimeOffset expiresAt, DateTimeOffset? endedAt = null)
    {
        await using var db = postgres.CreateDbContext(_db.Database.GetDbConnection().Database,
            new FakeTimeProvider(DateTimeOffset.UtcNow.AddHours(-1)));
        var session = new Session { UserId = _user.Id, ApplicationId = _billing.Id, RoleId = _member.Id, ExpiresAt = expiresAt };
        if (endedAt is { } ended)
        {
            session.End(ended);
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(_db.Database.GetDbConnection().Database);

    private static async Task<List<InvalidationEvent>> QueuedEvents(AuthDbContext db) =>
        (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload)).ToList();
}
