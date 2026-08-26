using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Catalog;

public sealed class CatalogApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenCanonicalMovie_WhenCreatedWithRelationships_ThenVideoReturnsCanonicalCatalogData()
    {
        // Arrange
        var movie = TestCatalog.Movies.RaidersOfTheLostCorset;
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name, TestContext.Current.CancellationToken);
        var performers = await Task.WhenAll(movie.Cast.Select(CreatePerformerAsync));
        var tags = await Task.WhenAll(movie.Tags.Select(CreateTagAsync));

        // Act
        var created = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(movie.Title)
                .WithStudio(studio)
                .WithPerformers(performers)
                .WithTags(tags)
                .Build(), TestContext.Current.CancellationToken);

        // Assert
        var video = await AsUser().GetVideoByIdAsync(created.Id, TestContext.Current.CancellationToken);
        video.Title.Should().Be(movie.Title);
        video.StudioId.Should().Be(studio.Id);
        video.StudioName.Should().Be(TestCatalog.Studio.Name);
        video.Performers.Select(performer => performer.Name).Should().BeEquivalentTo(movie.Cast.Select(performer => performer.Name));
        video.Tags.Select(tag => tag.Name).Should().BeEquivalentTo(movie.Tags.Select(tag => tag.Name));
    }

    private Task<Cove.Core.DTOs.PerformerDto> CreatePerformerAsync(CatalogPerformer performer)
        => AsUser().CreatePerformerAsync(
            new PerformerBuilder()
                .WithName(performer.Name)
                .WithDetails(performer.Description)
                .Build());

    private Task<Cove.Core.DTOs.TagDetailDto> CreateTagAsync(CatalogTag tag)
        => AsUser().CreateTagAsync(
            new TagBuilder()
                .WithName(tag.Name)
                .WithDescription(tag.Description)
                .Build());
}
