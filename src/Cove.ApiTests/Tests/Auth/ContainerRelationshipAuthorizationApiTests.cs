using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Xunit.Abstractions;

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
        var performer = await owner.CreatePerformerAsync(new PerformerBuilder().WithName($"Related performer {suffix}").Build());
        var studio = await owner.CreateStudioAsync($"Related studio {suffix}");
        var relationTag = await owner.CreateTagAsync($"Related tag {suffix}");
        var hiddenTag = await owner.CreateTagAsync($"Related hidden tag {suffix}");
        var visible = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Related visible video {suffix}")
            .WithStudio(studio)
            .WithPerformers([performer])
            .WithTags([relationTag])
            .Build());
        var hidden = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Related hidden video {suffix}")
            .WithStudio(studio)
            .WithPerformers([performer])
            .WithTags([relationTag, hiddenTag])
            .Build());
        var roleName = $"Restricted relationships {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Reads relationship projections without hidden media disclosures.",
            [Permissions.VideosRead, Permissions.PerformersRead, Permissions.StudiosRead, Permissions.TagsRead]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"));
        var username = $"restricted-relationships-{suffix}";
        const string password = "Restricted relationships password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var user = session.Client;

        (await user.GetVideosByPerformerAsync(performer.Id)).Select(video => video.Id).Should().Equal(visible.Id);
        (await user.GetVideosByStudioAsync(studio.Id)).Select(video => video.Id).Should().Equal(visible.Id);
        (await user.FindVideosAsync(new FilteredQueryRequest<VideoFilter>
        {
            ObjectFilter = new VideoFilter { TagIds = [relationTag.Id] },
            FindFilter = new FindFilter { Page = 1, PerPage = 25 },
        })).Items.Select(video => video.Id).Should().Equal(visible.Id);

        (await user.GetPerformerByIdAsync(performer.Id)).VideoCount.Should().Be(1);
        (await user.GetStudioByIdAsync(studio.Id)).VideoCount.Should().Be(1);
        (await user.GetTagByIdAsync(relationTag.Id)).VideoCount.Should().Be(1);
        await user.AssertResponseAsync($"/api/videos/{hidden.Id}", HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenRestrictedChildren_WhenContainersAreReadOrMutated_ThenRelationshipsStayFilteredAndAtomic()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Container hidden tag {suffix}");
        var visibleImagePath = Path.Combine(AsTestFileSystem().LibraryPath, $"container-visible-{suffix}.png");
        File.WriteAllBytes(visibleImagePath, ApiTestImages.RedPixelPng());
        File.SetLastWriteTimeUtc(visibleImagePath, DateTime.UtcNow.AddMinutes(-1));
        var imageScan = await owner.StartMetadataScanAsync(new ScanOptionsDto { Paths = [visibleImagePath] });
        (await owner.WaitForTerminalJobAsync(imageScan)).Status.Should().Be(JobStatus.Completed);
        var visibleImage = (await owner.GetImagesAsync()).Single(image =>
            image.Files.Any(file => Path.GetFullPath(file.Path) == Path.GetFullPath(visibleImagePath)));
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Container hidden image {suffix}")
            .WithTag(hiddenTag)
            .Build());
        var candidateImage = await owner.CreateImageAsync($"Container candidate image {suffix}");
        var visibleVideo = await owner.CreateVideoAsync($"Container visible video {suffix}");
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Container hidden video {suffix}")
            .WithTags([hiddenTag])
            .Build());
        var candidateVideo = await owner.CreateVideoAsync($"Container candidate video {suffix}");
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Restricted container gallery {suffix}")
            .Build());
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Restricted hidden gallery {suffix}")
            .WithTag(hiddenTag)
            .Build());
        var hiddenGroup = await owner.CreateGroupAsync($"Restricted hidden group {suffix}");
        await owner.AssertResponseAsync(HttpMethod.Put, $"/api/groups/{hiddenGroup.Id}",
            payload: new { tagIds = new[] { hiddenTag.Id } });
        var candidateAudio = await owner.CreateAudioAsync($"Container candidate audio {suffix}");
        var candidateText = await owner.CreateTextAsync($"Container candidate text {suffix}");
        await owner.AddGalleryImagesAsync(gallery, [visibleImage, hiddenImage]);
        await owner.SetGalleryCoverAsync(gallery, hiddenImage);
        await owner.UpdateGalleryAsync(gallery.Id, new { videoIds = new[] { visibleVideo.Id, hiddenVideo.Id } });
        var group = await owner.CreateGroupAsync($"Restricted container group {suffix}");
        await owner.CreateGroupItemAsync(group.Id, CreateVideoItem(0, visibleVideo.Id));
        await owner.CreateGroupItemAsync(group.Id, CreateVideoItem(1, hiddenVideo.Id));
        var hiddenParentSegment = await owner.CreateVideoSegmentAsync(hiddenVideo, $"Hidden parent segment {suffix}");
        await owner.CreateGroupItemAsync(group.Id, new GroupItemCreateDto(
            2, GroupItemKind.Segment, hiddenParentSegment.Id, EntityKinds.Segment, null,
            null, null, null, null, null, null));

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
            ]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Image, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Gallery, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Group, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "read"));
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            role.Id, EntityKinds.Tag, hiddenTag.Id.ToString(), "deny", "read"));
        var username = $"restricted-containers-{suffix}";
        const string password = "Restricted containers password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var user = session.Client;

        var visibleGallery = await user.GetGalleryByIdAsync(gallery.Id);
        visibleGallery.ImageCount.Should().Be(1);
        visibleGallery.VideoCount.Should().Be(1);
        visibleGallery.VideoIds.Should().Equal(visibleVideo.Id);
        visibleGallery.CoverImageId.Should().BeNull();
        (await user.GetGalleryCoverAsync(visibleGallery)).Content.Should().StartWith(ApiTestImages.RedPixelPng()[..8]);
        var page = await user.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25);
        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(item => item.HostId == visibleVideo.Id);

        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/galleries/{gallery.Id}/images",
            HttpStatusCode.Forbidden,
            new GalleryAddImagesDto([candidateImage.Id, hiddenImage.Id]));
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/galleries/{gallery.Id}",
            HttpStatusCode.Forbidden,
            new { videoIds = new[] { candidateVideo.Id, hiddenVideo.Id } });
        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items",
            HttpStatusCode.Forbidden,
            CreateVideoItem(2, hiddenVideo.Id));
        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items",
            HttpStatusCode.Forbidden,
            new GroupItemCreateDto(2, GroupItemKind.Video, hiddenVideo.Id, null, hiddenVideo.Id,
                null, null, null, null, null, null));
        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items/remove-hosts",
            HttpStatusCode.Forbidden,
            new GroupItemsRemoveHostsDto(GroupItemKind.Video, [visibleVideo.Id, hiddenVideo.Id]));
        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items/remove-hosts",
            HttpStatusCode.Forbidden,
            new GroupItemsRemoveHostsDto(GroupItemKind.Segment, [hiddenParentSegment.Id]));
        await user.AssertResponseAsync(
            HttpMethod.Post,
            $"/api/groups/{group.Id}/items/from-spans",
            HttpStatusCode.Forbidden,
            new GroupItemsFromSpansDto([
                new GroupItemSpanInputDto(null, visibleVideo.Id, 0, 1, null, null),
                new GroupItemSpanInputDto(null, hiddenVideo.Id, 0, 1, null, null),
            ]));
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/groups/{group.Id}",
            HttpStatusCode.Forbidden,
            new { tagIds = new[] { hiddenTag.Id } });
        await user.AssertResponseAsync(
            HttpMethod.Post,
            "/api/groups/bulk",
            HttpStatusCode.Forbidden,
            new BulkGroupUpdateDto
            {
                Ids = [group.Id],
                TagIds = [hiddenTag.Id],
                TagMode = BulkUpdateMode.Set,
            });
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/images/{candidateImage.Id}",
            HttpStatusCode.Forbidden,
            new { galleryIds = new[] { hiddenGallery.Id } });
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/audios/{candidateAudio.Id}",
            HttpStatusCode.Forbidden,
            new { groupIds = new[] { new { groupId = hiddenGroup.Id, videoIndex = 0 } } });
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/texts/{candidateText.Id}",
            HttpStatusCode.Forbidden,
            new { groupIds = new[] { new { groupId = hiddenGroup.Id, videoIndex = 0 } } });
        await user.AssertResponseAsync(
            HttpMethod.Put,
            $"/api/galleries/{gallery.Id}/image/source",
            HttpStatusCode.Forbidden,
            new EntityImageCoverSourceDto(ImageId: hiddenImage.Id));

        var ownerGallery = await owner.GetGalleryByIdAsync(gallery.Id);
        ownerGallery.ImageCount.Should().Be(2);
        ownerGallery.VideoIds.Should().BeEquivalentTo([visibleVideo.Id, hiddenVideo.Id]);
        (await owner.GetImageByIdAsync(candidateImage.Id)).GalleryIds.Should().NotContain(gallery.Id);
        (await owner.GetImageByIdAsync(candidateImage.Id)).GalleryIds.Should().NotContain(hiddenGallery.Id);
        var ownerGroup = await owner.GetGroupByIdAsync(group.Id);
        ownerGroup.Tags.Should().BeEmpty();
        (await owner.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25)).Items.Should().HaveCount(3);
        await user.CreateGroupItemAsync(group.Id, CreateVideoItem(1, candidateVideo.Id));
        var reorderedOwnerItems = (await owner.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25)).Items;
        reorderedOwnerItems.Should().HaveCount(4);
        reorderedOwnerItems.Select(item => item.OrderIndex).Should().Equal(0, 1, 2, 3);

        var share = await user.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Gallery, [gallery.Id.ToString()]));
        var shareViewer = AsShareLink(share);
        var sharedGallery = await shareViewer.GetGalleryByIdAsync(gallery.Id);
        sharedGallery.ImageCount.Should().Be(1);
        sharedGallery.VideoCount.Should().Be(1);
        sharedGallery.VideoIds.Should().Equal(visibleVideo.Id);
        sharedGallery.CoverImageId.Should().BeNull();
        (await shareViewer.GetGalleryCoverAsync(sharedGallery)).Content.Should().StartWith(ApiTestImages.RedPixelPng()[..8]);
        await shareViewer.AssertResponseAsync($"/api/images/{hiddenImage.Id}", HttpStatusCode.NotFound);
        await shareViewer.AssertResponseAsync($"/api/videos/{hiddenVideo.Id}", HttpStatusCode.NotFound);

        await owner.AddGalleryImagesAsync(gallery, [candidateImage]);
        await shareViewer.AssertResponseAsync($"/api/images/{candidateImage.Id}", HttpStatusCode.NotFound);

        var credentialGallery = await owner.CreateGalleryAsync(new GalleryBuilder()
            .WithTitle($"Share credential gallery {suffix}")
            .Build());
        await owner.UploadGalleryImageAsync(credentialGallery, ApiTestImages.OnePixelPng());
        var credentialShare = await user.CreateShareLinkAsync(new CreateShareLinkRequest(
            EntityKinds.Gallery, [credentialGallery.Id.ToString()]));
        var credentialViewer = AsShareLink(credentialShare);
        (await credentialViewer.GetGalleryCoverAsync(credentialGallery)).Content.Should().Equal(ApiTestImages.OnePixelPng());
        await AsAnonymous().AssertResponseAsync(
            $"/api/galleries/{credentialGallery.Id}/cover?share_token={Uri.EscapeDataString(credentialShare.PlaintextToken)}");
    }

    private static GroupItemCreateDto CreateVideoItem(int orderIndex, int videoId)
        => new(orderIndex, GroupItemKind.Video, videoId, EntityKinds.Video, videoId,
            null, null, null, null, null, null);
}
