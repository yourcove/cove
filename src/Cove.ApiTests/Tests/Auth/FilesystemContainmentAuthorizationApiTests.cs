using System.Net;
using System.Text;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Auth;

public sealed class FilesystemContainmentAuthorizationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenRestrictedMediaFiles_WhenFilesystemOperationsRun_ThenOwnersConstrainEveryPathAtomically()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var hiddenTag = await owner.CreateTagAsync($"Filesystem hidden tag {suffix}", TestContext.Current.CancellationToken);
        var visibleTag = await owner.CreateTagAsync($"Filesystem visible tag {suffix}", TestContext.Current.CancellationToken);
        var visible = await CreateFileBackedVideoAsync(
            owner,
            $"filesystem-visible-{suffix}.txt",
            "visible bytes",
            visibleTag.Id);
        var hidden = await CreateFileBackedVideoAsync(
            owner,
            $"filesystem-hidden-{suffix}.txt",
            "hidden bytes",
            hiddenTag.Id);
        var hiddenFolderId = await AsDbUser().GetFileParentFolderIdAsync(hidden.File.Id, TestContext.Current.CancellationToken);
        var destination = AsTestFileSystem().CreateLibraryDirectory($"filesystem-destination-{suffix}");

        var roleName = $"Filesystem containment {suffix}";
        var role = await owner.CreateRoleAsync(new CreateRoleRequest(
            roleName,
            "Manages files only when their media owners are allowed.",
            [Permissions.FilesRead, Permissions.FilesWrite, Permissions.FilesDelete, Permissions.VideosRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            role.Id,
            EntityKinds.Video,
            "deny",
            "tag",
            $"{{\"tagId\":{hiddenTag.Id}}}",
            "read"), TestContext.Current.CancellationToken);

        const string password = "Filesystem containment password 123!";
        var username = $"filesystem-containment-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
        using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);
        var user = session.Client;

        await user.AssertResponseAsync(HttpMethod.Post, $"/api/files/{hidden.File.Id}/reveal", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, new FileSetFingerprintsDto(hidden.File.Id, [new FingerprintEntryDto("md5", "hidden-change")]), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/files/move", HttpStatusCode.Forbidden, new MoveFilesDto([visible.File.Id, hidden.File.Id], destination), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/files/move", HttpStatusCode.Forbidden, new MoveFilesDto([], Path.Combine(destination, "unknown")), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, "/api/files/delete", HttpStatusCode.Forbidden, new DeleteFilesDto([visible.File.Id, hidden.File.Id], DeleteFromDisk: true), TestContext.Current.CancellationToken);
        await user.AssertResponseAsync($"/api/files/browse?path={Uri.EscapeDataString(AsTestFileSystem().LibraryPath)}", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await user.AssertResponseAsync(HttpMethod.Post, $"/api/files/folders/{hiddenFolderId}/reveal", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);

        AssertFileUnchanged(await owner.GetVideoByIdAsync(visible.Video.Id, TestContext.Current.CancellationToken), visible);
        AssertFileUnchanged(await owner.GetVideoByIdAsync(hidden.Video.Id, TestContext.Current.CancellationToken), hidden);
        AsTestFileSystem().LibraryFileExists(visible.Path).Should().BeTrue();
        AsTestFileSystem().LibraryFileExists(hidden.Path).Should().BeTrue();
        AsTestFileSystem().LibraryFileExists(Path.Combine(destination, visible.File.Basename)).Should().BeFalse();
        AsTestFileSystem().LibraryFileExists(Path.Combine(destination, hidden.File.Basename)).Should().BeFalse();
        (await AsDbUser().GetFileFingerprintsAsync(hidden.File.Id, TestContext.Current.CancellationToken)).Should().NotContainKey("md5");
        AsFileManagerRecorder().ReadInvocations().Should().NotContain(invocation =>
            string.Equals(invocation.TargetPath, hidden.Path, StringComparison.Ordinal)
            || string.Equals(invocation.TargetPath, Path.GetDirectoryName(hidden.Path), StringComparison.Ordinal));

        await user.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.OK, new FileSetFingerprintsDto(visible.File.Id, [new FingerprintEntryDto("md5", "visible-change")]), TestContext.Current.CancellationToken);
        (await AsDbUser().GetFileFingerprintsAsync(visible.File.Id, TestContext.Current.CancellationToken))["md5"].Should().Be("visible-change");

        var allowRoleName = $"Allow-only filesystem {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Exercises global filesystem denial for an allow-only content scope.",
            []), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id, EntityKinds.File, "allow", "all", "{}", "read"), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            allowRole.Id, EntityKinds.Video, "allow", "tag", $"{{\"tagId\":{visibleTag.Id}}}", "read"), TestContext.Current.CancellationToken);
        var allowUsername = $"allow-filesystem-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowUsername, password, Roles: [allowRoleName]), TestContext.Current.CancellationToken);
        using var allowSession = await owner.CreateAuthSessionAsync(allowUsername, password, TestContext.Current.CancellationToken);
        using (var allowHttp = allowSession.Client.CreateHttpClient())
        using (var allowReveal = await allowHttp.PostAsync($"/api/files/{visible.File.Id}/reveal", content: null, cancellationToken: TestContext.Current.CancellationToken))
        {
            allowReveal.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await allowReveal.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("unrestricted read access");
        }
        await allowSession.Client.AssertResponseAsync(HttpMethod.Post, $"/api/files/{hidden.File.Id}/reveal", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);
        await allowSession.Client.AssertResponseAsync($"/api/files/browse?path={Uri.EscapeDataString(AsTestFileSystem().LibraryPath)}", HttpStatusCode.Forbidden, TestContext.Current.CancellationToken);
        await allowSession.Client.AssertResponseAsync(HttpMethod.Post, $"/api/files/folders/{hiddenFolderId}/reveal", HttpStatusCode.Forbidden, cancellationToken: TestContext.Current.CancellationToken);

        await owner.AssertResponseAsync($"/api/files/browse?path={Uri.EscapeDataString(AsTestFileSystem().LibraryPath)}", cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GivenEveryFileOwnerKindAndOrphans_WhenFilesAreMutated_ThenBothFileAndOwnerAuthorizationMustAllow()
    {
        var owner = AsUser();
        var suffix = Guid.NewGuid().ToString("N");
        var video = await owner.CreateVideoAsync($"Filesystem owner video {suffix}", TestContext.Current.CancellationToken);
        var image = await owner.CreateImageAsync($"Filesystem owner image {suffix}", TestContext.Current.CancellationToken);
        var gallery = await owner.CreateGalleryAsync(new GalleryBuilder().WithTitle($"Filesystem owner gallery {suffix}").Build(), TestContext.Current.CancellationToken);
        var audio = await owner.CreateAudioAsync($"Filesystem owner audio {suffix}", TestContext.Current.CancellationToken);
        var text = await owner.CreateTextAsync($"Filesystem owner text {suffix}", TestContext.Current.CancellationToken);
        var fileRuleTag = await owner.CreateTagAsync($"Filesystem file-rule tag {suffix}", TestContext.Current.CancellationToken);
        await owner.UpdateAudioAsync(audio.Id, new { tagIds = new[] { fileRuleTag.Id } }, TestContext.Current.CancellationToken);
        await owner.UpdateTextAsync(text.Id, new { tagIds = new[] { fileRuleTag.Id } }, TestContext.Current.CancellationToken);
        var owners = new[]
        {
            (EntityKinds.Video, video.Id),
            (EntityKinds.Image, image.Id),
            (EntityKinds.Gallery, gallery.Id),
            (EntityKinds.Audio, audio.Id),
            (EntityKinds.Text, text.Id),
        };
        var fileIds = new Dictionary<string, int>();
        foreach (var (kind, ownerId) in owners)
        {
            var path = AsTestFileSystem().CreateLibraryFile(
                $"filesystem-owner-{kind}-{suffix}.bin",
                Encoding.UTF8.GetBytes(kind));
            fileIds[kind] = await AsDbUser().CreateOwnedFileAsync(kind, ownerId, path, TestContext.Current.CancellationToken);
        }
        var orphanPath = AsTestFileSystem().CreateLibraryFile(
            $"filesystem-orphan-{suffix}.bin",
            Encoding.UTF8.GetBytes("orphan"));
        var orphanId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, ownerId: null, path: orphanPath, cancellationToken: TestContext.Current.CancellationToken);
        var controlAudio = await owner.CreateAudioAsync($"Filesystem control audio {suffix}", TestContext.Current.CancellationToken);
        var controlText = await owner.CreateTextAsync($"Filesystem control text {suffix}", TestContext.Current.CancellationToken);
        var fileRuleStudio = await owner.CreateStudioAsync($"Filesystem file-rule studio {suffix}", TestContext.Current.CancellationToken);
        await owner.UpdateAudioAsync(controlAudio.Id, new { studioId = fileRuleStudio.Id }, TestContext.Current.CancellationToken);
        await owner.UpdateTextAsync(controlText.Id, new { studioId = fileRuleStudio.Id }, TestContext.Current.CancellationToken);
        var controlAudioPath = AsTestFileSystem().CreateLibraryFile(
            $"filesystem-control-audio-{suffix}.bin",
            Encoding.UTF8.GetBytes("control audio"));
        var controlTextPath = AsTestFileSystem().CreateLibraryFile(
            $"filesystem-control-text-{suffix}.bin",
            Encoding.UTF8.GetBytes("control text"));
        var controlAudioFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Audio, controlAudio.Id, controlAudioPath, TestContext.Current.CancellationToken);
        var controlTextFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Text, controlText.Id, controlTextPath, TestContext.Current.CancellationToken);

        const string password = "Filesystem owner kinds password 123!";
        for (var deniedIndex = 0; deniedIndex < owners.Length; deniedIndex++)
        {
            var (deniedKind, deniedOwnerId) = owners[deniedIndex];
            var roleName = $"Owner kind {deniedKind} denied {suffix}";
            var role = await owner.CreateRoleAsync(new CreateRoleRequest(
                roleName,
                "Denies exactly one media owner while permitting other owner kinds.",
                [
                    Permissions.FilesRead,
                    Permissions.FilesWrite,
                    Permissions.VideosRead, Permissions.ImagesRead, Permissions.GalleriesRead,
                    Permissions.AudiosRead, Permissions.TextsRead,
                ]), TestContext.Current.CancellationToken);
            await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
                role.Id, deniedKind, deniedOwnerId.ToString(), "deny", "read"), TestContext.Current.CancellationToken);
            await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
                role.Id, EntityKinds.File, fileIds[deniedKind].ToString(), "allow", "write"), TestContext.Current.CancellationToken);
            var username = $"filesystem-owner-{deniedKind}-{suffix}";
            await owner.CreateUserAsync(new CreateUserRequest(username, password, Roles: [roleName]), TestContext.Current.CancellationToken);
            using var session = await owner.CreateAuthSessionAsync(username, password, TestContext.Current.CancellationToken);

            foreach (var (candidateKind, _) in owners)
            {
                await session.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", candidateKind == deniedKind ? HttpStatusCode.Forbidden : HttpStatusCode.OK, FingerprintRequest(fileIds[candidateKind], $"mapping-{deniedKind}-{candidateKind}"), TestContext.Current.CancellationToken);
            }
            await session.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(orphanId, $"orphan-{deniedKind}"), TestContext.Current.CancellationToken);
        }

        var allowRoleName = $"Owner allowed file denied {suffix}";
        var allowRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            allowRoleName,
            "Can read the owner but is denied its file.",
            [Permissions.FilesRead, Permissions.FilesWrite, Permissions.VideosRead]), TestContext.Current.CancellationToken);
        await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            allowRole.Id, EntityKinds.File, fileIds[EntityKinds.Video].ToString(), "deny", "write"), TestContext.Current.CancellationToken);
        var allowUsername = $"filesystem-file-denied-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(allowUsername, password, Roles: [allowRoleName]), TestContext.Current.CancellationToken);
        using var allowSession = await owner.CreateAuthSessionAsync(allowUsername, password, TestContext.Current.CancellationToken);
        await allowSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(fileIds[EntityKinds.Video], "file-denied"), TestContext.Current.CancellationToken);

        var fileRuleRoleName = $"Audio text file rules {suffix}";
        var fileRuleRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            fileRuleRoleName,
            "Exercises File tag rules inherited from audio and text owners.",
            [Permissions.FilesRead, Permissions.FilesWrite, Permissions.AudiosRead, Permissions.TextsRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            fileRuleRole.Id,
            EntityKinds.File,
            "deny",
            "tag",
            $"{{\"tagId\":{fileRuleTag.Id}}}",
            "write"), TestContext.Current.CancellationToken);
        var fileRuleUsername = $"filesystem-file-rules-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(fileRuleUsername, password, Roles: [fileRuleRoleName]), TestContext.Current.CancellationToken);
        using var fileRuleSession = await owner.CreateAuthSessionAsync(fileRuleUsername, password, TestContext.Current.CancellationToken);
        await fileRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(fileIds[EntityKinds.Audio], "tagged-audio"), TestContext.Current.CancellationToken);
        await fileRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(fileIds[EntityKinds.Text], "tagged-text"), TestContext.Current.CancellationToken);
        await fileRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.OK, FingerprintRequest(controlAudioFileId, "untagged-audio"), TestContext.Current.CancellationToken);
        await fileRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.OK, FingerprintRequest(controlTextFileId, "untagged-text"), TestContext.Current.CancellationToken);

        var studioRuleRoleName = $"Audio text file studio rules {suffix}";
        var studioRuleRole = await owner.CreateRoleAsync(new CreateRoleRequest(
            studioRuleRoleName,
            "Exercises File studio rules inherited from audio and text owners.",
            [Permissions.FilesRead, Permissions.FilesWrite, Permissions.AudiosRead, Permissions.TextsRead]), TestContext.Current.CancellationToken);
        await owner.CreateContentRuleAsync(new CreateContentRuleRequest(
            studioRuleRole.Id,
            EntityKinds.File,
            "deny",
            "studio",
            $"{{\"studioId\":{fileRuleStudio.Id}}}",
            "write"), TestContext.Current.CancellationToken);
        var studioRuleUsername = $"filesystem-file-studio-rules-{suffix}";
        await owner.CreateUserAsync(new CreateUserRequest(studioRuleUsername, password, Roles: [studioRuleRoleName]), TestContext.Current.CancellationToken);
        using var studioRuleSession = await owner.CreateAuthSessionAsync(studioRuleUsername, password, TestContext.Current.CancellationToken);
        await studioRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(controlAudioFileId, "studio-audio"), TestContext.Current.CancellationToken);
        await studioRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.Forbidden, FingerprintRequest(controlTextFileId, "studio-text"), TestContext.Current.CancellationToken);
        await studioRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.OK, FingerprintRequest(fileIds[EntityKinds.Audio], "no-studio-audio"), TestContext.Current.CancellationToken);
        await studioRuleSession.Client.AssertResponseAsync(HttpMethod.Post, "/api/files/fingerprints", HttpStatusCode.OK, FingerprintRequest(fileIds[EntityKinds.Text], "no-studio-text"), TestContext.Current.CancellationToken);
    }

    private async Task<FixtureFile> CreateFileBackedVideoAsync(
        CoveClient client,
        string fileName,
        string contents,
        int? tagId = null)
    {
        var path = AsTestFileSystem().CreateLibraryFile(fileName, Encoding.UTF8.GetBytes(contents));
        var video = await client.CreateVideoFromFileAsync(path);
        if (tagId is not null)
        {
            video = await client.UpdateVideoAsync(video.Id, new { tagIds = new[] { tagId.Value } });
        }
        return new FixtureFile(video, video.Files.Should().ContainSingle().Which, path);
    }

    private static void AssertFileUnchanged(VideoDto video, FixtureFile expected)
    {
        var file = video.Files.Should().ContainSingle().Which;
        file.Id.Should().Be(expected.File.Id);
        file.Path.Should().Be(expected.Path);
    }

    private static FileSetFingerprintsDto FingerprintRequest(int fileId, string value)
        => new(fileId, [new FingerprintEntryDto("api-test", value)]);

    private sealed record FixtureFile(VideoDto Video, VideoFileDto File, string Path);
}
