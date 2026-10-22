using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Auth.Client;

public static class KaleAuthSetup
{
    /// <summary>The configuration section <see cref="AddKaleAuth{TOperations}(IHostApplicationBuilder, Action{KaleAuthOptions}?)"/> reads.</summary>
    public const string ConfigurationSection = "Kale";

    /// <summary>
    /// The usual way: <c>builder.AddKaleAuth&lt;MyOperations&gt;();</c>. Every setting comes from the "Kale"
    /// configuration section (appsettings, user secrets, environment): Authority, ApplicationKey, ClientSecret,
    /// GrpcAddress, RabbitMqUri, RabbitMqPassword, AllowInsecureHttpDevOnly. In a container the secrets are
    /// mounted files: Kale:ClientSecretFile and Kale:RabbitMqPasswordFile are read instead. For the local Kale,
    /// scripts/register-app.sh in the Kale repository writes all of it into the project's user secrets.
    /// </summary>
    /// <param name="configure">Optional changes in code, after the configuration.</param>
    public static IHostApplicationBuilder AddKaleAuth<TOperations>(this IHostApplicationBuilder builder, Action<KaleAuthOptions>? configure = null)
        where TOperations : struct, Enum
    {
        var section = builder.Configuration.GetSection(ConfigurationSection);
        builder.Services.AddKaleAuth<TOperations>(options =>
        {
            section.Bind(options);
            options.ClientSecret = ReadSecretFile(section["ClientSecretFile"]) ?? options.ClientSecret;
            options.RabbitMqPassword = ReadSecretFile(section["RabbitMqPasswordFile"]) ?? options.RabbitMqPassword;
            configure?.Invoke(options);

            // Before the generic checks: say which settings, and how to get them for the local Kale.
            if (options.Authority is null || string.IsNullOrWhiteSpace(options.ApplicationKey) || string.IsNullOrWhiteSpace(options.ClientSecret))
            {
                throw new InvalidOperationException(
                    "Kale isn't configured: set Kale:Authority, Kale:ApplicationKey and Kale:ClientSecret (appsettings, user " +
                    "secrets or environment; in a container Kale:ClientSecretFile). For the local Kale, run " +
                    "scripts/register-app.sh <key> <project folder> in the Kale repository: it registers the application and " +
                    "writes these into the project's user secrets.");
            }
        });
        return builder;
    }

    // A mounted secret file, without the newline `podman secret create` leaves; null when no file is configured.
    private static string? ReadSecretFile(string? path) =>
        string.IsNullOrEmpty(path) ? null : File.ReadAllText(path).Trim();

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
