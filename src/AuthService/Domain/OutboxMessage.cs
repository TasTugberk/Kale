namespace AuthService.Domain;

/// <summary>
/// An invalidation event (outbox pattern). It's saved in the same transaction as the change it describes, so it
/// never announces a change that was rolled back. Saving it hands the event to the client apps' invalidation
/// streams (a trigger, see <c>OutboxMessageConfiguration</c>); the hourly cleanup deletes it a day later
/// (<c>OutboxCleanup</c>).
/// </summary>
public sealed class OutboxMessage : BaseEntity
{
    /// <summary>The kind of event, e.g. "SessionStopped": for logs and for reading the table in SQL.</summary>
    public required string Type { get; init; }

    /// <summary>The event as sent: an <c>InvalidationEvent</c> (invalidation.proto) in protobuf bytes.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>
    /// The application whose streams get the event (the stopped session's or the edited role's); null for an
    /// event no application needs (UserDisabled).
    /// </summary>
    public Guid? ApplicationId { get; init; }
}
