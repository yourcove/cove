using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Videos;

[Collection(ApiTestLane2Collection.Name)]
public sealed class VideoMergeApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenSourceRemoteIdsAndGroupMembership_WhenMerged_ThenTheyMoveToTheTarget()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var targetRemoteId = new VideoRemoteIdDto("https://target-metadata.example/graphql", $"target-{suffix}");
        var sourceRemoteId = new VideoRemoteIdDto("https://source-metadata.example/graphql", $"source-{suffix}");
        var target = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge association target {suffix}")
            .WithRemoteId(targetRemoteId.Endpoint, targetRemoteId.RemoteId)
            .Build());
        var source = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge association source {suffix}")
            .WithRemoteId(sourceRemoteId.Endpoint, sourceRemoteId.RemoteId)
            .Build());
        var group = await AsUser().CreateGroupAsync($"Merge association group {suffix}");
        await AsUser().AddVideoToGroupAsync(source, group);

        // Act
        await AsUser().MergeVideosAsync(target, source);
        var persisted = await AsUser().GetVideoByIdAsync(target.Id);
        var groupItems = await AsUser().GetGroupItemsAsync(group);

        // Assert
        persisted.RemoteIds.Should().BeEquivalentTo(new[] { targetRemoteId, sourceRemoteId });
        groupItems.Should().ContainSingle(item => item.VideoId == target.Id);
    }

    [Fact]
    public async Task GivenAChildVideoOfTheSource_WhenMerged_ThenTheChildIsReparentedToTheTarget()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var target = await AsUser().CreateVideoAsync($"Merge child target {suffix}");
        var source = await AsUser().CreateVideoAsync($"Merge child source {suffix}");
        await AsDbUser().AttachVideoFileAsync(source.Id, duration: 60, size: 1);
        var childRequest = new VideoBuilder().WithTitle($"Merge child {suffix}").Build() with
        {
            ParentVideoId = source.Id,
            ClipStartSec = 10,
            ClipEndSec = 20,
        };
        var child = await AsUser().CreateVideoAsync(childRequest);

        // Act
        await AsUser().MergeVideosAsync(target, source);
        var persistedChild = await AsUser().GetVideoByIdAsync(child.Id);
        var persistedTarget = await AsUser().GetVideoByIdAsync(target.Id);

        // Assert
        persistedChild.ParentVideoId.Should().Be(target.Id);
        persistedChild.ClipStartSec.Should().Be(10);
        persistedChild.ClipEndSec.Should().Be(20);
        persistedTarget.ChildVideoCount.Should().Be(1);
    }

    [Fact]
    public async Task GivenTheTargetDescendsFromASource_WhenMerged_ThenTheHierarchyIsRejectedWithoutDeletion()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var source = await AsUser().CreateVideoAsync($"Merge ancestor source {suffix}");
        await AsDbUser().AttachVideoFileAsync(source.Id, duration: 60, size: 1);
        var child = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Merge direct child {suffix}").Build() with
        {
            ParentVideoId = source.Id,
            ClipStartSec = 5,
            ClipEndSec = 40,
        });
        var grandchild = await AsUser().CreateVideoAsync(new VideoBuilder().WithTitle($"Merge deep child {suffix}").Build() with
        {
            ParentVideoId = child.Id,
            ClipStartSec = 10,
            ClipEndSec = 20,
        });
        await AsDbUser().SetVideoParentAsync(grandchild.Id, child.Id);

        // Act
        var directMerge = () => AsUser().MergeVideosAsync(child, source);
        var deepMerge = () => AsUser().MergeVideosAsync(grandchild, source);

        // Assert
        await directMerge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await deepMerge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetVideoByIdAsync(source.Id)).Id.Should().Be(source.Id);
        (await AsUser().GetVideoByIdAsync(child.Id)).ParentVideoId.Should().Be(source.Id);
        (await AsUser().GetVideoByIdAsync(grandchild.Id)).ParentVideoId.Should().Be(child.Id);

        var unrelatedSource = await AsUser().CreateVideoAsync($"Merge cycle source {suffix}");
        await AsDbUser().SetVideoParentAsync(child.Id, grandchild.Id);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var cycleMerge = () => AsUser().MergeVideosAsync(child, new[] { unrelatedSource }, timeout.Token);
        await cycleMerge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetVideoByIdAsync(unrelatedSource.Id)).Id.Should().Be(unrelatedSource.Id);
    }

    [Fact]
    public async Task GivenSourceSegmentsAndDetections_WhenMerged_ThenTheyMoveToTheTarget()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var eva = AsUser(ApiTestUsers.Eva);
        var target = await AsUser().CreateVideoAsync($"Merge hosted-data target {suffix}");
        var source = await AsUser().CreateVideoAsync($"Merge hosted-data source {suffix}");
        var segment = await AsUser().CreateVideoSegmentAsync(source, $"Merge segment {suffix}");
        var detection = await AsUser().CreateVideoDetectionAsync(source, $"merge-detection-{suffix}");
        var profile = await eva.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto($"Merge span profile {suffix}", null, false));
        await eva.CreateSegmentDisplayRuleAsync(profile.Id, new SegmentDisplayRuleCreateDto("api-test", "chapter", null, null, SegmentHostType.Video, true, null, null, null, false, null, 1, 100));
        (await eva.GetVideoResolvedSpansAsync(target, profile.Id)).Spans.Should().BeEmpty();

        // Act
        await AsUser().MergeVideosAsync(target, source);
        var targetSegments = await AsUser().GetVideoSegmentsAsync(target);
        var targetDetections = await AsUser().GetVideoDetectionsAsync(target);
        var targetSpans = await eva.GetVideoResolvedSpansAsync(target, profile.Id);

        // Assert
        targetSegments.Should().ContainSingle(item => item.Id == segment.Id)
            .Which.HostId.Should().Be(target.Id);
        targetDetections.Should().ContainSingle(item => item.Id == detection.Id)
            .Which.HostId.Should().Be(target.Id);
        targetSpans.Spans.Should().ContainSingle()
            .Which.SegmentIds.Should().Equal(segment.Id);
    }

    [Fact]
    public async Task GivenEquivalentUrlsAndRemoteIds_WhenMerged_ThenOnlyOneNormalizedAssociationRemains()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var targetUrl = $"https://merge.example/resource/{suffix}";
        var target = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge dedup target {suffix}")
            .WithUrl(targetUrl)
            .WithRemoteId("https://metadata.example/graphql", $"remote-{suffix}")
            .Build());
        var source = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge dedup source {suffix}")
            .WithUrl(targetUrl.ToUpperInvariant())
            .WithRemoteId("HTTPS://METADATA.EXAMPLE/GRAPHQL", $"REMOTE-{suffix}")
            .Build());

        // Act
        await AsUser().MergeVideosAsync(target, source);
        var persisted = await AsUser().GetVideoByIdAsync(target.Id);

        // Assert
        persisted.Urls.Should().ContainSingle().Which.Should().Be(targetUrl);
        persisted.RemoteIds.Should().ContainSingle().Which.Should().Be(
            new VideoRemoteIdDto("https://metadata.example/graphql", $"remote-{suffix}"));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/merge")]
    public async Task GivenSelectedVideoSources_WhenMerged_ThenFilesAndDistinctRelationshipsMoveToTheTargetAndSourcesAreDeleted()
    {
        // Arrange
        var targetTag = await AsUser().CreateTagAsync($"Target tag {Guid.NewGuid():N}");
        var firstSourceTag = await AsUser().CreateTagAsync($"First source tag {Guid.NewGuid():N}");
        var secondSourceTag = await AsUser().CreateTagAsync($"Second source tag {Guid.NewGuid():N}");
        var sharedSourceTag = await AsUser().CreateTagAsync($"Shared source tag {Guid.NewGuid():N}");
        var targetPerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Target performer {Guid.NewGuid():N}").Build());
        var firstSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"First source performer {Guid.NewGuid():N}").Build());
        var secondSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Second source performer {Guid.NewGuid():N}").Build());
        var sharedSourcePerformer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Shared source performer {Guid.NewGuid():N}").Build());
        var targetGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Target gallery {Guid.NewGuid():N}").Build());
        var sourceGallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Source gallery {Guid.NewGuid():N}").Build());
        var targetUrl = $"https://merge.example/target/{Guid.NewGuid():N}";
        var sourceUrl = $"https://merge.example/source/{Guid.NewGuid():N}";
        var target = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge target {Guid.NewGuid():N}")
            .WithTags([targetTag])
            .WithPerformers([targetPerformer])
            .WithGallery(targetGallery)
            .WithUrl(targetUrl)
            .Build());
        var firstSourcePath = AsTestFileSystem().CreateTextFile("A first source file that must move during merge.");
        var secondSourcePath = AsTestFileSystem().CreateTextFile("A second source file that must move during merge.");
        var firstSource = await AsUser().CreateVideoFromFileAsync(firstSourcePath);
        firstSource = await AsUser().UpdateVideoAsync(firstSource.Id, new
        {
            tagIds = new[] { firstSourceTag.Id, sharedSourceTag.Id },
            performerIds = new[] { firstSourcePerformer.Id, sharedSourcePerformer.Id },
            galleryIds = new[] { sourceGallery.Id },
            urls = new[] { sourceUrl },
        });
        var secondSource = await AsUser().CreateVideoFromFileAsync(secondSourcePath);
        secondSource = await AsUser().UpdateVideoAsync(secondSource.Id, new
        {
            tagIds = new[] { secondSourceTag.Id, sharedSourceTag.Id },
            performerIds = new[] { secondSourcePerformer.Id, sharedSourcePerformer.Id },
            galleryIds = new[] { sourceGallery.Id },
            urls = new[] { sourceUrl },
        });
        var control = await AsUser().CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Merge control {Guid.NewGuid():N}")
            .WithTags([sharedSourceTag])
            .WithPerformers([sharedSourcePerformer])
            .WithGallery(sourceGallery)
            .WithUrl(sourceUrl)
            .Build());
        var forbiddenMerge = () => AsUser(ApiTestUsers.Eva).MergeVideosAsync(target, firstSource, secondSource);
        await forbiddenMerge.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        var memberRole = (await AsUser().GetRolesAsync()).Single(role => role.Name == BuiltinRoles.Member);
        await AsUser().UpdateRoleAsync(
            memberRole.Id,
            new UpdateRoleRequest(
                Description: null,
                Permissions: memberRole.Permissions.Append(Permissions.VideosDelete).Distinct().ToArray()));
        await AsUser().CreateContentRuleAsync(new CreateContentRuleRequest(
            memberRole.Id,
            EntityKinds.Performer,
            Effect: "deny",
            ScopeKind: "all",
            ScopeValue: "{}",
            AppliesTo: "read"));

        // Act
        var merged = await AsUser(ApiTestUsers.Eva).MergeVideosAsync(target, firstSource, secondSource);
        var persisted = await AsUser().GetVideoByIdAsync(target.Id);
        var controlAfter = await AsUser().GetVideoByIdAsync(control.Id);

        // Assert
        merged.Id.Should().Be(target.Id);
        merged.Performers.Should().BeEmpty();
        persisted.Performers.Select(performer => performer.Id).Should().BeEquivalentTo(new[] { targetPerformer.Id, firstSourcePerformer.Id, secondSourcePerformer.Id, sharedSourcePerformer.Id });
        foreach (var actual in new[] { merged, persisted })
        {
            actual.Tags.Select(tag => tag.Id).Should().BeEquivalentTo(new[] { targetTag.Id, firstSourceTag.Id, secondSourceTag.Id, sharedSourceTag.Id });
            actual.Galleries.Select(gallery => gallery.Id).Should().BeEquivalentTo(new[] { targetGallery.Id, sourceGallery.Id });
            actual.Urls.Should().BeEquivalentTo(targetUrl, sourceUrl);
            actual.Files.Select(file => file.Path).Should().BeEquivalentTo(firstSourcePath, secondSourcePath);
        }
        foreach (var source in new[] { firstSource, secondSource })
        {
            var sourceRead = () => AsUser().GetVideoByIdAsync(source.Id);
            await sourceRead.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        }
        controlAfter.Tags.Select(tag => tag.Id).Should().Equal(sharedSourceTag.Id);
        controlAfter.Performers.Select(performer => performer.Id).Should().Equal(sharedSourcePerformer.Id);
        controlAfter.Galleries.Select(gallery => gallery.Id).Should().Equal(sourceGallery.Id);
        controlAfter.Urls.Should().Equal(sourceUrl);
    }
}
