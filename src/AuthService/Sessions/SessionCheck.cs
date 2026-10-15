using AuthService.Domain;
using AuthService.Persistence;
using AuthService.Roles;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Sessions;

/// <summary>
/// Whether a session is ACTIVE, by the full definition in sessions.proto: not ended, not expired, and the user,
/// the user's access to the application and the application all active, and the user still holds the role.
/// Used by GetSession and by token refresh, so a change anywhere (admin stops it, disables the user, takes the
/// role away...) takes effect at the next check.
/// </summary>
public sealed class SessionCheck(AuthDbContext db, EligibleRoles roles, TimeProvider clock)
{
    public async Task<bool> IsActiveAsync(Session session, CancellationToken cancellationToken = default)
    {
        if (!session.IsActiveAt(clock.GetUtcNow()))
        {
            return false;
        }

        // One query: the user's access row for this application, with the user, the application and the role
        // all still valid.
        var heldRoleIds = roles.HeldRoleIds(session.UserId);
        return await (
                from access in db.UserApplications
                join user in db.Users on access.UserId equals user.Id
                join application in db.Applications on access.ApplicationId equals application.Id
                where access.UserId == session.UserId
                    && access.ApplicationId == session.ApplicationId
                    && access.IsActive
                    && user.IsActive
                    && application.IsActive
                    && heldRoleIds.Contains(session.RoleId)
                select access)
            .AnyAsync(cancellationToken);
    }
}
