using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Auth;

[Collection(ApiTestLane1Collection.Name)]
public sealed class ScopedMutationAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenBroadMutationPermissionsAndScopedDenies_WhenEntitiesAreMutated_ThenEveryPathIsForbiddenAndAtomic()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Mutation hidden tag {suffix}", TestContext.Current.CancellationToken);
        var deleteOnlyTag = await owner.CreateTagAsync($"Mutation delete-only tag {suffix}", TestContext.Current.CancellationToken);

        var visibleVideo = await owner.CreateVideoAsync($"Mutation visible video {suffix}", TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Mutation hidden video {suffix}")
            .WithTags([hiddenTag])
            .Build(), TestContext.Current.CancellationToken);
        var deleteDeniedMergeSource = await owner.CreateVideoAsync(new VideoBuilder()
            .WithTitle($"Mutation delete-denied merge source {suffix}")
            .WithTags([deleteOnlyTag])
            .Build(), TestContext.Current.CancellationToken);
        var disposableVisibleVideo = await owner.CreateVideoAsync($"Mutation disposable visible video {suffix}", TestContext.Current.CancellationToken);
        var visibleImage = await owner.CreateImageAsync($"Mutation visible image {suffix}", TestContext.Current.CancellationToken);
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder()
            .WithTitle($"Mutation hidden image {suffix}")
            .WithTag(hiddenTag)
            .Build(), TestContext.Current.CancellationToken);
        var visibleAudio = await owner.CreateAudioAsync($"Mutation visible audio {suffix}", TestContext.Current.CancellationToken);
        var hiddenAudio = await owner.CreateAudioAsync($"Mutation hidden audio {suffix}", TestContext.Current.CancellationToken);
        var visibleText = await owner.CreateTextAsync($"Mutation visible text {suffix}", TestContext.Current.CancellationToken);
        var hiddenText = await owner.CreateTextAsync($"Mutation hidden text {suffix}", TestContext.Current.CancellationToken);
        var visibleGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Mutation visible gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Mutation hidden gallery {suffix}").WithTag(hiddenTag).Build(), TestContext.Current.CancellationToken);
        var visibleGroup = await owner.CreateGroupAsync($"Mutation visible group {suffix}", TestContext.Current.CancellationToken);
        var hiddenGroup = await owner.CreateGroupAsync($"Mutation hidden group {suffix}", TestContext.Current.CancellationToken);

        await owner.AssertResponseAsync(HttpMethod.Put, $"/api/audios/{hiddenAudio.Id}", payload: new { tagIds = new[] { hiddenTag.Id } }, cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync(HttpMethod.Put, $"/api/texts/{hiddenText.Id}", payload: new { tagIds = new[] { hiddenTag.Id } }, cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync(HttpMethod.Put, $"/api/groups/{hiddenGroup.Id}", payload: new { tagIds = new[] { hiddenTag.Id } }, cancellationToken: TestContext.Current.CancellationToken);

        var roleName = $"Scoped mutator {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Broad media mutation permissions constrained by content rules.",
            [
                Permissions.VideosRead, Permissions.VideosWrite, Permissions.VideosDelete,
                Permissions.ImagesRead, Permissions.ImagesWrite, Permissions.ImagesDelete,
                Permissions.AudiosRead, Permissions.AudiosWrite, Permissions.AudiosDelete,
                Permissions.TextsRead, Permissions.TextsWrite, Permissions.TextsDelete,
                Permissions.GalleriesRead, Permissions.GalleriesWrite, Permissions.GalleriesDelete,
                Permissions.GroupsRead, Permissions.GroupsWrite, Permissions.GroupsDelete,
                Permissions.LibraryScan, Permissions.LibraryIdentify, Permissions.LibraryClean,
                Permissions.JobsRead, Permissions.JobsRun,
            ]), TestContext.Current.CancellationToken);
        foreach (var entityKind in new[]
                 {
                     EntityKinds.Video, EntityKinds.Image, EntityKinds.Audio,
                     EntityKinds.Text, EntityKinds.Gallery, EntityKinds.Group,
                 })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id, entityKind, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "write"), TestContext.Current.CancellationToken);
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id, entityKind, "deny", "tag", $"{{\"tagId\":{hiddenTag.Id}}}", "delete"), TestContext.Current.CancellationToken);
        }
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id, EntityKinds.Video, "deny", "tag", $"{{\"tagId\":{deleteOnlyTag.Id}}}", "delete"), TestContext.Current.CancellationToken);

        var username = $"scoped-mutator-{suffix}";
        const string password = "Scoped mutation password 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var user = session.Client;

        (await user.GetVideoByIdAsync(hiddenVideo.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenVideo.Id);
        (await user.GetImageByIdAsync(hiddenImage.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenImage.Id);
        (await user.GetAudioByIdAsync(hiddenAudio.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenAudio.Id);
        (await user.GetTextByIdAsync(hiddenText.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenText.Id);
        (await user.GetGalleryByIdAsync(hiddenGallery.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenGallery.Id);
        (await user.GetGroupByIdAsync(hiddenGroup.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hiddenGroup.Id);

        const string allowedVisibleTitle = "allowed scoped mutation";
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/videos/{visibleVideo.Id}", payload: new { title = allowedVisibleTitle }, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/videos/{deleteDeniedMergeSource.Id}", payload: new { title = "allowed write despite delete deny" }, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/videos/{disposableVisibleVideo.Id}", HttpStatusCode.NoContent, cancellationToken: TestContext.Current.CancellationToken);
        await owner.AssertResponseAsync($"/api/videos/{disposableVisibleVideo.Id}", HttpStatusCode.NotFound, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{visibleVideo.Id}/rescan", HttpStatusCode.BadRequest, cancellationToken: TestContext.Current.CancellationToken);

        await user.AssertResponseAsync(HttpMethod.Put, $"/api/videos/{hiddenVideo.Id}", HttpStatusCode.Forbidden, new { title = "forbidden" }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/images/{hiddenImage.Id}", HttpStatusCode.Forbidden, new { title = "forbidden" }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/audios/{hiddenAudio.Id}", HttpStatusCode.Forbidden, new { title = "forbidden" }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/texts/{hiddenText.Id}", HttpStatusCode.Forbidden, new { title = "forbidden" }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/galleries/{hiddenGallery.Id}", HttpStatusCode.Forbidden, new { title = "forbidden" }, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Put, $"/api/groups/{hiddenGroup.Id}", HttpStatusCode.Forbidden, new { name = "forbidden" }, TestContext.Current.CancellationToken);

        await user.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{hiddenVideo.Id}/cover/from-frame", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{hiddenVideo.Id}/generate-screenshot", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{hiddenVideo.Id}/assign-file", HttpStatusCode.Forbidden, new VideoAssignFileDto(-1), TestContext.Current.CancellationToken);
        foreach (var path in new[]
                 {
                     $"/api/videos/{hiddenVideo.Id}/image",
                     $"/api/audios/{hiddenAudio.Id}/image",
                     $"/api/texts/{hiddenText.Id}/image",
                     $"/api/groups/{hiddenGroup.Id}/image/front",
                     $"/api/groups/{hiddenGroup.Id}/image/back",
                     $"/api/galleries/{hiddenGallery.Id}/image",
                     $"/api/galleries/{hiddenGallery.Id}/image/back",
                 })
        {
            await user.AssertResponseAsync(HttpMethod.Delete, path, HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        }

        await AssertMixedBulkMutationForbiddenAsync(user, "/api/videos/bulk", visibleVideo.Id, hiddenVideo.Id);
        await AssertMixedBulkMutationForbiddenAsync(user, "/api/images/bulk", visibleImage.Id, hiddenImage.Id);
        await AssertMixedBulkMutationForbiddenAsync(user, "/api/audios/bulk", visibleAudio.Id, hiddenAudio.Id);
        await AssertMixedBulkMutationForbiddenAsync(user, "/api/texts/bulk", visibleText.Id, hiddenText.Id);
        await AssertMixedBulkMutationForbiddenAsync(user, "/api/galleries/bulk", visibleGallery.Id, hiddenGallery.Id);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/groups/bulk", HttpStatusCode.Forbidden, new { ids = new[] { visibleGroup.Id, hiddenGroup.Id }, description = "forbidden bulk" }, TestContext.Current.CancellationToken);

        await user.AssertResponseAsync(HttpMethod.Post, "/api/videos/destroy", HttpStatusCode.Forbidden, new BatchDeleteDto([visibleVideo.Id, hiddenVideo.Id]), TestContext.Current.CancellationToken);
        await AssertMixedBulkDeleteForbiddenAsync(user, "/api/images/bulk", visibleImage.Id, hiddenImage.Id);
        await AssertMixedBulkDeleteForbiddenAsync(user, "/api/audios/bulk", visibleAudio.Id, hiddenAudio.Id);
        await AssertMixedBulkDeleteForbiddenAsync(user, "/api/texts/bulk", visibleText.Id, hiddenText.Id);
        await AssertMixedBulkDeleteForbiddenAsync(user, "/api/galleries/bulk", visibleGallery.Id, hiddenGallery.Id);
        await AssertMixedBulkDeleteForbiddenAsync(user, "/api/groups/bulk", visibleGroup.Id, hiddenGroup.Id);

        await user.AssertResponseAsync(HttpMethod.Post, "/api/videos/merge", HttpStatusCode.Forbidden, new VideoMergeDto(visibleVideo.Id, [hiddenVideo.Id]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/videos/merge", HttpStatusCode.Forbidden, new VideoMergeDto(hiddenVideo.Id, [visibleVideo.Id]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/videos/merge", HttpStatusCode.Forbidden, new VideoMergeDto(visibleVideo.Id, [deleteDeniedMergeSource.Id]), TestContext.Current.CancellationToken);

        foreach (var (kind, id) in new[]
                 {
                     ("videos", hiddenVideo.Id), ("images", hiddenImage.Id),
                     ("audios", hiddenAudio.Id), ("texts", hiddenText.Id),
                     ("galleries", hiddenGallery.Id),
                 })
        {
            await user.AssertResponseAsync(HttpMethod.Post, $"/api/{kind}/{id}/rescan", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        }
        foreach (var path in new[]
                 {
                     "/api/jobs/scan", "/api/jobs/generate-thumbnails",
                     "/api/jobs/generate-video-phashes", "/api/jobs/generate-image-phashes",
                     "/api/jobs/clean", "/api/metadata/scan", "/api/metadata/generate",
                     "/api/metadata/clean", "/api/metadata/identify", "/api/metadata/sync-fingerprints",
                 })
        {
            await user.AssertResponseAsync(HttpMethod.Post, path, HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        }
        await user.AssertResponseAsync("/api/metadata/library-folders", cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync("/api/metadata/filesystem-policy", cancellationToken: TestContext.Current.CancellationToken);

        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/videos/{hiddenVideo.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/images/{hiddenImage.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/audios/{hiddenAudio.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/texts/{hiddenText.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/galleries/{hiddenGallery.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Delete, $"/api/groups/{hiddenGroup.Id}", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);

        var ownerVisibleVideo = await owner.GetVideoByIdAsync(visibleVideo.Id, TestContext.Current.CancellationToken);
        ownerVisibleVideo.Title.Should().Be(allowedVisibleTitle);
        ownerVisibleVideo.Organized.Should().BeFalse();
        (await owner.GetVideoByIdAsync(hiddenVideo.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenVideo.Title);
        (await owner.GetVideoByIdAsync(deleteDeniedMergeSource.Id, TestContext.Current.CancellationToken)).Title.Should().Be("allowed write despite delete deny");
        var ownerVisibleImage = await owner.GetImageByIdAsync(visibleImage.Id, TestContext.Current.CancellationToken);
        ownerVisibleImage.Title.Should().Be(visibleImage.Title);
        ownerVisibleImage.Organized.Should().BeFalse();
        (await owner.GetImageByIdAsync(hiddenImage.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenImage.Title);
        var ownerVisibleAudio = await owner.GetAudioByIdAsync(visibleAudio.Id, TestContext.Current.CancellationToken);
        ownerVisibleAudio.Title.Should().Be(visibleAudio.Title);
        ownerVisibleAudio.Organized.Should().BeFalse();
        (await owner.GetAudioByIdAsync(hiddenAudio.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenAudio.Title);
        var ownerVisibleText = await owner.GetTextByIdAsync(visibleText.Id, TestContext.Current.CancellationToken);
        ownerVisibleText.Title.Should().Be(visibleText.Title);
        ownerVisibleText.Organized.Should().BeFalse();
        (await owner.GetTextByIdAsync(hiddenText.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenText.Title);
        var ownerVisibleGallery = await owner.GetGalleryByIdAsync(visibleGallery.Id, TestContext.Current.CancellationToken);
        ownerVisibleGallery.Title.Should().Be(visibleGallery.Title);
        ownerVisibleGallery.Organized.Should().BeFalse();
        (await owner.GetGalleryByIdAsync(hiddenGallery.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenGallery.Title);
        var ownerVisibleGroup = await owner.GetGroupByIdAsync(visibleGroup.Id, TestContext.Current.CancellationToken);
        ownerVisibleGroup.Name.Should().Be(visibleGroup.Name);
        ownerVisibleGroup.Description.Should().BeNull();
        (await owner.GetGroupByIdAsync(hiddenGroup.Id, TestContext.Current.CancellationToken)).Name.Should().Be(hiddenGroup.Name);
    }

    private static Task AssertMixedBulkMutationForbiddenAsync(CoveClient user, string path, int visibleId, int hiddenId)
        => user.AssertResponseAsync(HttpMethod.Post, path, HttpStatusCode.Forbidden,
            new { ids = new[] { visibleId, hiddenId }, organized = true });

    private static Task AssertMixedBulkDeleteForbiddenAsync(CoveClient user, string path, int visibleId, int hiddenId)
        => user.AssertResponseAsync(HttpMethod.Delete, path, HttpStatusCode.Forbidden,
            new BatchDeleteDto([visibleId, hiddenId]));
}
