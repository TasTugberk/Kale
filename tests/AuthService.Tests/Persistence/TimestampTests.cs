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

        db.ChangeTracker.Clear();
        var stored = await db.Applications.SingleAsync();
        stored.CreatedAt.ShouldBe(Start);
        stored.ModifiedAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public async Task Updating_a_detached_entity_keeps_its_created_time()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();
        var billing = new Application { Key = "billing", Name = "Billing" };
        db.Applications.Add(billing);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // A new object with the same id, as an API handler might build from a request. Its CreatedAt is
        // the default value, and Update() marks every column as changed.
        clock.Advance(TimeSpan.FromHours(1));
        db.Applications.Update(new Application { Id = billing.Id, Key = "billing", Name = "Invoicing" });
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var stored = await db.Applications.SingleAsync();
        stored.Name.ShouldBe("Invoicing");
        stored.CreatedAt.ShouldBe(Start);
        stored.ModifiedAt.ShouldBe(Start.AddHours(1));
    }

    [Fact]
    public async Task Saving_without_changes_keeps_the_modified_time()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();
        var billing = new Application { Key = "billing", Name = "Billing" };
        db.Applications.Add(billing);
        await db.SaveChangesAsync();

        clock.Advance(TimeSpan.FromHours(1));
        await db.SaveChangesAsync();

        billing.ModifiedAt.ShouldBe(Start);
    }

    [Fact]
    public async Task Synchronous_save_sets_timestamps_too()
    {
        var clock = new FakeTimeProvider(Start);
        await using var db = postgres.CreateDbContext(clock);
        await db.Database.MigrateAsync();

        var billing = new Application { Key = "billing", Name = "Billing" };
        db.Applications.Add(billing);
#pragma warning disable CA1849 // This test is specifically about the synchronous SaveChanges path.
        db.SaveChanges();
#pragma warning restore CA1849

        billing.CreatedAt.ShouldBe(Start);
        billing.ModifiedAt.ShouldBe(Start);
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
