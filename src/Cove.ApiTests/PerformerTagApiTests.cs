using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

public sealed class PerformerTagApiTests(ITestOutputHelper output) : ApiTest(output)
{
    [Fact]
    public async Task GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag()
    {
        Assert.Empty(await AsUser().GetPerformersAsync());

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
}
