using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Tests.Interactions;

[Collection(ApiTestLane1Collection.Name)]
public sealed class RelationshipLikeRollupApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/like")]
    public async Task GivenVideoWithPerformers_WhenMembersLikeVideo_ThenEachPerformerShowsMemberVideoLikes()
    {
        // Arrange
        var movie = TestCatalog.Movies.RaidersOfTheLostCorset;
        var performers = await Task.WhenAll(movie.Cast.Select(performer =>
            AsUser().CreatePerformerAsync(
                new PerformerBuilder()
                    .WithName(performer.Name)
                    .Build())));
        var unrelatedPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder()
                .WithName(TestCatalog.Performers.VelvetThunder.Name)
                .Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(movie.Title)
                .WithPerformers(performers)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser(ApiTestUsers.Eva).IncrementVideoLikeAsync(video, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).IncrementVideoLikeAsync(video, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).IncrementVideoLikeAsync(video, TestContext.Current.CancellationToken);

        // Assert
        foreach (var performer in performers)
        {
            (await AsUser(ApiTestUsers.Eva).GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(2);
            (await AsUser(ApiTestUsers.Anthony).GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(1);
            (await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(0);
        }
        (await AsUser(ApiTestUsers.Eva).GetPerformerByIdAsync(unrelatedPerformer.Id, TestContext.Current.CancellationToken)).LikeCount.Should().Be(0);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/images/{id:int}/like")]
    [CoversEndpoint("GET", "/api/galleries/{id:int}/like-count")]
    public async Task GivenGalleryWithMedia_WhenMembersLikeMedia_ThenGalleryShowsMemberMediaLikes()
    {
        // Arrange
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
                .WithTitle("Wardrobe Vault Stills")
                .Build(), TestContext.Current.CancellationToken);
        var unrelatedGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder()
                .WithTitle("Unrelated Production Stills")
                .Build(), TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(TestCatalog.Movies.RaidersOfTheLostCorset.Title)
                .WithGallery(gallery)
                .Build(), TestContext.Current.CancellationToken);
        var image = await AsUser().CreateImageAsync(new ImageBuilder()
                .WithTitle("Golden Corset Discovery")
                .WithGallery(gallery)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser(ApiTestUsers.Eva).IncrementVideoLikeAsync(video, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).IncrementVideoLikeAsync(video, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).IncrementImageLikeAsync(image, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Anthony).IncrementImageLikeAsync(image, TestContext.Current.CancellationToken);

        // Assert
        (await AsUser(ApiTestUsers.Eva).GetGalleryLikeCountAsync(gallery, TestContext.Current.CancellationToken)).Should().Be(3);
        (await AsUser(ApiTestUsers.Anthony).GetGalleryLikeCountAsync(gallery, TestContext.Current.CancellationToken)).Should().Be(1);
        (await AsUser().GetGalleryLikeCountAsync(gallery, TestContext.Current.CancellationToken)).Should().Be(0);
        (await AsUser(ApiTestUsers.Eva).GetGalleryLikeCountAsync(unrelatedGallery, TestContext.Current.CancellationToken)).Should().Be(0);
    }
}
