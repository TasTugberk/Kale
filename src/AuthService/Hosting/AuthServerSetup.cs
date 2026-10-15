using AuthService.Keys;
using AuthService.Persistence;
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
    /// <summary>As long as a session (8 h): refreshing never extends past it, and keys outlive it.</summary>
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromHours(8);

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

        services.AddServerKeyProtection(configuration, environment);
        services.AddSingleton(new KeyRotationOptions());
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
                    .SetTokenEndpointUris("connect/token");

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
                options.SetRefreshTokenLifetime(RefreshTokenLifetime);

                var aspNetCore = options.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();
                if (allowInsecureHttp)
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            });
    }
}
