using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Auth.Client;

public static class KaleAuthSetup
{
    /// <summary>
    /// Everything a client app needs: token validation (signed by AuthService, audience = this application),
    /// [Authorize(Operation)] checked against AuthService, and operation registration on startup.
    /// </summary>
    public static IServiceCollection AddKaleAuth<TOperations>(this IServiceCollection services, Action<KaleAuthOptions> configure)
        where TOperations : struct, Enum
    {
        var options = new KaleAuthOptions();
        configure(options);
        options.Validate();

        var catalog = OperationCatalog.For<TOperations>(options.ApplicationKey);
        services.AddSingleton(options);
        services.AddKaleOperationAuthorization(catalog);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AuthServiceConnection>();
        services.AddSingleton<GrpcLookups>();
        services.AddSingleton<ISessionLookup>(provider => provider.GetRequiredService<GrpcLookups>());
        services.AddSingleton<IRoleOperationsLookup>(provider => provider.GetRequiredService<GrpcLookups>());

        services.AddHostedService<OperationRegistrationService>();
        services.AddHostedService<InvalidationListener>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(jwt =>
        {
            jwt.Authority = options.Authority!.AbsoluteUri;

            // DESIGN.md: apps must reject tokens whose audience is not their own.
            jwt.Audience = options.ApplicationKey;
            jwt.RequireHttpsMetadata = !options.AllowInsecureHttpDevOnly;
            jwt.BackchannelHttpHandler = options.BackchannelHandler;

            // Keep claim names as AuthService wrote them ("sub", "sid"), and accept only access tokens.
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.ValidTypes = ["at+jwt"];

            // The default 5 minutes would stretch a 10-minute token by half; AuthService's servers keep time.
            jwt.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(1);
        });
        return services;
    }
}
