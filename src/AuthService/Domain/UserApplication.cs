namespace AuthService.Domain;

/// <summary>
/// Access to one application. Sign-in requires an active row; roles alone are not enough.
/// </summary>
public sealed class UserApplication : IHasTimestamps
{
    public Guid UserId { get; init; }

    public Guid ApplicationId { get; init; }

    /// <summary>
    /// Picked at sign-in when the user doesn't choose. The database keeps it inside this application, but not
    /// to roles the user actually holds (those come from groups too), so sign-in must still check it against
    /// the user's eligible roles and ignore it if it isn't one.
    /// </summary>
    public Guid? DefaultRoleId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }
}
