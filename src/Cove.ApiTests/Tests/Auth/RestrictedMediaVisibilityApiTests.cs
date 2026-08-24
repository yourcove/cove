using System.Globalization;
using System.Net;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

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
        var group = await owner.CreateGroupAsync($"Restricted media group {suffix}", TestContext.Current.CancellationToken);

        var visibleVideo = await owner.CreateVideoAsync($"Visible video {suffix}", TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoAsync($"Hidden video {suffix}", TestContext.Current.CancellationToken);
        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Hidden audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible text {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Hidden text {suffix}").Build(), TestContext.Current.CancellationToken);
        var visibleImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Visible image {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Hidden image {suffix}").Build(), TestContext.Current.CancellationToken);
        var visibleGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Visible gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Hidden gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var entityImage = ApiTestImages.OnePixelPng();
        await owner.UploadVideoImageAsync(visibleVideo, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadVideoImageAsync(hiddenVideo, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadAudioImageAsync(visibleAudio, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadAudioImageAsync(hiddenAudio, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadTextImageAsync(visibleText, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadTextImageAsync(hiddenText, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadGalleryImageAsync(visibleGallery, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.UploadGalleryImageAsync(hiddenGallery, entityImage, cancellationToken: TestContext.Current.CancellationToken);
        await owner.CreateGalleryChapterAsync(hiddenGallery, new GalleryChapterCreateDto("Restricted chapter", 0), TestContext.Current.CancellationToken);
        await owner.CreateImageDetectionAsync(hiddenImage, "restricted-detection", TestContext.Current.CancellationToken);

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
            ]), TestContext.Current.CancellationToken);

        foreach (var item in media)
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                item.Kind,
                "deny",
                "all",
                "{}",
                "all"), TestContext.Current.CancellationToken);
            await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
                role.Id,
                item.Kind,
                item.VisibleId.ToString(CultureInfo.InvariantCulture),
                "allow",
                "all"), TestContext.Current.CancellationToken);
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
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var restrictedSession = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = restrictedSession.Client;

        (await restricted.GetVideosAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        var audioPage = await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [visibleAudio.Id, hiddenAudio.Id],
        }, TestContext.Current.CancellationToken);
        audioPage.TotalCount.Should().Be(1);
        audioPage.Items.Select(item => item.Id).Should().Equal(visibleAudio.Id);
        (await restricted.GetTextsAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleText.Id).And.NotContain(hiddenText.Id);
        (await restricted.GetImagesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleImage.Id).And.NotContain(hiddenImage.Id);
        (await restricted.GetGalleriesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleGallery.Id).And.NotContain(hiddenGallery.Id);

        (await restricted.AggregateVideosAsync(new FilteredQueryRequest<VideoFilter> { Ids = [visibleVideo.Id, hiddenVideo.Id] }, TestContext.Current.CancellationToken)).Count.Should().Be(1);
        (await restricted.AggregateAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [visibleAudio.Id, hiddenAudio.Id] }, TestContext.Current.CancellationToken)).Count.Should().Be(1);
        (await restricted.AggregateTextsAsync(new FilteredQueryRequest<TextDocumentFilter> { Ids = [visibleText.Id, hiddenText.Id] }, TestContext.Current.CancellationToken)).Count.Should().Be(1);
        (await restricted.AggregateImagesAsync(new FilteredQueryRequest<ImageFilter> { Ids = [visibleImage.Id, hiddenImage.Id] }, TestContext.Current.CancellationToken)).Count.Should().Be(1);
        (await restricted.AggregateGalleriesAsync(new FilteredQueryRequest<GalleryFilter> { Ids = [visibleGallery.Id, hiddenGallery.Id] }, TestContext.Current.CancellationToken)).Count.Should().Be(1);

        (await restricted.GetVideoByIdAsync(visibleVideo.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visibleVideo.Id);
        (await restricted.GetAudioByIdAsync(visibleAudio.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visibleAudio.Id);
        (await restricted.GetTextByIdAsync(visibleText.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visibleText.Id);
        (await restricted.GetImageByIdAsync(visibleImage.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visibleImage.Id);
        (await restricted.GetGalleryByIdAsync(visibleGallery.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visibleGallery.Id);
        (await restricted.GetVideoImageAsync(visibleVideo, cancellationToken: TestContext.Current.CancellationToken)).Content.Should().Equal(entityImage);
        (await restricted.GetAudioImageAsync(visibleAudio, TestContext.Current.CancellationToken)).Content.Should().Equal(entityImage);
        (await restricted.GetTextImageAsync(visibleText, TestContext.Current.CancellationToken)).Content.Should().Equal(entityImage);
        (await restricted.GetGalleryImageAsync(visibleGallery, TestContext.Current.CancellationToken)).Content.Should().Equal(entityImage);

        using (var searchClient = restricted.CreateHttpClient())
        using (var searchResponse = await searchClient.GetAsync($"/api/search/global?q={suffix}", TestContext.Current.CancellationToken))
        {
            searchResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            var searchBody = await searchResponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
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

        var groupItems = await restricted.GetGroupItemsPageAsync(group.Id, page: 1, perPage: 25, cancellationToken: TestContext.Current.CancellationToken);
        groupItems.TotalCount.Should().Be(5);
        groupItems.Items.Select(item => (item.HostType, item.HostId)).Should().BeEquivalentTo(
            media.Select(item => (item.Kind, item.VisibleId)));
        var manifest = await restricted.GetGroupPlaybackManifestAsync(group.Id, TestContext.Current.CancellationToken);
        manifest.Items.Select(item => (item.HostType, item.HostId)).Should().Equal(
            (EntityKinds.Video, visibleVideo.Id),
            (EntityKinds.Audio, visibleAudio.Id),
            (EntityKinds.Text, visibleText.Id),
            (EntityKinds.Image, visibleImage.Id));

        foreach (var item in media)
        {
            var resource = ResourceName(item.Kind);
            (await restricted.SendStatusAsync(HttpMethod.Put, $"/api/{resource}/{item.HiddenId}", new { title = $"Leaked mutation {suffix}" }, TestContext.Current.CancellationToken))
                .Should().Be(HttpStatusCode.Forbidden);
            (await restricted.SendStatusAsync(HttpMethod.Delete, $"/api/{resource}/{item.HiddenId}", cancellationToken: TestContext.Current.CancellationToken))
                .Should().Be(HttpStatusCode.Forbidden);
        }

        (await owner.GetVideoByIdAsync(hiddenVideo.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenVideo.Title);
        (await owner.GetAudioByIdAsync(hiddenAudio.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenAudio.Title);
        (await owner.GetTextByIdAsync(hiddenText.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenText.Title);
        (await owner.GetImageByIdAsync(hiddenImage.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenImage.Title);
        (await owner.GetGalleryByIdAsync(hiddenGallery.Id, TestContext.Current.CancellationToken)).Title.Should().Be(hiddenGallery.Title);

        using var anonymous = new HttpClient { BaseAddress = owner.BaseAddress };
        using var anonymousExisting = await anonymous.GetAsync($"/api/videos/{hiddenVideo.Id}", TestContext.Current.CancellationToken);
        using var anonymousMissing = await anonymous.GetAsync("/api/videos/2147483647", TestContext.Current.CancellationToken);
        anonymousExisting.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        anonymousMissing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var noPermissionUsername = $"restricted-media-no-permission-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(noPermissionUsername, password, Roles: []), TestContext.Current.CancellationToken);
        using var noPermissionSession = await owner.CreateAuthSessionAsync(noPermissionUsername, password, TestContext.Current.CancellationToken);
        (await noPermissionSession.Client.SendStatusAsync(HttpMethod.Get, $"/api/videos/{hiddenVideo.Id}", cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.Forbidden);
        (await noPermissionSession.Client.SendStatusAsync(HttpMethod.Get, "/api/videos/2147483647", cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task GivenTagScopedDenyRules_WhenMediaIsRead_ThenTaggedMediaIsHiddenAcrossKinds()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var restrictedTag = await owner.CreateTagAsync($"Restricted media tag {suffix}", TestContext.Current.CancellationToken);

        var visibleVideo = await owner.CreateVideoAsync($"Visible tag-scope video {suffix}", TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Hidden tag-scope video {suffix}").WithTags([restrictedTag]).Build(), TestContext.Current.CancellationToken);
        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible tag-scope audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Hidden tag-scope audio {suffix}").WithTag(restrictedTag).Build(), TestContext.Current.CancellationToken);
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible tag-scope text {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Hidden tag-scope text {suffix}").WithTag(restrictedTag).Build(), TestContext.Current.CancellationToken);
        var visibleImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Visible tag-scope image {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Hidden tag-scope image {suffix}").WithTag(restrictedTag).Build(), TestContext.Current.CancellationToken);
        var visibleGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Visible tag-scope gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var hiddenGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Hidden tag-scope gallery {suffix}").WithTag(restrictedTag).Build(), TestContext.Current.CancellationToken);

        var roleName = $"Tag-scoped media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises tag-scoped media authorization.",
            [Permissions.VideosRead, Permissions.AudiosRead, Permissions.TextsRead, Permissions.ImagesRead, Permissions.GalleriesRead]), TestContext.Current.CancellationToken);

        foreach (var entityKind in new[] { EntityKinds.Video, EntityKinds.Audio, EntityKinds.Text, EntityKinds.Image, EntityKinds.Gallery })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "tag",
                $"{{\"tagId\":{restrictedTag.Id}}}",
                "read"), TestContext.Current.CancellationToken);
        }

        var username = $"tag-scoped-media-{suffix}";
        const string password = "Tag-scoped media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.GetVideosAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleVideo.Id).And.NotContain(hiddenVideo.Id);
        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [visibleAudio.Id, hiddenAudio.Id] }, TestContext.Current.CancellationToken))
            .Items.Select(item => item.Id).Should().Contain(visibleAudio.Id).And.NotContain(hiddenAudio.Id);
        (await restricted.GetTextsAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleText.Id).And.NotContain(hiddenText.Id);
        (await restricted.GetImagesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleImage.Id).And.NotContain(hiddenImage.Id);
        (await restricted.GetGalleriesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(visibleGallery.Id).And.NotContain(hiddenGallery.Id);

        await AssertNotFoundAsync(() => restricted.GetVideoByIdAsync(hiddenVideo.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(hiddenAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(hiddenText.Id));
        await AssertNotFoundAsync(() => restricted.GetImageByIdAsync(hiddenImage.Id));
        await AssertNotFoundAsync(() => restricted.GetGalleryByIdAsync(hiddenGallery.Id));
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/videos/{hiddenVideo.Id}/history", cancellationToken: TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.NotFound);
        (await restricted.SendStatusAsync(HttpMethod.Get, "/api/videos/2147483647/history", cancellationToken: TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenNoDirectReadPermissions_WhenTagScopedAllowRulesExist_ThenOnlyMatchingMediaIsReadable()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var allowedTag = await owner.CreateTagAsync($"Allowed media tag {suffix}", TestContext.Current.CancellationToken);

        var allowedVideo = await owner.CreateVideoAsync(new VideoBuilder().WithTitle($"Allowed video {suffix}").WithTags([allowedTag]).Build(), TestContext.Current.CancellationToken);
        var deniedVideo = await owner.CreateVideoAsync($"Denied video {suffix}", TestContext.Current.CancellationToken);
        var allowedAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Allowed audio {suffix}").WithTag(allowedTag).Build(), TestContext.Current.CancellationToken);
        var deniedAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Denied audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var allowedText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Allowed text {suffix}").WithTag(allowedTag).Build(), TestContext.Current.CancellationToken);
        var deniedText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Denied text {suffix}").Build(), TestContext.Current.CancellationToken);
        var allowedImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Allowed image {suffix}").WithTag(allowedTag).Build(), TestContext.Current.CancellationToken);
        var deniedImage = await owner.CreateImageAsync(new ImageBuilder().WithTitle($"Denied image {suffix}").Build(), TestContext.Current.CancellationToken);
        var allowedGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Allowed gallery {suffix}").WithTag(allowedTag).Build(), TestContext.Current.CancellationToken);
        var deniedGallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Denied gallery {suffix}").Build(), TestContext.Current.CancellationToken);

        var roleName = $"Allow-scoped media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises scoped read grants without direct read permissions.",
            []), TestContext.Current.CancellationToken);
        foreach (var entityKind in new[] { EntityKinds.Video, EntityKinds.Audio, EntityKinds.Text, EntityKinds.Image, EntityKinds.Gallery })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "allow",
                "tag",
                $"{{\"tagId\":{allowedTag.Id}}}",
                "read"), TestContext.Current.CancellationToken);
        }

        var username = $"allow-scoped-media-{suffix}";
        const string password = "Allow-scoped media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.GetVideosAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(allowedVideo.Id).And.NotContain(deniedVideo.Id);
        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter> { Ids = [allowedAudio.Id, deniedAudio.Id] }, TestContext.Current.CancellationToken))
            .Items.Select(item => item.Id).Should().Contain(allowedAudio.Id).And.NotContain(deniedAudio.Id);
        (await restricted.GetTextsAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(allowedText.Id).And.NotContain(deniedText.Id);
        (await restricted.GetImagesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(allowedImage.Id).And.NotContain(deniedImage.Id);
        (await restricted.GetGalleriesAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should().Contain(allowedGallery.Id).And.NotContain(deniedGallery.Id);

        (await restricted.GetVideoByIdAsync(allowedVideo.Id, TestContext.Current.CancellationToken)).Id.Should().Be(allowedVideo.Id);
        (await restricted.GetAudioByIdAsync(allowedAudio.Id, TestContext.Current.CancellationToken)).Id.Should().Be(allowedAudio.Id);
        (await restricted.GetTextByIdAsync(allowedText.Id, TestContext.Current.CancellationToken)).Id.Should().Be(allowedText.Id);
        (await restricted.GetImageByIdAsync(allowedImage.Id, TestContext.Current.CancellationToken)).Id.Should().Be(allowedImage.Id);
        (await restricted.GetGalleryByIdAsync(allowedGallery.Id, TestContext.Current.CancellationToken)).Id.Should().Be(allowedGallery.Id);

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
        var restrictedStudio = await owner.CreateStudioAsync($"Restricted media studio {suffix}", TestContext.Current.CancellationToken);

        var visibleAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Visible scoped audio {suffix}").Build(), TestContext.Current.CancellationToken);
        var studioAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Studio-scoped audio {suffix}").WithStudio(restrictedStudio).Build(), TestContext.Current.CancellationToken);
        var attributeAudio = await owner.CreateAudioAsync(new AudioBuilder().WithTitle($"Attribute-scoped audio {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);
        var visibleText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Visible scoped text {suffix}").Build(), TestContext.Current.CancellationToken);
        var studioText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Studio-scoped text {suffix}").WithStudio(restrictedStudio).Build(), TestContext.Current.CancellationToken);
        var attributeText = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Attribute-scoped text {suffix}").AsOrganized().Build(), TestContext.Current.CancellationToken);

        var roleName = $"Studio and attribute media role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises studio- and attribute-scoped audio and text authorization.",
            [Permissions.AudiosRead, Permissions.TextsRead]), TestContext.Current.CancellationToken);

        foreach (var entityKind in new[] { EntityKinds.Audio, EntityKinds.Text })
        {
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "studio",
                $"{{\"studioId\":{restrictedStudio.Id}}}",
                "read"), TestContext.Current.CancellationToken);
            await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
                role.Id,
                entityKind,
                "deny",
                "attribute",
                "{\"path\":\"organized\",\"equals\":true}",
                "read"), TestContext.Current.CancellationToken);
        }

        var username = $"studio-attribute-media-{suffix}";
        const string password = "Studio attribute media 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.FindAudiosAsync(new FilteredQueryRequest<AudioFilter>
        {
            Ids = [visibleAudio.Id, studioAudio.Id, attributeAudio.Id],
        }, TestContext.Current.CancellationToken)).Items.Select(item => item.Id).Should().Equal(visibleAudio.Id);
        (await restricted.FindTextsAsync(new FilteredQueryRequest<TextDocumentFilter>
        {
            Ids = [visibleText.Id, studioText.Id, attributeText.Id],
        }, TestContext.Current.CancellationToken)).Items.Select(item => item.Id).Should().Equal(visibleText.Id);

        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(studioAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetAudioByIdAsync(attributeAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(studioText.Id));
        await AssertNotFoundAsync(() => restricted.GetTextByIdAsync(attributeText.Id));
    }

    [Fact]
    public async Task GivenRestrictedFileBackedMedia_WhenContentIsDelivered_ThenOnlyAllowedBytesAreReturned()
    {
        var owner = AsUser();
        var ffmpegCapabilities = await owner.GetFfmpegCapabilitiesAsync(TestContext.Current.CancellationToken);
        ffmpegCapabilities.FfmpegFound.Should().BeTrue();
        ffmpegCapabilities.FfmpegPath.Should().NotBeNullOrWhiteSpace();
        var ffmpegPath = ffmpegCapabilities.FfmpegPath!;
        var suffix = Guid.NewGuid().ToString("N");
        var fileSystem = AsTestFileSystem();
        var visibleAudio = await owner.CreateAudioFromFileAsync(fileSystem.CreatePcmWaveFile($"visible-{suffix}.wav", sampleFrames: 80), TestContext.Current.CancellationToken);
        var hiddenAudio = await owner.CreateAudioFromFileAsync(fileSystem.CreatePcmWaveFile($"hidden-{suffix}.wav", sampleFrames: 80), TestContext.Current.CancellationToken);
        var visibleText = await owner.CreateTextFromFileAsync(fileSystem.CreateTextFile($"Visible content {suffix}"), TestContext.Current.CancellationToken);
        var hiddenText = await owner.CreateTextFromFileAsync(fileSystem.CreateTextFile($"Hidden content {suffix}"), TestContext.Current.CancellationToken);
        var visibleVideoPath = await fileSystem.CreateSyntheticVideoAsync(ffmpegPath, $"visible-delivery-{suffix}.mp4", 16, 16, 1, "blue", TestContext.Current.CancellationToken);
        var hiddenVideoPath = await fileSystem.CreateSyntheticVideoAsync(ffmpegPath, $"hidden-delivery-{suffix}.mp4", 16, 16, 1, "red", TestContext.Current.CancellationToken);
        var visibleVideo = await owner.CreateVideoFromFileAsync(visibleVideoPath, TestContext.Current.CancellationToken);
        var hiddenVideo = await owner.CreateVideoFromFileAsync(hiddenVideoPath, TestContext.Current.CancellationToken);

        var roleName = $"Restricted delivery role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises protected media delivery.",
            [Permissions.StreamRead]), TestContext.Current.CancellationToken);
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
                "read"), TestContext.Current.CancellationToken);
        }
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id,
            EntityKinds.Video,
            "deny",
            "all",
            "{}",
            "read"), TestContext.Current.CancellationToken);

        var username = $"restricted-delivery-{suffix}";
        const string password = "Restricted delivery 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.GetAudioStreamAsync(visibleAudio.Id, TestContext.Current.CancellationToken)).Content.Should().NotBeEmpty();
        (await restricted.GetTextContentAsync(visibleText.Id, TestContext.Current.CancellationToken)).Content.Should().Contain(suffix);
        (await restricted.GetTextFileAsync(visibleText, TestContext.Current.CancellationToken)).Content.Should().NotBeEmpty();
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{visibleVideo.Id}", cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.OK);

        await AssertNotFoundAsync(() => restricted.GetAudioStreamAsync(hiddenAudio.Id));
        await AssertNotFoundAsync(() => restricted.GetTextContentAsync(hiddenText.Id));
        await AssertNotFoundAsync(() => restricted.GetTextFileAsync(hiddenText));
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{hiddenVideo.Id}", cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.NotFound);
        (await restricted.SendStatusAsync(HttpMethod.Get, $"/api/stream/video/{hiddenVideo.Id}/hls/master.m3u8", cancellationToken: TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GivenMixedVisibleAndHiddenIds_WhenBulkMutationsAreAttempted_ThenRequestsAreAtomic()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var visible = await owner.CreateAudioAsync($"Visible bulk audio {suffix}", TestContext.Current.CancellationToken);
        var hidden = await owner.CreateAudioAsync($"Hidden bulk audio {suffix}", TestContext.Current.CancellationToken);
        var roleName = $"Restricted bulk role {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Exercises atomic authorization for mixed bulk selections.",
            [Permissions.AudiosWrite, Permissions.AudiosDelete]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(role.Id, EntityKinds.Audio, "deny", "all", "{}", "all"), TestContext.Current.CancellationToken);
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            role.Id,
            EntityKinds.Audio,
            visible.Id.ToString(CultureInfo.InvariantCulture),
            "allow",
            "all"), TestContext.Current.CancellationToken);

        var username = $"restricted-bulk-{suffix}";
        const string password = "Restricted bulk 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.SendStatusAsync(HttpMethod.Post, "/api/audios/bulk", new BulkAudioUpdateDto
        {
            Ids = [visible.Id, hidden.Id],
            Details = $"Forbidden bulk mutation {suffix}",
        }, TestContext.Current.CancellationToken)).Should().Be(HttpStatusCode.Forbidden);
        (await restricted.SendStatusAsync(HttpMethod.Delete, "/api/audios/bulk", new BatchDeleteDto([visible.Id, hidden.Id]), TestContext.Current.CancellationToken))
            .Should().Be(HttpStatusCode.Forbidden);

        (await owner.GetAudioByIdAsync(visible.Id, TestContext.Current.CancellationToken)).Details.Should().BeNull();
        (await owner.GetAudioByIdAsync(hidden.Id, TestContext.Current.CancellationToken)).Details.Should().BeNull();
        (await owner.GetAudioByIdAsync(visible.Id, TestContext.Current.CancellationToken)).Id.Should().Be(visible.Id);
        (await owner.GetAudioByIdAsync(hidden.Id, TestContext.Current.CancellationToken)).Id.Should().Be(hidden.Id);
    }

    [Fact]
    public async Task GivenConflictingRolesAndOverrides_WhenMediaIsRead_ThenDenyPrecedenceWins()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var deniedTag = await owner.CreateTagAsync($"Deny precedence tag {suffix}", TestContext.Current.CancellationToken);
        var visible = await owner.CreateTextAsync($"Visible precedence text {suffix}", TestContext.Current.CancellationToken);
        var deniedByRole = await owner.CreateTextAsync(new TextDocumentBuilder().WithTitle($"Role-denied text {suffix}").WithTag(deniedTag).Build(), TestContext.Current.CancellationToken);
        var deniedByOverride = await owner.CreateTextAsync($"Override-denied text {suffix}", TestContext.Current.CancellationToken);

        var allowRoleName = $"Precedence allow role {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Provides broad text read access.",
            []), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id,
            EntityKinds.Text,
            "allow",
            "all",
            "{}",
            "read"), TestContext.Current.CancellationToken);
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            allowRole.Id,
            EntityKinds.Text,
            deniedByOverride.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "read"), TestContext.Current.CancellationToken);

        var denyRoleName = $"Precedence deny role {suffix}";
        var denyRole = await owner.CreateRoleAsync(new CreateRoleRequest(denyRoleName, "Restricts tagged text across roles.", []), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            denyRole.Id,
            EntityKinds.Text,
            "deny",
            "tag",
            $"{{\"tagId\":{deniedTag.Id}}}",
            "read"), TestContext.Current.CancellationToken);

        var username = $"deny-precedence-{suffix}";
        const string password = "Deny precedence 123!";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [allowRoleName, denyRoleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var restricted = session.Client;

        (await restricted.GetTextsAsync(TestContext.Current.CancellationToken)).Select(item => item.Id).Should()
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
