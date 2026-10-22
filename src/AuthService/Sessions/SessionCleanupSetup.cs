namespace AuthService.Sessions;

public static class SessionCleanupSetup
{
    /// <summary>The hourly cleanup of finished sessions and old OpenIddict tokens (Sessions:KeepFinishedFor, default 7 days).</summary>
    public static void AddSessionCleanup(this IServiceCollection services, IConfiguration configuration)
    {
        var keepFor = configuration.GetValue("Sessions:KeepFinishedFor", SessionCleanupSettings.Default);
        if (keepFor <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"Sessions:KeepFinishedFor must be positive, not {keepFor}.");
        }

        services.AddSingleton(new SessionCleanupSettings(keepFor));
        services.AddScoped<SessionCleanup>();
        services.AddHostedService<SessionCleanupService>();
    }
}
