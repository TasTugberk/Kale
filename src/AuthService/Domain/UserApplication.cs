namespace AuthService.Domain;

/// <summary>
/// Access to one application. Sign-in requires an active row; roles alone are not enough.
/// </summary>
public sealed class UserApplication : IHasTimestamps
{
    public Guid UserId { get; init; }

    public Guid ApplicationId { get; init; }

    /// <summary>Picked at sign-in when the user doesn't choose. Must be a role of this application.</summary>
    public Guid? DefaultRoleId { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }
}
