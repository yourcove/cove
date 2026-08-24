using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoLifecycleAndQueryApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenSubVideo_WhenReadByIdAndListed_ThenParentFileMetadataIsReturned()
    {
        // Arrange
        var parent = await AsUser().CreateVideoAsync($"Parent video {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(parent.Id, duration: 120, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        var child = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle($"Sub-video {Guid.NewGuid():N}")
                .Build() with
            {
                ParentVideoId = parent.Id,
                ClipStartSec = 30,
                ClipEndSec = 60,
            }, TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [child.Id] },
        };

        // Act
        var retrieved = await AsUser().GetVideoByIdAsync(child.Id, TestContext.Current.CancellationToken);
        var listed = await AsUser().FindVideosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        retrieved.ParentVideoId.Should().Be(parent.Id);
        retrieved.ParentVideoTitle.Should().Be(parent.Title);
        retrieved.ClipStartSec.Should().Be(30);
        retrieved.ClipEndSec.Should().Be(60);
        retrieved.Files.Should().ContainSingle().Which.Basename.Should().Be("aggregate-source.mp4");
        listed.Items.Should().ContainSingle().Which.Files.Should().ContainSingle()
            .Which.Basename.Should().Be("aggregate-source.mp4");
    }

    [Fact]
    public async Task GivenVideoMetadata_WhenPartiallyUpdatedAndCleared_ThenUnspecifiedValuesArePreserved()
    {
        // Arrange
        var studio = await AsUser().CreateStudioAsync($"Original studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var video = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle($"Original title {Guid.NewGuid():N}")
                .WithCode("ORIGINAL-CODE")
                .WithDetails("Original details")
                .WithDirector("Original director")
                .WithDate("2026-08-01")
                .WithStudio(studio)
                .WithCaptions("Original captions")
                .WithUrl("https://original.example/video")
                .Build(), TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser().UpdateVideoAsync(video.Id, new
        {
            title = "Updated title",
            details = "Updated details",
            clearFields = new[] { "date", "studioId" },
        }, TestContext.Current.CancellationToken);
        var retrieved = await AsUser().GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken);

        // Assert
        updated.Title.Should().Be("Updated title");
        retrieved.Details.Should().Be("Updated details");
        retrieved.Code.Should().Be("ORIGINAL-CODE");
        retrieved.Director.Should().Be("Original director");
        retrieved.Captions.Should().Be("Original captions");
        retrieved.Urls.Should().Equal("https://original.example/video");
        retrieved.Date.Should().BeNull();
        retrieved.StudioId.Should().BeNull();
        retrieved.StudioName.Should().BeNull();
    }

    [Fact]
    public async Task GivenVideo_WhenMemberUpdatesIt_ThenWriteAccessIsAllowed()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Member update {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var updated = await AsUser(ApiTestUsers.Eva).UpdateVideoAsync(video.Id, new { details = "Updated by member" }, TestContext.Current.CancellationToken);

        // Assert
        updated.Details.Should().Be("Updated by member");
    }

    [Fact]
    public async Task GivenMissingVideo_WhenUpdatedOrRead_ThenNotFoundIsReturned()
    {
        // Arrange
        const int missingId = int.MaxValue;

        // Act
        var update = () => AsUser().UpdateVideoAsync(missingId, new { details = "Missing" });
        var read = () => AsUser().GetVideoByIdAsync(missingId);

        // Assert
        await update.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/find")]
    public async Task GivenMatchingVideos_WhenFilteredAndPaged_ThenOnlyTheRequestedPageIsReturned()
    {
        // Arrange
        var first = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle("A filtered video").AsOrganized().Build(), TestContext.Current.CancellationToken);
        var second = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle("B filtered video").AsOrganized().Build(), TestContext.Current.CancellationToken);
        var excluded = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle("Excluded video").Build(), TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter
            {
                Ids = [first.Id, second.Id, excluded.Id],
                Organized = true,
            },
            FindFilter = new FindFilter
            {
                Page = 2,
                PerPage = 1,
                Sort = "title",
            },
        };

        // Act
        var result = await AsUser(ApiTestUsers.Eva).FindVideosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        result.TotalCount.Should().Be(2);
        result.Page.Should().Be(2);
        result.PerPage.Should().Be(1);
        result.Items.Should().ContainSingle().Which.Id.Should().Be(second.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/aggregate")]
    public async Task GivenSelectedVideos_WhenAggregated_ThenCountDurationAndFileSizeAreScopedToSelection()
    {
        // Arrange
        var first = await AsUser().CreateVideoAsync($"Aggregate first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await AsUser().CreateVideoAsync($"Aggregate second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var excluded = await AsUser().CreateVideoAsync($"Aggregate excluded {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(first.Id, duration: 12.5, size: 1_000, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(second.Id, duration: 7.25, size: 2_500, cancellationToken: TestContext.Current.CancellationToken);
        await AsDbUser().AttachVideoFileAsync(excluded.Id, duration: 90, size: 9_999, cancellationToken: TestContext.Current.CancellationToken);
        var request = new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { Ids = [first.Id, second.Id] },
        };

        // Act
        var aggregate = await AsUser(ApiTestUsers.Eva).AggregateVideosAsync(request, TestContext.Current.CancellationToken);

        // Assert
        aggregate.Should().Be(new VideoAggregate(Count: 2, Duration: 19.75, FileSize: 3_500));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/bulk")]
    public async Task GivenVideosWithRelationships_WhenBulkSetIsApplied_ThenEverySelectedVideoIsMutated()
    {
        // Arrange
        var originalStudio = await AsUser().CreateStudioAsync($"Original studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementTag = await AsUser().CreateTagAsync($"Bulk tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var replacementPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Bulk performer {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var replacementGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Bulk gallery {Guid.NewGuid():N}").Build(), TestContext.Current.CancellationToken);
        var replacementGroup = await AsUser().CreateGroupAsync($"Bulk group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var videos = await Task.WhenAll(Enumerable.Range(1, 2).Select(index =>
            AsUser().CreateVideoAsync(
                new VideoBuilder()
                    .WithTitle($"Bulk video {index} {Guid.NewGuid():N}")
                    .WithCode($"ORIGINAL-{index}")
                    .WithDirector("Original director")
                    .WithDate("2026-08-01")
                    .WithStudio(originalStudio)
                    .Build())));
        var unselected = await AsUser().CreateVideoAsync(new VideoBuilder()
                .WithTitle($"Unselected bulk control {Guid.NewGuid():N}")
                .WithCode("CONTROL-CODE")
                .WithDirector("Control director")
                .WithDate("2026-08-02")
                .WithStudio(originalStudio)
                .Build(), TestContext.Current.CancellationToken);
        var request = new BulkVideoUpdateDto
        {
            Ids = videos.Select(video => video.Id).ToList(),
            ClearFields = ["studioId", "date"],
            Rating = 72,
            Organized = true,
            IsVr = true,
            Code = "BULK-CODE",
            Director = "Bulk director",
            TagIds = [replacementTag.Id],
            TagMode = BulkUpdateMode.Set,
            PerformerIds = [replacementPerformer.Id],
            PerformerMode = BulkUpdateMode.Set,
            GalleryIds = [replacementGallery.Id],
            GalleryMode = BulkUpdateMode.Set,
            GroupIds = [new VideoGroupInputDto(replacementGroup.Id, 3)],
            GroupMode = BulkUpdateMode.Set,
        };

        // Act
        var updatedCount = await AsUser().BulkUpdateVideosAsync(request, TestContext.Current.CancellationToken);
        var updated = await Task.WhenAll(videos.Select(video => AsUser().GetVideoByIdAsync(video.Id)));
        var selectedEngagement = await Task.WhenAll(updated.Select(video => AsUser().GetVideoEngagementAsync(video)));
        var unselectedAfter = await AsUser().GetVideoByIdAsync(unselected.Id, TestContext.Current.CancellationToken);
        var unselectedEngagement = await AsUser().GetVideoEngagementAsync(unselectedAfter, TestContext.Current.CancellationToken);

        // Assert
        updatedCount.Should().Be(2);
        updated.Should().AllSatisfy(video =>
        {
            video.Code.Should().Be("BULK-CODE");
            video.Director.Should().Be("Bulk director");
            video.Organized.Should().BeTrue();
            video.IsVr.Should().BeTrue();
            video.StudioId.Should().BeNull();
            video.Date.Should().BeNull();
            video.Tags.Should().ContainSingle(tag => tag.Id == replacementTag.Id);
            video.Performers.Should().ContainSingle(performer => performer.Id == replacementPerformer.Id);
            video.Galleries.Should().ContainSingle(gallery => gallery.Id == replacementGallery.Id);
            video.Groups.Should().ContainSingle(group => group.Id == replacementGroup.Id && group.VideoIndex == 3);
        });
        selectedEngagement.Should().AllSatisfy(engagement => engagement.Rating.Should().Be(72));
        unselectedAfter.Title.Should().Be(unselected.Title);
        unselectedAfter.Code.Should().Be("CONTROL-CODE");
        unselectedAfter.Director.Should().Be("Control director");
        unselectedAfter.Organized.Should().BeFalse();
        unselectedAfter.IsVr.Should().BeFalse();
        unselectedAfter.StudioId.Should().Be(originalStudio.Id);
        unselectedAfter.Date.Should().Be("2026-08-02");
        unselectedAfter.Tags.Should().BeEmpty();
        unselectedAfter.Performers.Should().BeEmpty();
        unselectedAfter.Galleries.Should().BeEmpty();
        unselectedAfter.Groups.Should().BeEmpty();
        unselectedEngagement.Rating.Should().BeNull();
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}")]
    public async Task GivenVideo_WhenDeleted_ThenItCanNoLongerBeRead()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Single delete {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        await AsUser().DeleteVideoAsync(video.Id, TestContext.Current.CancellationToken);

        // Assert
        var read = () => AsUser().GetVideoByIdAsync(video.Id);
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenVideo_WhenMemberDeletesIt_ThenForbiddenIsReturnedWithoutRemovingIt()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Protected delete {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var deletion = () => AsUser(ApiTestUsers.Eva).DeleteVideoAsync(video.Id);

        // Assert
        await deletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken)).Id.Should().Be(video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/destroy")]
    public async Task GivenSelectedVideosAndMissingId_WhenBatchDestroyed_ThenOnlyExistingSelectionsAreRemoved()
    {
        // Arrange
        var first = await AsUser().CreateVideoAsync($"Batch delete first {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var second = await AsUser().CreateVideoAsync($"Batch delete second {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var retained = await AsUser().CreateVideoAsync($"Batch delete retained {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var deletedCount = await AsUser().DestroyVideosAsync(new BatchDeleteDto([first.Id, int.MaxValue, second.Id]), TestContext.Current.CancellationToken);

        // Assert
        deletedCount.Should().Be(2);
        foreach (var deleted in new[] { first, second })
        {
            var read = () => AsUser().GetVideoByIdAsync(deleted.Id);
            await read.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 404 (NotFound)*");
        }
        (await AsUser().GetVideoByIdAsync(retained.Id, TestContext.Current.CancellationToken)).Id.Should().Be(retained.Id);
    }

    [Fact]
    public async Task GivenVideos_WhenMemberBatchDestroysThem_ThenForbiddenIsReturnedWithoutRemovingThem()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Protected batch delete {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var deletion = () => AsUser(ApiTestUsers.Eva).DestroyVideosAsync(new BatchDeleteDto([video.Id]));

        // Assert
        await deletion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetVideoByIdAsync(video.Id, TestContext.Current.CancellationToken)).Id.Should().Be(video.Id);
    }
}
