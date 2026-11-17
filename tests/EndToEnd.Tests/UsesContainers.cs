using AuthService.Tests.Persistence;

namespace EndToEnd.Tests;

/// <summary>
/// One PostgreSQL container for this test assembly (xUnit collections don't cross assemblies). Client apps get
/// their invalidation events from AuthService's gRPC stream, so no broker is needed.
/// </summary>
[CollectionDefinition(Name)]
public sealed class UsesContainers : ICollectionFixture<PostgresFixture>
{
    public const string Name = "PostgreSQL";
}
