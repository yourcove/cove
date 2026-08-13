using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerTagApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag()
    {
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName("Example Performer")
                .Build());
        var tag = await AsUser().CreateTagAsync(
            new TagBuilder()
                .WithName("Example Tag")
                .Build());

        await AsUser().LinkTagToPerformerAsync(tag, performer);

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenTag_WhenPerformerIsCreatedWithTag_ThenPerformerHasTag()
    {
        var tag = await AsUser().CreateTagAsync("Creation Tag");

        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName("Tagged From Creation")
                .WithTag(tag)
                .Build());

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }
}
