namespace AuthService.Domain;

/// <summary>A named set of operations within one application, e.g. "Accountant" in billing.</summary>
public sealed class Role
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public Guid ApplicationId { get; init; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }
}
