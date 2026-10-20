using Auth.Contracts.V1;
using Grpc.Core;

namespace Auth.Client;

/// <summary>The lookups the authorization handler needs, answered by AuthService over gRPC.</summary>
public sealed class GrpcLookups(AuthServiceConnection connection) : ISessionLookup, IRoleOperationsLookup
{
    // Requests wait on these answers, so don't let a slow AuthService hold them for long (fails closed).
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public async Task<SessionInfo?> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var session = await connection.Sessions.GetSessionAsync(
                new GetSessionRequest { SessionId = sessionId }, deadline: DateTime.UtcNow + Deadline, cancellationToken: cancellationToken);
            return new SessionInfo(session.Status == SessionStatus.Active, Guid.Parse(session.RoleId));
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.NotFound or StatusCode.InvalidArgument)
        {
            // Unknown (or another application's) session, or a sid that isn't even a UUID: not allowed in.
            return null;
        }
    }

    public async Task<IReadOnlySet<string>?> GetAsync(Guid roleId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await connection.Roles.GetEffectiveOperationsAsync(
                new GetEffectiveOperationsRequest { RoleId = roleId.ToString() }, deadline: DateTime.UtcNow + Deadline, cancellationToken: cancellationToken);
            return response.Operations.ToHashSet(StringComparer.Ordinal);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }
}
