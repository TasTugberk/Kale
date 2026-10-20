using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Users;

/// <summary>
/// Disables a user: they can't sign in, and their open sessions in every application stop now. The change and
/// the events are saved together. Stopping (not just refusing) the sessions means enabling the user again later
/// doesn't bring old sessions back.
/// </summary>
public sealed class UserDisabler(AuthDbContext db, SessionStopper sessions)
{
    public async Task DisableAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No user with id {userId}.");

        // Already disabled: their sessions were stopped back then, and none can have started since.
        if (!user.IsActive)
        {
            return;
        }

        user.IsActive = false;
        var openSessions = await db.Sessions.Where(s => s.UserId == userId && s.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var session in openSessions)
        {
            sessions.Stop(session);
        }

        db.OutboxMessages.Add(OutboxEvents.UserDisabled(userId));
        await db.SaveChangesAsync(cancellationToken);
    }
}
