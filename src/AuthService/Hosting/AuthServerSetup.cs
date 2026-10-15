using AuthService.Keys;
using AuthService.Persistence;
using AuthService.SignIn;
using AuthService.Tokens;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AuthService.Hosting;

/// <summary>
/// The OpenID Connect / OAuth server (OpenIddict) and its keys, as decided in DESIGN.md and
/// docs/design/phase-3-openiddict.md.
/// </summary>
public static class AuthServerSetup
{
    /// <summary>Decision: sessions last 8 h (absolute) unless Sessions:Lifetime says otherwise.</summary>
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromHours(8);

    public static void AddAuthServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var isDevOrTest = environment.IsDevelopment() || environment.IsEnvironment("Testing");

        // Dev/test only: the in-memory test server and the local compose network speak plain HTTP.
        var allowInsecureHttp = configuration.GetValue<bool>("OpenIddict:AllowInsecureHttpDevOnly");
        if (allowInsecureHttp && !isDevOrTest)
        {
            throw new InvalidOperationException(
                "OpenIddict:AllowInsecureHttpDevOnly is only allowed in Development and Testing. Tokens must travel over HTTPS.");
        }

        // A fixed issuer: without it, "iss" and the discovery URLs would follow each request's Host header.
        var issuer = configuration["Auth:Issuer"];
        if (string.IsNullOrWhiteSpace(issuer) && !isDevOrTest)
        {
            throw new InvalidOperationException("Set Auth:Issuer to the public base URL of AuthService, e.g. https://auth.example.com/.");
        }

        // Refresh tokens live exactly as long as a session, so the keys that encrypt them must outlive both.
        var sessionLifetime = configuration.GetValue("Sessions:Lifetime", DefaultSessionLifetime);
        var keyRotation = new KeyRotationOptions();
        if (sessionLifetime <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Sessions:Lifetime must be positive, not {sessionLifetime}.");
        }

        if (sessionLifetime + ServerKeyRefresher.RefreshInterval > keyRotation.Retention)
        {
            throw new InvalidOperationException(
                $"Sessions:Lifetime ({sessionLifetime}) is too long: refresh tokens would outlive the keys that encrypt them ({keyRotation.Retention}).");
        }

        services.AddSingleton(new SessionSettings(sessionLifetime));
        services.AddServerKeyProtection(configuration, environment);
        services.AddSingleton(keyRotation);
        services.AddScoped<KeyRing>();
        services.AddSingleton<ServerKeyRefresher>();
        services.AddHostedService(provider => provider.GetRequiredService<ServerKeyRefresher>());

        // Keys come from KeyRing: ConfigureServerKeys hands them to OpenIddict each time its options are built.
        services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureServerKeys>();

        services.AddOpenIddict()
            .AddCore(options => options
                .UseEntityFrameworkCore()
                .UseDbContext<AuthDbContext>()
                .ReplaceDefaultEntities<Guid>())
            .AddServer(options =>
            {
                if (!string.IsNullOrWhiteSpace(issuer))
                {
                    options.SetIssuer(new Uri(issuer));
                }

                options
                    .SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetEndSessionEndpointUris("connect/endsession");

                // DESIGN.md: authorization code + PKCE for users, client credentials for apps, refresh tokens.
                options
                    .AllowAuthorizationCodeFlow()
                    .RequireProofKeyForCodeExchange()
                    .AllowClientCredentialsFlow()
                    .AllowRefreshTokenFlow();

                // PKCE with S256 only. OpenIddict also allows "plain" by default, which sends the verifier itself
                // and so protects nothing against an intercepted code.
                options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

                // Signed (not encrypted) JWTs, so client apps validate them locally through JWKS.
                options.DisableAccessTokenEncryption();
                options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                options.SetRefreshTokenLifetime(sessionLifetime);

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough()
                    .EnableEndSessionEndpointPassthrough();
                if (allowInsecureHttp)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })

            // AuthService also accepts its own tokens, for the gRPC API apps call: only tokens issued by this
            // server, for the API's own audience.
            .AddValidation(options =>
            {
                options.UseLocalServer();
                options.UseAspNetCore();
                options.AddAudiences(TokenEndpoint.AuthServiceAudience);
            });
    }
}
