using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Tests.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Outbox;

/// <summary>
/// Nothing deletes an outbox row once its event went out (the trigger's NOTIFY carries the event itself), so old
/// rows are deleted after a short while, or the table would grow forever.
/// </summary>
[Collection(UsesPostgres.Name)]
public sealed class OutboxCleanupTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Now = new(2026, 6, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Deletes_events_saved_before_the_cutoff_and_keeps_the_rest()
    {
        var clock = new FakeTimeProvider(Now.AddDays(-2));
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();
        await AddEvent(db);
        clock.SetUtcNow(Now.AddHours(-1));
        var recent = await AddEvent(db);

        var deleted = await new OutboxCleanup(db).DeleteSavedBeforeAsync(Now - OutboxCleanup.KeepFor);

        deleted.ShouldBe(1);
        db.ChangeTracker.Clear();
        (await db.OutboxMessages.Select(m => m.Id).ToListAsync()).ShouldBe([recent]);
    }

    private static async Task<Guid> AddEvent(AuthDbContext db)
    {
        var message = OutboxEvents.UserDisabled(Guid.CreateVersion7());
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync();
        return message.Id;
    }
}
