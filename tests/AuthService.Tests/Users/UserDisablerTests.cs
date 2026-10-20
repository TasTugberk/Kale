using Auth.Contracts.V1;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using AuthService.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Users;

/// <summary>
/// Disabling a user stops their open sessions in every application, in the same save as the "session stopped"
/// and "user disabled" events. Enabling them again later doesn't bring the old sessions back.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class UserDisablerTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 6, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Start);
    private AuthDbContext _db = null!;
    private UserDisabler _disabler = null!;
    private User _user = null!;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _disabler = new UserDisabler(_db, new SessionStopper(_db, _clock));
        _user = await _db.AddUser("deniz");
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Disabling_a_user_stops_their_open_sessions_and_queues_the_events()
    {
        var billingSession = await AddSession("billing");
        var hrSession = await AddSession("hr");
        var endedSession = await AddSession("crm");
        endedSession.End(Start);
        await _db.SaveChangesAsync();
        _clock.Advance(TimeSpan.FromMinutes(5));

        await _disabler.DisableAsync(_user.Id);

        await using var fresh = Fresh();
        (await fresh.Users.SingleAsync()).IsActive.ShouldBeFalse();
        (await fresh.Sessions.SingleAsync(s => s.Id == billingSession.Id)).EndedAt.ShouldBe(Start.AddMinutes(5));
        (await fresh.Sessions.SingleAsync(s => s.Id == hrSession.Id)).EndedAt.ShouldBe(Start.AddMinutes(5));
        (await fresh.Sessions.SingleAsync(s => s.Id == endedSession.Id)).EndedAt.ShouldBe(Start, "already ended");

        var events = await QueuedEvents(fresh);
        events.Where(e => e.EventCase == InvalidationEvent.EventOneofCase.SessionStopped)
            .Select(e => e.SessionStopped.SessionId)
            .ShouldBe([billingSession.Id.ToString(), hrSession.Id.ToString()], ignoreOrder: true);
        events.Single(e => e.EventCase == InvalidationEvent.EventOneofCase.UserDisabled)
            .UserDisabled.UserId.ShouldBe(_user.Id.ToString());
    }

    [Fact]
    public async Task A_sign_in_still_in_progress_finishes_first_and_its_session_is_stopped_too()
    {
        var billing = await _db.AddApplication("billing");
        var member = await _db.AddRole(billing, "Member");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = billing.Id });
        await _db.SaveChangesAsync();

        // A sign-in holds a shared lock on the user from its IsActive check until its session is saved.
        await using var signIn = Fresh();
        await using var transaction = await signIn.Database.BeginTransactionAsync();
        await signIn.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {_user.Id} FOR SHARE");

        var disable = _disabler.DisableAsync(_user.Id);
        await Task.Delay(500);
        disable.IsCompleted.ShouldBeFalse("disabling must wait for the sign-in");

        var lateSession = new Session { UserId = _user.Id, ApplicationId = billing.Id, RoleId = member.Id, ExpiresAt = Start.AddHours(8) };
        signIn.Sessions.Add(lateSession);
        await signIn.SaveChangesAsync();
        await transaction.CommitAsync();
        await disable;

        await using var fresh = Fresh();
        (await fresh.Sessions.SingleAsync(s => s.Id == lateSession.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task Disabling_a_disabled_user_queues_nothing()
    {
        await _disabler.DisableAsync(_user.Id);

        await _disabler.DisableAsync(_user.Id);

        await using var fresh = Fresh();
        (await fresh.OutboxMessages.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task An_unknown_user_is_an_error()
    {
        await Should.ThrowAsync<InvalidOperationException>(() => _disabler.DisableAsync(Guid.CreateVersion7()));
    }

    // An open session of this user in a new application with one role.
    private async Task<Session> AddSession(string applicationKey)
    {
        var application = await _db.AddApplication(applicationKey);
        var role = await _db.AddRole(application, "Member");
        _db.UserApplications.Add(new UserApplication { UserId = _user.Id, ApplicationId = application.Id });
        var session = new Session { UserId = _user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = Start.AddHours(8) };
        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();
        return session;
    }

    private AuthDbContext Fresh() => postgres.CreateDbContext(_db.Database.GetDbConnection().Database, _clock);

    private static async Task<List<InvalidationEvent>> QueuedEvents(AuthDbContext db) =>
        (await db.OutboxMessages.ToListAsync()).Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload)).ToList();
}
