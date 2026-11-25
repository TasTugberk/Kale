using Auth.Client;
using AuthService.Operations;
using AuthService.Persistence;
using AuthService.Sessions;
using Microsoft.EntityFrameworkCore;

namespace AuthService.Admin;

/// <summary>
/// The admin API's session and role checks. Client apps get these answers over gRPC; AuthService has the data
/// itself, so Auth.Client's SessionAccess asks here. Only sessions and roles of the "auth" application count,
/// as the gRPC API answers only for the calling application.
/// </summary>
public sealed class AdminSessionLookups(IServiceScopeFactory scopes) : ISessionLookup, IRoleOperationsLookup
{
    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
        AnsweredOrUnavailableAsync(() => GetSessionAsync(sessionId, cancellationToken));

    public Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken) =>
        AnsweredOrUnavailableAsync(() => GetRoleOperationsAsync(roleId, cancellationToken));

    // A lookup that fails (e.g. the database times out or the connection drops) has no answer: Auth.Client turns
    // AuthServiceUnreachableException into 503, as for a client app that can't reach Kale. Any other exception
    // would become 403, which the admin UI reads as "auth.Admin was taken away" and signs the admin out.
    private static async Task<T> AnsweredOrUnavailableAsync<T>(Func<Task<T>> lookup)
    {
        try
        {
            return await lookup();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new AuthServiceUnreachableException("The admin API's session check couldn't read the database.", exception);
        }
    }

    private async Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var id))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var authApplicationId = await AuthApplicationIdAsync(db, cancellationToken);
        if (authApplicationId is null)
        {
            return null;
        }

        var session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id && s.ApplicationId == authApplicationId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var isActive = await scope.ServiceProvider.GetRequiredService<SessionCheck>().IsActiveAsync(session, cancellationToken);
        return new SessionInfo(isActive, session.RoleId);
    }

    private async Task<IReadOnlySet<string>?> GetRoleOperationsAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var authApplicationId = await AuthApplicationIdAsync(db, cancellationToken);
        if (authApplicationId is null)
        {
            return null;
        }

        return await scope.ServiceProvider.GetRequiredService<EffectiveOperations>()
            .ForRoleAsync(authApplicationId.Value, roleId, cancellationToken);
    }

    // Null before `admin bootstrap` created the application.
    private static Task<Guid?> AuthApplicationIdAsync(AuthDbContext db, CancellationToken cancellationToken) =>
        db.Applications.Where(a => a.Key == AuthApplication.Key).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken);
}
