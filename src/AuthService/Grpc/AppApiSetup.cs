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

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
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
