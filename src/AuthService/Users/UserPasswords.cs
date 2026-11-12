using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Users;

/// <summary>
/// Sets a user's password (an admin's reset). Whoever knew the old password may hold sessions made with it, so
/// the user's open sessions in every application stop, in the same save as the new password and their events.
/// </summary>
public sealed class UserPasswords(AuthDbContext db, UserManager<User> users, SessionStopper sessions, TimeProvider clock)
{
    /// <summary>Throws a <see cref="ValidationException"/> with every reason when Identity refuses the password.</summary>
    public async Task SetAsync(Guid userId, string password, CancellationToken cancellationToken = default)
    {
        // The same lock as UserAccounts.DisableAsync. Sign-in takes a shared lock on the user, so a sign-in in
        // progress either finishes first (and its session is stopped below) or waits until this is saved.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);

        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new InvalidOperationException($"No user with id {userId}.");

        // The caller may have loaded this user earlier in the same DbContext (e.g. to check the current password),
        // and EF then hands back that copy unchanged. Identity's save below writes every column, so read the row as
        // it is now, under the lock: otherwise a change made in between (e.g. a disable) would be undone.
        await db.Entry(user).ReloadAsync(cancellationToken);
        await CheckAsync(user, password);

        user.PasswordHash = users.PasswordHasher.HashPassword(user, password);

        // A new password starts afresh: a lockout from guesses at the old one (often the reason for a reset) ends.
        user.AccessFailedCount = 0;
        user.LockoutEnd = null;

        var openSessions = await db.Sessions.OpenAt(clock.GetUtcNow()).Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        foreach (var session in openSessions)
        {
            sessions.Stop(session);
        }

        // Identity renews the security stamp, so the sign-in cookies issued with the old password stop working at
        // their next check, and saves. Its store uses this same DbContext, so that one save also holds the password,
        // the stopped sessions and their events.
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded)
        {
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Every reason at once, like UserCreator, so an admin can fix the password in one go.
    private async Task CheckAsync(User user, string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ValidationException("Give a password.");
        }

        var errors = new List<string>();
        foreach (var validator in users.PasswordValidators)
        {
            errors.AddRange((await validator.ValidateAsync(users, user, password)).Errors.Select(e => e.Description));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(" ", errors));
        }
    }
}
