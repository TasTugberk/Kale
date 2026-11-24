using System.ComponentModel.DataAnnotations;
using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.EntityFrameworkCore.Models;

namespace AuthService.Users;

/// <summary>
/// Corrects a user's names, and deletes a user with everything that hangs on them. Both lock the user row like
/// <see cref="UserAccounts"/>: sign-in takes a shared lock on it, so a sign-in in progress either finishes first
/// or waits and then sees the change.
/// </summary>
public sealed class UserEditor(AuthDbContext db, UserManager<User> users, SessionStopper sessions, TimeProvider clock)
{
    /// <summary>
    /// A new user name and display name, under the same rules as a new user (<see cref="UserCreator.NameProblemsAsync"/>),
    /// every problem in one <see cref="ValidationException"/>. The security stamp stays: a rename doesn't sign the
    /// user out of AuthService, and their sessions stay open: ID tokens carry the names, read at
    /// every code exchange and refresh, so the next refresh (at most 10 minutes) brings the new ones.
    /// </summary>
    public async Task UpdateAsync(Guid userId, string userName, string displayName, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RowLocks.LockUserForUpdateAsync(db, userId, cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"No user with id {userId}.");

        // The row as it is now, under the lock (the context may hold an older copy, see UserPasswords).
        await db.Entry(user).ReloadAsync(cancellationToken);
        user.UserName = userName;
        user.DisplayName = displayName?.Trim() ?? "";
        var errors = await UserCreator.NameProblemsAsync(users, user);
        if (errors.Count > 0)
        {
            throw new ValidationException(string.Join(" ", errors));
        }

        // Identity's save, not ours: it normalizes the name (what the login page looks up) and gives the user a new
        // concurrency stamp, so an Identity save of an older copy (e.g. a failed login counting up) fails instead
        // of writing the old name back. Unlike UserManager.SetUserNameAsync it keeps the security stamp.
        IdentityResult result;
        try
        {
            result = await users.UpdateAsync(user);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Taken by a user created a moment ago, after the check above: the same answer as if it had come later.
            throw new ValidationException($"Username '{userName}' is already taken.", exception);
        }

        if (!result.Succeeded)
        {
            throw new ValidationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Deletes the user, in one transaction: their open sessions stop ("session stopped" events, so client apps
    /// refuse their tokens at once), then their sessions, OpenIddict tokens and authorizations go, then the user,
    /// whose access rows, direct roles, group memberships and Identity rows the database deletes with them.
    /// Refused for the last admin, and for <paramref name="deletedBy"/> itself (an admin deleting their own
    /// account would end the session they're using; another admin can do it).
    /// </summary>
    public async Task DeleteAsync(Guid userId, Guid? deletedBy, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RowLocks.LockUserForUpdateAsync(db, userId, cancellationToken);
        if (!await db.Users.AnyAsync(u => u.Id == userId, cancellationToken))
        {
            throw new NotFoundException($"No user with id {userId}.");
        }

        if (userId == deletedBy)
        {
            throw new ValidationException("You can't delete your own account; another administrator can.");
        }

        if (await LastAdminCheck.IsAdminAsync(db, userId, cancellationToken))
        {
            await LastAdminCheck.EnsureAnotherAdminRemainsAsync(db, excludedRoleId: null, excludedUserId: userId, cancellationToken);
        }

        var open = await db.Sessions.OpenAt(clock.GetUtcNow()).Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        foreach (var session in open)
        {
            sessions.Stop(session);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Sessions restrict deleting the access rows that the user's delete cascades to (see SessionConfiguration):
        // they go first, now that the open ones are announced.
        await db.Sessions.Where(s => s.UserId == userId).ExecuteDeleteAsync(cancellationToken);

        // OpenIddict's rows name the user only as a subject (no foreign key), so the database can't cascade them.
        // Its codes and refresh tokens would be refused anyway (no user, no session); deleting them leaves nothing
        // of the user behind. Tokens first: they reference authorizations.
        var subject = userId.ToString();
        await db.Set<OpenIddictEntityFrameworkCoreToken<Guid>>()
            .Where(t => t.Subject == subject || t.Authorization!.Subject == subject)
            .ExecuteDeleteAsync(cancellationToken);
        await db.Set<OpenIddictEntityFrameworkCoreAuthorization<Guid>>()
            .Where(a => a.Subject == subject)
            .ExecuteDeleteAsync(cancellationToken);

        // A bulk delete, so the database's cascades remove the rest. Through the change tracker, EF would also
        // apply them to rows it happens to track (e.g. an access row a session still points to) and refuse.
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // Nothing tracked here exists anymore (the user, the stopped sessions).
        db.ChangeTracker.Clear();
    }
}
