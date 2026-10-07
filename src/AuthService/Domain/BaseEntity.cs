namespace AuthService.Domain;

/// <summary>
/// Columns shared by every entity that has its own id. Join tables such as <see cref="RoleOperation"/>
/// don't inherit it: their key is the pair they join.
/// </summary>
public abstract class BaseEntity : IHasTimestamps
{
    /// <summary>UUID v7: generated in code and time-ordered, which keeps indexes compact.</summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>Set by <c>AuthDbContext</c> when the entity is first saved.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Set by <c>AuthDbContext</c> on insert and on every save that changes the entity.
    /// Bulk updates (<c>ExecuteUpdate</c>) and raw SQL bypass it, so they must set it themselves.
    /// </summary>
    public DateTimeOffset ModifiedAt { get; set; }
}
