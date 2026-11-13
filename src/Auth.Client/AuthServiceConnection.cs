using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Auth.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Hosting;

namespace Auth.Client;

/// <summary>
/// The app's connection to AuthService: a client-credentials token (cached until shortly before it expires)
/// and one long-lived gRPC channel that sends it with every call.
/// </summary>
public sealed class AuthServiceConnection : IDisposable
{
    private static readonly TimeSpan RenewBeforeExpiry = TimeSpan.FromMinutes(1);

    // Open connections are closed after this long and opened again, so a changed DNS entry for AuthService
    // (e.g. it moved) is seen within minutes instead of never: otherwise they live as long as the process.
    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);

    private readonly KaleAuthOptions _options;
    private readonly TimeProvider _clock;
    private readonly HttpMessageHandler _handler;
    private readonly bool _ownsHandler;
    private readonly HttpClient _http;
    private readonly GrpcChannel _channel;
    private readonly SemaphoreSlim _renewing = new(1, 1);

    // One immutable object, so a reader never sees a new token with the old expiry (or the reverse).
    private volatile AppToken? _token;

    public AuthServiceConnection(KaleAuthOptions options, TimeProvider clock, IHostEnvironment environment)
    {
        // Checked here, not later: the host creates this (for the registration service) before starting
        // anything, so a misconfigured app stops before it sends its client secret over plain HTTP.
        if (options.AllowInsecureHttpDevOnly && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException("KaleAuthOptions.AllowInsecureHttpDevOnly is only allowed in Development and Testing.");
        }

        _options = options;
        _clock = clock;
        // One handler for the token requests and the gRPC channel: the application's (e.g. a test server's), else
        // our own. EnableMultipleHttp2Connections is what gRPC's default handler sets (more than 100 calls at once).
        _ownsHandler = options.BackchannelHandler is null;
        _handler = options.BackchannelHandler
            ?? new SocketsHttpHandler
            {
                PooledConnectionLifetime = ConnectionLifetime,
                EnableMultipleHttp2Connections = true,

                // The invalidation stream is idle most of the time. Without pings, a connection that died silently
                // (AuthService gone, a firewall forgetting it) would go unnoticed, and the caches would keep their
                // longer lifetimes while no events can arrive.
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            };
        _http = new HttpClient(_handler, disposeHandler: false);
        _http.BaseAddress = options.Authority;

        // Callers wait on the token, so a hung token endpoint must not hold them (or the renewal lock) for long.
        _http.Timeout = TimeSpan.FromSeconds(10);

        // The call's own cancellation (and deadline) also stops waiting for the token.
        var callCredentials = CallCredentials.FromInterceptor(async (context, metadata) =>
            metadata.Add("Authorization", $"Bearer {await GetTokenAsync(context.CancellationToken)}"));
        var grpcAddress = options.GrpcAddress ?? options.Authority!;
        var isHttps = grpcAddress.Scheme == Uri.UriSchemeHttps;
        _channel = GrpcChannel.ForAddress(grpcAddress, new GrpcChannelOptions
        {
            HttpHandler = _handler,
            DisposeHttpClient = false,
            Credentials = ChannelCredentials.Create(isHttps ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure, callCredentials),

            // Plain HTTP (h2c) only when the options explicitly allow it for local development.
            UnsafeUseInsecureChannelCallCredentials = !isHttps && options.AllowInsecureHttpDevOnly,
        });

        Sessions = new SessionService.SessionServiceClient(_channel);
        Roles = new RoleService.RoleServiceClient(_channel);
        Registration = new OperationRegistration.OperationRegistrationClient(_channel);
        Invalidation = new InvalidationStream.InvalidationStreamClient(_channel);
    }

    public InvalidationStream.InvalidationStreamClient Invalidation { get; }

    public SessionService.SessionServiceClient Sessions { get; }

    public RoleService.RoleServiceClient Roles { get; }

    public OperationRegistration.OperationRegistrationClient Registration { get; }

    /// <summary>The handler under the HTTP client and the gRPC channel.</summary>
    internal HttpMessageHandler Handler => _handler;

    /// <summary>
    /// Whether the handler pings an idle connection (ours does). One from the application is trusted only if it's
    /// a <see cref="SocketsHttpHandler"/> set up to: another kind (or one wrapped in a DelegatingHandler) can't be
    /// looked into.
    /// </summary>
    internal bool SendsKeepAlivePings =>
        _handler is SocketsHttpHandler sockets && sockets.KeepAlivePingDelay != Timeout.InfiniteTimeSpan;

    public void Dispose()
    {
        _channel.Dispose();
        _http.Dispose();

        // The application's handler is the application's to dispose.
        if (_ownsHandler)
        {
            _handler.Dispose();
        }

        _renewing.Dispose();
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is { } current && current.IsFresh(_clock.GetUtcNow()))
        {
            return current.Value;
        }

        await _renewing.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have renewed it while this one waited.
            if (_token is not { } token || !token.IsFresh(_clock.GetUtcNow()))
            {
                using var response = await _http.PostAsync(new Uri("connect/token", UriKind.Relative), new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = _options.ApplicationKey,
                    ["client_secret"] = _options.ClientSecret,
                }), cancellationToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken)
                    ?? throw new InvalidOperationException("AuthService returned an empty token response.");
                token = new AppToken(body.AccessToken, _clock.GetUtcNow().AddSeconds(body.ExpiresIn));
                _token = token;
            }

            return token.Value;
        }
        finally
        {
            _renewing.Release();
        }
    }

    private sealed record AppToken(string Value, DateTimeOffset ExpiresAt)
    {
        // Renew a little early, so a token never expires in the middle of a call.
        public bool IsFresh(DateTimeOffset now) => now < ExpiresAt - RenewBeforeExpiry;
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
