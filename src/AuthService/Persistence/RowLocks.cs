using Microsoft.EntityFrameworkCore;

namespace AuthService.Persistence;

/// <summary>
/// Every row lock AuthService takes explicitly (<c>SELECT 1 … FOR …</c>), in one place. Each lock is held until
/// the caller's transaction commits or rolls back, so call these inside a transaction, at its start. Why a caller
/// needs its lock is commented at the call; who takes what is in docs/design/locking.md.
/// <para>
/// The lock order, which keeps them from deadlocking. When a transaction takes several, it takes them:
/// </para>
/// <list type="number">
/// <item>groups, then roles, then users, then user_applications. Sign-in locks its user before the application's
/// roles, but only FOR SHARE, and nothing that locks roles locks users. Inserts that reference a role after the
/// users were locked take only FOR KEY SHARE on it (through the foreign key), which is harmless for the same
/// reason. The applications row: ApplicationService takes it (FOR UPDATE) before anything else, sign-in (FOR SHARE)
/// after the user and before the roles;</item>
/// <item>several rows of one table in id order (<c>ORDER BY id</c>);</item>
/// <item>advisory locks (<see cref="AdvisoryLocks"/>) after the row locks.</item>
/// </list>
/// Inserts take locks too: a new row's foreign keys take FOR KEY SHARE on the rows it references.
/// </summary>
public static class RowLocks
{
    public static async Task LockGroupForUpdateAsync(AuthDbContext db, Guid groupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM groups WHERE id = {groupId} FOR UPDATE", cancellationToken);

    public static async Task LockGroupForShareAsync(AuthDbContext db, Guid groupId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM groups WHERE id = {groupId} FOR SHARE", cancellationToken);

    public static async Task LockRoleForUpdateAsync(AuthDbContext db, Guid roleId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM roles WHERE id = {roleId} FOR UPDATE", cancellationToken);

    /// <summary>All roles of the application, in id order.</summary>
    public static async Task LockApplicationRolesForKeyShareAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM roles WHERE application_id = {applicationId} ORDER BY id FOR KEY SHARE", cancellationToken);

    public static async Task LockUserForShareAsync(AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR SHARE", cancellationToken);

    public static async Task LockUserForUpdateAsync(AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = {userId} FOR UPDATE", cancellationToken);

    /// <summary>Several users, in id order.</summary>
    public static async Task LockUsersForUpdateAsync(AuthDbContext db, IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.ToArray();
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM users WHERE id = ANY({ids}) ORDER BY id FOR UPDATE", cancellationToken);
    }

    /// <summary>The user's access row (<c>user_applications</c>) for the application.</summary>
    public static async Task LockAccessRowForUpdateAsync(AuthDbContext db, Guid userId, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT 1 FROM user_applications WHERE user_id = {userId} AND application_id = {applicationId} FOR UPDATE",
            cancellationToken);

    public static async Task LockApplicationForUpdateAsync(AuthDbContext db, Guid applicationId, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE id = {applicationId} FOR UPDATE", cancellationToken);

    /// <summary>The application with this key (the same row as by its id).</summary>
    public static async Task LockApplicationForUpdateAsync(AuthDbContext db, string applicationKey, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE key = {applicationKey} FOR UPDATE", cancellationToken);

    /// <summary>The application with this key, FOR SHARE (sign-in, against a concurrent deactivation).</summary>
    public static async Task LockApplicationForShareAsync(AuthDbContext db, string applicationKey, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM applications WHERE key = {applicationKey} FOR SHARE", cancellationToken);
}
