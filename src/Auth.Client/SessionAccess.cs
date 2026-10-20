namespace Auth.Client;

/// <summary>
/// DESIGN.md per-request check, steps 2-3: is the token's session still ACTIVE, and which operations does its
/// role hold? Both answers come from AuthService and are cached: session status briefly (so a stopped session
/// is refused within seconds), role operations longer (role edits evict them through events).
/// </summary>
public sealed class SessionAccess(ISessionLookup sessions, IRoleOperationsLookup roles, TimeProvider clock)
{
    public static readonly TimeSpan SessionStatusLifetime = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan RoleOperationsLifetime = TimeSpan.FromMinutes(5);

    private readonly TimedCache<string, SessionInfo?> _sessions = new(SessionStatusLifetime, clock);
    private readonly TimedCache<Guid, IReadOnlySet<string>?> _roles = new(RoleOperationsLifetime, clock);

    /// <summary>The session's operations, or null when the session isn't ACTIVE (or unknown).</summary>
    public async Task<IReadOnlySet<string>?> GetOperationsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _sessions.GetOrAddAsync(sessionId, () => sessions.GetAsync(sessionId, cancellationToken));
        if (session is not { IsActive: true })
        {
            return null;
        }

        return await _roles.GetOrAddAsync(session.RoleId, () => roles.GetAsync(session.RoleId, cancellationToken));
    }

    /// <summary>A session was stopped (event): check it with AuthService on its next use.</summary>
    public void EvictSession(string sessionId) => _sessions.Evict(sessionId);

    /// <summary>A role was edited (event): load its operations again on its next use.</summary>
    public void EvictRole(Guid roleId) => _roles.Evict(roleId);

    /// <summary>How many sessions are cached right now (for tests and diagnostics).</summary>
    internal int CachedSessionCount => _sessions.Count;

    /// <summary>When events may have been missed (e.g. after reconnecting): forget everything.</summary>
    public void EvictAll()
    {
        _sessions.Clear();
        _roles.Clear();
    }
}
