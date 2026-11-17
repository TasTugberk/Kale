namespace AuthService.Domain;

/// <summary>
/// An invalidation event waiting to be published to RabbitMQ (outbox pattern). It's saved in the same transaction
/// as the change it describes, so an event is never lost and never announces a change that was rolled back.
/// The publisher deletes it once RabbitMQ has confirmed it.
/// Saving it also hands the event to the client apps' invalidation streams (a trigger, see
/// <c>OutboxMessageConfiguration</c>), which are replacing RabbitMQ.
/// </summary>
public sealed class OutboxMessage : BaseEntity
{
    /// <summary>The kind of event, e.g. "SessionStopped": for logs and for reading the table in SQL.</summary>
    public required string Type { get; init; }

    /// <summary>The event as published: an <c>InvalidationEvent</c> (invalidation.proto) in protobuf bytes.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// The application whose streams get the event (the stopped session's or the edited role's); null for an
    /// event no application needs (UserDisabled).
    /// </summary>
    public Guid? ApplicationId { get; init; }
}
