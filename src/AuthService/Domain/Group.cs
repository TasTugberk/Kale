namespace AuthService.Domain;

/// <summary>A global set of users, e.g. "Finance". Roles given to a group apply to all its members.</summary>
public sealed class Group : BaseEntity
{
    public required string Name { get; set; }

    public string? Description { get; set; }
}
