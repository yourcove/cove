using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

[Collection(ApiTestLane1Collection.Name)]
public sealed class MetadataOperationsApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/metadata/library-folders")]
    public async Task GivenConfiguredLibrary_WhenFoldersAreRequested_ThenRootAndImmediateChildrenAreReported()
    {
        // Arrange
        var libraryPath = Path.GetFullPath(AsTestFileSystem().LibraryPath);
        var childPath = Path.Combine(libraryPath, $"child-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(childPath, "grandchild"));

        // Act
        var roots = await AsUser(ApiTestUsers.Eva).GetMetadataLibraryFoldersAsync();
        var children = await AsUser(ApiTestUsers.Eva).GetMetadataLibraryFoldersAsync(libraryPath);

        // Assert
        var root = roots.Should().ContainSingle(folder => folder.Path == libraryPath).Which;
        root.Name.Should().Be(libraryPath);
        root.HasChildren.Should().BeTrue();
        var child = children.Should().ContainSingle().Which;
        child.Name.Should().Be(Path.GetFileName(childPath));
        child.Path.Should().Be(childPath);
        child.HasChildren.Should().BeTrue();
    }

    [Fact]
    public async Task GivenPathOutsideConfiguredLibrary_WhenFoldersAreRequested_ThenForbiddenIsReturned()
    {
        // Arrange
        var outsidePath = Path.GetDirectoryName(AsTestFileSystem().LibraryPath)!;

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).GetMetadataLibraryFoldersAsync(outsidePath);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*OUTSIDE_LIBRARY*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/scan")]
    public async Task GivenTextFile_WhenSelectiveScanCompletes_ThenTextIsImported()
    {
        // Arrange
        var filePath = AsTestFileSystem().CreateTextFile("A selective metadata scan document.");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));

        // Act
        var jobId = await AsUser(ApiTestUsers.Eva).StartMetadataScanAsync(new ScanOptionsDto
        {
            Paths = [filePath],
        });
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId);

        // Assert
        job.Type.Should().Be("scan");
        job.Status.Should().Be(JobStatus.Completed);
        var text = (await AsUser(ApiTestUsers.Eva).GetTextsAsync()).Should().ContainSingle().Which;
        text.Files.Should().ContainSingle(file => Path.GetFullPath(file.Path) == Path.GetFullPath(filePath));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/generate")]
    public async Task GivenScannedText_WhenGenerateCompletes_ThenTextFingerprintIsAdded()
    {
        // Arrange
        var filePath = AsTestFileSystem().CreateTextFile("A deterministic fingerprint source document.");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));
        var scanJobId = await AsUser().StartMetadataScanAsync(new ScanOptionsDto { Paths = [filePath] });
        (await AsUser().WaitForTerminalJobAsync(scanJobId)).Status.Should().Be(JobStatus.Completed);
        var textFile = (await AsUser().GetTextsAsync()).Should().ContainSingle().Which.Files.Should().ContainSingle().Which;
        (await AsDbUser().GetFileFingerprintsAsync(textFile.Id)).Should().NotContainKey("phash");

        // Act
        var jobId = await AsUser(ApiTestUsers.Eva).StartMetadataGenerateAsync(new GenerateOptionsDto
        {
            Thumbnails = false,
            TextPhashes = true,
            Paths = [filePath],
        });
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId);

        // Assert
        job.Type.Should().Be("generate");
        job.Status.Should().Be(JobStatus.Completed);
        var fingerprints = await AsDbUser().GetFileFingerprintsAsync(textFile.Id);
        fingerprints.Should().ContainKey("phash").WhoseValue.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GivenVideoSelectionAndNonVideoWorkWithoutPaths_WhenGenerateStarts_ThenBadRequestIsReturned()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Generate validation {Guid.NewGuid():N}");
        var options = new GenerateOptionsDto
        {
            VideoIds = [video.Id],
            TextPhashes = true,
        };

        // Act
        var action = () => AsUser().StartMetadataGenerateAsync(options);

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*Non-video generate options require paths*");
    }

    [Fact]
    public async Task GivenFilelessVideo_WhenCleanIsDryRun_ThenVideoIsPreserved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Dry-run clean {Guid.NewGuid():N}");

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto { DryRun = true });
        var job = await AsUser().WaitForTerminalJobAsync(jobId);

        // Assert
        job.Type.Should().Be("clean");
        job.Status.Should().Be(JobStatus.Completed);
        (await AsUser().GetVideosAsync()).Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/clean")]
    public async Task GivenFilelessVideo_WhenCleanCompletes_ThenOrphanedVideoIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Orphan clean {Guid.NewGuid():N}");

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto());
        var job = await AsUser().WaitForTerminalJobAsync(jobId);

        // Assert
        job.Type.Should().Be("clean");
        job.Status.Should().Be(JobStatus.Completed);
        (await AsUser().GetVideosAsync()).Should().NotContain(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenMember_WhenCleanStarts_ThenForbiddenIsReturnedWithoutRemovingVideo()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Protected clean {Guid.NewGuid():N}");

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).StartMetadataCleanAsync(new CleanOptionsDto());

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetVideosAsync()).Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/clean-generated")]
    public async Task GivenLiveAndOrphanedGeneratedFiles_WhenCleanGeneratedCompletes_ThenOnlyOrphanIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Generated artifact owner {Guid.NewGuid():N}");
        var liveFile = AsTestFileSystem().CreateGeneratedFile($"thumbnails/{video.Id}.jpg", [1, 2, 3]);
        var orphanFile = AsTestFileSystem().CreateGeneratedFile($"thumbnails/{video.Id + 1_000_000}.jpg", [4, 5, 6]);

        // Act
        var jobId = await AsUser().StartMetadataCleanGeneratedAsync();
        var job = await AsUser().WaitForTerminalJobAsync(jobId);

        // Assert
        job.Type.Should().Be("clean-generated");
        job.Status.Should().Be(JobStatus.Completed);
        File.Exists(liveFile).Should().BeTrue();
        File.Exists(orphanFile).Should().BeFalse();
    }

    [Fact]
    public async Task GivenMember_WhenCleanGeneratedStarts_ThenForbiddenIsReturnedWithoutDeletingFile()
    {
        // Arrange
        var generatedFile = AsTestFileSystem().CreateGeneratedFile("thumbnails/1000000.jpg", [1]);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).StartMetadataCleanGeneratedAsync();

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        File.Exists(generatedFile).Should().BeTrue();
    }
}
