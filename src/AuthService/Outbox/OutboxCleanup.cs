using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Outbox;

/// <summary>
/// Deletes old outbox rows (<see cref="AuthService.Sessions.SessionCleanupService"/> passes now minus
/// <see cref="KeepFor"/>), so the table doesn't grow forever.
/// </summary>
public sealed class OutboxCleanup(AuthDbContext db)
{
    /// <summary>
    /// How long a row is kept. Nothing reads a row after it's saved: the trigger's NOTIFY carries the event itself
    /// to every instance's streams, and a client that reconnects clears its caches instead of getting missed events
    /// replayed. A day leaves the recent events in the table for looking into a problem in SQL (and for the audit
    /// hooks, DESIGN.md), and is short enough that the table stays small.
    /// </summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(1);

    /// <returns>How many rows were deleted.</returns>
    public async Task<int> DeleteSavedBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default)
    {
        // One bulk DELETE, without loading the rows; nothing else deletes or updates them.
        return await db.OutboxMessages
            .Where(m => m.CreatedAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
