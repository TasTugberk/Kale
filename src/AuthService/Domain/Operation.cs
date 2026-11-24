namespace AuthService.Domain;

/// <summary>
/// One permission defined by an app's operations enum, stored as "{appKey}.{EnumMember}" (e.g. "billing.InvoiceRead").
/// Operations are registered by the app itself; they are marked obsolete, never deleted.
/// </summary>
public sealed class Operation : BaseEntity
{
    public Guid ApplicationId { get; init; }

    /// <summary>
    /// Copy of the application's key. It exists only so the database can check that <see cref="Name"/> starts with it.
    /// </summary>
    public required string ApplicationKey { get; init; }

    public required string Name { get; init; }

    /// <summary>Set when the app has stopped registering this operation. A single column, so "obsolete" can't disagree with a date.</summary>
    public DateTimeOffset? ObsoletedAt { get; set; }

    /// <summary>
    /// When a registration last listed this operation. It becomes obsolete only once no registration has listed it
    /// for a while (Operations:ObsoleteAfter), so two versions of the app running at once both keep theirs.
    /// </summary>
    public DateTimeOffset LastRegisteredAt { get; set; }

    public bool IsObsolete => ObsoletedAt is not null;
}
