using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class PerformerCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformerDetails_WhenPerformerIsCreated_ThenPerformerCanBeRetrieved()
    {
        Assert.Empty(await AsUser().GetPerformersAsync());

        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName("Lane Two Performer")
                .Build());

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        Assert.Equal(performer.Name, performerAfter.Name);
    }
}
