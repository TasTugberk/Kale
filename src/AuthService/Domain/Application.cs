namespace AuthService.Domain;

/// <summary>A client app registered with the auth service, e.g. "billing".</summary>
public sealed class Application : BaseEntity
{
    /// <summary>
    /// Public, unique, never changes: it is the OAuth client id and the prefix of every operation name.
    /// </summary>
    public required string Key { get; init; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// The client secret before the latest rotation, hashed the way OpenIddict hashes the current one (which
    /// OpenIddict keeps on its client). Still accepted until <see cref="OldSecretValidUntil"/>, so running instances
    /// keep working until they're redeployed with the new secret. Both are set, or neither.
    /// </summary>
    public string? OldSecretHash { get; set; }

    public DateTimeOffset? OldSecretValidUntil { get; set; }
}
