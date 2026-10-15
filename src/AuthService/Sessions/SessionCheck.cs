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

        var userAccessAndApplicationActive = await db.UserApplications
            .Where(access => access.UserId == session.UserId
                && access.ApplicationId == session.ApplicationId
                && access.IsActive)
            .AnyAsync(access =>
                db.Users.Any(user => user.Id == access.UserId && user.IsActive) &&
                db.Applications.Any(application => application.Id == access.ApplicationId && application.IsActive),
                cancellationToken);

        return userAccessAndApplicationActive
            && await roles.HoldsRoleAsync(session.UserId, session.RoleId, cancellationToken);
    }
}
