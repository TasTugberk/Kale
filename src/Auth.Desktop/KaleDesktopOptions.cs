namespace Auth.Desktop;

/// <summary>How a desktop application reaches Kale. Only <see cref="Authority"/> and <see cref="ApplicationKey"/> are required.</summary>
public sealed class KaleDesktopOptions
{
    /// <summary>Kale's address (its issuer), e.g. https://auth.example.com/.</summary>
    public Uri? Authority { get; set; }

    /// <summary>
    /// The application's key in Kale, e.g. "orders": the same application as its API. The desktop app signs in as
    /// the client "{key}.desktop", which an admin switches on for the application.
    /// </summary>
    public string? ApplicationKey { get; set; }

    /// <summary>
    /// Local development only: allows a plain http <see cref="Authority"/> (e.g. the local Kale's
    /// http://auth.kale.localhost:5080/). Tokens must travel over https everywhere else.
    /// </summary>
    public bool AllowInsecureHttpDevOnly { get; set; }

    /// <summary>How long <see cref="KaleDesktopSignIn.SignInAsync"/> waits for the user to finish in the browser.</summary>
    public TimeSpan SignInTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Opens Kale's pages. By default the user's default browser (<see cref="SystemBrowser"/>).</summary>
    public IBrowser Browser { get; set; } = new SystemBrowser();

    /// <summary>
    /// Optional: keeps the refresh token between runs of the app, so users don't sign in at every start. Without it
    /// (the default) tokens live in memory only. On Windows, protect what you store with DPAPI (ProtectedData).
    /// </summary>
    public IRefreshTokenStore? RefreshTokenStore { get; set; }

    /// <summary>For calls to Kale's token endpoint (e.g. a proxy, or an in-memory server in tests). Default: a new handler.</summary>
    public HttpMessageHandler? BackchannelHandler { get; set; }

    /// <summary>The clock for token expiry; replaced in tests.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;
}
