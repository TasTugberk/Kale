using AuthService.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace AuthService.Tests.Persistence;

[Collection(UsesPostgres.Name)]
public sealed class TimestampTests(PostgresFixture postgres)
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task New_entity_gets_created_and_modified_time_from_the_clock()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();

        var billing = new Application { Key = "billing", Name = "Billing" };
        db.Applications.Add(billing);
        await db.SaveChangesAsync();

        billing.CreatedAt.ShouldBe(Start);
        billing.ModifiedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task Changing_an_entity_updates_only_the_modified_time()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();
        var billing = new Application { Key = "billing", Name = "Billing" };
        db.Applications.Add(billing);
        await db.SaveChangesAsync();

        clock.Advance(TimeSpan.FromHours(1));
        billing.Name = "Invoicing";
        await db.SaveChangesAsync();

        billing.CreatedAt.ShouldBe(Start);
        billing.ModifiedAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public async Task Timestamps_are_stored_in_the_database()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();
        db.Applications.Add(new Application { Key = "billing", Name = "Billing" });
        await db.SaveChangesAsync();

        // Forget the tracked entity so the next query really reads from PostgreSQL.
        db.ChangeTracker.Clear();
        var stored = await db.Applications.SingleAsync();

        stored.CreatedAt.ShouldBe(Start);
        stored.ModifiedAt.ShouldBe(Start);
    }
}
