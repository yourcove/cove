using System.Text.Json;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Groups;

[Collection(ApiTestLane1Collection.Name)]
public sealed class GroupDynamicOrderingAndSpanApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/groups/dynamic-sources")]
    [CoversEndpoint("PUT", "/api/groups/{id:int}/query")]
    [CoversEndpoint("POST", "/api/groups/{id:int}/snapshot")]
    public async Task GivenFilteredDynamicGroup_WhenMemberReadsSourcesUpdatesQueryAndSnapshots_ThenOnlyMatchingVideoIsPersisted()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var matching = await owner.CreateVideoAsync($"Matching dynamic video {suffix}");
        var excluded = await owner.CreateVideoAsync($"Excluded dynamic video {Guid.NewGuid():N}");
        var group = await owner.CreateGroupAsync($"Dynamic group {suffix}");
        var queryJson = JsonSerializer.Serialize(new
        {
            entityTypes = new[] { "video" },
            findFilters = new Dictionary<string, object>
            {
                ["video"] = new { q = suffix, sort = "title", direction = "asc" },
            },
        });

        var sources = await member.GetDynamicGroupSourcesAsync();
        await member.UpdateGroupQueryAsync(group.Id, new GroupQueryUpdateDto("  filter  ", queryJson));
        var dynamicGroup = await owner.GetGroupByIdAsync(group.Id);

        sources.Should().Equal(
            new DynamicGroupSourceDto("continue-watching", "Continue Watching"),
            new DynamicGroupSourceDto("filter", "Filtered Entities"),
            new DynamicGroupSourceDto("save-for-later", "Save for Later"),
            new DynamicGroupSourceDto("watch-history", "Watch History"));
        dynamicGroup.Kind.Should().Be(GroupKind.Dynamic);
        dynamicGroup.QuerySourceKey.Should().Be("filter");
        dynamicGroup.QueryJson.Should().Be(queryJson);
        dynamicGroup.AllowedHostTypes.Should().Equal("video");
        dynamicGroup.ItemCount.Should().Be(1);
        dynamicGroup.VideoCount.Should().Be(1);
        dynamicGroup.CachedItemCount.Should().BeNull();

        await member.SnapshotGroupAsync(group.Id);
        var snapshotted = await owner.GetGroupByIdAsync(group.Id);
        var items = await owner.GetGroupItemsAsync(snapshotted);

        snapshotted.Kind.Should().Be(GroupKind.Static);
        snapshotted.QuerySourceKey.Should().BeNull();
        snapshotted.QueryJson.Should().BeNull();
        snapshotted.LastResolvedAt.Should().BeNull();
        snapshotted.CachedItemCount.Should().BeNull();
        snapshotted.AllowedHostTypes.Should().Equal("video");
        snapshotted.ItemCount.Should().Be(1);
        snapshotted.VideoCount.Should().Be(1);
        var item = items.Should().ContainSingle().Which;
        item.Kind.Should().Be(GroupItemKind.Video);
        item.HostType.Should().Be("video");
        item.HostId.Should().Be(matching.Id);
        item.VideoId.Should().Be(matching.Id);
        item.VideoTitle.Should().Be(matching.Title);
        item.Title.Should().Be(matching.Title);
        item.OrderIndex.Should().Be(0);
        item.SnapshotAt.Should().NotBeNullOrWhiteSpace();
        items.Should().NotContain(candidate => candidate.VideoId == excluded.Id);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/groups/reorder")]
    public async Task GivenGroupsWithManualOrder_WhenMemberReordersSelection_ThenRequestOrderAndControlArePreserved()
    {
        var owner = AsUser();
        var first = await owner.CreateGroupAsync(GroupRequest($"First ordered group {Guid.NewGuid():N}", 1));
        var second = await owner.CreateGroupAsync(GroupRequest($"Second ordered group {Guid.NewGuid():N}", 2));
        var third = await owner.CreateGroupAsync(GroupRequest($"Third ordered group {Guid.NewGuid():N}", 3));
        var control = await owner.CreateGroupAsync(GroupRequest($"Control ordered group {Guid.NewGuid():N}", 90));

        await AsUser(ApiTestUsers.Eva).ReorderGroupsAsync(new GroupItemsReorderDto([third.Id, first.Id, second.Id], StartIndex: 30));
        var reordered = await Task.WhenAll(new[] { first, second, third, control }.Select(group => owner.GetGroupByIdAsync(group.Id)));
        var byId = reordered.ToDictionary(group => group.Id);

        byId[third.Id].SortOrder.Should().Be(30);
        byId[first.Id].SortOrder.Should().Be(31);
        byId[second.Id].SortOrder.Should().Be(32);
        byId[control.Id].SortOrder.Should().Be(90);
    }

    [Fact]
    [CoversEndpoint("PUT", "/api/groups/{id:int}/subgroups/reorder")]
    public async Task GivenOrderedSubGroups_WhenMemberReordersThem_ThenOnlyTargetParentChanges()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var parent = await owner.CreateGroupAsync($"Reordered subgroup parent {Guid.NewGuid():N}");
        var first = await owner.CreateGroupAsync($"First reordered subgroup {Guid.NewGuid():N}");
        var second = await owner.CreateGroupAsync($"Second reordered subgroup {Guid.NewGuid():N}");
        var third = await owner.CreateGroupAsync($"Third reordered subgroup {Guid.NewGuid():N}");
        var controlParent = await owner.CreateGroupAsync($"Control subgroup parent {Guid.NewGuid():N}");
        var controlChild = await owner.CreateGroupAsync($"Control subgroup child {Guid.NewGuid():N}");
        await member.AddSubGroupAsync(parent.Id, new AddSubGroupDto(first.Id, 0));
        await member.AddSubGroupAsync(parent.Id, new AddSubGroupDto(second.Id, 1));
        await member.AddSubGroupAsync(parent.Id, new AddSubGroupDto(third.Id, 2));
        await member.AddSubGroupAsync(controlParent.Id, new AddSubGroupDto(controlChild.Id, 0));

        await member.ReorderSubGroupsAsync(parent.Id, new ReorderSubGroupsDto([third.Id, first.Id, second.Id]));

        (await owner.GetSubGroupsAsync(parent.Id)).Select(group => group.Id).Should().Equal(third.Id, first.Id, second.Id);
        (await owner.GetSubGroupsAsync(controlParent.Id)).Select(group => group.Id).Should().Equal(controlChild.Id);
        (await owner.GetGroupByIdAsync(parent.Id)).SubGroupCount.Should().Be(3);
        (await owner.GetGroupByIdAsync(controlParent.Id)).SubGroupCount.Should().Be(1);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/groups/{groupid:int}/items/from-spans")]
    public async Task GivenResolvedDerivedAndManualSpans_WhenMemberSnapshotsThem_ThenExactOrderedItemsAreCreated()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Group span video {suffix}");
        var retainedVideo = await owner.CreateVideoAsync($"Retained group video {suffix}");
        var tag = await owner.CreateTagAsync($"Group span tag {suffix}");
        var group = await owner.CreateGroupAsync($"Group span snapshot {suffix}");
        var retainedItem = await owner.AddVideoToGroupAsync(retainedVideo, group);
        var profile = await member.CreateSegmentDisplayProfileAsync(new SegmentDisplayProfileCreateDto($"Group span profile {suffix}", null, false));
        await member.CreateSegmentDisplayRuleAsync(profile.Id, new SegmentDisplayRuleCreateDto(
            "group-span", "chapter", tag.Id, null, SegmentHostType.Video, true,
            null, null, 1, false, "#224466", 2, 100));
        await owner.CreateVideoSegmentAsync(video, Segment(2, 4, tag.Id, "First span"));
        await owner.CreateVideoSegmentAsync(video, Segment(4.5, 7, tag.Id, "Second span"));
        var resolved = (await member.GetVideoResolvedSpansAsync(video, profile.Id)).Spans.Should().ContainSingle().Which;
        var derivedQuery = new SegmentSpanDerivedQueryDto(
            "union",
            [new SegmentSpanOperandDto("group-span", "chapter", [tag.Id], null)],
            MergeGapSec: 0,
            MinDurationSec: 0);
        var derived = await member.QueryVideoResolvedSpansAsync(video, new SegmentSpanQueryRequestDto(
            profile.Id,
            derivedQuery.Operator,
            derivedQuery.Operands,
            derivedQuery.MergeGapSec,
            derivedQuery.MinDurationSec));
        derived.Spans.Should().HaveCount(2);
        var request = new GroupItemsFromSpansDto([
            new GroupItemSpanInputDto(resolved.SpanKey, video.Id, null, null, "Resolved snapshot", profile.Id),
            new GroupItemSpanInputDto(null, video.Id, null, null, "Derived snapshot", profile.Id, derivedQuery),
            new GroupItemSpanInputDto(derived.Spans[0].SpanKey, video.Id, null, null, "Filtered derived snapshot", profile.Id, derivedQuery),
            new GroupItemSpanInputDto(null, video.Id, 12, 16, "Manual range", null),
            new GroupItemSpanInputDto(null, video.Id, null, null, "Manual whole video", null),
        ]);

        var created = await member.CreateGroupItemsFromSpansAsync(group.Id, request);
        var persisted = await owner.GetGroupItemsAsync(group);

        created.Should().HaveCount(6);
        created.Select(item => item.OrderIndex).Should().Equal(1, 2, 3, 4, 5, 6);
        created.Should().AllSatisfy(item =>
        {
            item.GroupId.Should().Be(group.Id);
            item.HostType.Should().Be("video");
            item.HostId.Should().Be(video.Id);
            item.VideoId.Should().Be(video.Id);
            item.VideoTitle.Should().Be(video.Title);
            item.SnapshotAt.Should().NotBeNullOrWhiteSpace();
        });

        created[0].Kind.Should().Be(GroupItemKind.VideoRange);
        created[0].StartSec.Should().Be(2);
        created[0].EndSec.Should().Be(7);
        created[0].Title.Should().Be("Resolved snapshot");
        created[0].SourceSpanKey.Should().Be(resolved.SpanKey);
        created[0].SourceProfileId.Should().Be(profile.Id);
        created[0].SourceQueryJson.Should().BeNull();

        var derivedItems = created.Skip(1).Take(2).ToList();
        derivedItems.Select(item => (item.StartSec, item.EndSec)).Should().Equal((2d, 4d), (4.5d, 7d));
        derivedItems.Select(item => item.Title).Should().OnlyContain(title => title == "Derived snapshot");
        derivedItems.Select(item => item.SourceSpanKey).Should().Equal(derived.Spans.Select(span => span.SpanKey));
        derivedItems.Select(item => item.SourceProfileId).Should().OnlyContain(profileId => profileId == profile.Id);
        derivedItems.Should().AllSatisfy(item =>
        {
            var sourceQuery = JsonSerializer.Deserialize<SegmentSpanDerivedQueryDto>(item.SourceQueryJson!, ApiJson.Options);
            sourceQuery.Should().BeEquivalentTo(derivedQuery);
        });

        created[3].Kind.Should().Be(GroupItemKind.VideoRange);
        created[3].StartSec.Should().Be(derived.Spans[0].StartSec);
        created[3].EndSec.Should().Be(derived.Spans[0].EndSec);
        created[3].Title.Should().Be("Filtered derived snapshot");
        created[3].SourceSpanKey.Should().Be(derived.Spans[0].SpanKey);
        created[3].SourceProfileId.Should().Be(profile.Id);
        JsonSerializer.Deserialize<SegmentSpanDerivedQueryDto>(created[3].SourceQueryJson!, ApiJson.Options).Should().BeEquivalentTo(derivedQuery);

        created[4].Kind.Should().Be(GroupItemKind.VideoRange);
        created[4].StartSec.Should().Be(12);
        created[4].EndSec.Should().Be(16);
        created[4].Title.Should().Be("Manual range");
        created[4].SourceSpanKey.Should().BeNull();
        created[4].SourceProfileId.Should().BeNull();
        created[4].SourceQueryJson.Should().BeNull();
        created[5].Kind.Should().Be(GroupItemKind.Video);
        created[5].StartSec.Should().BeNull();
        created[5].EndSec.Should().BeNull();
        created[5].Title.Should().Be("Manual whole video");
        created[5].SourceSpanKey.Should().BeNull();
        created[5].SourceProfileId.Should().BeNull();
        created[5].SourceQueryJson.Should().BeNull();

        persisted.Skip(1).Select(ItemState).Should().Equal(created.Select(ItemState));
        persisted.Skip(1).Should().AllSatisfy(item => item.SnapshotAt.Should().NotBeNullOrWhiteSpace());
        persisted[0].VideoId.Should().Be(retainedVideo.Id);
        persisted[0].OrderIndex.Should().Be(0);
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/groups/bulk")]
    public async Task GivenNormalAndProtectedGroups_WhenBulkDeleteIsRequested_ThenMemberIsForbiddenAndOwnerSkipsBuiltIn()
    {
        var owner = AsUser();
        var normal = await owner.CreateGroupAsync($"Bulk delete group {Guid.NewGuid():N}");
        var retained = await owner.CreateGroupAsync($"Retained bulk delete group {Guid.NewGuid():N}");
        var builtIn = (await owner.GetGroupsAsync()).Single(group => group.QuerySourceKey == "save-for-later");
        var request = new BatchDeleteDto([normal.Id, normal.Id, builtIn.Id, 0, int.MaxValue]);
        var forbidden = () => AsUser(ApiTestUsers.Eva).BulkDeleteGroupsAsync(request);

        await forbidden.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await owner.GetGroupByIdAsync(normal.Id)).Id.Should().Be(normal.Id);
        (await owner.GetGroupByIdAsync(builtIn.Id)).Id.Should().Be(builtIn.Id);

        var result = await owner.BulkDeleteGroupsAsync(request);

        result.Should().Be(new GroupBulkDeleteResponse(Deleted: 1, Skipped: 1));
        var deleted = () => owner.GetGroupByIdAsync(normal.Id);
        await deleted.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        (await owner.GetGroupByIdAsync(builtIn.Id)).QuerySourceKey.Should().Be("save-for-later");
        (await owner.GetGroupByIdAsync(retained.Id)).Id.Should().Be(retained.Id);
    }

    private static GroupCreateDto GroupRequest(string name, int sortOrder) => new(
        Name: name,
        Aliases: null,
        Date: null,
        Rating: null,
        StudioId: null,
        Director: null,
        Description: null,
        Urls: [],
        TagIds: [],
        SortOrder: sortOrder);

    private static SegmentCreateDto Segment(double startSec, double endSec, int tagId, string title) => new(
        startSec,
        endSec,
        tagId,
        "chapter",
        null,
        null,
        "group-span",
        null,
        0.8f,
        title,
        null);

    private static object ItemState(GroupItemDto item) => new
    {
        item.Id,
        item.GroupId,
        item.OrderIndex,
        item.Kind,
        item.HostType,
        item.HostId,
        item.VideoId,
        item.VideoTitle,
        item.StartSec,
        item.EndSec,
        item.Title,
        item.SourceSpanKey,
        item.SourceProfileId,
        item.SourceQueryJson,
    };
}
