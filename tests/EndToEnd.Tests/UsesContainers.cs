using AuthService.Tests.Outbox;
using AuthService.Tests.Persistence;

namespace EndToEnd.Tests;

/// <summary>
/// One PostgreSQL and one RabbitMQ container for this test assembly (xUnit collections don't cross assemblies).
/// </summary>
[CollectionDefinition(Name)]
public sealed class UsesContainers : ICollectionFixture<PostgresFixture>, ICollectionFixture<RabbitMqFixture>
{
    public const string Name = "PostgreSQL and RabbitMQ";
}
