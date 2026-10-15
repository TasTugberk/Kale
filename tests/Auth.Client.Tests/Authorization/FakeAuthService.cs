namespace Auth.Client.Tests.Authorization;

/// <summary>Stands in for AuthService's gRPC API: sessions and role operations the test sets up, with call counts.</summary>
internal sealed class FakeAuthService : ISessionLookup, IRoleOperationsLookup
{
    public Dictionary<string, SessionInfo> Sessions { get; } = [];

    public Dictionary<Guid, HashSet<string>> RoleOperations { get; } = [];

    public int SessionCalls { get; private set; }

    public int RoleCalls { get; private set; }

    public Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionCalls++;
        return Task.FromResult(Sessions.TryGetValue(sessionId, out var session) ? session : null);
    }

    public Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        RoleCalls++;
        return Task.FromResult<IReadOnlySet<string>?>(RoleOperations.TryGetValue(roleId, out var operations) ? operations : null);
    }
}
