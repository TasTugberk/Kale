using AuthService.Operations;
using AuthService.Roles;
using AuthService.Sessions;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenIddict.Validation.AspNetCore;

namespace AuthService.Grpc;

/// <summary>The gRPC API client apps call with their client-credentials token (contracts in Auth.Contracts).</summary>
public static class AppApiSetup
{
    public static void AddAppApi(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        // DESIGN.md: plain HTTP/2 (h2c) only inside the local compose network, clearly marked dev-only in config.
        // The h2c listener itself is an ordinary Kestrel endpoint; this flag must be set for it, and only locally.
        var allowInsecureGrpc = configuration.GetValue<bool>("Grpc:AllowInsecureDevOnly");
        if (allowInsecureGrpc && !environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
        {
            throw new InvalidOperationException(
                "Grpc:AllowInsecureDevOnly is only allowed in Development and Testing. gRPC must use TLS.");
        }

        if (!allowInsecureGrpc && HasPlainHttp2Endpoint(configuration))
        {
            throw new InvalidOperationException(
                "Kestrel is set to serve HTTP/2 without TLS (h2c: Protocols=Http2 on a plain-HTTP endpoint or in EndpointDefaults). For local development set Grpc:AllowInsecureDevOnly=true; otherwise remove that Protocols setting and use https (TLS endpoints get HTTP/2 without it).");
        }

        services.AddGrpc();
        services.AddScoped<OperationRegistrar>();
        services.AddScoped<EffectiveOperations>();
        services.AddScoped<EligibleRoles>();
        services.AddScoped<SessionCheck>();

        // No default scheme here: the AppCaller policy names the bearer scheme, and the sign-in pages use a cookie.
        services.AddAuthentication();
        services.AddAuthorization(options => options.AddPolicy(AppCaller.Policy, policy => policy
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireAssertion(context => AppCaller.IsApplicationToken(context.User))));
    }

    // Without TLS, Kestrel speaks HTTP/2 only where the protocols are limited to Http2: on one endpoint
    // (Kestrel:Endpoints:{name}:Protocols), or on every endpoint by default (Kestrel:EndpointDefaults:Protocols).
    // The default also reaches addresses from ASPNETCORE_URLS or --urls, which aren't listed under
    // Kestrel:Endpoints, so it counts on its own. (HTTP/2 over TLS never needs it: TLS negotiates the protocol.)
    private static bool HasPlainHttp2Endpoint(IConfiguration configuration)
    {
        if (IsHttp2Only(configuration["Kestrel:EndpointDefaults:Protocols"]))
        {
            return true;
        }

        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var isPlainHttp = endpoint["Url"]?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true;
            if (isPlainHttp && IsHttp2Only(endpoint["Protocols"]))
            {
                return true;
            }
        }

        return false;
    }

    // Read the value the way Kestrel does (as the enum: any case, surrounding spaces, or its number), so no
    // spelling Kestrel accepts can slip past the check.
    private static bool IsHttp2Only(string? protocols) =>
        Enum.TryParse<HttpProtocols>(protocols, ignoreCase: true, out var parsed) && parsed == HttpProtocols.Http2;

    public static void MapAppApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<OperationRegistrationGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<SessionGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<RoleGrpcService>().RequireAuthorization(AppCaller.Policy);
    }
}
