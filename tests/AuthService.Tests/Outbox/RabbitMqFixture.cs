using AuthService.Tests.Persistence;
using RabbitMQ.Client;
using Testcontainers.RabbitMq;

namespace AuthService.Tests.Outbox;

/// <summary>One RabbitMQ container shared by the tests in the <see cref="UsesPostgresAndRabbitMq"/> collection.</summary>
public sealed class RabbitMqFixture : IAsyncLifetime
{
    // Official images are multi-arch (arm64 + amd64).
    public const string Image = "docker.io/library/rabbitmq:4-alpine";

    public const string Password = "kale-test-password";

    private readonly RabbitMqContainer _container = new RabbitMqBuilder(Image).WithPassword(Password).Build();

    public Uri Uri => new(_container.GetConnectionString());

    /// <summary>The address with only the user name, as the app gets it (the password comes from a file).</summary>
    public Uri UriWithoutPassword => new UriBuilder(Uri) { Password = "" }.Uri;

    /// <summary>Drops every client connection, as a broker restart would.</summary>
    public async Task CloseAllConnectionsAsync()
    {
        var result = await _container.ExecAsync(["rabbitmqctl", "close_all_connections", "closed by test"]);
        result.ExitCode.ShouldBe(0, result.Stderr);
    }

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    public async Task<IConnection> ConnectAsync() => await new ConnectionFactory { Uri = Uri }.CreateConnectionAsync();
}

/// <summary>
/// Tests that need RabbitMQ as well as PostgreSQL. A separate collection, so the other database tests don't wait
/// for a broker to start (and the two collections run in parallel).
/// </summary>
[CollectionDefinition(Name)]
public sealed class UsesPostgresAndRabbitMq : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "Postgres and RabbitMQ";
}
