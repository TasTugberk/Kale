namespace AuthService.Domain;

/// <summary>A client app registered with the auth service, e.g. "billing".</summary>
public sealed class Application
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Public, unique, never changes: it is the OAuth client id and the prefix of every operation name.
    /// </summary>
    public required string Key { get; init; }

    public required string Name { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; private set; }
}
