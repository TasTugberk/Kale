using AuthService.Admin;
using AuthService.Domain;
using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Users;

/// <summary>
/// Decision: a user who gains a role of an application (directly, or through a group) and has no access row for
/// it gets an active one in the same change, so an administrator doesn't have to give access separately. An
/// existing row is never changed: access that an administrator turned off stays off. Taking a role away leaves
/// access alone.
/// </summary>
public static class AccessWithRole
{
    /// <summary>The application whose access a role needs.</summary>
    public static async Task<Guid> ApplicationOfRoleAsync(AuthDbContext db, Guid roleId, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken)
            ?? throw new NotFoundException($"No role with id {roleId}.");
        return role.ApplicationId;
    }

    /// <summary>
    /// Adds (doesn't save) an active access row for each pair of user and application that has none. The caller
    /// holds the users' row locks, the same lock <see cref="UserAccess"/> takes before it creates access, so two
    /// changes can't both find the row missing.
    /// </summary>
    public static async Task AddMissingAsync(
        AuthDbContext db, IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<Guid> applicationIds, CancellationToken cancellationToken)
    {
        var existing = await db.UserApplications
            .Where(a => userIds.Contains(a.UserId) && applicationIds.Contains(a.ApplicationId))
            .Select(a => new { a.UserId, a.ApplicationId })
            .ToListAsync(cancellationToken);
        var existingPairs = existing.Select(a => (a.UserId, a.ApplicationId)).ToHashSet();

        foreach (var userId in userIds)
        {
            foreach (var applicationId in applicationIds)
            {
                if (!existingPairs.Contains((userId, applicationId)))
                {
                    db.UserApplications.Add(new UserApplication { UserId = userId, ApplicationId = applicationId, IsActive = true });
                }
            }
        }
    }
}
