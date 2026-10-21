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

    /// <summary>
    /// Throws unless some active user, with active access to "auth", holds auth.Admin through a role other than
    /// <paramref name="excludedRoleId"/> (directly or through a group).
    /// </summary>
    public static async Task EnsureAnotherAdminRemainsAsync(AuthDbContext db, Guid excludedRoleId, CancellationToken cancellationToken)
    {
        // AuthOperations has no implications, so holding auth.Admin means a role that was granted it.
        var otherAdminRoles = db.RoleOperations
            .Where(ro => ro.RoleId != excludedRoleId)
            .Where(ro => db.Operations.Any(o => o.Id == ro.OperationId && o.Name == AdminOperation && o.ObsoletedAt == null))
            .Select(ro => ro.RoleId);
        var authApplication = db.Applications.Where(a => a.Key == AuthApplication.Key).Select(a => a.Id);

        var anotherAdminRemains = await db.Users
            .Where(u => u.IsActive)
            .Where(u => db.UserApplications.Any(a => a.UserId == u.Id && a.IsActive && authApplication.Contains(a.ApplicationId)))
            .Where(u => db.UserRoles.Any(ur => ur.UserId == u.Id && otherAdminRoles.Contains(ur.RoleId))
                || db.UserGroups.Any(ug => ug.UserId == u.Id
                    && db.GroupRoles.Any(gr => gr.GroupId == ug.GroupId && otherAdminRoles.Contains(gr.RoleId))))
            .AnyAsync(cancellationToken);

        if (!anotherAdminRemains)
        {
            throw new ValidationException(
                $"That would leave nobody who can administer Kale: no other active user would hold {AdminOperation}.");
        }
    }
}
