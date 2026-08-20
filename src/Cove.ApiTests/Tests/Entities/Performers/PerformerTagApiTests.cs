using Cove.ApiTests.Assertions;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

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
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.CherryPoppins.Name)
                .Build());
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);

        // Act
        await AsUser().LinkTagToPerformerAsync(tag, performer);

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenTag_WhenPerformerIsCreatedWithTag_ThenPerformerHasTag()
    {
        // Arrange
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.AccidentalDoubleEntendre.Name);

        // Act
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.BeaHaven.Name)
                .WithTag(tag)
                .Build());

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.ShouldHaveTag(tag);
    }

    [Fact]
    public async Task GivenPerformerWithTag_WhenAnotherTagIsLinked_ThenBothTagsArePreserved()
    {
        // Arrange
        var existingTag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);
        var additionalTag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);
        var performer = await AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .WithTag(existingTag)
                .Build());

        // Act
        await AsUser().LinkTagToPerformerAsync(additionalTag, performer);

        // Assert
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);
        performerAfter.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(
            [existingTag.Id, additionalTag.Id]);
    }
}
