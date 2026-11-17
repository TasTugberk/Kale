namespace Auth.Web;

/// <summary>
/// How a web application signs its users in through Kale. The same settings, in the same "Kale" configuration
/// section, as Kale.Auth.Client's KaleAuthOptions: a project using both packages configures them once.
/// </summary>
public sealed class KaleSignInOptions
{
    /// <summary>Kale's base address, e.g. https://auth.example.com/.</summary>
    public Uri? Authority { get; set; }

    /// <summary>This application's key in Kale (its client id and its tokens' audience), e.g. "orders".</summary>
    public string ApplicationKey { get; set; } = "";

    /// <summary>From Kale, shown once when the application was registered. Keep it in a secret store.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Allows a plain-HTTP Authority and cookies without Secure. Only in Development/Testing (startup fails otherwise).</summary>
    public bool AllowInsecureHttpDevOnly { get; set; }

    internal void Validate(bool isDevelopmentOrTesting)
    {
        if (Authority is null || string.IsNullOrWhiteSpace(ApplicationKey) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException(
                "Kale isn't configured: set Kale:Authority, Kale:ApplicationKey and Kale:ClientSecret (appsettings, user " +
                "secrets or environment; in a container Kale:ClientSecretFile). For the local Kale, run " +
                "scripts/register-app.sh <key> --web <address> <project folder> in the Kale repository: it registers the " +
                "application and writes these into the project's user secrets.");
        }

        if (!Authority.IsAbsoluteUri)
        {
            throw new InvalidOperationException("Kale:Authority must be Kale's absolute address, e.g. https://auth.example.com/.");
        }

        if (AllowInsecureHttpDevOnly && !isDevelopmentOrTesting)
        {
            throw new InvalidOperationException("Kale:AllowInsecureHttpDevOnly is only allowed in Development and Testing.");
        }

        if (Authority.Scheme != Uri.UriSchemeHttps && !AllowInsecureHttpDevOnly)
        {
            throw new InvalidOperationException("Kale:Authority must use https (AllowInsecureHttpDevOnly is for local development only).");
        }
    }
}
