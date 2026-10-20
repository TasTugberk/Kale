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
    /// RabbitMQ, where AuthService announces stopped sessions and edited roles (amqps://user@host/; amqp:// only in
    /// Development/Testing). Required outside Development/Testing: without it a stopped session is noticed only
    /// when its cached status expires.
    /// </summary>
    public Uri? RabbitMqUri { get; set; }

    /// <summary>RabbitMQ password, when it's not in <see cref="RabbitMqUri"/> (keep it in a secret store).</summary>
    public string? RabbitMqPassword { get; set; }

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

        if (GrpcAddress is not null && (!GrpcAddress.IsAbsoluteUri || (GrpcAddress.Scheme != Uri.UriSchemeHttps && !AllowInsecureHttpDevOnly)))
        {
            throw new InvalidOperationException(
                "KaleAuthOptions.GrpcAddress must be an absolute https address (plain http needs AllowInsecureHttpDevOnly, for local development only).");
        }

        if (RabbitMqUri is not null && (!RabbitMqUri.IsAbsoluteUri || RabbitMqUri.Scheme is not ("amqp" or "amqps")))
        {
            throw new InvalidOperationException("KaleAuthOptions.RabbitMqUri must be an amqps:// (or, locally, amqp://) address.");
        }

        // Relative URLs (connect/token) replace the last path segment unless the base ends with a slash.
        if (!Authority.AbsolutePath.EndsWith('/'))
        {
            Authority = new Uri(Authority.AbsoluteUri + "/");
        }
    }
}
