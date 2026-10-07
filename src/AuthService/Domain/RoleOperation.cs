namespace AuthService.Domain;

/// <summary>Grants an operation to a role. Both must belong to <see cref="ApplicationId"/>.</summary>
public sealed class RoleOperation
{
    public Guid ApplicationId { get; init; }

    public Guid RoleId { get; init; }

    public Guid OperationId { get; init; }
}
