using System.ComponentModel.DataAnnotations;
using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Users;

/// <summary>
/// Two-step sign-in with an authenticator app: the user turns it on and off on Kale's own pages, an admin resets it
/// (a lost phone). Identity keeps the key and the recovery codes (user_tokens) and the TwoFactorEnabled flag; each
/// change here takes several of Identity's saves, so it runs in one transaction with the user row locked, like
/// <see cref="UserPasswords"/>.
/// </summary>
public sealed class UserTwoFactor(AuthDbContext db, UserManager<User> users, SessionStopper sessions, TimeProvider clock)
{
    public const int RecoveryCodeCount = 10;

    /// <summary>
    /// Turns it on when <paramref name="code"/> is right for the key the setup page showed. Returns the new recovery
    /// codes (shown once), or null for a wrong code. Throws a <see cref="ValidationException"/> when it's already on. Identity renews the security stamp, which signs out the user's
    /// other browsers: the caller refreshes this browser's cookie.
    /// </summary>
    public async Task<IReadOnlyList<string>?> TurnOnAsync(User user, string code, CancellationToken cancellationToken = default)
    {
        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, code))
        {
            return null;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(user, cancellationToken);

        // Two setup forms sent at once: the second must not replace the recovery codes the first one showed.
        if (user.TwoFactorEnabled)
        {
            throw new ValidationException("Two-step sign-in is already on.");
        }

        Check(await users.SetTwoFactorEnabledAsync(user, true));
        var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, RecoveryCodeCount)
            ?? throw new InvalidOperationException("Identity made no recovery codes.");
        await transaction.CommitAsync(cancellationToken);
        return [.. recoveryCodes];
    }

    /// <summary>
    /// The user turns it off themselves (the page has checked a code). Their sessions stay: they proved the second
    /// factor just now. The caller refreshes this browser's cookie (the security stamp changes).
    /// </summary>
    public async Task TurnOffAsync(User user, CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync(user, cancellationToken);
        await ClearAsync(user);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// An admin's reset, for a user who lost their authenticator app and their recovery codes: it's off, with a new
    /// key nobody has seen, no recovery codes and a new security stamp. Whoever got in with the old factor may hold
    /// sessions, so the user's open sessions in every application stop, like after a new password. They can set it
    /// up again on their account page (with SignIn:RequireTwoFactor, right after their next sign-in).
    /// </summary>
    public async Task ResetAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        // The same lock as UserPasswords.SetAsync: a sign-in in progress finishes first (and its session is stopped
        // below) or waits until this is saved.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await RowLocks.LockUserForUpdateAsync(db, userId, cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, cancellationToken)
            ?? throw new NotFoundException($"No user with id {userId}.");

        // As in UserPasswords: Identity's saves write every column, so start from the row as it is under the lock.
        await db.Entry(user).ReloadAsync(cancellationToken);

        var openSessions = await db.Sessions.OpenAt(clock.GetUtcNow()).Where(s => s.UserId == userId).ToListAsync(cancellationToken);
        foreach (var session in openSessions)
        {
            sessions.Stop(session);
        }

        // Identity's first save below (same DbContext) also writes the stopped sessions and their events.
        await ClearAsync(user);
        await transaction.CommitAsync(cancellationToken);
    }

    // Each of these saves, and each renews the security stamp except the recovery codes.
    private async Task ClearAsync(User user)
    {
        Check(await users.SetTwoFactorEnabledAsync(user, false));

        // A new key nobody has seen: the old one in the user's app stops working, and a later setup starts afresh.
        Check(await users.ResetAuthenticatorKeyAsync(user));

        // Zero new codes replace the old ones (Identity has no call that only removes them).
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
    }

    private async Task LockAsync(User user, CancellationToken cancellationToken)
    {
        await RowLocks.LockUserForUpdateAsync(db, user.Id, cancellationToken);
        await db.Entry(user).ReloadAsync(cancellationToken);
    }

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
