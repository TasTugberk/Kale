using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

/// <summary>
/// Every row lock AuthService takes explicitly (<c>SELECT 1 … FOR …</c>), in one place. Each is held until the
/// caller's transaction ends, so call these inside one, at its start. Why each caller needs its lock is commented
/// at the call; who takes what is in docs/design/locking.md. When a transaction takes several, the order is:
/// <list type="number">
/// <item>groups, then roles, then users, then user_applications (sign-in takes its user before roles, but only
/// FOR SHARE, which nothing that locks roles conflicts with);</item>
/// <item>several rows of one table in id order (<c>ORDER BY id</c>);</item>
/// <item>advisory locks (<see cref="AdvisoryLocks"/>) after the row locks.</item>
/// </list>
/// Inserts take locks too: a new row's foreign keys take FOR KEY SHARE on the rows it references.
/// </summary>
public static class RowLocks
{
    /// <summary>
    /// The group, exclusively: for a change to its roles or members that others must not overlap (members
    /// joining wait, or are among the members read after this).
    /// </summary>
    public static async Task LockGroupForChangeAsync(AuthDbContext db, Guid groupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM groups WHERE id = {groupId} FOR UPDATE", cancellationToken);

    /// <summary>The group, shared: members join side by side, but not during a change to the group.</summary>
    public static async Task LockGroupForNewMemberAsync(AuthDbContext db, Guid groupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM groups WHERE id = {groupId} FOR SHARE", cancellationToken);

    /// <summary>The role, exclusively: for editing or deleting it.</summary>
    public static async Task LockRoleForChangeAsync(AuthDbContext db, Guid roleId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {roleId} FOR UPDATE", cancellationToken);

    /// <summary>
    /// All roles of the application, FOR KEY SHARE, in id order: sign-ins don't block each other, only a role
    /// edit or delete waits.
    /// </summary>
    public static async Task LockApplicationRolesForSignInAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM roles WHERE application_id = {applicationId} ORDER BY id FOR KEY SHARE", cancellationToken);

    /// <summary>The user, shared: a sign-in, so that changes to the user wait for it (and it for them).</summary>
    public static async Task LockUserForSignInAsync(AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR SHARE", cancellationToken);

    /// <summary>The user, exclusively: a change to what they may do takes turns with their sign-ins.</summary>
    public static async Task LockUserForChangeAsync(AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);

    /// <summary>Several users, exclusively, in id order (see <see cref="LockUserForChangeAsync"/>).</summary>
    public static async Task LockUsersForChangeAsync(AuthDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", cancellationToken);
    }

    /// <summary>The user's access row for the application, exclusively: two sign-ins of the user take turns.</summary>
    public static async Task LockAccessForSignInAsync(AuthDbContext db, Guid userId, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM user_applications WHERE user_id = {userId} AND application_id = {applicationId} FOR UPDATE",
            cancellationToken);

    /// <summary>The application, exclusively: edits of it (and of its OpenIddict client) take turns.</summary>
    public static async Task LockApplicationForChangeAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE id = {applicationId} FOR UPDATE", cancellationToken);

    /// <summary>The application with this key, exclusively (see <see cref="LockApplicationForChangeAsync"/>).</summary>
    public static async Task LockApplicationForChangeAsync(AuthDbContext db, string applicationKey, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE key = {applicationKey} FOR UPDATE", cancellationToken);
}
