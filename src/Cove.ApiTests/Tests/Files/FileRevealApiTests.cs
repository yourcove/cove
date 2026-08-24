using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Files;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FileRevealApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/files/folders/{id:int}/reveal")]
    [CoversEndpoint("POST", "/api/files/{id:int}/reveal")]
    public async Task GivenFixturePaths_WhenFilesAndFoldersAreRevealed_ThenAuthorizationExistenceAndTargetsAreExact()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var revealDirectoryName = $"reveal target {suffix}";
        var folderPath = AsTestFileSystem().CreateLibraryDirectory(revealDirectoryName);
        var filePath = AsTestFileSystem().CreateLibraryNestedFile(
            Path.Combine(revealDirectoryName, $"file target {suffix}.txt"),
            Encoding.UTF8.GetBytes("file reveal fixture"));
        var video = await AsUser().CreateVideoFromFileAsync(filePath, TestContext.Current.CancellationToken);
        var file = video.Files.Should().ContainSingle().Which;
        var folderId = await AsDbUser().GetFileParentFolderIdAsync(file.Id, TestContext.Current.CancellationToken);

        var missingDirectoryName = $"missing-reveal-{suffix}";
        var missingDirectory = AsTestFileSystem().CreateLibraryDirectory(missingDirectoryName);
        var missingFilePath = AsTestFileSystem().CreateLibraryNestedFile(
            Path.Combine(missingDirectoryName, "missing.txt"),
            Encoding.UTF8.GetBytes("missing reveal fixture"));
        var missingVideo = await AsUser().CreateVideoFromFileAsync(missingFilePath, TestContext.Current.CancellationToken);
        var missingFile = missingVideo.Files.Should().ContainSingle().Which;
        var missingFolderId = await AsDbUser().GetFileParentFolderIdAsync(missingFile.Id, TestContext.Current.CancellationToken);
        AsTestFileSystem().DeleteLibraryFile(missingFilePath);
        Directory.Delete(missingDirectory);

        var viewerUsername = $"file-reveal-viewer-{suffix}";
        const string viewerPassword = "File reveal viewer password 123!";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            viewerPassword,
            DisplayName: "File Reveal Viewer",
            Roles: [BuiltinRoles.Viewer]), TestContext.Current.CancellationToken);
        using var viewerSession = await AsUser().CreateAuthSessionAsync(viewerUsername, viewerPassword, TestContext.Current.CancellationToken);
        var forbiddenFile = () => viewerSession.Client.RevealFileInManagerAsync(file.Id);
        var forbiddenFolder = () => viewerSession.Client.RevealFolderInManagerAsync(folderId);
        await forbiddenFile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await forbiddenFolder.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");

        var memberRole = (await AsUser().GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var readDeny = await AsUser().CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.File,
            file.Id.ToString(global::System.Globalization.CultureInfo.InvariantCulture),
            "deny",
            "read"), TestContext.Current.CancellationToken);
        try
        {
            var entityForbidden = () => AsUser(ApiTestUsers.Eva).RevealFileInManagerAsync(file.Id);
            await entityForbidden.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*returned 403 (Forbidden)*");
        }
        finally
        {
            await AsUser().DeleteEntityOverrideAsync(readDeny.Id, CancellationToken.None);
        }

        var missingFileId = () => AsUser(ApiTestUsers.Eva).RevealFileInManagerAsync(int.MaxValue);
        var missingFolderIdRequest = () => AsUser(ApiTestUsers.Eva).RevealFolderInManagerAsync(int.MaxValue);
        var missingPhysicalFile = () => AsUser(ApiTestUsers.Eva).RevealFileInManagerAsync(missingFile.Id);
        var missingPhysicalFolder = () => AsUser(ApiTestUsers.Eva).RevealFolderInManagerAsync(missingFolderId);
        await missingFileId.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await missingFolderIdRequest.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await missingPhysicalFile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await missingPhysicalFolder.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        AsFileManagerRecorder().ReadInvocations().Should().BeEmpty();

        await AsUser(ApiTestUsers.Eva).RevealFileInManagerAsync(file.Id, TestContext.Current.CancellationToken);
        (await AsFileManagerRecorder().WaitForInvocationsAsync(1, TestContext.Current.CancellationToken)).Should().Equal(
            new FileManagerInvocation("file", filePath));

        await AsUser(ApiTestUsers.Eva).RevealFolderInManagerAsync(folderId, TestContext.Current.CancellationToken);
        (await AsFileManagerRecorder().WaitForInvocationsAsync(2, TestContext.Current.CancellationToken)).Should().Equal(
            new FileManagerInvocation("file", filePath),
            new FileManagerInvocation("folder", folderPath));
    }
}
