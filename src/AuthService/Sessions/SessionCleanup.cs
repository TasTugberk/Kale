using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Sessions;

/// <summary>How long a finished (ended or expired) session is kept before it's deleted (Sessions:KeepFinishedFor).</summary>
public sealed record SessionCleanupSettings(TimeSpan KeepFinishedFor)
{
    public static readonly TimeSpan Default = TimeSpan.FromDays(7);
}

/// <summary>
/// Deletes sessions that finished longer ago than <see cref="SessionCleanupSettings.KeepFinishedFor"/>, so the
/// table doesn't grow forever. Nothing reads a finished session (only open ones are checked or listed) and
/// nothing references one, so they can go once they're past any use for looking into a recent problem.
/// </summary>
public sealed class SessionCleanup(AuthDbContext db, TimeProvider clock, SessionCleanupSettings settings)
{
    /// <returns>How many sessions were deleted.</returns>
    public async Task<int> DeleteFinishedAsync(CancellationToken cancellationToken = default)
    {
        var cutoff = clock.GetUtcNow() - settings.KeepFinishedFor;

        // A session finished when it ended or when it expired, whichever came first. One bulk DELETE, without
        // loading the rows. An admin change that loaded one of these rows at the same instant (e.g. deleting its
        // role) fails its save and rolls back; trying again works. Rare enough (sessions finished a week ago,
        // once an hour) not to need a lock.
        return await db.Sessions
            .Where(s => (s.EndedAt != null && s.EndedAt < cutoff) || s.ExpiresAt < cutoff)
            .ExecuteDeleteAsync(cancellationToken);
    }
}
