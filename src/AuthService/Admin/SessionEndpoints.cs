using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// Admin API: who is signed in where, and stopping a session. DESIGN.md: admins must be able to stop a session
/// even while its token is valid; <see cref="SessionStopper"/> also tells client apps (event).
/// </summary>
public static class SessionEndpoints
{
    public static void MapSessionEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/sessions", ListOpenAsync);
        api.MapDelete("/sessions/{id:guid}", StopAsync);
    }

    /// <summary>
    /// Sessions that haven't ended or expired, newest first, optionally for one application or user. "Open" is
    /// only the session's own state: one can still be refused by the full ACTIVE check (SessionCheck), e.g. when
    /// its application was deactivated or the user lost the role.
    /// </summary>
    private static async Task<IResult> ListOpenAsync(
        Guid? applicationId, Guid? userId, AuthDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var open = db.Sessions.AsNoTracking().Where(s => s.EndedAt == null && s.ExpiresAt > now);
        if (applicationId is not null)
        {
            open = open.Where(s => s.ApplicationId == applicationId);
        }

        if (userId is not null)
        {
            open = open.Where(s => s.UserId == userId);
        }

        var sessions = await (
            from session in open
            join user in db.Users on session.UserId equals user.Id
            join application in db.Applications on session.ApplicationId equals application.Id
            join role in db.Roles on session.RoleId equals role.Id
            // Then by id (UUID v7, time-ordered), so sessions started in the same instant keep a fixed order.
            orderby session.CreatedAt descending, session.Id descending
            select new
            {
                session.Id,
                session.UserId,
                user.UserName,
                ApplicationKey = application.Key,
                RoleName = role.Name,
                session.CreatedAt,
                session.ExpiresAt,
            }).ToListAsync(cancellationToken);
        return Results.Ok(sessions);
    }

    private static async Task<IResult> StopAsync(Guid id, SessionStopper sessions, CancellationToken cancellationToken) =>
        await sessions.StopAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
}
