using Microsoft.EntityFrameworkCore.Diagnostics;
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
    public void SelectVideoFile_UsesThePrimaryFileInsideTheRequestedPath()
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "first.mp4"),
            (2, "/library/private", "selected.mp4"));
        video.PrimaryFileId = 2;

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Id);
    }

    [Fact]
    public void SelectVideoFile_ReturnsNullWhenNoFileMatchesTheRequestedPath()
    {
        var video = CreateVideoWithFiles((1, "/library/original", "first.mp4"));
        video.PrimaryFileId = 1;

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(99)]
    public void SelectVideoFile_DoesNotFallBackToMatchingSecondaryFile(int? primaryFileId)
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "primary.mp4"),
            (2, "/library/private", "secondary.mp4"));
        video.PrimaryFileId = primaryFileId;

        Assert.Null(GenerateJobService.SelectVideoFile(video, ["/library/private"]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(2, 2)]
    [InlineData(99, null)]
    public void SelectVideoFile_WithoutPathFilterRequiresAnAvailablePrimaryFile(int? primaryFileId, int? expectedFileId)
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "first.mp4"),
            (2, "/library/private", "primary.mp4"));
        video.PrimaryFileId = primaryFileId;

        Assert.Equal(expectedFileId, GenerateJobService.SelectVideoFile(video, [])?.Id);
    }

    [Theory]
    [InlineData("/library/video.mp4", "/library", true)]
    [InlineData("/library/video.mp4", "/library/", true)]
    [InlineData("/library", "/library", true)]
    [InlineData("/library-other/video.mp4", "/library", false)]
    [InlineData("/LIBRARY/video.mp4", "/library", true)]
    [InlineData("\\library\\video.mp4", "/library", true)]
    [InlineData("C:\\library\\video.mp4", "C:/library", true)]
    [InlineData("C:/library/video.mp4", "C:\\library", true)]
    [InlineData("C:/library-other/video.mp4", "C:/library", false)]
    [InlineData("/library/nested/video.mp4", "/library/nested", true)]
    [InlineData("/library/nested-other/video.mp4", "/library/nested", false)]
    [InlineData("/library/video.mp4", "/", true)]
    [InlineData("relative/library/video.mp4", "relative/library", true)]
    [InlineData("relative/library-other/video.mp4", "relative/library", false)]
    public void IsUnderAnyPath_UsesDirectorySegmentBoundaries(
        string candidate,
        string filter,
        bool expected)
    {
        Assert.Equal(expected, GeneratePathFilter.Contains(candidate, [filter]));
    }

    [Theory]
    [InlineData("/canonical/video.mp4", "/stale", "wrong.mp4")]
    [InlineData("C:/canonical/video.mp4", "C:/stale", "wrong.mp4")]
    [InlineData("//server/share/video.mp4", "//other/share", "wrong.mp4")]
    public void Resolve_UsesTheCanonicalFilePathInsteadOfNavigationData(string storedPath, string folderPath, string basename)
    {
        var file = new VideoFile
        {
            Path = storedPath,
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath },
        };

        var expected = OperatingSystem.IsWindows() ? storedPath.Replace('/', '\\') : storedPath;
        Assert.Equal(expected, GeneratePathFilter.Resolve(file));
    }

    [Fact]
    public async Task Start_PathScopedOverwrite_ReportsFailureForTheMatchingPrimaryFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(tempRoot, "original");
        var selectedRoot = Path.Combine(tempRoot, "selected");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(selectedRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(originalRoot, "first.mp4"), [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(selectedRoot, "selected.mp4"), [2], TestContext.Current.CancellationToken);

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
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                selectedFileId = video.Files.Single(file => file.Basename == "selected.mp4").Id;
                video.PrimaryFileId = selectedFileId;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
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
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(selectedFileId, thumbnails.PreviewSourceFileId);
            Assert.Equal(1, jobs.Progress.DeclaredTotal);
            var unit = Assert.Single(jobs.Progress.Units);
            Assert.Equal(JobUnitOutcome.Failed, unit.Outcome);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageGenerationBoundsMaterializationAndAppliesSelectionBeforeLoadingFiles(bool scoped)
    {
        var guard = new GenerationMaterializationGuard();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite("Data Source=:memory:").AddInterceptors(guard).Options;
        await using var db = new CoveContext(dbOptions);
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var folder = new Folder { Path = "/generation/memory" };
        var images = Enumerable.Range(0, 601).Select(index => new Image
        {
            Title = $"image {index}",
            Files = [new ImageFile { ParentFolder = folder, Basename = $"{index}.jpg" }],
        }).ToArray();
        db.Images.AddRange(images);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var selectedId = images[^1].Id;
        db.ChangeTracker.Clear();
        guard.Enabled = true;
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var trackedDuringWork = false;
        var thumbnails = new CapturingThumbnailService(Path.GetTempPath())
        {
            OnImageThumbnail = _ =>
            {
                guard.Processed++;
                trackedDuringWork |= db.ChangeTracker.Entries().Any();
                return Task.FromResult(true);
            },
        };
        var service = new NonVideoGenerationService(thumbnails, new NullFingerprintService(),
            new FileFingerprintWriter(scopeFactory), NullLogger<NonVideoGenerationService>.Instance);
        await service.GenerateAsync(db, new GenerateOptionsDto
        {
            Thumbnails = false, ImageThumbnails = true,
            ImageIds = scoped ? [selectedId] : null,
        }, 1, new CapturingJobProgress(), TestContext.Current.CancellationToken);
        Assert.Equal(scoped ? 1 : 601, guard.Materialized);
        Assert.Equal(guard.Materialized, guard.Processed);
        Assert.False(trackedDuringWork);
    }

    [Fact]
    public async Task VideoGenerationBatchesGraphsAndKeepsCountedUnitsWhenAssetsChange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-generate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var guard = new GenerationMaterializationGuard { EntityType = typeof(Video) };
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "library.db")};Pooling=False").AddInterceptors(guard).Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            await using var provider = services.BuildServiceProvider();
            int firstId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                var folder = new Folder { Path = Path.Combine(root, "missing-source") };
                var videos = Enumerable.Range(0, 601).Select(index => new Video
                {
                    Title = $"video {index}", Files = [new VideoFile { ParentFolder = folder, Basename = $"{index}.mp4" }],
                }).ToArray();
                db.Videos.AddRange(videos);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                firstId = videos[0].Id;
            }
            var jobs = new CapturingJobService();
            jobs.Progress.OnDeclared = () =>
            {
                // This was selected as missing, then appears before processing begins.
                File.WriteAllBytes(Path.Combine(root, $"{firstId}.jpg"), [0]);
                guard.Enabled = true;
            };
            jobs.Progress.OnCompleted = () => guard.Processed++;
            var thumbnails = new CapturingThumbnailService(root);
            var fingerprints = new NullFingerprintService();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var writer = new FileFingerprintWriter(scopes);
            var service = new GenerateJobService(jobs, thumbnails, thumbnails, fingerprints, writer,
                new NonVideoGenerationService(thumbnails, fingerprints, writer, NullLogger<NonVideoGenerationService>.Instance),
                scopes, new CoveConfiguration { MaxParallelTasks = 1 }, NullLogger<GenerateJobService>.Instance);
            service.Start(new GenerateOptionsDto { Thumbnails = true });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(601, jobs.Progress.DeclaredTotal);
            Assert.Equal(601, jobs.Progress.Units.Count);
            Assert.Equal(601, guard.Processed);
            Assert.Equal(601, guard.Materialized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class GenerationMaterializationGuard : IMaterializationInterceptor
    {
        public Type EntityType { get; init; } = typeof(ImageFile);
        public bool Enabled { get; set; }
        public int Materialized { get; private set; }
        public int Processed { get; set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Enabled && EntityType.IsInstanceOfType(entity))
            {
                Materialized++;
                Assert.True(Materialized - Processed <= GenerationSelection.BatchSize,
                    "Generation materialized more than one batch before processing it.");
            }
            return entity;
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
                Path = BaseFileEntity.ComputePath(file.Folder, file.Basename),
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
        public int? DeclaredTotal { get; private set; }
        public Action? OnDeclared { get; set; }
        public Action? OnCompleted { get; set; }
        public void DeclareUnitCount(int totalUnits) { DeclaredTotal = totalUnits; OnDeclared?.Invoke(); }

        public void Report(double progress, string? subTask = null)
        {
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            var unit = new CapturingJobUnit(OnCompleted);
            Units.Add(unit);
            return unit;
        }
    }

    private sealed class CapturingJobUnit(Action? onCompleted = null) : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            if (Outcome == null)
            {
                Outcome = outcome;
                onCompleted?.Invoke();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingThumbnailService(string generatedRoot) : IThumbnailService, IVideoAssetGenerator
    {
        public int? PreviewSourceFileId { get; private set; }
        public Func<int, Task<bool>>? OnImageThumbnail { get; init; }

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
            => OnImageThumbnail?.Invoke(imageId) ?? Task.FromResult(true);

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
