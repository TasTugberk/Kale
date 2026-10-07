using AuthService.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Roles;

/// <summary>A role a user may sign in with.</summary>
public sealed record EligibleRole(Guid Id, string Name);

/// <summary>
/// Which roles a user holds: assigned directly (user_roles) or through any of their groups (group_roles).
/// Users and groups are global; roles belong to one application.
/// </summary>
public sealed class EligibleRoles(AuthDbContext db)
{
    /// <summary>
    /// The roles the user can choose when signing in to <paramref name="applicationId"/> (DESIGN.md sign-in
    /// step 3), sorted by name for display. Checking that the user may access the application at all is a
    /// separate step (an active UserApplication).
    /// </summary>
    public async Task<IReadOnlyList<EligibleRole>> ForUserAsync(
        Guid userId, Guid applicationId, CancellationToken cancellationToken = default)
    {
        var heldRoleIds = HeldRoleIds(userId);

        return await db.Roles
            .Where(r => r.ApplicationId == applicationId && heldRoleIds.Contains(r.Id))
            .OrderBy(r => r.Name)
            .Select(r => new EligibleRole(r.Id, r.Name))
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Whether the user still holds the role. The session check and token refresh use it, so a role taken
    /// away (directly, from the group, or by leaving the group) stops a session that uses it.
    /// Only about assignments: it ignores <c>User.IsActive</c> and access rows, which the session check
    /// tests separately.
    /// </summary>
    public Task<bool> HoldsRoleAsync(Guid userId, Guid roleId, CancellationToken cancellationToken = default) =>
        HeldRoleIds(userId).ContainsAsync(roleId, cancellationToken);

    // Not executed on its own: EF Core folds it into the calling query as one SQL statement.
    private IQueryable<Guid> HeldRoleIds(Guid userId)
    {
        var direct = db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Select(ur => ur.RoleId);

        var throughGroups =
            from membership in db.UserGroups
            join groupRole in db.GroupRoles on membership.GroupId equals groupRole.GroupId
            where membership.UserId == userId
            select groupRole.RoleId;

        return direct.Union(throughGroups);
    }
}
