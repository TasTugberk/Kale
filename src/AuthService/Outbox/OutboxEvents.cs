using Auth.Contracts.V1;
using AuthService.Domain;
using Google.Protobuf;

namespace AuthService.Outbox;

/// <summary>Builds the outbox rows for the invalidation events in invalidation.proto.</summary>
public static class OutboxEvents
{
    /// <param name="applicationId">The session's application: only its invalidation streams get the event.</param>
    public static OutboxMessage SessionStopped(Guid sessionId, Guid applicationId) =>
        For(new InvalidationEvent { SessionStopped = new SessionStopped { SessionId = sessionId.ToString() } }, applicationId);

    /// <param name="applicationId">The role's application: only its invalidation streams get the event.</param>
    public static OutboxMessage RoleEdited(Guid roleId, Guid applicationId) =>
        For(new InvalidationEvent { RoleEdited = new RoleEdited { RoleId = roleId.ToString() } }, applicationId);

    /// <summary>
    /// For audit only: no invalidation stream gets it, since the user's sessions each get their own SessionStopped.
    /// </summary>
    public static OutboxMessage UserDisabled(Guid userId) =>
        For(new InvalidationEvent { UserDisabled = new UserDisabled { UserId = userId.ToString() } }, applicationId: null);

    private static OutboxMessage For(InvalidationEvent invalidation, Guid? applicationId)
    {
        // The row's id doubles as the event id, so a redelivered event can be recognized (and logged) as the same.
        var id = Guid.CreateVersion7();
        invalidation.EventId = id.ToString();
        return new OutboxMessage
        {
            Id = id, Type = invalidation.EventCase.ToString(), Payload = invalidation.ToByteArray(), ApplicationId = applicationId,
        };
    }
}
