namespace AuthService.Domain;

/// <summary>
/// One sign-in of one user to one application with one chosen role. Its <see cref="BaseEntity.Id"/> is the
/// access token's "sid" claim. A user signed in to two applications has two independent sessions.
/// </summary>
public sealed class Session : BaseEntity
{
    public Guid UserId { get; init; }

    public Guid ApplicationId { get; init; }

    public Guid RoleId { get; init; }

    /// <summary>Absolute end of the session; refreshing tokens doesn't extend it.</summary>
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    /// Set when an admin stops the session or the user signs out. The only record of "ended", so it can't
    /// disagree with a separate status column.
    /// </summary>
    public DateTimeOffset? EndedAt { get; private set; }

    /// <summary>
    /// Not ended and not expired. That's only the session's own part: the session check (GetSession) and token
    /// refresh must also require that the user, the user's access to the application and the application are
    /// active, and that the user <b>still holds <see cref="RoleId"/></b>, directly or through a group. Otherwise
    /// a revoked role would stay usable until the session expires.
    /// </summary>
    public bool IsActiveAt(DateTimeOffset now) => EndedAt is null && now < ExpiresAt;

    /// <summary>Ends the session. Ending it again keeps the first end time.</summary>
    public void End(DateTimeOffset now) => EndedAt ??= now;
}
