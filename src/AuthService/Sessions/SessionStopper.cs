using AuthService.Domain;
using AuthService.Outbox;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Sessions;

/// <summary>
/// Ends sessions (an admin's stop, a user's sign-out, a disabled user) and queues a "session stopped" event in
/// the same save, so client apps refuse the session's still-valid token right away instead of after their
/// cache time.
/// </summary>
public sealed class SessionStopper(AuthDbContext db, TimeProvider clock)
{
    /// <summary>Stops the session. False when it doesn't exist. Stopping an ended session changes nothing.</summary>
    public async Task<bool> StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await db.Sessions.SingleOrDefaultAsync(s => s.Id == sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        if (Stop(session))
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Ends a loaded session and queues its event without saving, so the caller saves both together with its
    /// own change. False when the session had already ended.
    /// </summary>
    public bool Stop(Session session)
    {
        // Already ended means already announced: keep the first end time and don't send a second event.
        // Two stops at the very same moment can both get here and queue two events. That's harmless (clients
        // handle events at least once anyway), so no locking for it.
        if (session.EndedAt is not null)
        {
            return false;
        }

        session.End(clock.GetUtcNow());
        db.OutboxMessages.Add(OutboxEvents.SessionStopped(session.Id));
        return true;
    }
}
