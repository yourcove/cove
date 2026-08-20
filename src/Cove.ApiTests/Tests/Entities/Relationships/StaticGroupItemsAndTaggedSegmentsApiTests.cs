using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Relationships;

[Collection(ApiTestLane2Collection.Name)]
public sealed class StaticGroupItemsAndTaggedSegmentsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/groups/{groupid:int}/items/page")]
    [CoversEndpoint("PUT", "/api/groups/{groupid:int}/items/{id:int}")]
    [CoversEndpoint("DELETE", "/api/groups/{groupid:int}/items/{id:int}")]
    public async Task GivenStaticGroupItems_WhenMemberPagesUpdatesAndDeletes_ThenPublicStateMatches()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var group = await AsUser().CreateGroupAsync($"Static item lifecycle {suffix}");
        var alphaVideo = await AsUser().CreateVideoAsync($"Alpha group item {suffix}");
        var betaVideo = await AsUser().CreateVideoAsync($"Beta group item {suffix}");
        var gammaVideo = await AsUser().CreateVideoAsync($"Gamma group item {suffix}");
        var alphaItem = await AsUser().AddVideoToGroupAsync(alphaVideo, group);
        var betaItem = await AsUser().AddVideoToGroupAsync(betaVideo, group);
        var gammaItem = await AsUser().AddVideoToGroupAsync(gammaVideo, group);

        // Act
        var page = await AsUser(ApiTestUsers.Eva).GetGroupItemsPageAsync(
            group.Id,
            page: 2,
            perPage: 1,
            sort: "title",
            direction: "asc",
            query: suffix);
        var updated = await AsUser(ApiTestUsers.Eva).UpdateGroupItemAsync(
            group.Id,
            alphaItem.Id,
            new GroupItemUpdateDto(
                OrderIndex: 0,
                Kind: GroupItemKind.VideoRange,
                StartSec: 3,
                EndSec: 8,
                Title: "  Edited range  ",
                Notes: "  Edited notes  "));
        await AsUser(ApiTestUsers.Eva).DeleteGroupItemAsync(group.Id, gammaItem.Id);
        var remaining = await AsUser(ApiTestUsers.Eva).GetGroupItemsPageAsync(group.Id, perPage: 10);

        // Assert
        page.TotalCount.Should().Be(3);
        page.Page.Should().Be(2);
        page.PerPage.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(betaItem.Id);
        updated.Id.Should().Be(alphaItem.Id);
        updated.OrderIndex.Should().Be(0);
        updated.Kind.Should().Be(GroupItemKind.VideoRange);
        updated.StartSec.Should().Be(3);
        updated.EndSec.Should().Be(8);
        updated.Title.Should().Be("Edited range");
        updated.Notes.Should().Be("Edited notes");
        remaining.Items.Select(item => item.Id).Should().Equal(alphaItem.Id, betaItem.Id);
        remaining.Items.Select(item => item.OrderIndex).Should().Equal(0, 1);

        var missingDelete = () => AsUser(ApiTestUsers.Eva).DeleteGroupItemAsync(group.Id, gammaItem.Id);
        await missingDelete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/groups/{groupid:int}/items/reorder")]
    [CoversEndpoint("POST", "/api/groups/{groupid:int}/items/remove-hosts")]
    public async Task GivenStaticGroupItems_WhenMemberReordersAndRemovesHosts_ThenSelectionAndIndexesArePreserved()
    {
        // Arrange
        var group = await AsUser().CreateGroupAsync($"Static item ordering {Guid.NewGuid():N}");
        var firstVideo = await AsUser().CreateVideoAsync($"First ordered video {Guid.NewGuid():N}");
        var secondVideo = await AsUser().CreateVideoAsync($"Second ordered video {Guid.NewGuid():N}");
        var thirdVideo = await AsUser().CreateVideoAsync($"Third ordered video {Guid.NewGuid():N}");
        var firstItem = await AsUser().AddVideoToGroupAsync(firstVideo, group);
        var secondItem = await AsUser().AddVideoToGroupAsync(secondVideo, group);
        var thirdItem = await AsUser().AddVideoToGroupAsync(thirdVideo, group);

        // Act
        var invalidReorder = () => AsUser(ApiTestUsers.Eva).ReorderGroupItemsAsync(
            group.Id,
            new GroupItemsReorderDto([firstItem.Id, firstItem.Id]));
        await invalidReorder.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");

        await AsUser(ApiTestUsers.Eva).ReorderGroupItemsAsync(
            group.Id,
            new GroupItemsReorderDto([firstItem.Id, thirdItem.Id, secondItem.Id]));
        var reordered = await AsUser(ApiTestUsers.Eva).GetGroupItemsPageAsync(group.Id, perPage: 10);
        var removed = await AsUser(ApiTestUsers.Eva).RemoveGroupItemHostsAsync(
            group.Id,
            new GroupItemsRemoveHostsDto(GroupItemKind.Video, [thirdVideo.Id, int.MaxValue]));
        var remaining = await AsUser(ApiTestUsers.Eva).GetGroupItemsPageAsync(group.Id, perPage: 10);

        // Assert
        reordered.Items.Select(item => item.Id).Should().Equal(firstItem.Id, thirdItem.Id, secondItem.Id);
        reordered.Items.Select(item => item.OrderIndex).Should().Equal(0, 1, 2);
        removed.Should().Be(1);
        remaining.Items.Select(item => item.Id).Should().Equal(firstItem.Id, secondItem.Id);
        remaining.Items.Select(item => item.OrderIndex).Should().Equal(0, 1);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/groups/{groupid:int}/playback-manifest")]
    public async Task GivenPlayableStaticItem_WhenMemberReadsManifest_ThenStreamContractIsReturned()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Manifest video {Guid.NewGuid():N}");
        var group = await AsUser().CreateGroupAsync($"Manifest group {Guid.NewGuid():N}");
        var groupItem = await AsUser().AddVideoToGroupAsync(video, group);

        // Act
        var manifest = await AsUser(ApiTestUsers.Eva).GetGroupPlaybackManifestAsync(group.Id);

        // Assert
        var item = manifest.Items.Should().ContainSingle().Which;
        item.GroupItemId.Should().Be(groupItem.Id);
        item.HostType.Should().Be("video");
        item.HostId.Should().Be(video.Id);
        item.VideoId.Should().Be(video.Id);
        item.Title.Should().Be(video.Title);
        item.Src.Should().Be($"/api/stream/video/{video.Id}");
        item.StartSec.Should().Be(0);
        item.EndSec.Should().BeNull();

        var missing = () => AsUser(ApiTestUsers.Eva).GetGroupPlaybackManifestAsync(int.MaxValue);
        await missing.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("GET", "/api/segments/{id:int}")]
    [CoversEndpoint("GET", "/api/segments/source-keys/distinct")]
    [CoversEndpoint("GET", "/api/segments/kinds/distinct")]
    public async Task GivenVideoSegments_WhenMemberReadsDetailAndDistinctValues_ThenJoinedMetadataAndCountsAreReturned()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Segment detail video {Guid.NewGuid():N}");
        var tag = await AsUser().CreateTagAsync($"Segment detail tag {Guid.NewGuid():N}");
        var chapter = await AsUser().CreateVideoSegmentAsync(video, Segment(
            startSec: 1,
            endSec: 4,
            tagId: tag.Id,
            kind: "chapter",
            sourceKey: "api-test-primary",
            title: "Opening chapter"));
        await AsUser().CreateVideoSegmentAsync(video, Segment(
            startSec: 5,
            endSec: 7,
            tagId: null,
            kind: "highlight",
            sourceKey: "api-test-primary",
            title: "Highlight one"));
        await AsUser().CreateVideoSegmentAsync(video, Segment(
            startSec: 8,
            endSec: 9,
            tagId: null,
            kind: "highlight",
            sourceKey: "api-test-secondary",
            title: "Highlight two"));

        // Act
        var detail = await AsUser(ApiTestUsers.Eva).GetSegmentByIdAsync(chapter.Id);
        var sourceKeys = await AsUser(ApiTestUsers.Eva).GetDistinctSegmentSourceKeysAsync();
        var kinds = await AsUser(ApiTestUsers.Eva).GetDistinctSegmentKindsAsync();

        // Assert
        detail.Id.Should().Be(chapter.Id);
        detail.HostType.Should().Be(SegmentHostType.Video);
        detail.HostId.Should().Be(video.Id);
        detail.HostTitle.Should().Be(video.Title);
        detail.StartSec.Should().Be(1);
        detail.EndSec.Should().Be(4);
        detail.TagId.Should().Be(tag.Id);
        detail.TagName.Should().Be(tag.Name);
        detail.Kind.Should().Be("chapter");
        detail.SourceKey.Should().Be("api-test-primary");
        detail.Title.Should().Be("Opening chapter");
        sourceKeys.Should().Equal(
            new SegmentDistinctValueDto("api-test-primary", 2),
            new SegmentDistinctValueDto("api-test-secondary", 1));
        kinds.Should().Equal(
            new SegmentDistinctValueDto("highlight", 2),
            new SegmentDistinctValueDto("chapter", 1));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/segments/bulk/remove-tag")]
    public async Task GivenTaggedSegments_WhenMemberBulkRemovesTag_ThenTagRowsDeleteAndOtherKindsDetach()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Bulk tag video {Guid.NewGuid():N}");
        var removedTag = await AsUser().CreateTagAsync($"Removed segment tag {Guid.NewGuid():N}");
        var retainedTag = await AsUser().CreateTagAsync($"Retained segment tag {Guid.NewGuid():N}");
        var tagOnly = await AsUser().CreateVideoSegmentAsync(video, Segment(1, 2, removedTag.Id, "tag", "api-test-bulk", "Tag occurrence"));
        var chapter = await AsUser().CreateVideoSegmentAsync(video, Segment(3, 4, removedTag.Id, "chapter", "api-test-bulk", "Tagged chapter"));
        var retained = await AsUser().CreateVideoSegmentAsync(video, Segment(5, 6, retainedTag.Id, "chapter", "api-test-bulk", "Retained chapter"));

        // Act
        var invalid = () => AsUser(ApiTestUsers.Eva).RemoveTagFromSegmentsAsync(0, [chapter.Id]);
        await invalid.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        var removed = await AsUser(ApiTestUsers.Eva).RemoveTagFromSegmentsAsync(
            removedTag.Id,
            [tagOnly.Id, chapter.Id, retained.Id, int.MaxValue]);

        // Assert
        removed.Should().Be(2);
        (await AsUser(ApiTestUsers.Eva).GetSegmentByIdAsync(chapter.Id)).TagId.Should().BeNull();
        (await AsUser(ApiTestUsers.Eva).GetSegmentByIdAsync(retained.Id)).TagId.Should().Be(retainedTag.Id);
        var deleted = () => AsUser(ApiTestUsers.Eva).GetSegmentByIdAsync(tagOnly.Id);
        await deleted.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }

    private static SegmentCreateDto Segment(
        double startSec,
        double? endSec,
        int? tagId,
        string kind,
        string sourceKey,
        string title)
        => new(
            StartSec: startSec,
            EndSec: endSec,
            TagId: tagId,
            Kind: kind,
            RefId: null,
            Payload: null,
            SourceKey: sourceKey,
            SourceRunId: null,
            Confidence: null,
            Title: title,
            ColorHint: null);
}
