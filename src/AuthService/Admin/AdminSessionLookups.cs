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
    public async Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(sessionId, out var id))
        {
            return null;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var authApplicationId = await AuthApplicationIdAsync(db, cancellationToken);
        var session = await db.Sessions.AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == id && s.ApplicationId == authApplicationId, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var isActive = await scope.ServiceProvider.GetRequiredService<SessionCheck>().IsActiveAsync(session, cancellationToken);
        return new SessionInfo(isActive, session.RoleId);
    }

    public async Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var authApplicationId = await AuthApplicationIdAsync(db, cancellationToken);
        return await scope.ServiceProvider.GetRequiredService<EffectiveOperations>()
            .ForRoleAsync(authApplicationId, roleId, cancellationToken);
    }

    private static Task<Guid> AuthApplicationIdAsync(AuthDbContext db, CancellationToken cancellationToken) =>
        db.Applications.Where(a => a.Key == AuthApplication.Key).Select(a => a.Id).SingleOrDefaultAsync(cancellationToken);
}
