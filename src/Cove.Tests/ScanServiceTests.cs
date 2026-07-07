using Cove.Core.Events;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class ScanServiceTests
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp4" };
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg" };
    private static readonly HashSet<string> GalleryExtensions = new(StringComparer.OrdinalIgnoreCase) { ".zip" };
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3" };
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase) { ".epub" };

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsTrueWhenDurationIsMissing()
    {
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 0,
        };

        Assert.True(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsTrueWhenDimensionsAreMissing()
    {
        var videoFile = new VideoFile
        {
            Width = 0,
            Height = 1080,
            Duration = 307.9,
        };

        Assert.True(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void NeedsVideoMetadataProbe_ReturnsFalseWhenCoreVideoMetricsExist()
    {
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 307.9,
        };

        Assert.False(ScanService.NeedsVideoMetadataProbe(videoFile));
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsTrueForGalleryArchiveWhenImagesAreExcluded()
    {
        Assert.True(ScanService.IsMediaTypeExcludedByScanTarget(
            ".zip",
            excludeVideo: false,
            excludeImage: true,
            excludeAudio: false,
            excludeText: false,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsTrueForTextsWhenTextsAreExcluded()
    {
        Assert.True(ScanService.IsMediaTypeExcludedByScanTarget(
            ".epub",
            excludeVideo: false,
            excludeImage: false,
            excludeAudio: false,
            excludeText: true,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public void IsMediaTypeExcludedByScanTarget_ReturnsFalseForAllowedMediaTypes()
    {
        Assert.False(ScanService.IsMediaTypeExcludedByScanTarget(
            ".zip",
            excludeVideo: false,
            excludeImage: false,
            excludeAudio: false,
            excludeText: false,
            VideoExtensions,
            ImageExtensions,
            GalleryExtensions,
            AudioExtensions,
            TextExtensions));
    }

    [Fact]
    public async Task StartScan_SkipsCaptionSyncForKnownUnchangedVideosDuringNormalScan()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();

            Assert.Empty(video.Captions);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_TreatsSubSecondStoredModTimeDifferenceAsUnchanged()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            var wholeSecond = new DateTime(DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(videoPath, wholeSecond.AddMilliseconds(500));
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath, storedModTime: wholeSecond);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();

            Assert.Empty(video.Captions);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_SkipsChangedPathWhenExistingFileKindDiffersFromExtension()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var path = Path.Combine(tempRoot, "known.mp3");
            await File.WriteAllBytesAsync(path, [1, 2, 3, 4]);
            var oldStoredModTime = DateTime.UtcNow.AddDays(-1);

            await using var environment = await CreateEnvironmentAsync(tempRoot, path, storedModTime: oldStoredModTime);

            environment.Service.StartScan();

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();

            Assert.Equal(1, await db.Set<BaseFileEntity>().CountAsync());
            Assert.Equal(0, await db.AudioFiles.CountAsync());
            Assert.Equal(1, await db.VideoFiles.CountAsync());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_RescanSyncsCaptionsForKnownVideos()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "known.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "known.en.vtt"), "WEBVTT");

            await using var environment = await CreateEnvironmentAsync(tempRoot, videoPath);

            environment.Service.StartScan(new ScanOperationOptions { Rescan = true });

            await using var verificationScope = environment.Services.CreateAsyncScope();
            var db = verificationScope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = await db.VideoFiles.Include(item => item.Captions).SingleAsync();
            var caption = Assert.Single(video.Captions);

            Assert.Equal("known.en.vtt", caption.Filename);
            Assert.Equal("en", caption.LanguageCode);
            Assert.Equal("vtt", caption.CaptionType);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ApplyFfprobeMetadata_OverwritesStaleCodecAndResolutionOnReEncode()
    {
        // Simulates the reported HEVC -> AV1 in-place re-encode: an already-populated file being re-probed
        // must have its codec/resolution/duration replaced, not silently kept.
        var videoFile = new VideoFile
        {
            Width = 1920,
            Height = 1080,
            Duration = 100,
            BitRate = 5_000_000,
            VideoCodec = "hevc",
            AudioCodec = "aac",
            FrameRate = 25,
        };

        const string json = """
        {
          "format": { "duration": "42.5", "bit_rate": "1000000" },
          "streams": [
            { "codec_type": "video", "codec_name": "av1", "width": 1280, "height": 720, "r_frame_rate": "30/1" },
            { "codec_type": "audio", "codec_name": "opus" }
          ]
        }
        """;

        ScanService.ApplyFfprobeMetadata(videoFile, json);

        Assert.Equal("av1", videoFile.VideoCodec);
        Assert.Equal(1280, videoFile.Width);
        Assert.Equal(720, videoFile.Height);
        Assert.Equal("opus", videoFile.AudioCodec);
        Assert.Equal(42.5, videoFile.Duration);
        Assert.Equal(1_000_000, videoFile.BitRate);
        Assert.Equal(30, videoFile.FrameRate);
    }

    [Fact]
    public async Task StartScan_RelinksMovedVideoInsteadOfCreatingDuplicate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            // oshash needs at least one 64KB chunk to be non-null.
            var bytes = new byte[70_000];
            new Random(1234).NextBytes(bytes);
            var originalPath = Path.Combine(tempRoot, "original.mp4");
            await File.WriteAllBytesAsync(originalPath, bytes);

            await using var environment = await CreateBareEnvironmentAsync(tempRoot);

            // First scan: creates the Video + VideoFile and its oshash identity fingerprint.
            environment.Service.StartScan();

            int videoFileId;
            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var seededFile = await db.VideoFiles.Include(f => f.Fingerprints).SingleAsync();
                videoFileId = seededFile.Id;
                Assert.Contains(seededFile.Fingerprints, fp => fp.Type == "oshash" && !string.IsNullOrEmpty(fp.Value));

                // Stamp the entity so we can prove the move preserves it rather than recreating it.
                var seededVideo = await db.Videos.SingleAsync();
                seededVideo.Title = "Preserve me";
                await db.SaveChangesAsync();
            }

            // Move the file to a subfolder (identical bytes -> identical oshash) and remove the original.
            var subDir = Path.Combine(tempRoot, "sub");
            Directory.CreateDirectory(subDir);
            var movedPath = Path.Combine(subDir, "renamed.mp4");
            File.Move(originalPath, movedPath);

            // Second scan: should re-point the existing record, not create a duplicate.
            environment.Service.StartScan();

            await using (var scope = environment.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                Assert.Equal(1, await db.Videos.CountAsync());
                var movedFile = await db.VideoFiles.SingleAsync();
                Assert.Equal(videoFileId, movedFile.Id);
                Assert.Equal("renamed.mp4", movedFile.Basename);
                Assert.EndsWith("sub/renamed.mp4", movedFile.Path);
                Assert.Equal("Preserve me", (await db.Videos.SingleAsync()).Title);
            }
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_AttachesDuplicateFileToExistingEntityInsteadOfCreatingSecondVideo()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-scan-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var bytes = new byte[70_000];
            new Random(4321).NextBytes(bytes);
            var originalPath = Path.Combine(tempRoot, "original.mp4");
            await File.WriteAllBytesAsync(originalPath, bytes);

            await using var environment = await CreateBareEnvironmentAsync(tempRoot);
            environment.Service.StartScan();

            // A COPY of the same bytes appears while the original remains on disk: identical content should
            // join the existing video as a second file, not spawn a separate duplicate entity.
            var copyPath = Path.Combine(tempRoot, "copy.mp4");
            await File.WriteAllBytesAsync(copyPath, bytes);

            environment.Service.StartScan();

            await using var scope = environment.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Equal(1, await db.Videos.CountAsync());
            Assert.Equal(2, await db.VideoFiles.CountAsync());
            var video = await db.Videos.Include(v => v.Files).SingleAsync();
            Assert.Equal(2, video.Files.Count);
            Assert.Contains(video.Files, f => f.Basename == "original.mp4");
            Assert.Contains(video.Files, f => f.Basename == "copy.mp4");
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static async Task<TestEnvironment> CreateBareEnvironmentAsync(string libraryRoot)
    {
        var services = new ServiceCollection();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-service-{Guid.NewGuid():N}")
            .Options;

        services.AddSingleton(dbOptions);
        services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync();
        }

        var config = new CoveConfiguration
        {
            CovePaths = [new CovePath { Path = libraryRoot }],
        };

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = libraryRoot,
            CoveVersion = "test",
        });

        var service = new ScanService(
            new ImmediateJobService(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            new EventBus(),
            new NoOpFingerprintService(),
            new NoOpThumbnailService(),
            new TextExtractionService(),
            new ZipGalleryReader(new ZipFileReader()),
            extensionManager,
            NullLogger<ScanService>.Instance);

        return new TestEnvironment(provider, service);
    }

    private static async Task<TestEnvironment> CreateEnvironmentAsync(string libraryRoot, string videoPath, DateTime? storedModTime = null)
    {
        var services = new ServiceCollection();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"scan-service-{Guid.NewGuid():N}")
            .Options;

        services.AddSingleton(dbOptions);
        services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

        var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            await db.Database.EnsureCreatedAsync();

            var folder = new Folder
            {
                Path = NormalizeStoredFolderPath(libraryRoot),
                ModTime = Directory.GetLastWriteTimeUtc(libraryRoot),
            };

            var video = new Video
            {
                Title = "Known video",
            };

            var fileInfo = new FileInfo(videoPath);
            var effectiveStoredModTime = storedModTime ?? fileInfo.LastWriteTimeUtc;
            video.Files.Add(new VideoFile
            {
                Basename = Path.GetFileName(videoPath),
                ParentFolder = folder,
                Size = fileInfo.Length,
                ModTime = effectiveStoredModTime,
                Format = "mp4",
                Width = 1920,
                Height = 1080,
                Duration = 42,
                VideoCodec = "h264",
                AudioCodec = "aac",
            });

            db.Videos.Add(video);
            await db.SaveChangesAsync();
        }
        var jobService = new ImmediateJobService();
        var config = new CoveConfiguration
        {
            CovePaths =
            [
                new CovePath
                {
                    Path = libraryRoot,
                }
            ],
        };

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = libraryRoot,
            CoveVersion = "test",
        });

        var service = new ScanService(
            jobService,
            provider.GetRequiredService<IServiceScopeFactory>(),
            config,
            new EventBus(),
            new NoOpFingerprintService(),
            new NoOpThumbnailService(),
            new TextExtractionService(),
            new ZipGalleryReader(new ZipFileReader()),
            extensionManager,
            NullLogger<ScanService>.Instance);

        return new TestEnvironment(provider, service);
    }

    private static string NormalizeStoredFolderPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var normalized = !string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalized.Replace('\\', '/');
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options);

    private sealed class ImmediateJobService : IJobService
    {
        private int _nextId;

        public string Enqueue(string type, string description, Func<Cove.Core.Interfaces.IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            work(new ImmediateJobProgress(), CancellationToken.None).GetAwaiter().GetResult();
            return $"job-{Interlocked.Increment(ref _nextId)}";
        }

        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class ImmediateJobProgress : Cove.Core.Interfaces.IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private sealed class NoOpFingerprintService : IFingerprintService
    {
        public Task<string?> ComputeMd5Async(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public string StartGenerateVideoPhashes() => "noop";

        public string StartGenerateImagePhashes() => "noop";
    }

    private sealed class NoOpThumbnailService : IThumbnailService
    {
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension = 640, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension = 640, CancellationToken ct = default) => Task.FromResult<(Stream stream, string contentType, bool supportsRangeRequests)?>(null);

        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateSegmentAnimatedPreviewAsync(int videoId, double startSec, double? endSec = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public string GetThumbnailPathForVideo(int videoId) => string.Empty;

        public string GetTimestampedThumbnailPath(int videoId, double seconds) => string.Empty;

        public string GetSegmentAnimatedPreviewPath(int videoId, double seconds) => string.Empty;

        public string GetPreviewPath(int videoId) => string.Empty;

        public string GetSpritePath(int videoId) => string.Empty;

        public string GetSpriteVttPath(int videoId) => string.Empty;

        public string StartGenerateAllThumbnails() => "noop";
    }

    private sealed class TestEnvironment(ServiceProvider services, ScanService service) : IAsyncDisposable
    {
        public ServiceProvider Services { get; } = services;
        public ScanService Service { get; } = service;

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
        }
    }
}

