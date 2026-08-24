using System.Globalization;
using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Entities.Relationships;

[Collection(ApiTestLane2Collection.Name)]
public sealed class GroupItemBranchCoverageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenMemberReadDenialsForGroupItemHosts_WhenMemberReadsParent_ThenScopedHostsAreHiddenButFaceRemainsPermissionOnly()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var group = await owner.CreateGroupAsync($"Scoped group item parent {suffix}", TestContext.Current.CancellationToken);
        var studio = await owner.CreateStudioAsync($"Scoped group item studio {suffix}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Scoped group item tag {suffix}", TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Scoped group item gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var face = await owner.CreateFaceAsync(new FaceCreateDto($"Scoped group item face {suffix}", null, false, "api-test"), TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Scoped group item video {suffix}", TestContext.Current.CancellationToken);
        var segment = await owner.CreateVideoSegmentAsync(video, $"Scoped group item segment {suffix}", TestContext.Current.CancellationToken);
        var created = new[]
        {
            await owner.CreateGroupItemAsync(group.Id, CreateItem(0, GroupItemKind.Studio, "studio", studio.Id), TestContext.Current.CancellationToken),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(1, GroupItemKind.Tag, "tag", tag.Id), TestContext.Current.CancellationToken),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(2, GroupItemKind.Gallery, "gallery", gallery.Id), TestContext.Current.CancellationToken),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(3, GroupItemKind.Face, "face", face.Id), TestContext.Current.CancellationToken),
            await owner.CreateGroupItemAsync(group.Id, CreateItem(4, GroupItemKind.Segment, "segment", segment.Id), TestContext.Current.CancellationToken),
        };
        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;

        (await member.GetGroupItemsPageAsync(group.Id, perPage: 10, cancellationToken: TestContext.Current.CancellationToken)).Items.Select(item => item.Id).Should().Equal(created.Select(item => item.Id));

        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Studio, studio.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Tag, tag.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Gallery, gallery.Id);
        await CreateReadDenyAsync(owner, memberRole.Id, EntityKinds.Video, video.Id);

        var page = await member.GetGroupItemsPageAsync(group.Id, perPage: 10, cancellationToken: TestContext.Current.CancellationToken);
        var deniedStudio = () => member.GetStudioByIdAsync(studio.Id);
        var deniedTag = () => member.GetTagByIdAsync(tag.Id);
        var deniedGallery = () => member.GetGalleryByIdAsync(gallery.Id);
        var deniedVideo = () => member.GetVideoByIdAsync(video.Id);
        var deniedSegment = () => member.GetSegmentByIdAsync(segment.Id);
        var retainedFace = await member.GetFaceByIdAsync(face.Id, TestContext.Current.CancellationToken);

        await deniedStudio.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedTag.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedGallery.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedVideo.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await deniedSegment.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle().Which.Id.Should().Be(created[3].Id);
        page.Items.Single().HostType.Should().Be("face");
        page.Items.Single().HostId.Should().Be(face.Id);
        retainedFace.Id.Should().Be(face.Id);
    }

    [Fact]
    public async Task GivenInvalidGroupItemCreateRequests_WhenMemberPostsItems_ThenEachReturnsBadRequestWithoutPersistingRows()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Group item validation video {suffix}", TestContext.Current.CancellationToken);
        var staticGroup = await owner.CreateGroupAsync($"Group item validation static {suffix}", TestContext.Current.CancellationToken);
        var audioOnlyGroup = await owner.CreateGroupAsync(GroupRequest(
            $"Group item validation allowed hosts {suffix}",
            ["audio"]), TestContext.Current.CancellationToken);
        var dynamicGroup = await owner.CreateGroupAsync($"Group item validation dynamic {suffix}", TestContext.Current.CancellationToken);
        await member.UpdateGroupQueryAsync(dynamicGroup.Id, new GroupQueryUpdateDto(
            "filter",
            FilterQuery($"no-dynamic-item-{suffix}", ["video"])), TestContext.Current.CancellationToken);

        await AssertBadRequestAndNoItemsAsync(owner, staticGroup.Id, () => member.CreateGroupItemAsync(
            staticGroup.Id,
            CreateItem(0, GroupItemKind.Video, "video", hostId: null)));
        await AssertBadRequestAndNoItemsAsync(owner, staticGroup.Id, () => member.CreateGroupItemAsync(
            staticGroup.Id,
            CreateItem(0, GroupItemKind.Video, "video", int.MaxValue)));
        await AssertBadRequestAndNoItemsAsync(owner, staticGroup.Id, () => member.CreateGroupItemAsync(
            staticGroup.Id,
            CreateItem(0, GroupItemKind.Video, "unsupported", video.Id)));
        await AssertBadRequestAndNoItemsAsync(owner, staticGroup.Id, () => member.CreateGroupItemAsync(
            staticGroup.Id,
            CreateItem(0, GroupItemKind.Group, "group", staticGroup.Id)));
        await AssertBadRequestAndNoItemsAsync(owner, staticGroup.Id, () => member.CreateGroupItemAsync(
            staticGroup.Id,
            CreateItem(0, GroupItemKind.VideoRange, "video", video.Id, startSec: 7, endSec: 3)));
        await AssertBadRequestAndNoItemsAsync(owner, audioOnlyGroup.Id, () => member.CreateGroupItemAsync(
            audioOnlyGroup.Id,
            CreateItem(0, GroupItemKind.Video, "video", video.Id)));
        Func<Task> createDynamicItem = () => member.CreateGroupItemAsync(
            dynamicGroup.Id,
            CreateItem(0, GroupItemKind.Video, "video", video.Id));
        await createDynamicItem.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var restoredStaticGroup = await member.UpdateGroupAsync(dynamicGroup.Id, new GroupUpdateDto(
            Name: null,
            Aliases: null,
            Date: null,
            Rating: null,
            StudioId: null,
            Director: null,
            Description: null,
            Urls: null,
            TagIds: null,
            CustomFields: null,
            Kind: GroupKind.Static), TestContext.Current.CancellationToken);
        var restoredStaticPage = await owner.GetGroupItemsPageAsync(dynamicGroup.Id, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);

        (await owner.GetGroupByIdAsync(staticGroup.Id, TestContext.Current.CancellationToken)).ItemCount.Should().Be(0);
        (await owner.GetGroupByIdAsync(audioOnlyGroup.Id, TestContext.Current.CancellationToken)).ItemCount.Should().Be(0);
        restoredStaticGroup.Kind.Should().Be(GroupKind.Static);
        restoredStaticPage.TotalCount.Should().Be(0);
        restoredStaticPage.Items.Should().BeEmpty();
        (await owner.GetGroupByIdAsync(dynamicGroup.Id, TestContext.Current.CancellationToken)).ItemCount.Should().Be(0);
    }

    [Fact]
    public async Task GivenMixedStaticGroupItems_WhenMemberReadsPlaybackManifest_ThenOnlyPlayableItemsKeepTheirExactOrderAndProjection()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var group = await owner.CreateGroupAsync($"Mixed manifest group {suffix}", TestContext.Current.CancellationToken);
        var childGroup = await owner.CreateGroupAsync($"Mixed manifest child group {suffix}", TestContext.Current.CancellationToken);
        var video = await owner.CreateVideoAsync($"Mixed manifest video {suffix}", TestContext.Current.CancellationToken);
        var audio = await owner.CreateAudioAsync($"Mixed manifest audio {suffix}", TestContext.Current.CancellationToken);
        var image = await owner.CreateImageAsync($"Mixed manifest image {suffix}", TestContext.Current.CancellationToken);
        var text = await owner.CreateTextAsync($"Mixed manifest text {suffix}", TestContext.Current.CancellationToken);
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder()
            .WithName($"Mixed manifest performer {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var studio = await owner.CreateStudioAsync($"Mixed manifest studio {suffix}", TestContext.Current.CancellationToken);
        var tag = await owner.CreateTagAsync($"Mixed manifest tag {suffix}", TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Mixed manifest gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var face = await owner.CreateFaceAsync(new FaceCreateDto($"Mixed manifest face {suffix}", null, false, "api-test"), TestContext.Current.CancellationToken);
        var segment = await owner.CreateVideoSegmentAsync(video, new SegmentCreateDto(
            StartSec: 4,
            EndSec: 9,
            TagId: null,
            Kind: "chapter",
            RefId: null,
            Payload: null,
            SourceKey: "api-test",
            SourceRunId: null,
            Confidence: null,
            Title: $"Mixed manifest segment {suffix}",
            ColorHint: null), TestContext.Current.CancellationToken);

        var nonPlayable = new List<GroupItemDto>
        {
            await owner.CreateGroupItemAsync(group.Id, CreateItem(0, GroupItemKind.Performer, "performer", performer.Id), TestContext.Current.CancellationToken),
        };
        var videoRangeItem = await owner.CreateGroupItemAsync(group.Id, CreateItem(
            1, GroupItemKind.VideoRange, "video", video.Id, startSec: 1, endSec: 4, title: "Video range item title"), TestContext.Current.CancellationToken);
        nonPlayable.Add(await owner.CreateGroupItemAsync(group.Id, CreateItem(2, GroupItemKind.Studio, "studio", studio.Id), TestContext.Current.CancellationToken));
        var audioItem = await owner.CreateGroupItemAsync(group.Id, CreateItem(
            3, GroupItemKind.Audio, "audio", audio.Id, startSec: 3, endSec: 7, title: "Audio item title"), TestContext.Current.CancellationToken);
        nonPlayable.Add(await owner.CreateGroupItemAsync(group.Id, CreateItem(4, GroupItemKind.Tag, "tag", tag.Id), TestContext.Current.CancellationToken));
        var imageItem = await owner.CreateGroupItemAsync(group.Id, CreateItem(
            5, GroupItemKind.Image, "image", image.Id, title: "Image item title"), TestContext.Current.CancellationToken);
        nonPlayable.Add(await owner.CreateGroupItemAsync(group.Id, CreateItem(6, GroupItemKind.Gallery, "gallery", gallery.Id), TestContext.Current.CancellationToken));
        var textItem = await owner.CreateGroupItemAsync(group.Id, CreateItem(
            7, GroupItemKind.Text, "text", text.Id, title: "Text item title"), TestContext.Current.CancellationToken);
        nonPlayable.Add(await owner.CreateGroupItemAsync(group.Id, CreateItem(8, GroupItemKind.Face, "face", face.Id), TestContext.Current.CancellationToken));
        var segmentItem = await owner.CreateGroupItemAsync(group.Id, CreateItem(
            9, GroupItemKind.Segment, "segment", segment.Id, title: "Segment item title"), TestContext.Current.CancellationToken);
        nonPlayable.Add(await owner.CreateGroupItemAsync(group.Id, CreateItem(10, GroupItemKind.Group, "group", childGroup.Id), TestContext.Current.CancellationToken));

        var page = await member.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);
        var manifest = await member.GetGroupPlaybackManifestAsync(group.Id, TestContext.Current.CancellationToken);

        page.TotalCount.Should().Be(11);
        page.Items.Select(item => item.Id).Should().Equal(
            nonPlayable[0].Id,
            videoRangeItem.Id,
            nonPlayable[1].Id,
            audioItem.Id,
            nonPlayable[2].Id,
            imageItem.Id,
            nonPlayable[3].Id,
            textItem.Id,
            nonPlayable[4].Id,
            segmentItem.Id,
            nonPlayable[5].Id);
        page.Items.Select(item => item.OrderIndex).Should().Equal(Enumerable.Range(0, 11));
        manifest.Items.Select(ManifestState.From).Should().Equal(
            new ManifestState(videoRangeItem.Id, "video", video.Id, video.Id, null, null, null, null, video.Title,
                $"/api/stream/video/{video.Id}", 1, 4, 3, null, $"/api/stream/video/{video.Id}/screenshot", "Video range item title", null, false),
            new ManifestState(audioItem.Id, "audio", audio.Id, null, audio.Id, null, null, null, null,
                $"/api/audios/{audio.Id}/stream", 3, 7, 4, null, null, "Audio item title", null, false),
            new ManifestState(imageItem.Id, "image", image.Id, null, null, image.Id, null, null, null,
                $"/api/stream/image/{image.Id}", 0, null, null, null, $"/api/stream/image/{image.Id}/thumbnail", "Image item title", null, false),
            new ManifestState(textItem.Id, "text", text.Id, null, null, null, text.Id, null, null,
                $"/api/texts/{text.Id}/file", 0, null, null, null, null, "Text item title", null, false),
            new ManifestState(segmentItem.Id, "segment", segment.Id, video.Id, null, null, null, segment.Id, video.Title,
                $"/api/stream/video/{video.Id}", 4, 9, 5, null, $"/api/stream/video/{video.Id}/screenshot", "Segment item title", null, false));
        foreach (var item in nonPlayable)
            manifest.Items.Should().NotContain(candidate => candidate.GroupItemId == item.Id);
    }

    [Fact]
    public async Task GivenFiveTypeFilteredDynamicGroup_WhenMemberListsPagesAndReadsManifest_ThenSyntheticItemsStayOrderedAndPlayable()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Dynamic five type video {suffix}", TestContext.Current.CancellationToken);
        var excludedVideo = await owner.CreateVideoAsync($"Dynamic excluded control {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var audio = await owner.CreateAudioAsync($"Dynamic five type audio {suffix}", TestContext.Current.CancellationToken);
        var image = await owner.CreateImageAsync($"Dynamic five type image {suffix}", TestContext.Current.CancellationToken);
        var text = await owner.CreateTextAsync($"Dynamic five type text {suffix}", TestContext.Current.CancellationToken);
        var segment = await owner.CreateVideoSegmentAsync(video, new SegmentCreateDto(
            StartSec: 2,
            EndSec: 5,
            TagId: null,
            Kind: "chapter",
            RefId: null,
            Payload: null,
            SourceKey: "api-test",
            SourceRunId: null,
            Confidence: null,
            Title: $"Dynamic five type segment {suffix}",
            ColorHint: null), TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Dynamic five type group {suffix}", TestContext.Current.CancellationToken);
        await member.UpdateGroupQueryAsync(group.Id, new GroupQueryUpdateDto(
            "filter",
            FilterQuery(suffix, ["video", "audio", "image", "text", "segment"])), TestContext.Current.CancellationToken);
        var dynamicGroup = await owner.GetGroupByIdAsync(group.Id, TestContext.Current.CancellationToken);

        var listed = await member.GetGroupItemsAsync(dynamicGroup, TestContext.Current.CancellationToken);
        var firstPage = await member.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 2, cancellationToken: TestContext.Current.CancellationToken);
        var secondPage = await member.GetGroupItemsPageAsync(group.Id, page: 2, perPage: 2, cancellationToken: TestContext.Current.CancellationToken);
        var thirdPage = await member.GetGroupItemsPageAsync(group.Id, page: 3, perPage: 2, cancellationToken: TestContext.Current.CancellationToken);
        var manifest = await member.GetGroupPlaybackManifestAsync(group.Id, TestContext.Current.CancellationToken);

        dynamicGroup.Kind.Should().Be(GroupKind.Dynamic);
        dynamicGroup.AllowedHostTypes.Should().Equal("video", "audio", "image", "text", "segment");
        listed.Select(DynamicItemState.From).Should().Equal(
            new DynamicItemState(-1, group.Id, 0, GroupItemKind.Video, "video", video.Id, video.Id, video.Title, null, null, null, null, video.Title),
            new DynamicItemState(-2, group.Id, 1, GroupItemKind.Audio, "audio", audio.Id, null, null, null, null, null, null, audio.Title),
            new DynamicItemState(-3, group.Id, 2, GroupItemKind.Image, "image", image.Id, null, null, image.Id, image.Title, null, null, image.Title),
            new DynamicItemState(-4, group.Id, 3, GroupItemKind.Text, "text", text.Id, null, null, null, null, null, null, text.Title),
            new DynamicItemState(-5, group.Id, 4, GroupItemKind.Segment, "segment", segment.Id, video.Id, null, null, null, 2, 5, segment.Title));
        listed.Should().NotContain(item => item.HostType == "video" && item.HostId == excludedVideo.Id);
        firstPage.TotalCount.Should().Be(5);
        firstPage.Page.Should().Be(1);
        firstPage.PerPage.Should().Be(2);
        firstPage.Items.Select(item => item.Id).Should().Equal(-1, -2);
        secondPage.TotalCount.Should().Be(5);
        secondPage.Page.Should().Be(2);
        secondPage.PerPage.Should().Be(2);
        secondPage.Items.Select(item => item.Id).Should().Equal(-3, -4);
        thirdPage.TotalCount.Should().Be(5);
        thirdPage.Page.Should().Be(3);
        thirdPage.PerPage.Should().Be(2);
        thirdPage.Items.Select(item => item.Id).Should().Equal(-5);
        firstPage.Items.Concat(secondPage.Items).Concat(thirdPage.Items).Select(item => item.Id).Should().OnlyHaveUniqueItems();
        manifest.Items.Select(ManifestState.From).Should().Equal(
            new ManifestState(-1, "video", video.Id, video.Id, null, null, null, null, video.Title,
                $"/api/stream/video/{video.Id}", 0, null, null, null, $"/api/stream/video/{video.Id}/screenshot", video.Title, null, false),
            new ManifestState(-2, "audio", audio.Id, null, audio.Id, null, null, null, null,
                $"/api/audios/{audio.Id}/stream", 0, null, null, null, null, audio.Title, null, false),
            new ManifestState(-3, "image", image.Id, null, null, image.Id, null, null, null,
                $"/api/stream/image/{image.Id}", 0, null, null, null, $"/api/stream/image/{image.Id}/thumbnail", image.Title, null, false),
            new ManifestState(-4, "text", text.Id, null, null, null, text.Id, null, null,
                $"/api/texts/{text.Id}/file", 0, null, null, null, null, text.Title, null, false),
            new ManifestState(-5, "segment", segment.Id, video.Id, null, null, null, segment.Id, video.Title,
                $"/api/stream/video/{video.Id}", 2, 5, 3, null, $"/api/stream/video/{video.Id}/screenshot", segment.Title, null, false));
    }

    private static async Task AssertBadRequestAndNoItemsAsync(CoveClient reader, int groupId, Func<Task> action)
    {
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var page = await reader.GetGroupItemsPageAsync(groupId, perPage: 25);
        page.TotalCount.Should().Be(0);
        page.Items.Should().BeEmpty();
    }

    private static async Task CreateReadDenyAsync(CoveClient owner, int roleId, string entityKind, int entityId)
    {
        var created = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            roleId,
            entityKind,
            entityId.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"));

        created.RoleId.Should().Be(roleId);
        created.EntityKind.Should().Be(entityKind);
        created.EntityId.Should().Be(entityId.ToString(CultureInfo.InvariantCulture));
        created.Effect.Should().Be("deny");
        created.AppliesTo.Should().Be("read");
    }

    private static GroupCreateDto GroupRequest(string name, List<string> allowedHostTypes) => new(
        Name: name,
        Aliases: null,
        Date: null,
        Rating: null,
        StudioId: null,
        Director: null,
        Description: null,
        Urls: [],
        TagIds: [],
        AllowedHostTypes: allowedHostTypes);

    private static GroupItemCreateDto CreateItem(
        int orderIndex,
        GroupItemKind kind,
        string hostType,
        int? hostId,
        double? startSec = null,
        double? endSec = null,
        string? title = null)
        => new(
            OrderIndex: orderIndex,
            Kind: kind,
            VideoId: string.Equals(hostType, "video", StringComparison.OrdinalIgnoreCase) ? hostId : null,
            HostType: hostType,
            HostId: hostId,
            StartSec: startSec,
            EndSec: endSec,
            Title: title,
            Notes: null,
            SourceSpanKey: null,
            SourceProfileId: null);

    private static string FilterQuery(string query, IReadOnlyList<string> entityTypes)
        => JsonSerializer.Serialize(new
        {
            entityTypes,
            findFilters = entityTypes.ToDictionary(
                entityType => entityType,
                _ => (object)new { q = query, sort = "title", direction = "asc" }),
        });

    private sealed record DynamicItemState(
        int Id,
        int GroupId,
        int OrderIndex,
        GroupItemKind Kind,
        string HostType,
        int HostId,
        int? VideoId,
        string? VideoTitle,
        int? ImageId,
        string? ImageTitle,
        double? StartSec,
        double? EndSec,
        string? Title)
    {
        public static DynamicItemState From(GroupItemDto item) => new(
            item.Id,
            item.GroupId,
            item.OrderIndex,
            item.Kind,
            item.HostType,
            item.HostId,
            item.VideoId,
            item.VideoTitle,
            item.ImageId,
            item.ImageTitle,
            item.StartSec,
            item.EndSec,
            item.Title);
    }

    private sealed record ManifestState(
        int GroupItemId,
        string HostType,
        int HostId,
        int? VideoId,
        int? AudioId,
        int? ImageId,
        int? TextId,
        int? SegmentId,
        string? VideoTitle,
        string Src,
        double StartSec,
        double? EndSec,
        double? DurationSec,
        double? DisplayDurationSec,
        string? PosterPath,
        string? Title,
        string? Format,
        bool HasVideoTrack)
    {
        public static ManifestState From(GroupPlaybackManifestItemDto item) => new(
            item.GroupItemId,
            item.HostType,
            item.HostId,
            item.VideoId,
            item.AudioId,
            item.ImageId,
            item.TextId,
            item.SegmentId,
            item.VideoTitle,
            item.Src,
            item.StartSec,
            item.EndSec,
            item.DurationSec,
            item.DisplayDurationSec,
            item.PosterPath,
            item.Title,
            item.Format,
            item.HasVideoTrack);
    }
}
