using Auth.Contracts.V1;
using Grpc.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Auth.Client;

/// <summary>
/// DESIGN.md cache invalidation: keeps AuthService's invalidation stream open (invalidation_stream.proto, over the
/// same gRPC connection as the lookups) and evicts the matching cache entries, so a stopped session or an edited
/// role takes effect right away. AuthService sends this application's events only.
/// </summary>
public sealed partial class InvalidationListener(
    AuthServiceConnection connection, SessionAccess access, ILogger<InvalidationListener> logger) : BackgroundService
{
    // Real time, not the app's TimeProvider: how long to wait before reconnecting isn't business time.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Evicts what the event makes stale. False when it can't be read (an id that isn't a UUID): the caller logs
    /// it, and the cache times still apply. Public for AuthService, which feeds its own events in-process.
    /// </summary>
    public static bool TryApply(InvalidationEvent invalidation, SessionAccess access)
    {
        switch (invalidation.EventCase)
        {
            case InvalidationEvent.EventOneofCase.SessionStopped:
                access.EvictSession(invalidation.SessionStopped.SessionId);
                return true;

            case InvalidationEvent.EventOneofCase.RoleEdited:
                if (!Guid.TryParse(invalidation.RoleEdited.RoleId, out var roleId))
                {
                    return false;
                }

                access.EvictRole(roleId);
                return true;

            // UserDisabled (not sent to applications any more): the user's sessions get their own SessionStopped.
            // Events added to the contract later are ignored until this package knows them.
            default:
                return true;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ListenUntilTheStreamEndsAsync(stoppingToken);
            }
            catch (Exception) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // Every end is a reason to reconnect (the proto: any status but PERMISSION_DENIED means "try again";
                // a deactivated application tries again too, in case it's reactivated).
                LogStreamEnded(exception, RetryDelay);
            }
            finally
            {
                // Events may be missed from now on: session status goes back to its short lifetime.
                access.ReceivingEvents = false;
            }

            await Task.Delay(RetryDelay, stoppingToken);
        }
    }

    private async Task ListenUntilTheStreamEndsAsync(CancellationToken stoppingToken)
    {
        using var call = connection.Invalidation.Subscribe(new SubscribeRequest(), cancellationToken: stoppingToken);

        // The headers mean "subscribed": every change saved from now on reaches this stream. Events sent while this
        // instance wasn't connected are lost, so forget everything cached before. (A refusal also comes as
        // headers, then fails on the first read; clearing the caches for it is harmless.)
        await call.ResponseHeadersAsync;
        access.EvictAll();
        access.ReceivingEvents = true;
        LogListening();

        await foreach (var invalidation in call.ResponseStream.ReadAllAsync(stoppingToken))
        {
            if (!TryApply(invalidation, access))
            {
                LogUnreadableEvent(invalidation.EventId);
            }
        }

        // AuthService never ends the stream normally; treat it like any other end.
        throw new RpcException(new Status(StatusCode.Unavailable, "The invalidation stream ended."));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Receiving invalidation events from AuthService.")]
    private partial void LogListening();

    [LoggerMessage(Level = LogLevel.Warning, Message = "The invalidation stream ended; reconnecting in {RetryDelay}.")]
    private partial void LogStreamEnded(Exception exception, TimeSpan retryDelay);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped an invalidation event that couldn't be read (event id {EventId}).")]
    private partial void LogUnreadableEvent(string eventId);
}
