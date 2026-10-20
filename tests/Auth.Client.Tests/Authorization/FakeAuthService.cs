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

    /// <summary>When set, role lookups wait for it, so a test can evict while a lookup is running.</summary>
    public TaskCompletionSource? HoldRoleLookups { get; set; }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionCalls++;
        if (FailWith is not null)
        {
            throw FailWith;
        }

        return Task.FromResult(Sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public async Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        RoleCalls++;
        var answer = RoleOperations.TryGetValue(roleId, out var operations) ? operations : null;
        if (HoldRoleLookups is not null)
        {
            await HoldRoleLookups.Task;
        }

        return answer;
    }
}
