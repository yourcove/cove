using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class ContainerRelationshipAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenRestrictedRelatedMedia_WhenRelationshipViewsAreRead_ThenIdsAndCountsExcludeHiddenVideos()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Related performer {suffix}").Build(), TestContext.Current.CancellationToken);
        var studio = await owner.CreateStudioAsync($"Related studio {suffix}", TestContext.Current.CancellationToken);
        var relationTag = await owner.CreateTagAsync($"Related tag {suffix}", TestContext.Current.CancellationToken);
        var hiddenTag = await owner.CreateTagAsync($"Related hidden tag {suffix}", TestContext.Current.CancellationToken);
        var visible = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Related visible video {suffix}")
            .WithStudio(studio)
            .WithPerformers([performer])
            .WithTags([relationTag])
            .Build(), TestContext.Current.CancellationToken);
        var hidden = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Related hidden video {suffix}")
            .WithStudio(studio)
            .WithPerformers([performer])
            .WithTags([relationTag, hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var roleName = $"Restricted relationships {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Reads relationship projections without hidden media disclosures.",
            [Permissions.VideosRead, Permissions.PerformersRead, Permissions.StudiosRead, Permissions.TagsRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var username = $"restricted-relationships-{suffix}";
        const string password = "Restricted relationships password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var user = session.Client;

        (await user.GetVideosByPerformerAsync(performer.Id, TestContext.Current.CancellationToken)).Select(video => video.Id).Should().Equal(visible.Id);
        (await user.GetVideosByStudioAsync(studio.Id, TestContext.Current.CancellationToken)).Select(video => video.Id).Should().Equal(visible.Id);
        (await user.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { TagIds = [relationTag.Id] },
            FindFilter = new FindFilter { Page = 1, PerPage = 25 },
        }, TestContext.Current.CancellationToken)).Items.Select(video => video.Id).Should().Equal(visible.Id);

        (await user.GetPerformerByIdAsync(performer.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(1);
        (await user.GetStudioByIdAsync(studio.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(1);
        (await user.GetTagByIdAsync(relationTag.Id, TestContext.Current.CancellationToken)).VideoCount.Should().Be(1);
        await user.AssertResponseAsync($"/api/videos/{hidden.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenRestrictedChildren_WhenContainersAreReadOrMutated_ThenRelationshipsStayFilteredAndAtomic()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Container hidden tag {suffix}", TestContext.Current.CancellationToken);
        var visibleImagePath = Path.Combine(AsTestFileSystem().LibraryPath, $"container-visible-{suffix}.png");
        File.WriteAllBytes(visibleImagePath, ApiTestImages.RedPixelPng());
        File.SetLastWriteTimeUtc(visibleImagePath, DateTime.UtcNow.AddMinutes(-1));
        var imageScan = await owner.StartMetadataScanAsync(new ScanOptionsDto { Paths = [visibleImagePath] }, TestContext.Current.CancellationToken);
        (await owner.WaitForTerminalJobAsync(imageScan, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        var visibleImage = (await owner.GetImagesAsync(TestContext.Current.CancellationToken)).Single(image =>
            image.Files.Any(file => Path.GetFullPath(file.Path) == Path.GetFullPath(visibleImagePath)));
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Container hidden image {suffix}")
            .WithTag(hiddenTag)
            .Build(), TestContext.Current.CancellationToken);
        var candidateImage = await owner.CreateImageAsync($"Container candidate image {suffix}", TestContext.Current.CancellationToken);
        var visibleVideo = await owner.CreateVideoAsync($"Container visible video {suffix}", TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Container hidden video {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var candidateVideo = await owner.CreateVideoAsync($"Container candidate video {suffix}", TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Restricted container gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Restricted hidden gallery {suffix}")
            .WithTag(hiddenTag)
            .Build(), TestContext.Current.CancellationToken);
        var hiddenGroup = await owner.CreateGroupAsync($"Restricted hidden group {suffix}", TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync(HttpMethod.Put, $"/api/groups/{hiddenGroup.Id}", payload: new { tagIds = new[] { hiddenTag.Id } }, cancellationToken: TestContext.Current.CancellationToken);
        var candidateAudio = await owner.CreateAudioAsync($"Container candidate audio {suffix}", TestContext.Current.CancellationToken);
        var candidateText = await owner.CreateTextAsync($"Container candidate text {suffix}", TestContext.Current.CancellationToken);
        await owner.AddGalleryImagesAsync(gallery, [visibleImage, hiddenImage], TestContext.Current.CancellationToken);
        await owner.SetGalleryCoverAsync(gallery, hiddenImage, TestContext.Current.CancellationToken);
        await owner.UpdateGalleryAsync(gallery.Id, new { videoIds = new[] { visibleVideo.Id, hiddenVideo.Id } }, TestContext.Current.CancellationToken);
        var group = await owner.CreateGroupAsync($"Restricted container group {suffix}", TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, CreateVideoItem(0, visibleVideo.Id), TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, CreateVideoItem(1, hiddenVideo.Id), TestContext.Current.CancellationToken);
        var hiddenParentSegment = await owner.CreateVideoSegmentAsync(hiddenVideo, $"Hidden parent segment {suffix}", TestContext.Current.CancellationToken);
        await owner.CreateGroupItemAsync(group.Id, new GroupItemCreateDto(
            2, GroupItemKind.Segment, hiddenParentSegment.Id, EntityKinds.Segment, null,
            null, null, null, null, null, null), TestContext.Current.CancellationToken);

        var roleName = $"Restricted containers {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises filtered container relationships and atomic child mutations.",
            [
                Permissions.GalleriesRead, Permissions.GalleriesWrite,
                Permissions.GroupsRead, Permissions.GroupsWrite,
                Permissions.ImagesRead, Permissions.ImagesWrite, Permissions.VideosRead,
                Permissions.AudiosRead, Permissions.AudiosWrite, Permissions.TextsRead, Permissions.TextsWrite,
                Permissions.SegmentsRead,
                Permissions.TagsRead, Permissions.StudiosRead, Permissions.PerformersRead,
                Permissions.ShareLinksWrite, Permissions.StreamRead,
            ]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Image, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Gallery, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Group, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            role.Id, EntityKinds.Tag, hiddenTag.Id.ToString(), "deny", "read"), TestContext.Current.CancellationToken);
        var username = $"restricted-containers-{suffix}";
        const string password = "Restricted containers password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var user = session.Client;

        var visibleGallery = await user.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        visibleGallery.ImageCount.Should().Be(1);
        visibleGallery.VideoCount.Should().Be(1);
        visibleGallery.VideoIds.Should().Equal(visibleVideo.Id);
        visibleGallery.CoverImageId.Should().BeNull();
        (await user.GetGalleryCoverAsync(visibleGallery, TestContext.Current.CancellationToken)).Content.Should().StartWith(ApiTestImages.RedPixelPng()[..8]);
        var page = await user.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.HostId == visibleVideo.Id);

        await user.AssertResponseAsync(HttpMethod.Post, $"/api/galleries/{gallery.Id}/images", HttpStatusCode.Forbidden, new GalleryAddImagesDto([candidateImage.Id, hiddenImage.Id]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/galleries/{gallery.Id}", HttpStatusCode.Forbidden, new { videoIds = new[] { candidateVideo.Id, hiddenVideo.Id } }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/groups/{group.Id}/items", HttpStatusCode.Forbidden, CreateVideoItem(2, hiddenVideo.Id), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/groups/{group.Id}/items", HttpStatusCode.Forbidden, new GroupItemCreateDto(2, GroupItemKind.Video, hiddenVideo.Id, null, hiddenVideo.Id,
                null, null, null, null, null, null), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/groups/{group.Id}/items/remove-hosts", HttpStatusCode.Forbidden, new GroupItemsRemoveHostsDto(GroupItemKind.Video, [visibleVideo.Id, hiddenVideo.Id]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/groups/{group.Id}/items/remove-hosts", HttpStatusCode.Forbidden, new GroupItemsRemoveHostsDto(GroupItemKind.Segment, [hiddenParentSegment.Id]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/groups/{group.Id}/items/from-spans", HttpStatusCode.Forbidden, new GroupItemsFromSpansDto([
                new GroupItemSpanInputDto(null, visibleVideo.Id, 0, 1, null, null),
                new GroupItemSpanInputDto(null, hiddenVideo.Id, 0, 1, null, null),
            ]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/groups/{group.Id}", HttpStatusCode.Forbidden, new { tagIds = new[] { hiddenTag.Id } }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/groups/bulk", HttpStatusCode.Forbidden, new BulkGroupUpdateDto
            {
                Ids = [group.Id],
                TagIds = [hiddenTag.Id],
                TagMode = BulkUpdateMode.Set,
            }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/images/{candidateImage.Id}", HttpStatusCode.Forbidden, new { galleryIds = new[] { hiddenGallery.Id } }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/audios/{candidateAudio.Id}", HttpStatusCode.Forbidden, new { groupIds = new[] { new { groupId = hiddenGroup.Id, videoIndex = 0 } } }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/texts/{candidateText.Id}", HttpStatusCode.Forbidden, new { groupIds = new[] { new { groupId = hiddenGroup.Id, videoIndex = 0 } } }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/galleries/{gallery.Id}/image/source", HttpStatusCode.Forbidden, new EntityImageCoverSourceDto(ImageId: hiddenImage.Id), TestContext.Current.CancellationToken);

        var ownerGallery = await owner.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        ownerGallery.ImageCount.Should().Be(2);
        ownerGallery.VideoIds.Should().BeEquivalentTo([visibleVideo.Id, hiddenVideo.Id]);
        (await owner.GetImageByIdAsync(candidateImage.Id, TestContext.Current.CancellationToken)).GalleryIds.Should().NotContain(gallery.Id);
        (await owner.GetImageByIdAsync(candidateImage.Id, TestContext.Current.CancellationToken)).GalleryIds.Should().NotContain(hiddenGallery.Id);
        var ownerGroup = await owner.GetGroupByIdAsync(group.Id, TestContext.Current.CancellationToken);
        ownerGroup.Tags.Should().BeEmpty();
        (await owner.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken)).Items.Should().HaveCount(3);
        await user.CreateGroupItemAsync(group.Id, CreateVideoItem(1, candidateVideo.Id), TestContext.Current.CancellationToken);
        var reorderedOwnerItems = (await owner.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken)).Items;
        reorderedOwnerItems.Should().HaveCount(4);
        reorderedOwnerItems.Select(item => item.OrderIndex).Should().Equal(0, 1, 2, 3);

        var share = await user.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Gallery, [gallery.Id.ToString()]), TestContext.Current.CancellationToken);
        var shareViewer = AsShareLink(share);
        var sharedGallery = await shareViewer.GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        sharedGallery.ImageCount.Should().Be(1);
        sharedGallery.VideoCount.Should().Be(1);
        sharedGallery.VideoIds.Should().Equal(visibleVideo.Id);
        sharedGallery.CoverImageId.Should().BeNull();
        (await shareViewer.GetGalleryCoverAsync(sharedGallery, TestContext.Current.CancellationToken)).Content.Should().StartWith(ApiTestImages.RedPixelPng()[..8]);
        await shareViewer.AssertResponseAsync($"/api/images/{hiddenImage.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await shareViewer.AssertResponseAsync($"/api/videos/{hiddenVideo.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);

        await owner.AddGalleryImagesAsync(gallery, [candidateImage], TestContext.Current.CancellationToken);
        await shareViewer.AssertResponseAsync($"/api/images/{candidateImage.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);

        var credentialGallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Share credential gallery {suffix}")
            .Build(), TestContext.Current.CancellationToken);
        await owner.UploadGalleryImageAsync(credentialGallery, ApiTestImages.OnePixelPng(), cancellationToken: TestContext.Current.CancellationToken);
        var credentialShare = await user.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Gallery, [credentialGallery.Id.ToString()]), TestContext.Current.CancellationToken);
        var credentialViewer = AsShareLink(credentialShare);
        (await credentialViewer.GetGalleryCoverAsync(credentialGallery, TestContext.Current.CancellationToken)).Content.Should().Equal(ApiTestImages.OnePixelPng());
        await AsAnonymous().AssertResponseAsync($"/api/galleries/{credentialGallery.Id}/cover?share_token={Uri.EscapeDataString(credentialShare.PlaintextToken)}", cancellationToken: TestContext.Current.CancellationToken);
    }

    private static GroupItemCreateDto CreateVideoItem(int orderIndex, int videoId)
        => new(orderIndex, GroupItemKind.Video, videoId, EntityKinds.Video, videoId,
            null, null, null, null, null, null);
}
