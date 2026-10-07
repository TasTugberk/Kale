using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.Persistence;

[Collection(UsesPostgres.Name)]
public sealed class MigrationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Migrations_apply_to_an_empty_database()
    {
        await using var db = postgres.CreateDbContext();

        await db.Database.MigrateAsync();

        (await db.Database.GetPendingMigrationsAsync()).ShouldBeEmpty();
        (await db.Database.CanConnectAsync()).ShouldBeTrue();
    }

    [Fact]
    public async Task Model_has_no_changes_missing_from_migrations()
    {
        await using var db = postgres.CreateDbContext();

        db.Database.HasPendingModelChanges().ShouldBeFalse(
            "the EF model changed without a migration; run `dotnet ef migrations add <Name>`");
    }
}
