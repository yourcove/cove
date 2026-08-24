using System.Globalization;
using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;

namespace Cove.ApiTests.Tests.Files;

[Collection(ApiTestLane2Collection.Name)]
public sealed class FileOpsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/files/move")]
    [CoversEndpoint("POST", "/api/files/fingerprints")]
    public async Task GivenFixtureFiles_WhenMemberMovesAndUpdatesFingerprints_ThenPathsPermissionsAndControlsAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var fileSystem = AsTestFileSystem();
        var suffix = Guid.NewGuid().ToString("N");
        var destinationName = $"file-ops-destination-{suffix}";
        var destination = fileSystem.CreateLibraryDirectory(destinationName);
        var moved = await CreateFileBackedVideoAsync(owner, $"move-{suffix}.txt", "move source");
        var missing = await CreateFileBackedVideoAsync(owner, $"missing-{suffix}.txt", "missing source");
        var collision = await CreateFileBackedVideoAsync(owner, $"collision-{suffix}.txt", "collision source");
        var control = await CreateFileBackedVideoAsync(owner, $"control-{suffix}.txt", "control source");
        var collisionBytes = Encoding.UTF8.GetBytes("destination collision");
        var collisionDestination = fileSystem.CreateLibraryNestedFile(
            Path.Combine(destinationName, collision.File.Basename),
            collisionBytes);

        var missingDestination = Path.Combine(destination, "not-created");
        Func<Task> invalidDestination = () => member.MoveFilesAsync(
            new MoveFilesDto([moved.File.Id], missingDestination));
        await invalidDestination.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        AssertSingleFile(await owner.GetVideoByIdAsync(moved.Video.Id, TestContext.Current.CancellationToken), moved, moved.Path);
        fileSystem.LibraryFileExists(moved.Path).Should().BeTrue();

        fileSystem.DeleteLibraryFile(missing.Path);
        var missingMove = await member.MoveFilesAsync(new MoveFilesDto([missing.File.Id], destination), TestContext.Current.CancellationToken);
        missingMove.Moved.Should().Be(0);
        missingMove.Total.Should().Be(1);
        AssertSingleFile(await owner.GetVideoByIdAsync(missing.Video.Id, TestContext.Current.CancellationToken), missing, missing.Path);
        fileSystem.LibraryFileExists(missing.Path).Should().BeFalse();

        var collisionMove = await member.MoveFilesAsync(new MoveFilesDto([collision.File.Id], destination), TestContext.Current.CancellationToken);
        collisionMove.Moved.Should().Be(0);
        collisionMove.Total.Should().Be(1);
        AssertSingleFile(await owner.GetVideoByIdAsync(collision.Video.Id, TestContext.Current.CancellationToken), collision, collision.Path);
        fileSystem.LibraryFileExists(collision.Path).Should().BeTrue();
        File.ReadAllBytes(collisionDestination).Should().Equal(collisionBytes);

        var movedResult = await member.MoveFilesAsync(new MoveFilesDto([moved.File.Id, moved.File.Id, int.MaxValue], destination), TestContext.Current.CancellationToken);
        movedResult.Moved.Should().Be(1);
        movedResult.Total.Should().Be(1);
        var movedPath = Path.Combine(destination, moved.File.Basename);
        AssertSingleFile(await owner.GetVideoByIdAsync(moved.Video.Id, TestContext.Current.CancellationToken), moved, movedPath);
        fileSystem.LibraryFileExists(moved.Path).Should().BeFalse();
        fileSystem.LibraryFileExists(movedPath).Should().BeTrue();
        File.ReadAllBytes(movedPath).Should().Equal(Encoding.UTF8.GetBytes("move source"));
        AssertSingleFile(await owner.GetVideoByIdAsync(control.Video.Id, TestContext.Current.CancellationToken), control, control.Path);
        fileSystem.LibraryFileExists(control.Path).Should().BeTrue();

        var added = await member.SetFileFingerprintsAsync(new FileSetFingerprintsDto(
            moved.File.Id,
            [new FingerprintEntryDto("md5", "initial-md5"), new FingerprintEntryDto("oshash", "preserved-oshash")]), TestContext.Current.CancellationToken);
        added.Updated.Should().Be(2);
        var updated = await member.SetFileFingerprintsAsync(new FileSetFingerprintsDto(
            moved.File.Id,
            [new FingerprintEntryDto("MD5", "updated-md5")]), TestContext.Current.CancellationToken);
        updated.Updated.Should().Be(1);
        var fingerprints = await AsDbUser().GetFileFingerprintsAsync(moved.File.Id, TestContext.Current.CancellationToken);
        fingerprints.Should().HaveCount(2);
        fingerprints["md5"].Should().Be("updated-md5");
        fingerprints["oshash"].Should().Be("preserved-oshash");
        var publicFingerprints = (await owner.GetVideoByIdAsync(moved.Video.Id, TestContext.Current.CancellationToken)).Files
            .Should().ContainSingle().Which.Fingerprints
            .ToDictionary(fingerprint => fingerprint.Type, fingerprint => fingerprint.Value, StringComparer.OrdinalIgnoreCase);
        publicFingerprints.Should().HaveCount(2);
        publicFingerprints["md5"].Should().Be("updated-md5");
        publicFingerprints["oshash"].Should().Be("preserved-oshash");
        Func<Task> missingFingerprintFile = () => member.SetFileFingerprintsAsync(new FileSetFingerprintsDto(
            int.MaxValue,
            [new FingerprintEntryDto("md5", "missing-file")]));
        await missingFingerprintFile.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var memberRole = (await owner.GetRolesAsync(TestContext.Current.CancellationToken))
            .Should().ContainSingle(role => role.Name == BuiltinRoles.Member).Which;
        var writeDeny = await owner.CreateEntityOverrideAsync(new CreateEntityOverrideRequest(
            memberRole.Id,
            EntityKinds.File,
            moved.File.Id.ToString(CultureInfo.InvariantCulture),
            "deny",
            "write"), TestContext.Current.CancellationToken);
        var deniedDestination = fileSystem.CreateLibraryDirectory($"file-ops-denied-{suffix}");
        Func<Task> deniedFingerprints = () => member.SetFileFingerprintsAsync(new FileSetFingerprintsDto(
            moved.File.Id,
            [new FingerprintEntryDto("md5", "forbidden-md5")]));
        Func<Task> deniedMove = () => member.MoveFilesAsync(new MoveFilesDto(
            [moved.File.Id, control.File.Id],
            deniedDestination));
        await deniedFingerprints.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await deniedMove.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        var afterDeniedFingerprints = (await owner.GetVideoByIdAsync(moved.Video.Id, TestContext.Current.CancellationToken)).Files
            .Should().ContainSingle().Which.Fingerprints
            .ToDictionary(fingerprint => fingerprint.Type, fingerprint => fingerprint.Value, StringComparer.OrdinalIgnoreCase);
        afterDeniedFingerprints.Should().HaveCount(2);
        afterDeniedFingerprints["md5"].Should().Be("updated-md5");
        afterDeniedFingerprints["oshash"].Should().Be("preserved-oshash");
        AssertSingleFile(await owner.GetVideoByIdAsync(moved.Video.Id, TestContext.Current.CancellationToken), moved, movedPath);
        AssertSingleFile(await owner.GetVideoByIdAsync(control.Video.Id, TestContext.Current.CancellationToken), control, control.Path);
        fileSystem.LibraryFileExists(Path.Combine(deniedDestination, moved.File.Basename)).Should().BeFalse();
        fileSystem.LibraryFileExists(Path.Combine(deniedDestination, control.File.Basename)).Should().BeFalse();
        await owner.DeleteEntityOverrideAsync(writeDeny.Id, TestContext.Current.CancellationToken);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/files/delete")]
    public async Task GivenFixtureFiles_WhenOwnerDeletesRecordsAndDiskFiles_ThenPermissionsAndPhysicalRetentionAreExact()
    {
        var owner = AsUser();
        var member = AsUser(ApiTestUsers.Eva);
        var fileSystem = AsTestFileSystem();
        var suffix = Guid.NewGuid().ToString("N");
        var recordOnly = await CreateFileBackedVideoAsync(owner, $"record-only-{suffix}.txt", "retain physical source");
        var physical = await CreateFileBackedVideoAsync(owner, $"physical-delete-{suffix}.txt", "delete physical source");
        var control = await CreateFileBackedVideoAsync(owner, $"delete-control-{suffix}.txt", "delete control source");

        Func<Task> deniedDelete = () => member.DeleteFilesAsync(new DeleteFilesDto([recordOnly.File.Id], DeleteFromDisk: true));
        await deniedDelete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        AssertSingleFile(await owner.GetVideoByIdAsync(recordOnly.Video.Id, TestContext.Current.CancellationToken), recordOnly, recordOnly.Path);
        fileSystem.LibraryFileExists(recordOnly.Path).Should().BeTrue();

        var deletedUnknown = await owner.DeleteFilesAsync(new DeleteFilesDto([int.MaxValue], DeleteFromDisk: false), TestContext.Current.CancellationToken);
        deletedUnknown.Deleted.Should().Be(0);
        AssertSingleFile(await owner.GetVideoByIdAsync(recordOnly.Video.Id, TestContext.Current.CancellationToken), recordOnly, recordOnly.Path);

        var deletedRecordOnly = await owner.DeleteFilesAsync(new DeleteFilesDto(
            [recordOnly.File.Id, recordOnly.File.Id, int.MaxValue],
            DeleteFromDisk: false), TestContext.Current.CancellationToken);
        deletedRecordOnly.Deleted.Should().Be(1);
        (await owner.GetVideoByIdAsync(recordOnly.Video.Id, TestContext.Current.CancellationToken)).Files.Should().BeEmpty();
        fileSystem.LibraryFileExists(recordOnly.Path).Should().BeTrue();

        var deletedPhysical = await owner.DeleteFilesAsync(new DeleteFilesDto([physical.File.Id], DeleteFromDisk: true), TestContext.Current.CancellationToken);
        deletedPhysical.Deleted.Should().Be(1);
        (await owner.GetVideoByIdAsync(physical.Video.Id, TestContext.Current.CancellationToken)).Files.Should().BeEmpty();
        fileSystem.LibraryFileExists(physical.Path).Should().BeFalse();

        AssertSingleFile(await owner.GetVideoByIdAsync(control.Video.Id, TestContext.Current.CancellationToken), control, control.Path);
        fileSystem.LibraryFileExists(control.Path).Should().BeTrue();
    }

    private async Task<FixtureFile> CreateFileBackedVideoAsync(
        CoveClient client,
        string fileName,
        string contents)
    {
        var path = AsTestFileSystem().CreateLibraryFile(fileName, Encoding.UTF8.GetBytes(contents));
        var video = await client.CreateVideoFromFileAsync(path);
        var file = video.Files.Should().ContainSingle().Which;
        file.Path.Should().Be(path);
        return new FixtureFile(video, file, path);
    }

    private static void AssertSingleFile(VideoDto video, FixtureFile expected, string expectedPath)
    {
        var file = video.Files.Should().ContainSingle().Which;
        file.Id.Should().Be(expected.File.Id);
        file.Basename.Should().Be(expected.File.Basename);
        file.Path.Should().Be(expectedPath);
    }

    private sealed record FixtureFile(VideoDto Video, VideoFileDto File, string Path);
}
