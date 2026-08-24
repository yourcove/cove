using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoStudioRelationshipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/videos/{id:int}")]
    public async Task GivenUnlinkedStudio_WhenLinkedRemovedAndRelinked_ThenVideoVisibilityTracksRelationship()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studios.BarelyDressedPictures.Name, TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title, TestContext.Current.CancellationToken);

        // Act & Assert
        await AssertRelationshipAsync(video, studio, isLinked: false);
        await AsUser().UpdateVideoAsync(video.Id, new { studioId = studio.Id }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, studio, isLinked: true);
        await AsUser().UpdateVideoAsync(video.Id, new { clearFields = new[] { "studioId" } }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, studio, isLinked: false);
        await AsUser().UpdateVideoAsync(video.Id, new { studioId = studio.Id }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, studio, isLinked: true);
    }

    [Fact]
    public async Task GivenLinkedStudio_WhenDifferentStudioIsAssigned_ThenExistingRelationshipIsReplaced()
    {
        // Arrange
        var removed = await AsUser().CreateStudioAsync(TestCatalog.Studios.BarelyDressedPictures.Name, TestContext.Current.CancellationToken);
        var added = await AsUser().CreateStudioAsync(TestCatalog.Studios.SecondTakeFeatures.Name, TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(TestCatalog.Movies.TheFastAndTheFlirtatious.Title)
                .WithStudio(removed)
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().UpdateVideoAsync(video.Id, new { studioId = added.Id }, TestContext.Current.CancellationToken);

        // Assert
        await AssertRelationshipAsync(video, removed, isLinked: false);
        await AssertRelationshipAsync(video, added, isLinked: true);
    }

    [Fact]
    public async Task GivenManyVideos_WhenLinkedToStudio_ThenStudioReturnsEveryVideo()
    {
        // Arrange
        const int videoCount = 20;
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studios.ElectricMarquee.Name, TestContext.Current.CancellationToken);
        var videos = await Task.WhenAll(Enumerable.Range(1, videoCount)
            .Select(index => AsUser().CreateVideoAsync($"API test video {index} {Guid.NewGuid():N}")));

        // Act
        foreach (var video in videos)
            await AsUser().UpdateVideoAsync(video.Id, new { studioId = studio.Id }, TestContext.Current.CancellationToken);

        // Assert
        var videosForStudio = await AsUser().GetVideosByStudioAsync(studio.Id, TestContext.Current.CancellationToken);
        videosForStudio.Select(video => video.Id).Should().BeEquivalentTo(videos.Select(video => video.Id));
        (await AsUser().GetStudioByIdAsync(studio.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(videoCount);
    }

    private async Task AssertRelationshipAsync(VideoDto video, StudioDto studio, bool isLinked)
    {
        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id);
        var videosForStudio = await AsUser().GetVideosByStudioAsync(studio.Id);
        var studioAfter = await AsUser().GetStudioByIdAsync(studio.Id);

        if (isLinked)
        {
            videoAfter.StudioId.Should().Be(studio.Id);
            videoAfter.StudioName.Should().Be(studio.Name);
        }
        else
        {
            videoAfter.StudioId.Should().NotBe(studio.Id);
            videoAfter.StudioName.Should().NotBe(studio.Name);
        }
        videosForStudio.Count(candidate => candidate.Id == video.Id).Should().Be(isLinked ? 1 : 0);
        studioAfter.VideoCount.Should().Be(isLinked ? 1 : 0);
    }
}
