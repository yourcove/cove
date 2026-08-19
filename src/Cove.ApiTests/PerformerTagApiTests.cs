using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
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
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);

        await AsUser().LinkTagToPerformerAsync(tag, performer);

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenTag_WhenPerformerIsCreatedWithTag_ThenPerformerHasTag()
    {
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.AccidentalDoubleEntendre.Name);

        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.BeaHaven.Name)
                .WithTag(tag)
                .Build());

        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }
}
