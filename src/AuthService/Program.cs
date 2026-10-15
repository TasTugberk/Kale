using AuthService.Applications;
using AuthService.Keys;
using AuthService.Persistence;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);

// Read eagerly so a missing connection string stops startup instead of failing on the first request.
var authDbConnectionString = builder.Configuration.GetConnectionString(AuthDatabaseOptions.ConnectionStringName);
if (string.IsNullOrWhiteSpace(authDbConnectionString))
{
    throw new InvalidOperationException(
        $"Connection string '{AuthDatabaseOptions.ConnectionStringName}' is not configured.");
}

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AuthDbContext>(options => options.UseAuthDatabase(authDbConnectionString));

builder.Services.AddServerKeyProtection(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(new KeyRotationOptions());
builder.Services.AddScoped<KeyRing>();

builder.Services.AddSingleton<ServerKeyRefresher>();
builder.Services.AddHostedService(services => services.GetRequiredService<ServerKeyRefresher>());
builder.Services.AddSingleton<IConfigureOptions<OpenIddictServerOptions>, ConfigureServerKeys>();

// Dev/test only: the in-memory test server and the local compose network speak plain HTTP.
var allowInsecureHttp = builder.Configuration.GetValue<bool>("OpenIddict:AllowInsecureHttpDevOnly");

builder.Services.AddOpenIddict()
    .AddCore(options => options
        .UseEntityFrameworkCore()
        .UseDbContext<AuthDbContext>()
        .ReplaceDefaultEntities<Guid>())
    .AddServer(options =>
    {
        options
            .SetAuthorizationEndpointUris("connect/authorize")
            .SetTokenEndpointUris("connect/token");

        // DESIGN.md: authorization code + PKCE for users, client credentials for apps, refresh tokens.
        options
            .AllowAuthorizationCodeFlow()
            .RequireProofKeyForCodeExchange()
            .AllowClientCredentialsFlow()
            .AllowRefreshTokenFlow();

        // PKCE with S256 only. OpenIddict also allows "plain" by default, which sends the verifier itself and
        // so protects nothing against an intercepted code.
        options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));

        // Signed (not encrypted) JWTs, so client apps validate them locally through JWKS.
        options.DisableAccessTokenEncryption();
        options.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));

        // Keys come from KeyRing through ConfigureServerKeys.
        var aspNetCore = options.UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough();
        if (allowInsecureHttp)
        {
            aspNetCore.DisableTransportSecurityRequirement();
        }
    });

builder.Services.AddScoped<ApplicationService>();

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

app.Run();
