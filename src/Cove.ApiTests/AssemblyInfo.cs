using Xunit;
using Cove.ApiTests.Infrastructure;

[assembly: AssemblyFixture(typeof(CoveApiTestPool))]
[assembly: CollectionBehavior(MaxParallelThreads = CoveApiTestPool.MaxParallelThreads, ParallelAlgorithm = Xunit.Sdk.ParallelAlgorithm.Conservative)]
