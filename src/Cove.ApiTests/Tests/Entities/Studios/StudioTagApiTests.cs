using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Studios;

[Collection(ApiTestLane2Collection.Name)]
public sealed class StudioTagApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenStudioAndTag_WhenTagIsLinked_ThenStudioHasOnlyTag()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        var tag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);

        // Act
        await AsUser().UpdateStudioAsync(studio.Id, new { tagIds = new[] { tag.Id } });

        // Assert
        var studioAfter = await AsUser().GetStudioByIdAsync(studio.Id);
        studioAfter.Tags.Should().ContainSingle().Which.Id.Should().Be(tag.Id);
    }

    [Fact]
    public async Task GivenStudioWithTag_WhenAnotherTagIsLinked_ThenBothTagsArePreserved()
    {
        // Arrange
        var existingTag = await AsUser().CreateTagAsync(TestCatalog.Tags.Brooding.Name);
        var additionalTag = await AsUser().CreateTagAsync(TestCatalog.Tags.TheatricalEntrance.Name);
        var studio = await AsUser().CreateStudioAsync(
            new StudioBuilder()
                .WithName(TestCatalog.Studio.Name)
                .WithTag(existingTag)
                .Build());

        // Act
        await AsUser().UpdateStudioAsync(
            studio.Id,
            new { tagIds = new[] { existingTag.Id, additionalTag.Id } });

        // Assert
        var studioAfter = await AsUser().GetStudioByIdAsync(studio.Id);
        studioAfter.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(
            [existingTag.Id, additionalTag.Id]);
    }
}
