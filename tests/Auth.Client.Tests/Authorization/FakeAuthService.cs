namespace Auth.Client.Tests.Authorization;

/// <summary>Stands in for AuthService's gRPC API: sessions and role operations the test sets up, with call counts.</summary>
internal sealed class FakeAuthService : ISessionLookup, IRoleOperationsLookup
{
    public Dictionary<string, SessionInfo> Sessions { get; } = [];

    public Dictionary<Guid, HashSet<string>> RoleOperations { get; } = [];

    public int SessionCalls { get; private set; }

    public int RoleCalls { get; private set; }

    /// <summary>When set, every lookup throws it (AuthService unreachable).</summary>
    public Exception? FailWith { get; set; }

    /// <summary>
    /// When set, session lookups wait for it, so a test can act while a lookup is running. The wait ends early
    /// when the lookup's token is cancelled, as a real gRPC call or database query would.
    /// </summary>
    public TaskCompletionSource? HoldSessionLookups { get; set; }

    /// <summary>As <see cref="HoldSessionLookups"/>, for role lookups.</summary>
    public TaskCompletionSource? HoldRoleLookups { get; set; }

    public async Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionCalls++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var answer = Sessions.TryGetValue(sessionId, out var session) ? session : null;
        if (HoldSessionLookups is not null)
        {
            await HoldSessionLookups.Task.WaitAsync(cancellationToken);
        }

        return answer;
    }

    public async Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        RoleCalls++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        var answer = RoleOperations.TryGetValue(roleId, out var operations) ? operations : null;
        if (HoldRoleLookups is not null)
        {
            await HoldRoleLookups.Task.WaitAsync(cancellationToken);
        }

        return answer;
    }
}
