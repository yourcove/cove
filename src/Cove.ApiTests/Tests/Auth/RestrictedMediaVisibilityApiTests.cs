using System.Globalization;
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
public sealed class RestrictedMediaVisibilityApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenRestrictedMediaRole_WhenMediaIsReadOrMutated_ThenHiddenEntitiesNeverLeakOrChange()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var group = await owner.CreateGroupAsync($"Restricted media group {suffix}");

        var visibleVideo = await owner.CreateVideoAsync($"Visible video {suffix}");
        var hiddenVideo = await owner.CreateVideoAsync($"Hidden video {suffix}");
        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible audio {suffix}").Build());
        var hiddenAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Hidden audio {suffix}").Build());
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible text {suffix}").Build());
        var hiddenText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Hidden text {suffix}").Build());
        var visibleImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Visible image {suffix}").Build());
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Hidden image {suffix}").Build());
        var visibleGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Visible gallery {suffix}").Build());
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Hidden gallery {suffix}").Build());
        var entityImage = ApiTestImages.OnePixelPng();
        await owner.UploadVideoImageAsync(visibleVideo, entityImage);
        await owner.UploadVideoImageAsync(hiddenVideo, entityImage);
        await owner.UploadAudioImageAsync(visibleAudio, entityImage);
        await owner.UploadAudioImageAsync(hiddenAudio, entityImage);
        await owner.UploadTextImageAsync(visibleText, entityImage);
        await owner.UploadTextImageAsync(hiddenText, entityImage);
        await owner.UploadGalleryImageAsync(visibleGallery, entityImage);
        await owner.UploadGalleryImageAsync(hiddenGallery, entityImage);
        await owner.CreateGalleryChapterAsync(hiddenGallery, new GalleryChapterCreateDto("Restricted chapter", 0));
        await owner.CreateImageDetectionAsync(hiddenImage, "restricted-detection");

        var media = new (string Kind, int VisibleId, int HiddenId)[]
        {
            (EntityKinds.Video, visibleVideo.Id, hiddenVideo.Id),
            (EntityKinds.Audio, visibleAudio.Id, hiddenAudio.Id),
            (EntityKinds.Text, visibleText.Id, hiddenText.Id),
            (EntityKinds.Image, visibleImage.Id, hiddenImage.Id),
            (EntityKinds.Gallery, visibleGallery.Id, hiddenGallery.Id),
        };

        var roleName = $"Restricted media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises entity-scoped media authorization.",
            [
                Permissions.VideosWrite, Permissions.VideosDelete,
                Permissions.AudiosWrite, Permissions.AudiosDelete,
                Permissions.TextsWrite, Permissions.TextsDelete,
                Permissions.ImagesWrite, Permissions.ImagesDelete,
                Permissions.GalleriesWrite, Permissions.GalleriesDelete,
                Permissions.GroupsRead, Permissions.StreamRead, Permissions.SegmentsRead,
            ]));

        foreach (var item in media)
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                item.Kind,
                "deny",
                "all",
                "{}",
                "all"));
            await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
                role.Id,
                item.Kind,
                item.VisibleId.ToString(CultureInfo.InvariantCulture),
                "allow",
                "all"));
        }

        await AddGroupItemAsync(owner, group.Id, 0, GroupItemKind.Video, EntityKinds.Video, visibleVideo.Id);
        await AddGroupItemAsync(owner, group.Id, 1, GroupItemKind.Video, EntityKinds.Video, hiddenVideo.Id);
        await AddGroupItemAsync(owner, group.Id, 2, GroupItemKind.Audio, EntityKinds.Audio, visibleAudio.Id);
        await AddGroupItemAsync(owner, group.Id, 3, GroupItemKind.Audio, EntityKinds.Audio, hiddenAudio.Id);
        await AddGroupItemAsync(owner, group.Id, 4, GroupItemKind.Text, EntityKinds.Text, visibleText.Id);
        await AddGroupItemAsync(owner, group.Id, 5, GroupItemKind.Text, EntityKinds.Text, hiddenText.Id);
        await AddGroupItemAsync(owner, group.Id, 6, GroupItemKind.Image, EntityKinds.Image, visibleImage.Id);
        await AddGroupItemAsync(owner, group.Id, 7, GroupItemKind.Image, EntityKinds.Image, hiddenImage.Id);
        await AddGroupItemAsync(owner, group.Id, 8, GroupItemKind.Gallery, EntityKinds.Gallery, visibleGallery.Id);
        await AddGroupItemAsync(owner, group.Id, 9, GroupItemKind.Gallery, EntityKinds.Gallery, hiddenGallery.Id);

        var username = $"restricted-media-{suffix}";
        const string password = "Restricted media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var restrictedSession = await owner.CreateAuthSessionAsync(username, password);
        var restricted = restrictedSession.Client;

        (await restricted.GetVideosAsync()).Select(item => item.Id).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        var audioPage = await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [visibleAudio.Id, hiddenAudio.Id],
        });
        audioPage.TotalCount.Should().Be(1);
        audioPage.Items.Select(item => item.Id).Should().Equal(visibleAudio.Id);
        (await restricted.GetTextsAsync()).Select(item => item.Id).Should().Contain(visibleText.Id).And.NotContain(hiddenText.Id);
        (await restricted.GetImagesAsync()).Select(item => item.Id).Should().Contain(visibleImage.Id).And.NotContain(hiddenImage.Id);
        (await restricted.GetGalleriesAsync()).Select(item => item.Id).Should().Contain(visibleGallery.Id).And.NotContain(hiddenGallery.Id);

        (await restricted.AggregateVideosAsync(new FilteredQueryRequest<VideoFilter> { Ids = [visibleVideo.Id, hiddenVideo.Id] })).Count.Should().Be(1);
        (await restricted.AggregateAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [visibleAudio.Id, hiddenAudio.Id] })).Count.Should().Be(1);
        (await restricted.AggregateTextsAsync(new FilteredQueryRequest<TextDocumentFilter> { Ids = [visibleText.Id, hiddenText.Id] })).Count.Should().Be(1);
        (await restricted.AggregateImagesAsync(new FilteredQueryRequest<ImageFilter> { Ids = [visibleImage.Id, hiddenImage.Id] })).Count.Should().Be(1);
        (await restricted.AggregateGalleriesAsync(new FilteredQueryRequest<GalleryFilter> { Ids = [visibleGallery.Id, hiddenGallery.Id] })).Count.Should().Be(1);

        (await restricted.GetVideoByIdAsync(visibleVideo.Id)).Id.Should().Be(visibleVideo.Id);
        (await restricted.GetAudioByIdAsync(visibleAudio.Id)).Id.Should().Be(visibleAudio.Id);
        (await restricted.GetTextByIdAsync(visibleText.Id)).Id.Should().Be(visibleText.Id);
        (await restricted.GetImageByIdAsync(visibleImage.Id)).Id.Should().Be(visibleImage.Id);
        (await restricted.GetGalleryByIdAsync(visibleGallery.Id)).Id.Should().Be(visibleGallery.Id);
        (await restricted.GetVideoImageAsync(visibleVideo)).Content.Should().Equal(entityImage);
        (await restricted.GetAudioImageAsync(visibleAudio)).Content.Should().Equal(entityImage);
        (await restricted.GetTextImageAsync(visibleText)).Content.Should().Equal(entityImage);
        (await restricted.GetGalleryImageAsync(visibleGallery)).Content.Should().Equal(entityImage);

        using (var searchClient = restricted.CreateHttpClient())
        using (var searchResponse = await searchClient.GetAsync($"/api/search/global?q={suffix}"))
        {
            searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var searchBody = await searchResponse.Content.ReadAsStringAsync();
            searchBody.Should().Contain("Visible video").And.Contain("Visible audio").And.Contain("Visible text").And.Contain("Visible image").And.Contain("Visible gallery");
            searchBody.Should().NotContain("Hidden video").And.NotContain("Hidden audio").And.NotContain("Hidden text").And.NotContain("Hidden image").And.NotContain("Hidden gallery");
        }

        await AssertNotFoundAsync(() => restricted.GetVideoByIdAsync(hiddenVideo.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(hiddenAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(hiddenText.Id));
        await AssertNotFoundAsync(() => restricted.GetImageByIdAsync(hiddenImage.Id));
        await AssertNotFoundAsync(() => restricted.GetGalleryByIdAsync(hiddenGallery.Id));
        await AssertNotFoundAsync(() => restricted.GetVideoImageAsync(hiddenVideo));
        await AssertNotFoundAsync(() => restricted.GetAudioImageAsync(hiddenAudio));
        await AssertNotFoundAsync(() => restricted.GetTextImageAsync(hiddenText));
        await AssertNotFoundAsync(() => restricted.GetGalleryImageAsync(hiddenGallery));
        await AssertNotFoundAsync(() => restricted.GetGalleryCoverAsync(hiddenGallery));
        await AssertNotFoundAsync(() => restricted.GetGalleryChaptersAsync(hiddenGallery));
        await AssertNotFoundAsync(() => restricted.GetImageDetectionsAsync(hiddenImage));
        await AssertNotFoundAsync(() => restricted.GetVideoHistoryAsync(hiddenVideo));
        await AssertNotFoundAsync(() => restricted.GetAudioHistoryAsync(hiddenAudio));
        await AssertNotFoundAsync(() => restricted.GetTextHistoryAsync(hiddenText));
        await AssertNotFoundAsync(() => restricted.GetImageHistoryAsync(hiddenImage));

        var groupItems = await restricted.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25);
        groupItems.TotalCount.Should().Be(5);
        groupItems.Items.Select(item => (item.HostType, item.HostId)).Should().BeEquivalentTo(
            media.Select(item => (item.Kind, item.VisibleId)));
        var manifest = await restricted.GetGroupPlaybackManifestAsync(group.Id);
        manifest.Items.Select(item => (item.HostType, item.HostId)).Should().Equal(
            (EntityKinds.Video, visibleVideo.Id),
            (EntityKinds.Audio, visibleAudio.Id),
            (EntityKinds.Text, visibleText.Id),
            (EntityKinds.Image, visibleImage.Id));

        foreach (var item in media)
        {
            var resource = ResourceName(item.Kind);
            (await restricted.SendStatusAsync(HttpMethod.Put, $"/api/{resource}/{item.HiddenId}", new { title = $"Leaked mutation {suffix}" }))
                .Should().Be(HttpStatusCode.Forbidden);
            (await restricted.SendStatusAsync(HttpMethod.Delete, $"/api/{resource}/{item.HiddenId}"))
                .Should().Be(HttpStatusCode.Forbidden);
        }

        (await owner.GetVideoByIdAsync(hiddenVideo.Id)).Title.Should().Be(hiddenVideo.Title);
        (await owner.GetAudioByIdAsync(hiddenAudio.Id)).Title.Should().Be(hiddenAudio.Title);
        (await owner.GetTextByIdAsync(hiddenText.Id)).Title.Should().Be(hiddenText.Title);
        (await owner.GetImageByIdAsync(hiddenImage.Id)).Title.Should().Be(hiddenImage.Title);
        (await owner.GetGalleryByIdAsync(hiddenGallery.Id)).Title.Should().Be(hiddenGallery.Title);

        using var anonymous = new HttpClient { BaseAddress = owner.BaseAddress };
        using var anonymousExisting = await anonymous.GetAsync($"/api/videos/{hiddenVideo.Id}");
        using var anonymousMissing = await anonymous.GetAsync("/api/videos/2147483647");
        anonymousExisting.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymousMissing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noPermissionUsername = $"restricted-media-no-permission-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(noPermissionUsername, password, Roles: []));
        using var noPermissionSession = await owner.CreateAuthSessionAsync(noPermissionUsername, password);
        (await noPermissionSession.Client.SendStatusAsync(HttpMethod.Get, $"/api/videos/{hiddenVideo.Id}"))
            .Should().Be(HttpStatusCode.Forbidden);
        (await noPermissionSession.Client.SendStatusAsync(HttpMethod.Get, "/api/videos/2147483647"))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GivenTagScopedDenyRules_WhenMediaIsRead_ThenTaggedMediaIsHiddenAcrossKinds()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var restrictedTag = await owner.CreateTagAsync($"Restricted media tag {suffix}");

        var visibleVideo = await owner.CreateVideoAsync($"Visible tag-scope video {suffix}");
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Hidden tag-scope video {suffix}").WithTags([restrictedTag]).Build());
        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible tag-scope audio {suffix}").Build());
        var hiddenAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Hidden tag-scope audio {suffix}").WithTag(restrictedTag).Build());
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible tag-scope text {suffix}").Build());
        var hiddenText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Hidden tag-scope text {suffix}").WithTag(restrictedTag).Build());
        var visibleImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Visible tag-scope image {suffix}").Build());
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Hidden tag-scope image {suffix}").WithTag(restrictedTag).Build());
        var visibleGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Visible tag-scope gallery {suffix}").Build());
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Hidden tag-scope gallery {suffix}").WithTag(restrictedTag).Build());

        var roleName = $"Tag-scoped media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises tag-scoped media authorization.",
            [Permissions.VideosRead, Permissions.AudiosRead, Permissions.TextsRead, Permissions.ImagesRead, Permissions.GalleriesRead]));

        foreach (var entityKind in new[] { EntityKinds.Video, EntityKinds.Audio, EntityKinds.Text, EntityKinds.Image, EntityKinds.Gallery })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "tag",
                $"{{\"tagId\":{restrictedTag.Id}}}",
                "read"));
        }

        var username = $"tag-scoped-media-{suffix}";
        const string password = "Tag-scoped media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.GetVideosAsync()).Select(item => item.Id).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [visibleAudio.Id, hiddenAudio.Id] }))
            .Items.Select(item => item.Id).Should().Contain(visibleAudio.Id).And.NotContain(hiddenAudio.Id);
        (await restricted.GetTextsAsync()).Select(item => item.Id).Should().Contain(visibleText.Id).And.NotContain(hiddenText.Id);
        (await restricted.GetImagesAsync()).Select(item => item.Id).Should().Contain(visibleImage.Id).And.NotContain(hiddenImage.Id);
        (await restricted.GetGalleriesAsync()).Select(item => item.Id).Should().Contain(visibleGallery.Id).And.NotContain(hiddenGallery.Id);

        await AssertNotFoundAsync(() => restricted.GetVideoByIdAsync(hiddenVideo.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(hiddenAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(hiddenText.Id));
        await AssertNotFoundAsync(() => restricted.GetImageByIdAsync(hiddenImage.Id));
        await AssertNotFoundAsync(() => restricted.GetGalleryByIdAsync(hiddenGallery.Id));
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/videos/{hiddenVideo.Id}/history")).Should().Be(HttpStatusCode.NotFound);
        (await restricted.SendStatusAsync(HttpMethod.Get, "/api/videos/2147483647/history")).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenNoDirectReadPermissions_WhenTagScopedAllowRulesExist_ThenOnlyMatchingMediaIsReadable()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var allowedTag = await owner.CreateTagAsync($"Allowed media tag {suffix}");

        var allowedVideo = await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Allowed video {suffix}").WithTags([allowedTag]).Build());
        var deniedVideo = await owner.CreateVideoAsync($"Denied video {suffix}");
        var allowedAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Allowed audio {suffix}").WithTag(allowedTag).Build());
        var deniedAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Denied audio {suffix}").Build());
        var allowedText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Allowed text {suffix}").WithTag(allowedTag).Build());
        var deniedText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Denied text {suffix}").Build());
        var allowedImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Allowed image {suffix}").WithTag(allowedTag).Build());
        var deniedImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Denied image {suffix}").Build());
        var allowedGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Allowed gallery {suffix}").WithTag(allowedTag).Build());
        var deniedGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Denied gallery {suffix}").Build());

        var roleName = $"Allow-scoped media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises scoped read grants without direct read permissions.",
            []));
        foreach (var entityKind in new[] { EntityKinds.Video, EntityKinds.Audio, EntityKinds.Text, EntityKinds.Image, EntityKinds.Gallery })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "allow",
                "tag",
                $"{{\"tagId\":{allowedTag.Id}}}",
                "read"));
        }

        var username = $"allow-scoped-media-{suffix}";
        const string password = "Allow-scoped media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.GetVideosAsync()).Select(item => item.Id).Should().Contain(allowedVideo.Id).And.NotContain(deniedVideo.Id);
        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [allowedAudio.Id, deniedAudio.Id] }))
            .Items.Select(item => item.Id).Should().Contain(allowedAudio.Id).And.NotContain(deniedAudio.Id);
        (await restricted.GetTextsAsync()).Select(item => item.Id).Should().Contain(allowedText.Id).And.NotContain(deniedText.Id);
        (await restricted.GetImagesAsync()).Select(item => item.Id).Should().Contain(allowedImage.Id).And.NotContain(deniedImage.Id);
        (await restricted.GetGalleriesAsync()).Select(item => item.Id).Should().Contain(allowedGallery.Id).And.NotContain(deniedGallery.Id);

        (await restricted.GetVideoByIdAsync(allowedVideo.Id)).Id.Should().Be(allowedVideo.Id);
        (await restricted.GetAudioByIdAsync(allowedAudio.Id)).Id.Should().Be(allowedAudio.Id);
        (await restricted.GetTextByIdAsync(allowedText.Id)).Id.Should().Be(allowedText.Id);
        (await restricted.GetImageByIdAsync(allowedImage.Id)).Id.Should().Be(allowedImage.Id);
        (await restricted.GetGalleryByIdAsync(allowedGallery.Id)).Id.Should().Be(allowedGallery.Id);

        await AssertNotFoundAsync(() => restricted.GetVideoByIdAsync(deniedVideo.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(deniedAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(deniedText.Id));
        await AssertNotFoundAsync(() => restricted.GetImageByIdAsync(deniedImage.Id));
        await AssertNotFoundAsync(() => restricted.GetGalleryByIdAsync(deniedGallery.Id));
    }

    [Fact]
    public async Task GivenStudioAndAttributeRules_WhenAudioAndTextAreRead_ThenBothScopesAreEnforced()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var restrictedStudio = await owner.CreateStudioAsync($"Restricted media studio {suffix}");

        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible scoped audio {suffix}").Build());
        var studioAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Studio-scoped audio {suffix}").WithStudio(restrictedStudio).Build());
        var attributeAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Attribute-scoped audio {suffix}").AsOrganized().Build());
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible scoped text {suffix}").Build());
        var studioText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Studio-scoped text {suffix}").WithStudio(restrictedStudio).Build());
        var attributeText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Attribute-scoped text {suffix}").AsOrganized().Build());

        var roleName = $"Studio and attribute media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises studio- and attribute-scoped audio and text authorization.",
            [Permissions.AudiosRead, Permissions.TextsRead]));

        foreach (var entityKind in new[] { EntityKinds.Audio, EntityKinds.Text })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "studio",
                $"{{\"studioId\":{restrictedStudio.Id}}}",
                "read"));
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "attribute",
                "{\"path\":\"organized\",\"equals\":true}",
                "read"));
        }

        var username = $"studio-attribute-media-{suffix}";
        const string password = "Studio attribute media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [visibleAudio.Id, studioAudio.Id, attributeAudio.Id],
        })).Items.Select(item => item.Id).Should().Equal(visibleAudio.Id);
        (await restricted.FindTextsAsync(new FilteredQueryRequest<TextDocumentFilter>
        {
            Ids = [visibleText.Id, studioText.Id, attributeText.Id],
        })).Items.Select(item => item.Id).Should().Equal(visibleText.Id);

        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(studioAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(attributeAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(studioText.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(attributeText.Id));
    }

    [Fact]
    public async Task GivenRestrictedFileBackedMedia_WhenContentIsDelivered_ThenOnlyAllowedBytesAreReturned()
    {
        var owner = AsUser();
        var ffmpegCapabilities = await owner.GetFfmpegCapabilitiesAsync();
        ffmpegCapabilities.FfmpegFound.Should().BeTrue();
        ffmpegCapabilities.FfmpegPath.Should().NotBeNullOrWhiteSpace();
        var ffmpegPath = ffmpegCapabilities.FfmpegPath!;
        var suffix = Guid.NewGuid().ToString("N");
        var fileSystem = AsTestFileSystem();
        var visibleAudio = await owner.CreateAudioFromFileAsync(fileSystem.CreatePcmWaveFile($"visible-{suffix}.wav", sampleFrames: 80));
        var hiddenAudio = await owner.CreateAudioFromFileAsync(fileSystem.CreatePcmWaveFile($"hidden-{suffix}.wav", sampleFrames: 80));
        var visibleText = await owner.CreateTextFromFileAsync(fileSystem.CreateTextFile($"Visible content {suffix}"));
        var hiddenText = await owner.CreateTextFromFileAsync(fileSystem.CreateTextFile($"Hidden content {suffix}"));
        var visibleVideoPath = await fileSystem.CreateSyntheticVideoAsync(ffmpegPath, $"visible-delivery-{suffix}.mp4", 16, 16, 1, "blue");
        var hiddenVideoPath = await fileSystem.CreateSyntheticVideoAsync(ffmpegPath, $"hidden-delivery-{suffix}.mp4", 16, 16, 1, "red");
        var visibleVideo = await owner.CreateVideoFromFileAsync(visibleVideoPath);
        var hiddenVideo = await owner.CreateVideoFromFileAsync(hiddenVideoPath);

        var roleName = $"Restricted delivery role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises protected media delivery.",
            [Permissions.StreamRead]));
        foreach (var item in new (string Kind, int Id)[]
        {
            (EntityKinds.Audio, visibleAudio.Id),
            (EntityKinds.Text, visibleText.Id),
            (EntityKinds.Video, visibleVideo.Id),
        })
        {
            await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
                role.Id,
                item.Kind,
                item.Id.ToString(CultureInfo.InvariantCulture),
                "allow",
                "read"));
        }
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id,
            EntityKinds.Video,
            "deny",
            "all",
            "{}",
            "read"));

        var username = $"restricted-delivery-{suffix}";
        const string password = "Restricted delivery 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.GetAudioStreamAsync(visibleAudio.Id)).Content.Should().NotBeEmpty();
        (await restricted.GetTextContentAsync(visibleText.Id)).Content.Should().Contain(suffix);
        (await restricted.GetTextFileAsync(visibleText)).Content.Should().NotBeEmpty();
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{visibleVideo.Id}"))
            .Should().Be(HttpStatusCode.OK);

        await AssertNotFoundAsync(() => restricted.GetAudioStreamAsync(hiddenAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextContentAsync(hiddenText.Id));
        await AssertNotFoundAsync(() => restricted.GetTextFileAsync(hiddenText));
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{hiddenVideo.Id}"))
            .Should().Be(HttpStatusCode.NotFound);
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{hiddenVideo.Id}/hls/master.m3u8"))
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenMixedVisibleAndHiddenIds_WhenBulkMutationsAreAttempted_ThenRequestsAreAtomic()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var visible = await owner.CreateAudioAsync($"Visible bulk audio {suffix}");
        var hidden = await owner.CreateAudioAsync($"Hidden bulk audio {suffix}");
        var roleName = $"Restricted bulk role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises atomic authorization for mixed bulk selections.",
            [Permissions.AudiosWrite, Permissions.AudiosDelete]));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(role.Id, EntityKinds.Audio, "deny", "all", "{}", "all"));
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            role.Id,
            EntityKinds.Audio,
            visible.Id.ToString(CultureInfo.InvariantCulture),
            "allow",
            "all"));

        var username = $"restricted-bulk-{suffix}";
        const string password = "Restricted bulk 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.SendStatusAsync(HttpMethod.Post, "/api/audios/bulk", new BulkAudioUpdateDto
        {
            Ids = [visible.Id, hidden.Id],
            Details = $"Forbidden bulk mutation {suffix}",
        })).Should().Be(HttpStatusCode.Forbidden);
        (await restricted.SendStatusAsync(HttpMethod.Delete, "/api/audios/bulk", new BatchDeleteDto([visible.Id, hidden.Id])))
            .Should().Be(HttpStatusCode.Forbidden);

        (await owner.GetAudioByIdAsync(visible.Id)).Details.Should().BeNull();
        (await owner.GetAudioByIdAsync(hidden.Id)).Details.Should().BeNull();
        (await owner.GetAudioByIdAsync(visible.Id)).Id.Should().Be(visible.Id);
        (await owner.GetAudioByIdAsync(hidden.Id)).Id.Should().Be(hidden.Id);
    }

    [Fact]
    public async Task GivenConflictingRolesAndOverrides_WhenMediaIsRead_ThenDenyPrecedenceWins()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var deniedTag = await owner.CreateTagAsync($"Deny precedence tag {suffix}");
        var visible = await owner.CreateTextAsync($"Visible precedence text {suffix}");
        var deniedByRole = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Role-denied text {suffix}").WithTag(deniedTag).Build());
        var deniedByOverride = await owner.CreateTextAsync($"Override-denied text {suffix}");

        var allowRoleName = $"Precedence allow role {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Provides broad text read access.",
            []));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id,
            EntityKinds.Text,
            "allow",
            "all",
            "{}",
            "read"));
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            allowRole.Id,
            EntityKinds.Text,
            deniedByOverride.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"));

        var denyRoleName = $"Precedence deny role {suffix}";
        var denyRole = await owner.CreateRoleAsync(new CreateRoleRequest(denyRoleName, "Restricts tagged text across roles.", []));
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            denyRole.Id,
            EntityKinds.Text,
            "deny",
            "tag",
            $"{{\"tagId\":{deniedTag.Id}}}",
            "read"));

        var username = $"deny-precedence-{suffix}";
        const string password = "Deny precedence 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [allowRoleName, denyRoleName]));
        using var session = await owner.CreateAuthSessionAsync(username, password);
        var restricted = session.Client;

        (await restricted.GetTextsAsync()).Select(item => item.Id).Should()
            .Contain(visible.Id)
            .And.NotContain(deniedByRole.Id)
            .And.NotContain(deniedByOverride.Id);
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(deniedByRole.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(deniedByOverride.Id));
    }

    private static Task AddGroupItemAsync(
        CoveClient owner,
        int groupId,
        int orderIndex,
        GroupItemKind kind,
        string hostType,
        int hostId)
        => owner.CreateGroupItemAsync(groupId, new GroupItemCreateDto(
            orderIndex,
            kind,
            kind == GroupItemKind.Video ? hostId : null,
            hostType,
            hostId,
            null,
            null,
            null,
            null,
            null,
            null));

    private static async Task AssertNotFoundAsync(Func<Task> action)
        => await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

    private static string ResourceName(string entityKind) => entityKind switch
    {
        EntityKinds.Video => "videos",
        EntityKinds.Audio => "audios",
        EntityKinds.Text => "texts",
        EntityKinds.Image => "images",
        EntityKinds.Gallery => "galleries",
        _ => throw new ArgumentOutOfRangeException(nameof(entityKind), entityKind, null),
    };
}
