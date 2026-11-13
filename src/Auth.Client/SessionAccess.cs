namespace Auth.Client;

/// <summary>
/// DESIGN.md per-request check, steps 2-3: is the token's session still ACTIVE, and which operations does its
/// role hold? Both answers come from AuthService and are cached. Events (<see cref="InvalidationListener"/>) evict
/// what changed, so the time limits are only the fallback for a lost event: shorter for session status while
/// events aren't arriving.
/// </summary>
public sealed class SessionAccess
{
    /// <summary>Session status while no events arrive: a stopped session is refused within this time.</summary>
    public static readonly TimeSpan SessionStatusLifetime = TimeSpan.FromSeconds(20);

    /// <summary>Session status while events arrive (a stopped session's event evicts it at once).</summary>
    public static readonly TimeSpan SessionStatusLifetimeWhileReceivingEvents = TimeSpan.FromMinutes(2);

    public static readonly TimeSpan RoleOperationsLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How much longer than its lifetime an answer may be used while AuthService can't be reached (no connection,
    /// no answer in time). Afterwards requests fail closed. An answer from AuthService (even a refusal) never
    /// brings back an old one, and an evicted answer is gone for good.
    /// </summary>
    public static readonly TimeSpan UsableWhileUnreachable = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long one lookup may take. Requests wait on it, so a slow AuthService must not hold them for long
    /// (they fail closed). Also the gRPC deadline of <see cref="GrpcLookups"/>.
    /// </summary>
    public static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(5);

    private readonly ISessionLookup _sessionLookup;
    private readonly IRoleOperationsLookup _roleLookup;
    private readonly TimeProvider _clock;
    private readonly TimedCache<string, SessionInfo?> _sessions;
    private readonly TimedCache<Guid, IReadOnlySet<string>?> _roles;
    private volatile bool _receivingEvents;

    public SessionAccess(ISessionLookup sessions, IRoleOperationsLookup roles, TimeProvider clock)
    {
        _sessionLookup = sessions;
        _roleLookup = roles;
        _clock = clock;

        // The session lifetime is asked for at every read, so it follows the stream's state at that moment.
        _sessions = new(() => _receivingEvents ? SessionStatusLifetimeWhileReceivingEvents : SessionStatusLifetime, UsableWhileUnreachable, clock);
        _roles = new(() => RoleOperationsLifetime, UsableWhileUnreachable, clock);
    }

    /// <summary>
    /// Whether this instance is connected to AuthService's invalidation stream right now. Set by the listener;
    /// cached session status lives longer while it's true. Checked when an entry is read, so once the stream drops,
    /// a status cached while connected is used for 20 s from when it was fetched, like any other.
    /// </summary>
    public bool ReceivingEvents
    {
        get => _receivingEvents;
        set => _receivingEvents = value;
    }

    /// <summary>The session as AuthService last reported it; null when AuthService doesn't know it.</summary>
    public Task<SessionInfo?> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) =>
        // The lookups don't get this request's token: other requests may be waiting for the same lookup, and one
        // request giving up must not cancel it for them. This request's token ends only its own wait.
        _sessions.GetOrAddAsync(sessionId, () => LookUpSessionAsync(sessionId), cancellationToken);

    /// <summary>The role's operations; null when AuthService doesn't know the role.</summary>
    public Task<IReadOnlySet<string>?> GetRoleOperationsAsync(Guid roleId, CancellationToken cancellationToken = default) =>
        _roles.GetOrAddAsync(roleId, () => LookUpRoleAsync(roleId), cancellationToken);

    /// <summary>The session's operations, or null when the session isn't ACTIVE (or unknown).</summary>
    public async Task<IReadOnlySet<string>?> GetOperationsAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var session = await GetSessionAsync(sessionId, cancellationToken);
        if (session is not { IsActive: true })
        {
            return null;
        }

        return await GetRoleOperationsAsync(session.RoleId, cancellationToken);
    }

    // Bounded here rather than by each lookup: whatever the lookup is (gRPC, or AuthService's own database
    // queries), one that hangs would otherwise hold every request waiting for the same key. No answer in time
    // counts as unreachable.
    private async Task<SessionInfo?> LookUpSessionAsync(string sessionId)
    {
        using var timeout = new CancellationTokenSource(LookupTimeout, _clock);
        try
        {
            return await _sessionLookup.GetAsync(sessionId, timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new AuthServiceUnreachableException("AuthService didn't answer in time.", exception);
        }
    }

    private async Task<IReadOnlySet<string>?> LookUpRoleAsync(Guid roleId)
    {
        using var timeout = new CancellationTokenSource(LookupTimeout, _clock);
        try
        {
            return await _roleLookup.GetAsync(roleId, timeout.Token);
        }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            throw new AuthServiceUnreachableException("AuthService didn't answer in time.", exception);
        }
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
