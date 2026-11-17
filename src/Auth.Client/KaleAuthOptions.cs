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
    /// Where to call AuthService's gRPC API, when it's not <see cref="Authority"/>: e.g. the separate plain HTTP/2
    /// port of a local setup (plain HTTP needs <see cref="AllowInsecureHttpDevOnly"/>). Defaults to Authority.
    /// </summary>
    public Uri? GrpcAddress { get; set; }

    /// <summary>
    /// Optional HTTP handler for all calls to AuthService (token, JWKS, gRPC), e.g. for a proxy or for tests.
    /// Not a <see cref="SocketsHttpHandler"/> (refused): the gRPC channel takes one over (client-side load balancing
    /// replaces its ConnectCallback), and the token and JWKS requests through it then fail. An
    /// <see cref="HttpClientHandler"/> works, but sends no HTTP/2 keep-alive pings (a warning at startup).
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

        if (GrpcAddress is not null && !GrpcAddress.IsAbsoluteUri)
        {
            throw new InvalidOperationException("KaleAuthOptions.GrpcAddress must be an absolute address.");
        }

        if (GrpcAddress is not null && GrpcAddress.Scheme != Uri.UriSchemeHttps && !AllowInsecureHttpDevOnly)
        {
            throw new InvalidOperationException("KaleAuthOptions.GrpcAddress must use https (AllowInsecureHttpDevOnly is for local development only).");
        }

        if (UsesSocketsHttpHandler(BackchannelHandler))
        {
            throw new InvalidOperationException("KaleAuthOptions.BackchannelHandler can't be a SocketsHttpHandler (the gRPC channel takes it over, "
                + "and the token and JWKS requests through it then fail). Use an HttpClientHandler, e.g. for a proxy.");
        }

        // Relative URLs (connect/token) replace the last path segment unless the base ends with a slash.
        if (!Authority.AbsolutePath.EndsWith('/'))
        {
            Authority = new Uri(Authority.AbsoluteUri + "/");
        }
    }

    // gRPC looks through DelegatingHandlers for a SocketsHttpHandler, so this does too.
    private static bool UsesSocketsHttpHandler(HttpMessageHandler? handler)
    {
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler;
        }

        return handler is SocketsHttpHandler;
    }
}
