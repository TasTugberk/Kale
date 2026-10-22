using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Sessions;

/// <summary>
/// Finished sessions (ended or expired) are kept for a while, then deleted, so the table doesn't grow forever.
/// Open sessions are never touched.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class SessionCleanupTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);

    private readonly FakeTimeProvider _clock = new(Now);
    private AuthDbContext _db = null!;
    private SessionCleanup _cleanup = null!;
    private Guid _userId;
    private Guid _applicationId;
    private Guid _roleId;

    public async Task InitializeAsync()
    {
        _db = postgres.CreateDbContext(_clock);
        await _db.Database.MigrateAsync();
        _cleanup = new SessionCleanup(_db);

        var billing = await _db.AddApplication("billing");
        var member = await _db.AddRole(billing, "Member");
        var user = await _db.AddUser("deniz");
        _db.UserApplications.Add(new UserApplication { UserId = user.Id, ApplicationId = billing.Id });
        await _db.SaveChangesAsync();
        (_userId, _applicationId, _roleId) = (user.Id, billing.Id, member.Id);
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    [Fact]
    public async Task Deletes_sessions_finished_longer_ago_than_the_retention_and_keeps_the_rest()
    {
        var endedLongAgo = await AddSession(expiresAt: Now.AddDays(-7.5), endedAt: Now.AddDays(-8));
        var endedRecently = await AddSession(expiresAt: Now.AddDays(-5.5), endedAt: Now.AddDays(-6));
        var expiredLongAgo = await AddSession(expiresAt: Now.AddDays(-8));
        var expiredRecently = await AddSession(expiresAt: Now.AddDays(-6));
        var open = await AddSession(expiresAt: Now.AddHours(4));
        // Ended long ago, though its expiry is recent: ending is what finished it.
        var endedEarly = await AddSession(expiresAt: Now.AddDays(-6), endedAt: Now.AddDays(-8));

        var deleted = await _cleanup.DeleteFinishedBeforeAsync(Now - KeepFor);

        deleted.ShouldBe(3);
        var left = await _db.Sessions.Select(s => s.Id).ToListAsync();
        left.ShouldBe([endedRecently.Id, expiredRecently.Id, open.Id], ignoreOrder: true);
        left.ShouldNotContain(endedLongAgo.Id);
        left.ShouldNotContain(expiredLongAgo.Id);
        left.ShouldNotContain(endedEarly.Id);
    }

    [Fact]
    public async Task With_nothing_to_delete_it_deletes_nothing()
    {
        await AddSession(expiresAt: Now.AddHours(4));

        (await _cleanup.DeleteFinishedBeforeAsync(Now - KeepFor)).ShouldBe(0);
    }

    // Saved through a context whose clock is an hour before the session's earliest time: the database requires
    // a session to be created before it expires (CreatedAt comes from the context's clock).
    private async Task<Session> AddSession(DateTimeOffset expiresAt, DateTimeOffset? endedAt = null)
    {
        var createdAt = (endedAt is { } end && end < expiresAt ? end : expiresAt).AddHours(-1);
        await using var db = postgres.CreateDbContext(_db.Database.GetDbConnection().Database, new FakeTimeProvider(createdAt));
        var session = new Session { UserId = _userId, ApplicationId = _applicationId, RoleId = _roleId, ExpiresAt = expiresAt };
        if (endedAt is { } ended)
        {
            session.End(ended);
        }

        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}
