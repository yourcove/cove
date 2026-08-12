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
