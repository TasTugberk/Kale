using Auth.Contracts;
using Auth.Contracts.V1;
using AuthService.Invalidation;
using AuthService.Persistence;
using Grpc.Core;

namespace AuthService.Grpc;

/// <summary>Sends a client app instance the invalidation events of its application (invalidation_stream.proto).</summary>
public sealed partial class InvalidationStreamGrpcService(
    AuthDbContext db, InvalidationSubscribers subscribers, ILogger<InvalidationStreamGrpcService> logger)
    : InvalidationStream.InvalidationStreamBase
{
    public override async Task Subscribe(
        SubscribeRequest request, IServerStreamWriter<InvalidationEvent> responseStream, ServerCallContext context)
    {
        try
        {
            await StreamAsync(responseStream, context);
        }
        catch (Exception exception) when (exception is not RpcException && !context.CancellationToken.IsCancellationRequested)
        {
            // E.g. the database is unreachable for the application check. Any other failure would reach the client
            // as UNKNOWN; UNAVAILABLE is what the contract promises for "events may be missed, reconnect later".
            LogStreamFailed(exception);
            throw new RpcException(new Status(StatusCode.Unavailable, "AuthService couldn't keep this stream going; try again shortly."));
        }
    }

    private async Task StreamAsync(IServerStreamWriter<InvalidationEvent> responseStream, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var subscription = subscribers.TryAdd(caller.Id)
            ?? throw new RpcException(new Status(StatusCode.Unavailable, "AuthService isn't receiving invalidation events right now; try again shortly."));
        try
        {
            // Only now that events are queued for this stream: the client counts the marker as "connected" and
            // clears its caches then, so every change saved after that moment must reach it. A refusal has no
            // marker (some clients report a refusal as headers too).
            await context.WriteResponseHeadersAsync(new Metadata { { InvalidationStreamHeaders.Subscribed, "1" } });

            while (await subscription.Events.WaitToReadAsync(context.CancellationToken))
            {
                // An application deactivated while its stream is open loses the stream at its next event (until then
                // it gets nothing anyway). Checked once per batch of waiting events, not per event: deleting a role
                // queues one event for each of its sessions at once.
                await context.RequireActiveApplicationAsync(db);
                while (subscription.Events.TryRead(out var invalidation))
                {
                    await responseStream.WriteAsync(invalidation, context.CancellationToken);
                }
            }

            // Ended by this instance (see InvalidationSubscribers): the client reconnects and clears its caches.
            throw new RpcException(new Status(StatusCode.Unavailable, subscription.EndReason ?? "The stream ended."));
        }
        finally
        {
            subscribers.Remove(subscription);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "An invalidation stream failed; it ends as UNAVAILABLE.")]
    private partial void LogStreamFailed(Exception exception);
}
