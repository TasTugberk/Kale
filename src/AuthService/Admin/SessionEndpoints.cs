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
    /// A page (see <see cref="PageRequest"/>) of the sessions that haven't ended or expired, newest first,
    /// optionally for one application or user. <c>?search=</c> finds the text in the user name, the application key
    /// or the role name, ignoring case. "Open" is only the session's own state: one can still be refused by the full
    /// ACTIVE check (SessionCheck), e.g. when its application was deactivated or the user lost the role.
    /// </summary>
    private static async Task<IResult> ListOpenAsync(
        Guid? applicationId, Guid? userId, string? search, int? page, int? pageSize,
        AuthDbContext db, TimeProvider clock, CancellationToken cancellationToken)
    {
        var open = db.Sessions.AsNoTracking().OpenAt(clock.GetUtcNow());
        if (applicationId is not null)
        {
            open = open.Where(s => s.ApplicationId == applicationId);
        }

        if (userId is not null)
        {
            open = open.Where(s => s.UserId == userId);
        }

        // Filtered and sorted on the joined rows; EF can't translate a filter or a sort after the record is built.
        var joined =
            from session in open
            join user in db.Users on session.UserId equals user.Id
            join application in db.Applications on session.ApplicationId equals application.Id
            join role in db.Roles on session.RoleId equals role.Id
            select new { session, user, application, role };

        var pattern = Search.ContainsPattern(search);
        if (pattern is not null)
        {
            joined = joined.Where(row => EF.Functions.ILike(row.user.UserName!, pattern)
                || EF.Functions.ILike(row.application.Key, pattern)
                || EF.Functions.ILike(row.role.Name, pattern));
        }

        // Then by id (UUID v7, time-ordered), so sessions started in the same instant keep a fixed order.
        var rows = joined
            .OrderByDescending(row => row.session.CreatedAt).ThenByDescending(row => row.session.Id)
            .Select(row => new SessionRow(
                row.session.Id, row.session.UserId, row.user.UserName!, row.application.Key, row.role.Name,
                row.session.CreatedAt, row.session.ExpiresAt));
        return Results.Ok(await PageRequest.From(page, pageSize).OfAsync(rows, cancellationToken));
    }

    public sealed record SessionRow(
        Guid Id, Guid UserId, string UserName, string ApplicationKey, string RoleName, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

    private static async Task<IResult> StopAsync(Guid id, SessionStopper sessions, CancellationToken cancellationToken) =>
        await sessions.StopAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
}
