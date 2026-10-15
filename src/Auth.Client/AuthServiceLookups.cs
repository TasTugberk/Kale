namespace Auth.Client;

/// <summary>A session as AuthService reports it (GetSession): whether it's ACTIVE, and its role.</summary>
public sealed record SessionInfo(bool IsActive, Guid RoleId);

/// <summary>Asks AuthService about a session. Returns null when AuthService doesn't know it (NOT_FOUND).</summary>
public interface ISessionLookup
{
    Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken);
}

/// <summary>Asks AuthService for a role's operations, implications already expanded. Null when unknown.</summary>
public interface IRoleOperationsLookup
{
    Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken);
}
