namespace Cove.ApiTests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class ApiTestLane1Collection : ICollectionFixture<CoveApiTestFixture>
{
    public const string Name = "Fluent API tests lane 1";
}

[CollectionDefinition(Name)]
public sealed class ApiTestLane2Collection : ICollectionFixture<CoveApiTestFixture>
{
    public const string Name = "Fluent API tests lane 2";
}

[CollectionDefinition(Name)]
public sealed class DatabaseRestoreApiTestCollection : ICollectionFixture<CoveApiTestFixture>
{
    public const string Name = "Database restore API tests";
}

[CollectionDefinition(Name)]
public sealed class DatabaseWipeApiTestCollection : ICollectionFixture<CoveApiTestFixture>
{
    public const string Name = "Database wipe API tests";
}
