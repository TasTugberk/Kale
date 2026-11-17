namespace Auth.Desktop;

/// <summary>
/// Keeps the refresh token between runs of the app (optional, <see cref="KaleDesktopOptions.RefreshTokenStore"/>).
/// The refresh token lets anyone holding it act as the user until the session ends (8 h by default), so store it
/// protected for the current user: on Windows with DPAPI (<c>ProtectedData.Protect</c>, scope CurrentUser, from
/// the System.Security.Cryptography.ProtectedData package).
/// </summary>
public interface IRefreshTokenStore
{
    /// <summary>The stored refresh token, or null when there is none.</summary>
    Task<string?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Stores a new refresh token (each renewal gives one), or deletes the stored one when null.</summary>
    Task SaveAsync(string? refreshToken, CancellationToken cancellationToken);
}
