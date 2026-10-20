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

    private readonly KaleAuthOptions _options;
    private readonly TimeProvider _clock;
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
        _http = options.BackchannelHandler is { } handler
            ? new HttpClient(handler, disposeHandler: false)
            : new HttpClient();
        _http.BaseAddress = options.Authority;

        // Callers wait on the token, so a hung token endpoint must not hold them (or the renewal lock) for long.
        _http.Timeout = TimeSpan.FromSeconds(10);

        // The call's own cancellation (and deadline) also stops waiting for the token.
        var callCredentials = CallCredentials.FromInterceptor(async (context, metadata) =>
            metadata.Add("Authorization", $"Bearer {await GetTokenAsync(context.CancellationToken)}"));
        var isHttps = options.Authority!.Scheme == Uri.UriSchemeHttps;
        _channel = GrpcChannel.ForAddress(options.Authority, new GrpcChannelOptions
        {
            HttpHandler = options.BackchannelHandler,
            DisposeHttpClient = false,
            Credentials = ChannelCredentials.Create(isHttps ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure, callCredentials),

            // Plain HTTP (h2c) only when the options explicitly allow it for local development.
            UnsafeUseInsecureChannelCallCredentials = !isHttps && options.AllowInsecureHttpDevOnly,
        });

        Sessions = new SessionService.SessionServiceClient(_channel);
        Roles = new RoleService.RoleServiceClient(_channel);
        Registration = new OperationRegistration.OperationRegistrationClient(_channel);
    }

    public SessionService.SessionServiceClient Sessions { get; }

    public RoleService.RoleServiceClient Roles { get; }

    public OperationRegistration.OperationRegistrationClient Registration { get; }

    public void Dispose()
    {
        _channel.Dispose();
        _http.Dispose();
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
