namespace AuthService.Domain;

/// <summary>
/// Entities whose <see cref="CreatedAt"/> and <see cref="ModifiedAt"/> are set by <c>AuthDbContext</c> on save.
/// Values set by hand are overwritten. An interface (not only <see cref="BaseEntity"/>) because
/// <see cref="User"/> already inherits Identity's user class, and some tables have no id of their own.
/// </summary>
public interface IHasTimestamps
{
    DateTimeOffset CreatedAt { get; set; }

    DateTimeOffset ModifiedAt { get; set; }
}
