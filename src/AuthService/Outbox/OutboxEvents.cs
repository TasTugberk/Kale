using Auth.Contracts.V1;
using AuthService.Domain;
using Google.Protobuf;

namespace AuthService.Outbox;

/// <summary>Builds the outbox rows for the invalidation events in invalidation.proto.</summary>
public static class OutboxEvents
{
    public static OutboxMessage SessionStopped(Guid sessionId) =>
        For(new InvalidationEvent { SessionStopped = new SessionStopped { SessionId = sessionId.ToString() } });

    private static OutboxMessage For(InvalidationEvent invalidation)
    {
        // The row's id doubles as the event id, so a redelivered event can be recognized (and logged) as the same.
        var id = Guid.CreateVersion7();
        invalidation.EventId = id.ToString();
        return new OutboxMessage { Id = id, Type = invalidation.EventCase.ToString(), Payload = invalidation.ToByteArray() };
    }
}
