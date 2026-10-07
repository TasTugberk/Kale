using Microsoft.AspNetCore.Identity;

namespace AuthService.Domain;

/// <summary>
/// A person who signs in. Global: one user can access many applications.
/// Built on ASP.NET Core Identity's user (password hash, lockout, MFA), which is why it can't inherit
/// <see cref="BaseEntity"/> and implements <see cref="IHasTimestamps"/> instead.
/// </summary>
public sealed class User : IdentityUser<Guid>, IHasTimestamps
{
    public User() => Id = Guid.CreateVersion7();

    public required string DisplayName { get; set; }

    /// <summary>Disabled users can't sign in.</summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }
}
