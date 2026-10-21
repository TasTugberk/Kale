using AuthService.Admin;
using AuthService.Outbox;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Users;

/// <summary>
/// Changes to user accounts. Disabling a user means they can't sign in, and their open sessions in every
/// application stop now; the change and the events are saved together. Stopping (not just refusing) the sessions
/// means enabling the user again later doesn't bring old sessions back.
/// </summary>
public sealed class UserAccounts(AuthDbContext db, SessionStopper sessions)
{
    /// <summary>Lets a disabled user sign in again. Their old sessions stay ended.</summary>
    public async Task EnableAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No user with id {userId}.");
        user.IsActive = true;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DisableAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // Lock the user row until commit. Sign-in takes a shared lock on it before checking IsActive, so a sign-in
        // in progress either finishes first (and its session is stopped below) or waits and then sees the user
        // disabled. Without it, a session saved just after this commit would stay open.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No user with id {userId}.");

        // Already disabled: their sessions were stopped back then, and the lock above keeps new ones from starting.
        if (!user.IsActive)
        {
            return;
        }

        if (await LastAdminCheck.IsAdminAsync(db, userId, cancellationToken))
        {
            await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: userId, cancellationToken);
        }

        user.IsActive = false;
        var openSessions = await db.Sessions.Where(s => s.UserId == userId && s.EndedAt == null).ToListAsync(cancellationToken);
        foreach (var session in openSessions)
        {
            sessions.Stop(session);
        }

        db.OutboxMessages.Add(OutboxEvents.UserDisabled(userId));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
