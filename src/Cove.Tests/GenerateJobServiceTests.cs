using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class GenerateJobServiceTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("imported-cover", false)]
    public void ShouldGenerateDefaultVideoThumbnail_SkipsExplicitBlobCovers(
        string? imageBlobId,
        bool expected)
    {
        Assert.Equal(
            expected,
            GenerateJobService.ShouldGenerateDefaultVideoThumbnail(
                requested: true,
                imageBlobId));
    }

    [Fact]
    public void ShouldGenerateDefaultVideoThumbnail_RequiresThumbnailRequest()
    {
        Assert.False(GenerateJobService.ShouldGenerateDefaultVideoThumbnail(
            requested: false,
            imageBlobId: null));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, JobUnitOutcome.Failed)]
    public async Task ReportGenerateResultAsync_RecordsFailureOnlyWhenGenerationFails(
        bool result,
        JobUnitOutcome? expected)
    {
        var unit = new NullJobUnit("video");

        await GenerateJobService.ReportGenerateResultAsync(unit, Task.FromResult(result), "failed");

        Assert.Equal(expected, unit.Outcome);
    }

    [Fact]
    public void RequiresPathsForExplicitNonVideoWork_RejectsMixedIdOnlyRequest()
    {
        var options = new GenerateOptionsDto
        {
            VideoIds = [1],
            ImagePhashes = true,
        };

        Assert.True(GenerateJobService.RequiresPathsForExplicitNonVideoWork(options));
        Assert.False(GenerateJobService.RequiresPathsForExplicitNonVideoWork(options with { Paths = ["/library"] }));
    }

    [Fact]
    public void SelectVideoFile_UsesTheFileInsideTheRequestedPath()
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "first.mp4"),
            (2, "/library/private", "selected.mp4"));

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Id);
    }

    [Fact]
    public void SelectVideoFile_ReturnsNullWhenNoFileMatchesTheRequestedPath()
    {
        var video = CreateVideoWithFiles((1, "/library/original", "first.mp4"));

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData("/library/video.mp4", "/library", true)]
    [InlineData("/library/video.mp4", "/library/", true)]
    [InlineData("/library", "/library", true)]
    [InlineData("/library-other/video.mp4", "/library", false)]
    [InlineData("/LIBRARY/video.mp4", "/library", true)]
    public void IsUnderAnyPath_UsesDirectorySegmentBoundaries(
        string candidate,
        string filter,
        bool expected)
    {
        Assert.Equal(expected, GeneratePathFilter.Contains(candidate, [filter]));
    }

    [Fact]
    public async Task Start_PathScopedOverwrite_ReportsFailureForTheMatchingSourceFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(tempRoot, "original");
        var selectedRoot = Path.Combine(tempRoot, "selected");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(selectedRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(originalRoot, "first.mp4"), [1]);
            await File.WriteAllBytesAsync(Path.Combine(selectedRoot, "selected.mp4"), [2]);

            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(dbOptions));
            await using var provider = services.BuildServiceProvider();

            int selectedFileId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = CreateVideoWithFiles(
                    (1, originalRoot, "first.mp4"),
                    (2, selectedRoot, "selected.mp4"));
                db.Videos.Add(video);
                await db.SaveChangesAsync();
                selectedFileId = video.Files.Single(file => file.Basename == "selected.mp4").Id;
            }

            var jobs = new CapturingJobService();
            var thumbnails = new CapturingThumbnailService(tempRoot);
            var fingerprints = new NullFingerprintService();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var fingerprintWriter = new FileFingerprintWriter(scopeFactory);
            var nonVideoGeneration = new NonVideoGenerationService(
                thumbnails,
                fingerprints,
                fingerprintWriter,
                NullLogger<NonVideoGenerationService>.Instance);
            var service = new GenerateJobService(
                jobs,
                thumbnails,
                thumbnails,
                fingerprints,
                fingerprintWriter,
                nonVideoGeneration,
                scopeFactory,
                new CoveConfiguration { MaxParallelTasks = 1 },
                NullLogger<GenerateJobService>.Instance);

            service.Start(new GenerateOptionsDto
            {
                Thumbnails = false,
                Previews = true,
                Overwrite = true,
                Paths = [selectedRoot],
            });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(selectedFileId, thumbnails.PreviewSourceFileId);
            var unit = Assert.Single(jobs.Progress.Units);
            Assert.Equal(JobUnitOutcome.Failed, unit.Outcome);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static Video CreateVideoWithFiles(params (int Id, string Folder, string Basename)[] files)
    {
        var video = new Video();
        foreach (var file in files)
        {
            video.Files.Add(new VideoFile
            {
                Id = file.Id,
                Basename = file.Basename,
                ParentFolder = new Folder { Path = file.Folder },
            });
        }

        return video;
    }

    private sealed class CapturingJobService : IJobService
    {
        public CapturingJobProgress Progress { get; } = new();

        public Task Completion { get; private set; } = Task.CompletedTask;

        public string Enqueue(
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            bool exclusive = true)
        {
            Completion = work(Progress, CancellationToken.None);
            return "generate-job";
        }

        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class CapturingJobProgress : IJobProgress
    {
        public List<CapturingJobUnit> Units { get; } = [];

        public void Report(double progress, string? subTask = null)
        {
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            var unit = new CapturingJobUnit();
            Units.Add(unit);
            return unit;
        }
    }

    private sealed class CapturingJobUnit : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            Outcome ??= outcome;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingThumbnailService(string generatedRoot) : IThumbnailService, IVideoAssetGenerator
    {
        public int? PreviewSourceFileId { get; private set; }

        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(
            int imageId,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(
            int imageId,
            int maxDimension = 640,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(
            string blobId,
            int maxDimension = 640,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoThumbnailAsync(
            int videoId,
            double? atSeconds = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> GenerateThumbnailFromFileAsync(
            int videoId,
            int sourceFileId,
            double? atSeconds,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> GenerateImageThumbnailAsync(
            int imageId,
            int maxDimension = 640,
            bool overwrite = false,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> GeneratePreviewFromFileAsync(
            int videoId,
            int sourceFileId,
            bool overwrite,
            CancellationToken ct = default)
        {
            PreviewSourceFileId = sourceFileId;
            return Task.FromResult(false);
        }

        public Task GenerateSegmentAnimatedPreviewAsync(
            int videoId,
            double startSec,
            double? endSec = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> GenerateSegmentPreviewFromFileAsync(
            int videoId,
            int sourceFileId,
            double startSec,
            double? endSec,
            bool overwrite,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> GenerateSpriteFromFileAsync(
            int videoId,
            int sourceFileId,
            bool overwrite,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public string GetThumbnailPathForVideo(int videoId) => Path.Combine(generatedRoot, $"{videoId}.jpg");

        public string GetTimestampedThumbnailPath(int videoId, double seconds)
            => Path.Combine(generatedRoot, $"{videoId}_{seconds}.jpg");

        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds)
            => Path.Combine(generatedRoot, $"{videoId}_{seconds}.webp");

        public string GetPreviewPath(int videoId) => Path.Combine(generatedRoot, $"{videoId}.mp4");

        public string GetSpritePath(int videoId) => Path.Combine(generatedRoot, $"{videoId}_sprite.jpg");

        public string GetSpriteVttPath(int videoId) => Path.Combine(generatedRoot, $"{videoId}.vtt");

        public string StartGenerateAllThumbnails() => "generate-thumbnails";
    }

    private sealed class NullFingerprintService : IFingerprintService
    {
        public Task<string?> ComputeMd5Async(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public string StartGenerateVideoPhashes() => "video-phashes";

        public string StartGenerateImagePhashes() => "image-phashes";
    }
}
