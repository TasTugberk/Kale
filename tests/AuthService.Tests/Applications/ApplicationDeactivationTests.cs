using Auth.Contracts.V1;
using AuthService.Applications;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Tests.Hosting;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AuthService.Tests.Applications;

/// <summary>
/// Deactivating an application stops its open sessions, in the same save as the "session stopped" events, like
/// disabling a user: reactivating it later doesn't bring the old sessions (and their refresh tokens) back.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class ApplicationDeactivationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private AuthServiceApp _app = null!;
    private AsyncServiceScope _scope;
    private ApplicationService _applications = null!;
    private AuthDbContext _db = null!;
    private Application _billing = null!;
    private Role _member = null!;

    public async Task InitializeAsync()
    {
        _app = await AuthServiceApp.CreateAsync(postgres);
        _scope = _app.Services.CreateAsyncScope();
        _applications = _scope.ServiceProvider.GetRequiredService<ApplicationService>();
        _db = _scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        _billing = await _db.AddApplication("billing");
        _member = await _db.AddRole(_billing, "Member");
    }

    public async Task DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _app.DisposeAsync();
    }

    [Fact]
    public async Task Deactivating_an_application_stops_its_open_sessions_and_queues_the_events()
    {
        var open = await AddSessionAsync("deniz", _billing, _member);
        var ended = await AddSessionAsync("ayse", _billing, _member);
        ended.End(_app.Clock.GetUtcNow());
        var hr = await _db.AddApplication("hr");
        var hrSession = await AddSessionAsync("deniz-hr", hr, await _db.AddRole(hr, "Member"));
        await _db.SaveChangesAsync();
        var endedAt = ended.EndedAt;
        _app.Clock.Advance(TimeSpan.FromMinutes(5));

        await _applications.SetActiveAsync(_billing.Id, isActive: false);

        await using var fresh = Fresh();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Sessions.SingleAsync(s => s.Id == open.Id)).EndedAt.ShouldBe(_app.Clock.GetUtcNow());
        (await db.Sessions.SingleAsync(s => s.Id == ended.Id)).EndedAt.ShouldBe(endedAt, "already ended");
        (await db.Sessions.SingleAsync(s => s.Id == hrSession.Id)).EndedAt.ShouldBeNull("other applications' sessions stay");
        (await StoppedSessionIds(db)).ShouldBe([open.Id.ToString()]);
    }

    [Fact]
    public async Task Reactivating_an_application_doesnt_bring_its_old_sessions_back()
    {
        var session = await AddSessionAsync("deniz", _billing, _member);
        await _db.SaveChangesAsync();
        await _applications.SetActiveAsync(_billing.Id, isActive: false);

        await _applications.SetActiveAsync(_billing.Id, isActive: true);

        await using var fresh = Fresh();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Applications.SingleAsync(a => a.Id == _billing.Id)).IsActive.ShouldBeTrue();
        (await db.Sessions.SingleAsync(s => s.Id == session.Id)).EndedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_sign_in_still_in_progress_finishes_first_and_its_session_is_stopped_too()
    {
        var user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = _billing.Id });
        await _db.SaveChangesAsync();

        // A sign-in holds a shared lock on the application from its IsActive check until its session is saved.
        await using var signInScope = Fresh();
        var signIn = signInScope.ServiceProvider.GetRequiredService<AuthDbContext>();
        await using var transaction = await signIn.Database.BeginTransactionAsync();
        await signIn.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE id = {_billing.Id} FOR SHARE");

        var deactivate = _applications.SetActiveAsync(_billing.Id, isActive: false);
        await Task.Delay(500);
        deactivate.IsCompleted.ShouldBeFalse("deactivating must wait for the sign-in");

        var lateSession = new Session
        {
            UserId = user.Id, ApplicationId = _billing.Id, RoleId = _member.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
        };
        signIn.Sessions.Add(lateSession);
        await signIn.SaveChangesAsync();
        await transaction.CommitAsync();
        await deactivate;

        await using var fresh = Fresh();
        var db = fresh.ServiceProvider.GetRequiredService<AuthDbContext>();
        (await db.Sessions.SingleAsync(s => s.Id == lateSession.Id)).EndedAt.ShouldNotBeNull();
    }

    // An open session (not saved yet) of a new user with access to the application.
    private async Task<Session> AddSessionAsync(string userName, Application application, Role role)
    {
        var user = await _db.AddUser(userName);
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = application.Id });
        var session = new Session
        {
            UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = _app.Clock.GetUtcNow().AddHours(8),
        };
        _db.Sessions.Add(session);
        return session;
    }

    private AsyncServiceScope Fresh() => _app.Services.CreateAsyncScope();

    private static async Task<List<string>> StoppedSessionIds(AuthDbContext db) =>
        (await db.OutboxMessages.ToListAsync())
            .Select(m => InvalidationEvent.Parser.ParseFrom(m.Payload))
            .Where(e => e.EventCase == InvalidationEvent.EventOneofCase.SessionStopped)
            .Select(e => e.SessionStopped.SessionId)
            .ToList();
}
