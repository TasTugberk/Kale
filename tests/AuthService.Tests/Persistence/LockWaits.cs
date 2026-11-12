using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Tests.Persistence;

internal static class LockWaits
{
    /// <summary>
    /// Waits until some connection to <paramref name="db"/>'s database is blocked on a row lock, so a test knows
    /// the call it started has reached the statement that waits (a fixed delay can be too short on a busy machine).
    /// </summary>
    public static async Task UntilSomeoneWaitsAsync(AuthDbContext db)
    {
        var giveUpAt = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < giveUpAt)
        {
            // Inside a transaction PostgreSQL keeps showing the first snapshot of pg_stat_activity.
            await db.Database.ExecuteSqlAsync($"SELECT pg_stat_clear_snapshot()");
            var waiting = await db.Database
                .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("Nothing started waiting for a lock.");
    }
}
