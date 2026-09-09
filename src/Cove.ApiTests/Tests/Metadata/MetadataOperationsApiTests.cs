using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Metadata;

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
        var roots = await AsUser(ApiTestUsers.Eva).GetMetadataLibraryFoldersAsync(cancellationToken: TestContext.Current.CancellationToken);
        var children = await AsUser(ApiTestUsers.Eva).GetMetadataLibraryFoldersAsync(libraryPath, cancellationToken: TestContext.Current.CancellationToken);

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
        }, TestContext.Current.CancellationToken);
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("scan");
        job.Status.Should().Be(JobStatus.Completed);
        var text = (await AsUser(ApiTestUsers.Eva).GetTextsAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        text.Files.Should().ContainSingle(file => Path.GetFullPath(file.Path) == Path.GetFullPath(filePath));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/generate")]
    public async Task GivenScannedText_WhenGenerateCompletes_ThenTextFingerprintIsAdded()
    {
        // Arrange
        var filePath = AsTestFileSystem().CreateTextFile("A deterministic fingerprint source document.");
        File.SetLastWriteTimeUtc(filePath, DateTime.UtcNow.AddMinutes(-1));
        var scanJobId = await AsUser().StartMetadataScanAsync(new ScanOptionsDto { Paths = [filePath] }, TestContext.Current.CancellationToken);
        (await AsUser().WaitForTerminalJobAsync(scanJobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        var textFile = (await AsUser().GetTextsAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Which.Files.Should().ContainSingle().Which;
        (await AsDbUser().GetFileFingerprintsAsync(textFile.Id, TestContext.Current.CancellationToken)).Should().NotContainKey("phash");

        // Act
        var jobId = await AsUser(ApiTestUsers.Eva).StartMetadataGenerateAsync(new GenerateOptionsDto
        {
            Thumbnails = false,
            TextPhashes = true,
            Paths = [filePath],
        }, TestContext.Current.CancellationToken);
        var job = await AsUser(ApiTestUsers.Eva).WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("generate");
        job.Status.Should().Be(JobStatus.Completed);
        var fingerprints = await AsDbUser().GetFileFingerprintsAsync(textFile.Id, TestContext.Current.CancellationToken);
        fingerprints.Should().ContainKey("phash").WhoseValue.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GivenVideoSelectionAndNonVideoWorkWithoutPaths_WhenGenerateStarts_ThenBadRequestIsReturned()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Generate validation {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
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
        var video = await AsUser().CreateVideoAsync($"Dry-run clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto { DryRun = true }, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("clean");
        job.Status.Should().Be(JobStatus.Completed);
        (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/clean")]
    public async Task GivenFilelessVideo_WhenCleanCompletes_ThenOrphanedVideoIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Orphan clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto(), TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Type.Should().Be("clean");
        job.Status.Should().Be(JobStatus.Completed);
        (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().NotContain(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenVideoWithLiveAndMissingFiles_WhenCleanCompletes_ThenOnlyMissingFileRowIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Partial file clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var livePath = AsTestFileSystem().CreateLibraryFile($"clean-live-{Guid.NewGuid():N}.mp4", [1]);
        var missingPath = AsTestFileSystem().CreateLibraryFile($"clean-missing-{Guid.NewGuid():N}.mp4", [2]);
        var liveFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, video.Id, livePath, TestContext.Current.CancellationToken);
        var missingFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, video.Id, missingPath, TestContext.Current.CancellationToken);
        File.Delete(missingPath);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto(), TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        var cleaned = (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(candidate => candidate.Id == video.Id).Which;
        cleaned.Files.Should().ContainSingle(file => Path.GetFullPath(file.Path) == Path.GetFullPath(livePath));
        (await AsDbUser().FileRowExistsAsync(liveFileId, TestContext.Current.CancellationToken)).Should().BeTrue();
        (await AsDbUser().FileRowExistsAsync(missingFileId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task GivenMissingFilesInsideAndOutsideSelectedPath_WhenCleanCompletes_ThenOnlySelectedFileRowIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Scoped file clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var selectedDirectory = $"selected-{Guid.NewGuid():N}";
        var selectedPath = AsTestFileSystem().CreateLibraryNestedFile(Path.Combine(selectedDirectory, "missing.mp4"), [1]);
        var retainedPath = AsTestFileSystem().CreateLibraryNestedFile(Path.Combine($"retained-{Guid.NewGuid():N}", "missing.mp4"), [2]);
        var selectedFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, video.Id, selectedPath, TestContext.Current.CancellationToken);
        var retainedFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, video.Id, retainedPath, TestContext.Current.CancellationToken);
        File.Delete(selectedPath);
        File.Delete(retainedPath);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto
        {
            Paths = [Path.Combine(AsTestFileSystem().LibraryPath, selectedDirectory)],
        }, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        var cleaned = (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(candidate => candidate.Id == video.Id).Which;
        cleaned.Files.Should().ContainSingle(file => Path.GetFullPath(file.Path) == Path.GetFullPath(retainedPath));
        (await AsDbUser().FileRowExistsAsync(selectedFileId, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await AsDbUser().FileRowExistsAsync(retainedFileId, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task GivenVideoWithMissingFile_WhenCleanIsDryRun_ThenMissingFileRowIsPreserved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Dry-run file clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var missingPath = AsTestFileSystem().CreateLibraryFile($"clean-dry-run-{Guid.NewGuid():N}.mp4", [1]);
        var missingFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Video, video.Id, missingPath, TestContext.Current.CancellationToken);
        File.Delete(missingPath);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto { DryRun = true }, TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        var retained = (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(candidate => candidate.Id == video.Id).Which;
        retained.Files.Should().ContainSingle(file => Path.GetFullPath(file.Path) == Path.GetFullPath(missingPath));
        (await AsDbUser().FileRowExistsAsync(missingFileId, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task GivenAudioAndTextWithLiveAndMissingFiles_WhenCleanCompletes_ThenTheirMissingFileRowsAreRemoved()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var audio = await AsUser().CreateAudioAsync($"Partial audio clean {suffix}", TestContext.Current.CancellationToken);
        var text = await AsUser().CreateTextAsync($"Partial text clean {suffix}", TestContext.Current.CancellationToken);
        var audioLivePath = AsTestFileSystem().CreateLibraryFile($"audio-live-{suffix}.mp3", [1]);
        var audioMissingPath = AsTestFileSystem().CreateLibraryFile($"audio-missing-{suffix}.mp3", [2]);
        var textLivePath = AsTestFileSystem().CreateLibraryFile($"text-live-{suffix}.txt", [3]);
        var textMissingPath = AsTestFileSystem().CreateLibraryFile($"text-missing-{suffix}.txt", [4]);
        await AsDbUser().CreateOwnedFileAsync(EntityKinds.Audio, audio.Id, audioLivePath, TestContext.Current.CancellationToken);
        var audioMissingFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Audio, audio.Id, audioMissingPath, TestContext.Current.CancellationToken);
        await AsDbUser().CreateOwnedFileAsync(EntityKinds.Text, text.Id, textLivePath, TestContext.Current.CancellationToken);
        var textMissingFileId = await AsDbUser().CreateOwnedFileAsync(EntityKinds.Text, text.Id, textMissingPath, TestContext.Current.CancellationToken);
        File.Delete(audioMissingPath);
        File.Delete(textMissingPath);

        // Act
        var jobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto(), TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        var cleanedAudio = await AsUser().GetAudioByIdAsync(audio.Id, TestContext.Current.CancellationToken);
        cleanedAudio.Files.Should().ContainSingle();
        cleanedAudio.FileCount.Should().Be(1);
        var cleanedText = await AsUser().GetTextByIdAsync(text.Id, TestContext.Current.CancellationToken);
        cleanedText.Files.Should().ContainSingle();
        cleanedText.FileCount.Should().Be(1);
        (await AsDbUser().FileRowExistsAsync(audioMissingFileId, TestContext.Current.CancellationToken)).Should().BeFalse();
        (await AsDbUser().FileRowExistsAsync(textMissingFileId, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task GivenImageInsideSurvivingArchive_WhenCleanCompletes_ThenVirtualImageFileIsPreserved()
    {
        // Arrange
        var suffix = Guid.NewGuid().ToString("N");
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Zip clean {suffix}").Build(), TestContext.Current.CancellationToken);
        var archivePath = AsTestFileSystem().CreateGalleryArchive($"zip-clean-{suffix}.zip", "image.png", ApiTestImages.OnePixelPng());
        File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(-1));
        await AsDbUser().AttachGalleryArchiveAsync(gallery.Id, archivePath, TestContext.Current.CancellationToken);
        var rescanJobId = await AsUser().RescanGalleryAsync(gallery.Id, TestContext.Current.CancellationToken);
        (await AsUser().WaitForTerminalJobAsync(rescanJobId, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        var importedImage = (await AsUser().GetImagesAsync(TestContext.Current.CancellationToken)).Should().ContainSingle().Which;
        var virtualFile = importedImage.Files.Should().ContainSingle().Which;

        // Act
        var cleanJobId = await AsUser().StartMetadataCleanAsync(new CleanOptionsDto(), TestContext.Current.CancellationToken);
        var cleanJob = await AsUser().WaitForTerminalJobAsync(cleanJobId, TestContext.Current.CancellationToken);

        // Assert
        cleanJob.Status.Should().Be(JobStatus.Completed);
        File.Exists(archivePath).Should().BeTrue();
        var retainedImage = await AsUser().GetImageByIdAsync(importedImage.Id, TestContext.Current.CancellationToken);
        retainedImage.Files.Should().ContainSingle(file => file.Id == virtualFile.Id);
        (await AsDbUser().FileRowExistsAsync(virtualFile.Id, TestContext.Current.CancellationToken)).Should().BeTrue();
    }

    [Fact]
    public async Task GivenMember_WhenCleanStarts_ThenForbiddenIsReturnedWithoutRemovingVideo()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Protected clean {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        // Act
        var action = () => AsUser(ApiTestUsers.Eva).StartMetadataCleanAsync(new CleanOptionsDto());

        // Assert
        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetVideosAsync(TestContext.Current.CancellationToken)).Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/metadata/clean-generated")]
    public async Task GivenLiveAndOrphanedGeneratedFiles_WhenCleanGeneratedCompletes_ThenOnlyOrphanIsRemoved()
    {
        // Arrange
        var video = await AsUser().CreateVideoAsync($"Generated artifact owner {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var liveFile = AsTestFileSystem().CreateGeneratedFile($"thumbnails/{video.Id}.jpg", [1, 2, 3]);
        var orphanFile = AsTestFileSystem().CreateGeneratedFile($"thumbnails/{video.Id + 1_000_000}.jpg", [4, 5, 6]);

        // Act
        var jobId = await AsUser().StartMetadataCleanGeneratedAsync(TestContext.Current.CancellationToken);
        var job = await AsUser().WaitForTerminalJobAsync(jobId, TestContext.Current.CancellationToken);

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
