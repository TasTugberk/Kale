using Auth.Contracts.V1;
using AuthService.Invalidation;
using AuthService.Persistence;
using Grpc.Core;

namespace AuthService.Grpc;

/// <summary>Sends a client app instance the invalidation events of its application (invalidation_stream.proto).</summary>
public sealed class InvalidationStreamGrpcService(AuthDbContext db, InvalidationSubscribers subscribers)
    : InvalidationStream.InvalidationStreamBase
{
    public override async Task Subscribe(
        SubscribeRequest request, IServerStreamWriter<InvalidationEvent> responseStream, ServerCallContext context)
    {
        var caller = await context.RequireActiveApplicationAsync(db);
        var subscription = subscribers.TryAdd(caller.Id)
            ?? throw new RpcException(new Status(StatusCode.Unavailable, "AuthService isn't receiving invalidation events right now; try again shortly."));
        try
        {
            // Only now that events are queued for this stream: the client counts the headers as "connected" and
            // clears its caches then, so every change saved after that moment must reach it.
            await context.WriteResponseHeadersAsync(new Metadata());

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
}
