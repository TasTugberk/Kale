namespace Auth.Client;

/// <summary>How a client app reaches AuthService.</summary>
public sealed class KaleAuthOptions
{
    /// <summary>AuthService's base address, e.g. https://auth.example.com/.</summary>
    public Uri? Authority { get; set; }

    /// <summary>This application's key in AuthService (its client id and token audience), e.g. "billing".</summary>
    public string ApplicationKey { get; set; } = "";

    /// <summary>From AuthService, shown once when the application was created. Keep it in a secret store.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>Allows a plain-HTTP Authority. Only in Development/Testing (the app refuses to start otherwise).</summary>
    public bool AllowInsecureHttpDevOnly { get; set; }

    /// <summary>
    /// Optional HTTP handler for all calls to AuthService (token, JWKS, gRPC), e.g. for a proxy or for tests.
    /// </summary>
    public HttpMessageHandler? BackchannelHandler { get; set; }

    internal void Validate()
    {
        if (Authority is not { IsAbsoluteUri: true })
        {
            throw new InvalidOperationException("Set KaleAuthOptions.Authority to AuthService's absolute base address.");
        }

        if (Authority.Scheme != Uri.UriSchemeHttps && !AllowInsecureHttpDevOnly)
        {
            throw new InvalidOperationException("KaleAuthOptions.Authority must use https (AllowInsecureHttpDevOnly is for local development only).");
        }

        if (string.IsNullOrWhiteSpace(ApplicationKey) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException("Set KaleAuthOptions.ApplicationKey and ClientSecret.");
        }

        // Relative URLs (connect/token) replace the last path segment unless the base ends with a slash.
        if (!Authority.AbsolutePath.EndsWith('/'))
        {
            Authority = new Uri(Authority.AbsoluteUri + "/");
        }
    }
}
