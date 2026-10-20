namespace AuthService.Admin;

/// <summary>
/// AuthService's own operations. Decision: AuthService is itself the application "auth", and its admin API
/// requires auth.Admin (one superadmin level), checked the same way client apps check theirs.
/// </summary>
public enum AuthOperations
{
    Admin,
}

public static class AuthApplication
{
    /// <summary>AuthService's application key: the admin UI signs users in to it, and admin tokens carry it as audience.</summary>
    public const string Key = "auth";
}
