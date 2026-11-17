using Npgsql;

namespace AuthService.Invalidation;

public static class InvalidationStreamSetup
{
    /// <summary>
    /// The client apps' invalidation streams (gRPC InvalidationStream, mapped with the rest of the app API) and the
    /// listener that feeds them from PostgreSQL.
    /// </summary>
    public static IServiceCollection AddInvalidationStream(this IServiceCollection services, string authDbConnectionString)
    {
        var listenerConnection = new NpgsqlConnectionStringBuilder(authDbConnectionString)
        {
            // Its own connection, held open for good: a pooled one would be reset (which ends LISTEN) whenever it
            // went back to the pool, and would take a place in the pool the app's queries need.
            Pooling = false,
            ApplicationName = InvalidationNotificationListener.ConnectionName,
        };

        services.AddSingleton<InvalidationSubscribers>();
        services.AddSingleton(new InvalidationListenerSettings(listenerConnection.ConnectionString));
        services.AddHostedService<InvalidationNotificationListener>();
        return services;
    }
}
