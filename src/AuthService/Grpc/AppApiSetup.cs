using AuthService.Operations;
using AuthService.Roles;
using AuthService.Sessions;
using OpenIddict.Validation.AspNetCore;

namespace AuthService.Grpc;

/// <summary>The gRPC API client apps call with their client-credentials token (contracts in Auth.Contracts).</summary>
public static class AppApiSetup
{
    public static void AddAppApi(this IServiceCollection services)
    {
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

    public static void MapAppApi(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<OperationRegistrationGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<SessionGrpcService>().RequireAuthorization(AppCaller.Policy);
        endpoints.MapGrpcService<RoleGrpcService>().RequireAuthorization(AppCaller.Policy);
    }
}
