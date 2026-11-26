using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Persistence;

/// <summary>
/// The migration that makes "one open session per user and application" a database rule must apply to real data:
/// it ends what would break the unique index first.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class OneOpenSessionMigrationTests(PostgresFixture postgres)
{
    private const string MigrationBefore = "20261124180011_ApplicationOldSecrets";

    [Fact]
    public async Task Expired_sessions_end_at_their_expiry_and_only_the_newest_open_session_stays()
    {
        // Created two hours ago (the migration compares with the database's real now()).
        var twoHoursAgo = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200);
        await using var db = postgres.CreateDbContext(new FakeTimeProvider(twoHoursAgo));
        await db.GetService<IMigrator>().MigrateAsync(MigrationBefore);

        var billing = await db.AddApplication("billing");
        var member = await db.AddRole(billing, "Member");
        var deniz = await db.AddUser("deniz");
        var ayse = await db.AddUser("ayse");
        db.UserApplications.AddRange(
            new UserApplication { UserId = deniz.Id, ApplicationId = billing.Id },
            new UserApplication { UserId = ayse.Id, ApplicationId = billing.Id });
        await db.SaveChangesAsync();

        // Two open sessions of one user in one application with the same created_at (one clock), so the id decides
        // which is newer. Explicit ids a second apart: two made in the same millisecond would be in random order.
        // And another user's session that expired an hour ago without ending.
        var older = NewSession(deniz, billing, member, twoHoursAgo.AddHours(8), Guid.CreateVersion7(twoHoursAgo));
        var newer = NewSession(deniz, billing, member, twoHoursAgo.AddHours(8), Guid.CreateVersion7(twoHoursAgo.AddSeconds(1)));
        var expiredAt = twoHoursAgo.AddHours(1);
        var expired = NewSession(ayse, billing, member, expiredAt);
        db.Sessions.AddRange(older, newer, expired);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await db.Database.MigrateAsync();

        (await db.Sessions.SingleAsync(s => s.Id == older.Id)).EndedAt.ShouldNotBeNull("the older duplicate ends");
        (await db.Sessions.SingleAsync(s => s.Id == newer.Id)).EndedAt.ShouldBeNull("the newest stays open");
        (await db.Sessions.SingleAsync(s => s.Id == expired.Id)).EndedAt.ShouldBe(expiredAt);
    }

    private static Session NewSession(User user, Application application, Role role, DateTimeOffset expiresAt, Guid? id = null) =>
        new() { Id = id ?? Guid.CreateVersion7(), UserId = user.Id, ApplicationId = application.Id, RoleId = role.Id, ExpiresAt = expiresAt };
}
