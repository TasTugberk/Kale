using Auth.Contracts.V1;
using Grpc.Core;

namespace Auth.Client;

/// <summary>The lookups the authorization handler needs, answered by AuthService over gRPC.</summary>
public sealed class GrpcLookups(AuthServiceConnection connection) : ISessionLookup, IRoleOperationsLookup
{
    // SessionAccess cancels a lookup after the same time; the deadline also tells AuthService when to give up.
    private static readonly TimeSpan Deadline = SessionAccess.LookupTimeout;

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
        catch (RpcException exception) when (IsUnreachable(exception))
        {
            throw new AuthServiceUnreachableException("AuthService couldn't be reached.", exception);
        }
    }

    // No answer at all: no connection, the deadline passed, or the call broke off (also when getting this
    // application's own token failed). Anything else (PERMISSION_DENIED, UNAUTHENTICATED, ...) is an answer.
    private static bool IsUnreachable(RpcException exception) =>
        exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled
        || exception.InnerException is HttpRequestException;

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
        catch (RpcException exception) when (IsUnreachable(exception))
        {
            throw new AuthServiceUnreachableException("AuthService couldn't be reached.", exception);
        }
    }
}
