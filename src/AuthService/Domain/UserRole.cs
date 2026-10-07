namespace AuthService.Domain;

/// <summary>A role given directly to a user (as opposed to through a group).</summary>
public sealed class UserRole
{
    public Guid UserId { get; init; }

    public Guid RoleId { get; init; }
}
