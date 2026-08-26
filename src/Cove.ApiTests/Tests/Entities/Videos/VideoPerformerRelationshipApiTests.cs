using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoPerformerRelationshipApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenUnlinkedPerformer_WhenLinkedRemovedAndRelinked_ThenVideoVisibilityTracksRelationship()
    {
        // Arrange
        var performer = await CreatePerformerAsync(TestCatalog.Performers.CherryPoppins.Name);
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title, TestContext.Current.CancellationToken);

        // Act & Assert
        await AssertRelationshipAsync(video, performer, isLinked: false);
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = new[] { performer.Id } }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, performer, isLinked: true);
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = Array.Empty<int>() }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, performer, isLinked: false);
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = new[] { performer.Id } }, TestContext.Current.CancellationToken);
        await AssertRelationshipAsync(video, performer, isLinked: true);
    }

    [Fact]
    public async Task GivenDuplicatePerformer_WhenVideoIsCreated_ThenRelationshipRemainsUnique()
    {
        // Arrange
        var performer = await CreatePerformerAsync(TestCatalog.Performers.VelvetThunder.Name);
        var request = new VideoBuilder()
            .WithTitle(TestCatalog.Movies.TheFastAndTheFlirtatious.Title)
            .WithPerformers([performer, performer])
            .Build();

        // Act
        var video = await AsUser().CreateVideoAsync(request, TestContext.Current.CancellationToken);

        // Assert
        await AssertRelationshipAsync(video, performer, isLinked: true);
    }

    [Fact]
    public async Task GivenLinkedPerformer_WhenDuplicateRelationshipIsSubmitted_ThenRelationshipRemainsUnique()
    {
        // Arrange
        var performer = await CreatePerformerAsync(TestCatalog.Performers.VelvetThunder.Name);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(TestCatalog.Movies.TheFastAndTheFlirtatious.Title)
                .WithPerformers([performer])
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = new[] { performer.Id, performer.Id } }, TestContext.Current.CancellationToken);

        // Assert
        await AssertRelationshipAsync(video, performer, isLinked: true);
    }

    [Theory]
    [InlineData(BulkUpdateMode.Set)]
    [InlineData(BulkUpdateMode.Add)]
    public async Task GivenDuplicatePerformer_WhenVideosAreBulkUpdated_ThenRelationshipRemainsUnique(
        BulkUpdateMode mode)
    {
        // Arrange
        var performer = await CreatePerformerAsync(TestCatalog.Performers.BeaHaven.Name);
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title, TestContext.Current.CancellationToken);

        // Act
        await AsUser().BulkUpdateVideosAsync(new BulkVideoUpdateDto
        {
            Ids = [video.Id],
            PerformerIds = [performer.Id, performer.Id],
            PerformerMode = mode,
        }, TestContext.Current.CancellationToken);

        // Assert
        await AssertRelationshipAsync(video, performer, isLinked: true);
    }

    [Fact]
    public async Task GivenMultipleLinkedPerformers_WhenSetIsReplaced_ThenBothRelationshipDirectionsAreUpdated()
    {
        // Arrange
        var removed = await CreatePerformerAsync(TestCatalog.Performers.CherryPoppins.Name);
        var retained = await CreatePerformerAsync(TestCatalog.Performers.VelvetThunder.Name);
        var added = await CreatePerformerAsync(TestCatalog.Performers.BeaHaven.Name);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle(TestCatalog.Movies.RaidersOfTheLostCorset.Title)
                .WithPerformers([removed, retained])
                .Build(), TestContext.Current.CancellationToken);

        // Act
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = new[] { retained.Id, added.Id } }, TestContext.Current.CancellationToken);

        // Assert
        await AssertRelationshipAsync(video, removed, isLinked: false);
        await AssertRelationshipAsync(video, retained, isLinked: true);
        await AssertRelationshipAsync(video, added, isLinked: true);
    }

    [Fact]
    public async Task GivenManyPerformers_WhenLinkedToVideo_ThenAllRelationshipsAreReturned()
    {
        // Arrange
        const int performerCount = 30;
        var performers = await Task.WhenAll(Enumerable.Range(1, performerCount)
            .Select(index => CreatePerformerAsync($"API test performer {index} {Guid.NewGuid():N}")));
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title, TestContext.Current.CancellationToken);

        // Act
        await AsUser().UpdateVideoAsync(video.Id, new { performerIds = performers.Select(performer => performer.Id).ToArray() }, TestContext.Current.CancellationToken);

        // Assert
        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);
        videoAfter.Performers.Select(performer => performer.Id).Should().BeEquivalentTo(performers.Select(performer => performer.Id));
        foreach (var performer in performers)
        {
            var videosForPerformer = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);
            videosForPerformer.Count(candidate => candidate.Id == video.Id).Should().Be(1);
            (await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task GivenManyVideos_WhenLinkedToPerformer_ThenPerformerReturnsEveryVideo()
    {
        // Arrange
        const int videoCount = 20;
        var performer = await CreatePerformerAsync(TestCatalog.Performers.RandyDandy.Name);
        var videos = await Task.WhenAll(Enumerable.Range(1, videoCount)
            .Select(index => AsUser().CreateVideoAsync($"API test video {index} {Guid.NewGuid():N}")));

        // Act
        foreach (var video in videos)
            await AsUser().UpdateVideoAsync(video.Id, new { performerIds = new[] { performer.Id } }, TestContext.Current.CancellationToken);

        // Assert
        var videosForPerformer = await AsUser().GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken);
        videosForPerformer.Select(video => video.Id).Should().BeEquivalentTo(videos.Select(video => video.Id));
        (await AsUser().GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(videoCount);
    }

    private Task<PerformerDto> CreatePerformerAsync(string name)
        => AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(name).Build());

    private async Task AssertRelationshipAsync(VideoDto video, PerformerDto performer, bool isLinked)
    {
        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id);
        var videosForPerformer = await AsUser().GetVideosByPerformerAsync(performer.Id);
        var performerAfter = await AsUser().GetPerformerByIdAsync(performer.Id);

        videoAfter.Performers.Count(candidate => candidate.Id == performer.Id).Should().Be(isLinked ? 1 : 0);
        videosForPerformer.Count(candidate => candidate.Id == video.Id).Should().Be(isLinked ? 1 : 0);
        performerAfter.VideoCount.Should().Be(isLinked ? 1 : 0);
    }
}
