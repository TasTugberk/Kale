namespace Auth.Contracts;

/// <summary>Where invalidation events (invalidation.proto) travel: shared by AuthService and Auth.Client.</summary>
public static class InvalidationExchange
{
    /// <summary>The durable RabbitMQ fanout exchange; every client app instance binds its own queue to it.</summary>
    public const string Name = "kale.invalidation";
}
