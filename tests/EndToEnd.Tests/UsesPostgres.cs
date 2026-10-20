using AuthService.Tests.Persistence;

namespace EndToEnd.Tests;

/// <summary>One PostgreSQL container for this test assembly (xUnit collections don't cross assemblies).</summary>
[CollectionDefinition(Name)]
public sealed class UsesPostgres : ICollectionFixture<PostgresFixture>
{
    public const string Name = "Postgres";
}
