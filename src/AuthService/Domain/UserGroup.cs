namespace AuthService.Domain;

/// <summary>Membership: <see cref="UserId"/> is in <see cref="GroupId"/>.</summary>
public sealed class UserGroup
{
    public Guid UserId { get; init; }

    public Guid GroupId { get; init; }
}
