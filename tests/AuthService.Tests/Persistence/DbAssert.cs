using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AuthService.Tests.Persistence;

internal static class DbAssert
{
    /// <summary>
    /// Saving must fail with this PostgreSQL error code and constraint name, so we know the intended rule fired.
    /// </summary>
    public static async Task ShouldViolate(DbContext db, string sqlState, string constraintName)
    {
        var error = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());

        var postgresError = error.InnerException.ShouldBeOfType<PostgresException>();
        postgresError.SqlState.ShouldBe(sqlState);
        postgresError.ConstraintName.ShouldBe(constraintName);
    }
}
