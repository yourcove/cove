using Xunit;
using Xunit.Sdk;
using Xunit.v3;
using Cove.ApiTests.Infrastructure;

[assembly: AssemblyFixture(typeof(CoveApiTestPool))]
[assembly: Parallelization(
    Mode = ParallelMode.Collections,
    MaxThreads = CoveApiTestPool.MaxParallelThreads,
    Algorithm = ParallelAlgorithm.Conservative)]
