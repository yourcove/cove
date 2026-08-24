using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Entities.Performers;

[Collection(ApiTestLane1Collection.Name)]
public sealed class PerformerTagApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenPerformerAndTag_WhenTagIsLinked_ThenPerformerHasTag()
    {
        // Arrange
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build(), TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name, TestContext.Current.CancellationToken);

        // Act
        await AsUser().LinkTagToPerformerAsync(tag, performer, TestContext.Current.CancellationToken);

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenTag_WhenPerformerIsCreatedWithTag_ThenPerformerHasTag()
    {
        // Arrange
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.AccidentalDoubleEntendre.Name, TestContext.Current.CancellationToken);

        // Act
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.BeaHaven.Name)
                .WithTag(tag)
                .Build(), TestContext.Current.CancellationToken);

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenPerformerWithTag_WhenAnotherTagIsLinked_ThenBothTagsArePreserved()
    {
        // Arrange
        var existingTag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name, TestContext.Current.CancellationToken);
        var additionalTag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name, TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithTag(existingTag)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().LinkTagToPerformerAsync(additionalTag, performer, TestContext.Current.CancellationToken);

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken);
        performerAfter.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(
            [existingTag.Id, additionalTag.Id]);
    }
}
