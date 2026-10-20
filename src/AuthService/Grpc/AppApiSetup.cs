using AuthService.Operations;
using AuthService.Roles;
using AuthService.Sessions;
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
                "A plain-HTTP Kestrel endpoint limited to Http2 serves gRPC without TLS (h2c): set Grpc:AllowInsecureDevOnly=true (Development and Testing only), or use https.");
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

    // Kestrel endpoints from configuration (Kestrel:Endpoints:{name}:Url and :Protocols). Without TLS, only an
    // endpoint limited to Http2 speaks HTTP/2, so that's the one that serves gRPC in plain text.
    private static bool HasPlainHttp2Endpoint(IConfiguration configuration)
    {
        foreach (var endpoint in configuration.GetSection("Kestrel:Endpoints").GetChildren())
        {
            var isPlainHttp = endpoint["Url"]?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true;
            var protocols = endpoint["Protocols"] ?? configuration["Kestrel:EndpointDefaults:Protocols"];
            if (isPlainHttp && string.Equals(protocols, "Http2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void MapAppApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<OperationRegistrationGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<SessionGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<RoleGrpcService>().RequireAuthorization(AppCaller.Policy);
    }
}
