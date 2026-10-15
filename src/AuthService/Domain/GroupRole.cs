namespace AuthService.Domain;

/// <summary>A role given to every member of a group.</summary>
public sealed class GroupRole
{
    public Guid GroupId { get; init; }

    public Guid RoleId { get; init; }
}
