using System.ComponentModel.DataAnnotations;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// Nobody may take administration away from everyone: `admin bootstrap` runs only once, so the only way back
/// would be editing the database by hand. Changes that could remove the last admin ask this first.
/// </summary>
public static class LastAdminCheck
{
    public const string AdminOperation = $"{AuthApplication.Key}.{nameof(AuthOperations.Admin)}";

    // Our own number for this advisory lock, distinct from the one operation registration uses (1001).
    private const int LockNamespace = 1002;

    /// <summary>
    /// Throws unless some active user, with active access to "auth", still holds auth.Admin (directly or through
    /// a group) without <paramref name="excludedRoleId"/> and other than <paramref name="excludedUserId"/>: the
    /// role or user about to lose it.
    /// </summary>
    public static async Task EnsureAnotherAdminRemainsAsync(
        AuthDbContext db, Guid? excludedRoleId, Guid? excludedUserId, CancellationToken cancellationToken)
    {
        // Two such changes at once (two admins disabling each other, two admin roles deleted together) could
        // both still see the other admin and both commit, leaving nobody. Each takes this lock first; it's held
        // until the caller's transaction ends, so they take turns and the second one counts after the first.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("The last-admin check must run inside the change's transaction.");
        }

        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockNamespace}, 0)", cancellationToken);

        var anotherAdminRemains = await Admins(db, AdminRoles(db, excludedRoleId))
            .AnyAsync(u => u.Id != excludedUserId, cancellationToken);
        if (!anotherAdminRemains)
        {
            throw new ValidationException(
                $"That would leave nobody who can administer Kale: no other active user would hold {AdminOperation}.");
        }
    }

    /// <summary>Whether the user is an admin right now (active, with active access, holding auth.Admin).</summary>
    public static Task<bool> IsAdminAsync(AuthDbContext db, Guid userId, CancellationToken cancellationToken) =>
        Admins(db, AdminRoles(db, excludedRoleId: null)).AnyAsync(u => u.Id == userId, cancellationToken);

    // The roles granted auth.Admin, except the excluded one. AuthOperations has no implications, so holding
    // auth.Admin means holding a role that was granted it.
    private static IQueryable<Guid> AdminRoles(AuthDbContext db, Guid? excludedRoleId) =>
        db.RoleOperations
            .Where(ro => ro.RoleId != excludedRoleId)
            .Where(ro => db.Operations.Any(o => o.Id == ro.OperationId && o.Name == AdminOperation && o.ObsoletedAt == null))
            .Select(ro => ro.RoleId);

    // Active users with active access to "auth" who hold one of those roles, directly or through a group.
    private static IQueryable<Domain.User> Admins(AuthDbContext db, IQueryable<Guid> adminRoles)
    {
        var authApplication = db.Applications.Where(a => a.Key == AuthApplication.Key).Select(a => a.Id);
        return db.Users
            .Where(u => u.IsActive)
            .Where(u => db.UserApplications.Any(a => a.UserId == u.Id && a.IsActive && authApplication.Contains(a.ApplicationId)))
            .Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && adminRoles.Contains(ur.RoleId))
                || db.UserGroups.Any(ug => ug.UserId == u.Id
                    && db.GroupRoles.Any(gr => gr.GroupId == ug.GroupId && adminRoles.Contains(gr.RoleId))));
    }
}
