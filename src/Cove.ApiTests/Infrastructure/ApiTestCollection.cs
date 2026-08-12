namespace Cove.ApiTests.Infrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiTestCollection : ICollectionFixture<CoveApiTestFixture>
{
    public const string Name = "Fluent API tests";
}
