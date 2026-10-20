namespace AuthService.Outbox;

public static class OutboxSetup
{
    /// <summary>
    /// Publishing of invalidation events to RabbitMQ (RabbitMq:Uri, RabbitMq:PasswordFile). Required outside
    /// Development/Testing; without it, events stay in the outbox.
    /// </summary>
    public static IServiceCollection AddOutboxPublishing(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddScoped<OutboxPublisher>();

        var isDevOrTest = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var address = configuration["RabbitMq:Uri"];
        if (string.IsNullOrWhiteSpace(address))
        {
            if (!isDevOrTest)
            {
                throw new InvalidOperationException(
                    "RabbitMq:Uri is required: client apps learn about stopped sessions and edited roles through it.");
            }

            return services;
        }

        // Checked now, so a wrong address stops startup instead of failing quietly in the background forever.
        // Plain amqp:// would send the password and the events unencrypted, which only a local setup may do.
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !(uri.Scheme == "amqps" || (uri.Scheme == "amqp" && isDevOrTest)))
        {
            throw new InvalidOperationException(
                "RabbitMq:Uri must be an amqps:// address (plain amqp:// only in Development/Testing).");
        }

        // The password is a Podman secret: a mounted file, never part of the address in configuration.
        var passwordFile = configuration["RabbitMq:PasswordFile"];
        var password = string.IsNullOrEmpty(passwordFile) ? null : File.ReadAllText(passwordFile).Trim();

        services.AddSingleton(new RabbitMqSettings(uri, password));
        services.AddHostedService<OutboxPublishingService>();
        return services;
    }
}
