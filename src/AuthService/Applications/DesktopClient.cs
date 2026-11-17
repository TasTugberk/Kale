namespace AuthService.Applications;

/// <summary>
/// An application's desktop client (owner's decision): a desktop app (e.g. WPF) can't keep a secret, so it signs
/// users in as a public client through the system browser, with code + PKCE and a loopback redirect on any port
/// (RFC 8252). It belongs to the application: its tokens carry the application's key as audience, and its sessions
/// are the application's. Switched on and off only through <see cref="ApplicationService"/>.
/// </summary>
public static class DesktopClient
{
    /// <summary>
    /// The desktop client's id is "{key}.desktop". Application keys can't contain '.' (ck_applications_key_format),
    /// so it never equals a key.
    /// </summary>
    public const string Suffix = ".desktop";

    /// <summary>
    /// The registered return addresses. The app listens on a free port of its own choosing, so the registered
    /// addresses have no port, and the port of the requested one doesn't count when they're compared.
    /// </summary>
    public static readonly Uri RedirectUri = new("http://127.0.0.1/callback");

    public static readonly Uri PostLogoutRedirectUri = new("http://127.0.0.1/signed-out");

    public static string ClientIdOf(string applicationKey) => applicationKey + Suffix;

    /// <summary>The application a client id belongs to: the key itself, or the key in front of ".desktop".</summary>
    public static string ApplicationKeyOf(string clientId) =>
        clientId.EndsWith(Suffix, StringComparison.Ordinal) ? clientId[..^Suffix.Length] : clientId;
}
