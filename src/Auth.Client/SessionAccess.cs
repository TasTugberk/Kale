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

    /// <summary>
    /// How long one lookup may take. Requests wait on it, so a slow AuthService must not hold them for long
    /// (they fail closed). Also the gRPC deadline of <see cref="GrpcLookups"/>.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan UsableWhileUnreachable = TimeSpan.FromMinutes(5);

    private readonly TimedCache<string, SessionInfo?> _sessions = new(() => SessionStatusLifetime, UsableWhileUnreachable, clock);
    private readonly TimedCache<Guid, IReadOnlySet<string>?> _roles = new(() => RoleOperationsLifetime, UsableWhileUnreachable, clock);

    public bool ReceivingEvents { get; set; }

    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        _sessions.GetOrAddAsync(sessionId, () => LookUpSessionAsync(sessionId), cancellationToken);

    public Task<IReadOnlySet<string>?> GetRoleOperationsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        _roles.GetOrAddAsync(roleId, () => LookUpRoleAsync(roleId), cancellationToken);

    /// <summary>The session's operations, or null when the session isn't ACTIVE (or unknown).</summary>
    public async Task<IReadOnlySet<string>?> GetOperationsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        // The lookups don't get this request's token: other requests may be waiting for the same lookup, and one
        // request giving up must not cancel it for them. This request's token ends only its own wait.
        var session = await _sessions.GetOrAddAsync(sessionId, () => LookUpSessionAsync(sessionId), cancellationToken);
        if (session is not { IsActive: true })
        {
            return null;
        }

        return await _roles.GetOrAddAsync(session.RoleId, () => LookUpRoleAsync(session.RoleId), cancellationToken);
    }

    // Bounded here rather than by each lookup: whatever the lookup is (gRPC, or AuthService's own database
    // queries), one that hangs would otherwise hold every request waiting for the same key.
    private async Task<SessionInfo?> LookUpSessionAsync(string sessionId)
    {
        using var timeout = new CancellationTokenSource(LookupTimeout, clock);
        return await sessions.GetAsync(sessionId, timeout.Token);
    }

    private async Task<IReadOnlySet<string>?> LookUpRoleAsync(Guid roleId)
    {
        using var timeout = new CancellationTokenSource(LookupTimeout, clock);
        return await roles.GetAsync(roleId, timeout.Token);
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
