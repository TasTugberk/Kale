namespace AuthService.Outbox;

public static class OutboxSetup
{
    /// <summary>
    /// Publishing of invalidation events to RabbitMQ (RabbitMq:Uri). Required outside Development/Testing; without
    /// it, events stay in the outbox.
    /// </summary>
    public static IServiceCollection AddOutboxPublishing(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddScoped<OutboxPublisher>();

        var uri = configuration["RabbitMq:Uri"];
        if (string.IsNullOrWhiteSpace(uri))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    "RabbitMq:Uri is required: client apps learn about stopped sessions and edited roles through it.");
            }

            return services;
        }

        services.AddSingleton(new RabbitMqSettings(new Uri(uri)));
        services.AddHostedService<OutboxPublishingService>();
        return services;
    }
}
