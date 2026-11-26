using Auth.Contracts.V1;
using Grpc.Core;

namespace Auth.Client;

/// <summary>
/// Changes to this application's Kale sessions, from its server. Registered by AddKaleAuth.
/// </summary>
public sealed class KaleSessions(AuthServiceConnection connection, SessionAccess access)
{
    /// <summary>
    /// Ends one of this application's sessions (e.g. when its user signs out), so its tokens stop working at once
    /// everywhere, even if the browser never reaches Kale's sign-out page. <paramref name="sessionId"/> is the
    /// token's "sid". True when the session is ended now (also when it had ended already); false when Kale doesn't
    /// know it, or it belongs to another application. Throws <see cref="AuthServiceUnreachableException"/> when
    /// Kale couldn't be reached, and an RpcException when Kale refused the call (e.g. this application is inactive).
    /// </summary>
    public async Task<bool> EndAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.Sessions.EndSessionAsync(
                new EndSessionRequest { SessionId = sessionId },
                deadline: DateTime.UtcNow + SessionAccess.LookupTimeout,
                cancellationToken: cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.NotFound or StatusCode.InvalidArgument)
        {
            return false;
        }
        catch (RpcException exception) when (GrpcLookups.IsUnreachable(exception))
        {
            throw new AuthServiceUnreachableException("AuthService couldn't be reached.", exception);
        }

        // This instance needn't wait for the "session stopped" event to refuse the session's token.
        access.EvictSession(sessionId);
        return true;
    }
}
