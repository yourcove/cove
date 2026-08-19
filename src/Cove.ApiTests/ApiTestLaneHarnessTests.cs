using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiTestLaneHarnessCollection
{
    public const string Name = "API test lane harness";
}

[Collection(ApiTestLaneHarnessCollection.Name)]
public sealed class ApiTestLaneHarnessTests
{
    [Fact]
    public async Task GivenTwoLanes_WhenHostsStart_ThenHostStartupOverlaps()
    {
        var starts = new[]
        {
            CoveApiServer.StartAsync(),
            CoveApiServer.StartAsync(),
        };

        try
        {
            var servers = await Task.WhenAll(starts);
            Assert.True(
                servers[0].ProcessStartedTimestamp < servers[1].ReadyTimestamp
                    && servers[1].ProcessStartedTimestamp < servers[0].ReadyTimestamp,
                "Expected the two isolated API host processes to start concurrently.");
            Assert.NotEqual(servers[0].BaseAddress, servers[1].BaseAddress);
        }
        finally
        {
            var startedServers = starts
                .Where(start => start.IsCompletedSuccessfully)
                .Select(start => start.Result)
                .ToArray();
            await Task.WhenAll(startedServers.Select(async server => await server.DisposeAsync()));
        }
    }
}
