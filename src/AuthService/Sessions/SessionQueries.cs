using AuthService.Domain;

namespace AuthService.Sessions;

public static class SessionQueries
{
    /// <summary>
    /// Sessions that haven't ended or expired at <paramref name="now"/>: <see cref="Session.IsActiveAt"/> as a
    /// database query, the one definition of "open". Callers narrow it down (a user, an application, a role).
    /// An expired session has nothing left to stop, so stopping only these sends no needless events.
    /// </summary>
    public static IQueryable<Session> OpenAt(this IQueryable<Session> sessions, DateTimeOffset now) =>
        sessions.Where(s => s.EndedAt == null && s.ExpiresAt > now);
}
