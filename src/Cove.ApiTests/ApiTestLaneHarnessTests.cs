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
            servers[0].ProcessStartedTimestamp.Should().BeLessThan(
                servers[1].ReadyTimestamp,
                "the first API host process should start before the second host becomes ready");
            servers[1].ProcessStartedTimestamp.Should().BeLessThan(
                servers[0].ReadyTimestamp,
                "the second API host process should start before the first host becomes ready");
            servers[0].BaseAddress.Should().NotBe(servers[1].BaseAddress);
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
