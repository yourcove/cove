using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Tests;

public class ThumbnailServiceTests
{
    [Fact]
    public async Task SpriteGenerationLock_SerializesTheSameVideoButNotOtherVideos()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = ThumbnailService.RunWithSpriteGenerationLockAsync(101, async () =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return true;
        });
        await firstEntered.Task;

        var second = ThumbnailService.RunWithSpriteGenerationLockAsync(101, () =>
        {
            secondEntered.SetResult();
            return Task.FromResult(true);
        });
        var other = ThumbnailService.RunWithSpriteGenerationLockAsync(102, () =>
        {
            otherEntered.SetResult();
            return Task.FromResult(true);
        });

        await otherEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(secondEntered.Task.IsCompleted);
        releaseFirst.SetResult();
        await Task.WhenAll(first, second, other);
        Assert.True(secondEntered.Task.IsCompleted);
    }

    [Fact]
    public async Task RegenerateVideoAssets_PreservesExistingFilesWhenGenerationFails()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var videoPath = Path.Combine(tempRoot, "invalid.mp4");
            await File.WriteAllBytesAsync(videoPath, [1, 2, 3, 4]);
            var generatedPath = Path.Combine(tempRoot, "generated");

            var services = new ServiceCollection();
            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = new Video { Title = "invalid" };
                video.Files.Add(new VideoFile
                {
                    Basename = Path.GetFileName(videoPath),
                    ParentFolder = new Folder { Path = tempRoot },
                    Format = "mp4",
                    Duration = 42,
                    Size = new FileInfo(videoPath).Length,
                    ModTime = File.GetLastWriteTimeUtc(videoPath),
                });
                db.Videos.Add(video);
                await db.SaveChangesAsync();
            }

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                new CoveConfiguration { GeneratedPath = generatedPath },
                new ZipFileReader(),
                new NullBlobService(),
                NullLogger<ThumbnailService>.Instance);

            var assets = new Dictionary<string, byte[]>
            {
                [service.GetThumbnailPathForVideo(1)] = [1, 2, 3],
                [service.GetPreviewPath(1)] = [4, 5, 6],
                [service.GetSpritePath(1)] = [7, 8, 9],
                [service.GetSpriteVttPath(1)] = [10, 11, 12],
                [service.GetSegmentAnimatedPreviewPath(1, 5)] = [13, 14, 15],
            };
            foreach (var (path, bytes) in assets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllBytesAsync(path, bytes);
            }

            Assert.False(await service.RegenerateVideoThumbnailAsync(1));
            Assert.False(await service.RegenerateVideoPreviewAsync(1));
            Assert.False(await service.RegenerateVideoSpriteAsync(1));
            Assert.False(await service.GenerateSegmentPreviewFromFileAsync(1, 1, 5, null, overwrite: true));

            foreach (var (path, expectedBytes) in assets)
                Assert.Equal(expectedBytes, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetVideoFileInfoAsync_UsesTheExplicitSourceFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var firstPath = Path.Combine(tempRoot, "first.mp4");
            var selectedPath = Path.Combine(tempRoot, "selected.mp4");
            await File.WriteAllBytesAsync(firstPath, [1]);
            await File.WriteAllBytesAsync(selectedPath, [2]);

            var services = new ServiceCollection();
            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

            await using var provider = services.BuildServiceProvider();
            int videoId;
            int selectedFileId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = new Video { Title = "multi-file" };
                video.Files.Add(new VideoFile
                {
                    Basename = Path.GetFileName(firstPath),
                    ParentFolder = new Folder { Path = tempRoot },
                    Duration = 10,
                });
                var selectedFile = new VideoFile
                {
                    Basename = Path.GetFileName(selectedPath),
                    ParentFolder = new Folder { Path = tempRoot },
                    Duration = 20,
                };
                video.Files.Add(selectedFile);
                db.Videos.Add(video);
                await db.SaveChangesAsync();
                videoId = video.Id;
                selectedFileId = selectedFile.Id;
            }

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                new CoveConfiguration { GeneratedPath = Path.Combine(tempRoot, "generated") },
                new ZipFileReader(),
                new NullBlobService(),
                NullLogger<ThumbnailService>.Instance);

            var source = await service.GetVideoFileInfoAsync(videoId, selectedFileId, CancellationToken.None);
            var missingSource = await service.GetVideoFileInfoAsync(videoId, int.MaxValue, CancellationToken.None);

            // Canonical paths: Folder.Path is stored forward-slashed, so this mixes separators on Windows.
            Assert.NotNull(source.FilePath);
            Assert.Equal(Path.GetFullPath(selectedPath), Path.GetFullPath(source.FilePath));
            Assert.Equal(20, source.Duration);
            Assert.Null(missingSource.FilePath);
            Assert.Equal(0, missingSource.Duration);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CommitGeneratedFile_CancellationPreservesExistingFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var generatedPath = Path.Combine(tempRoot, "generated.jpg");
            var destinationPath = Path.Combine(tempRoot, "destination.jpg");
            await File.WriteAllBytesAsync(generatedPath, [4, 5, 6]);
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                ThumbnailService.TryCommitGeneratedFile(generatedPath, destinationPath, cancellation.Token));

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destinationPath));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(generatedPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CommitGeneratedSpriteFiles_RollsBackWhenVttReplacementFails()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var generatedSpritePath = Path.Combine(tempRoot, "generated-sprite.jpg");
            var generatedVttPath = Path.Combine(tempRoot, "generated.vtt");
            var destinationSpritePath = Path.Combine(tempRoot, "sprite.jpg");
            var destinationVttPath = Path.Combine(tempRoot, "missing", "sprite.vtt");
            await File.WriteAllBytesAsync(generatedSpritePath, [4, 5, 6]);
            await File.WriteAllBytesAsync(generatedVttPath, [7, 8, 9]);
            await File.WriteAllBytesAsync(destinationSpritePath, [1, 2, 3]);

            Assert.Throws<DirectoryNotFoundException>(() =>
                ThumbnailService.CommitGeneratedSpriteFiles(
                    generatedSpritePath,
                    generatedVttPath,
                    destinationSpritePath,
                    destinationVttPath));

            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(destinationSpritePath));
            Assert.False(File.Exists(destinationVttPath));
            Assert.Empty(Directory.EnumerateFiles(tempRoot, "*.backup.*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CommitGeneratedSpriteFiles_CancellationPreservesExistingPair()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var generatedSpritePath = Path.Combine(tempRoot, "generated-sprite.jpg");
            var generatedVttPath = Path.Combine(tempRoot, "generated.vtt");
            var destinationSpritePath = Path.Combine(tempRoot, "sprite.jpg");
            var destinationVttPath = Path.Combine(tempRoot, "sprite.vtt");
            await File.WriteAllBytesAsync(generatedSpritePath, [5, 6]);
            await File.WriteAllBytesAsync(generatedVttPath, [7, 8]);
            await File.WriteAllBytesAsync(destinationSpritePath, [1, 2]);
            await File.WriteAllBytesAsync(destinationVttPath, [3, 4]);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() =>
                ThumbnailService.CommitGeneratedSpriteFiles(
                    generatedSpritePath,
                    generatedVttPath,
                    destinationSpritePath,
                    destinationVttPath,
                    cancellation.Token));

            Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(destinationSpritePath));
            Assert.Equal(new byte[] { 3, 4 }, await File.ReadAllBytesAsync(destinationVttPath));
            Assert.Equal(new byte[] { 5, 6 }, await File.ReadAllBytesAsync(generatedSpritePath));
            Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(generatedVttPath));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task CommitGeneratedVideoAssets_ReplacesValidatedOutputs()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var generatedPreviewPath = Path.Combine(tempRoot, "generated-preview.mp4");
            var destinationPreviewPath = Path.Combine(tempRoot, "preview.mp4");
            await File.WriteAllBytesAsync(generatedPreviewPath, [4, 5, 6]);
            await File.WriteAllBytesAsync(destinationPreviewPath, [1, 2, 3]);

            Assert.True(ThumbnailService.TryCommitGeneratedFile(generatedPreviewPath, destinationPreviewPath));
            Assert.Equal(new byte[] { 4, 5, 6 }, await File.ReadAllBytesAsync(destinationPreviewPath));

            var generatedSpritePath = Path.Combine(tempRoot, "generated-sprite.jpg");
            var generatedVttPath = Path.Combine(tempRoot, "generated.vtt");
            var destinationSpritePath = Path.Combine(tempRoot, "sprite.jpg");
            var destinationVttPath = Path.Combine(tempRoot, "sprite.vtt");
            await File.WriteAllBytesAsync(generatedSpritePath, [10, 11, 12]);
            await File.WriteAllBytesAsync(generatedVttPath, [13, 14, 15]);
            await File.WriteAllBytesAsync(destinationSpritePath, [7, 8, 9]);
            await File.WriteAllBytesAsync(destinationVttPath, [16, 17, 18]);

            ThumbnailService.CommitGeneratedSpriteFiles(
                generatedSpritePath,
                generatedVttPath,
                destinationSpritePath,
                destinationVttPath);

            Assert.Equal(new byte[] { 10, 11, 12 }, await File.ReadAllBytesAsync(destinationSpritePath));
            Assert.Equal(new byte[] { 13, 14, 15 }, await File.ReadAllBytesAsync(destinationVttPath));
            Assert.Empty(Directory.EnumerateFiles(tempRoot, "*.backup.*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetImageStreamAsync_ExtractsLegacyZipBackedImageUsingResolvedPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var zipPath = Path.Combine(tempRoot, "gallery.zip");
            var expectedBytes = new byte[] { 1, 2, 3, 4 };

            using (var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("nested/cover.jpg");
                await using var entryStream = entry.Open();
                await entryStream.WriteAsync(expectedBytes);
            }

            var services = new ServiceCollection();
            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                var folder = new Folder { Path = Path.Combine(zipPath, "nested") };
                var image = new Cove.Core.Entities.Image { Title = "legacy" };
                image.Files.Add(new ImageFile
                {
                    Basename = "cover.jpg",
                    ParentFolder = folder,
                    Format = "jpeg",
                    Width = 1,
                    Height = 1,
                    Size = expectedBytes.Length,
                    ModTime = DateTime.UtcNow,
                });

                db.Images.Add(image);
                await db.SaveChangesAsync();
            }

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                new CoveConfiguration(),
                new ZipFileReader(),
                new NullBlobService(),
                NullLogger<ThumbnailService>.Instance);

            var result = await service.GetImageStreamAsync(1, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("image/jpeg", result.Value.contentType);
            Assert.False(result.Value.supportsRangeRequests);

            await using var stream = result.Value.stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(expectedBytes, buffer.ToArray());
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetImageThumbnailStreamAsync_ReturnsCappedCachedThumbnail()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var imagePath = Path.Combine(tempRoot, "large.jpg");
            using (var sourceImage = new Image<Rgba32>(2200, 1400))
            {
                await sourceImage.SaveAsJpegAsync(imagePath);
            }

            var sourceModTime = DateTime.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(imagePath, sourceModTime);

            var services = new ServiceCollection();
            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

            var config = new CoveConfiguration
            {
                GeneratedPath = Path.Combine(tempRoot, "generated"),
                WriteImageThumbnails = true,
            };

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                var folder = new Folder { Path = tempRoot };
                var image = new Cove.Core.Entities.Image { Title = "large" };
                image.Files.Add(new ImageFile
                {
                    Basename = "large.jpg",
                    ParentFolder = folder,
                    Format = "jpeg",
                    Width = 2200,
                    Height = 1400,
                    Size = new FileInfo(imagePath).Length,
                    ModTime = sourceModTime,
                });

                db.Images.Add(image);
                await db.SaveChangesAsync();
            }

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                config,
                new ZipFileReader(),
                new NullBlobService(),
                NullLogger<ThumbnailService>.Instance);

            var result = await service.GetImageThumbnailStreamAsync(1, 640, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("image/jpeg", result.Value.contentType);
            Assert.True(result.Value.supportsRangeRequests);

            await using var stream = result.Value.stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            using var thumbnail = await SixLabors.ImageSharp.Image.LoadAsync(buffer);
            Assert.True(Math.Max(thumbnail.Width, thumbnail.Height) <= 640);

            var thumbnailFiles = Directory.GetFiles(Path.Combine(config.GeneratedPath, "thumbnails"), "*.jpg", SearchOption.AllDirectories);
            Assert.Single(thumbnailFiles);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetImageThumbnailStreamAsync_PreservesTransparencyForPngSources()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var imagePath = Path.Combine(tempRoot, "transparent.png");
            using (var sourceImage = new Image<Rgba32>(120, 120))
            {
                sourceImage.ProcessPixelRows(accessor =>
                {
                    for (var y = 30; y < 90; y++)
                    {
                        var row = accessor.GetRowSpan(y);
                        for (var x = 30; x < 90; x++)
                            row[x] = new Rgba32(255, 64, 64, 255);
                    }
                });

                await sourceImage.SaveAsPngAsync(imagePath);
            }

            var sourceModTime = DateTime.UtcNow.AddMinutes(-1);
            File.SetLastWriteTimeUtc(imagePath, sourceModTime);

            var services = new ServiceCollection();
            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            services.AddSingleton(dbOptions);
            services.AddScoped<CoveContext>(_ => new TestCoveContext(dbOptions));

            var config = new CoveConfiguration
            {
                GeneratedPath = Path.Combine(tempRoot, "generated"),
                WriteImageThumbnails = true,
            };

            await using var provider = services.BuildServiceProvider();
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                var folder = new Folder { Path = tempRoot };
                var image = new Cove.Core.Entities.Image { Title = "transparent" };
                image.Files.Add(new ImageFile
                {
                    Basename = "transparent.png",
                    ParentFolder = folder,
                    Format = "png",
                    Width = 120,
                    Height = 120,
                    Size = new FileInfo(imagePath).Length,
                    ModTime = sourceModTime,
                });

                db.Images.Add(image);
                await db.SaveChangesAsync();
            }

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                config,
                new ZipFileReader(),
                new NullBlobService(),
                NullLogger<ThumbnailService>.Instance);

            var result = await service.GetImageThumbnailStreamAsync(1, 64, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("image/png", result.Value.contentType);
            Assert.True(result.Value.supportsRangeRequests);

            await using var stream = result.Value.stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            buffer.Position = 0;

            using var thumbnail = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(buffer);
            Assert.Equal(0, thumbnail[0, 0].A);
            Assert.True(thumbnail[Math.Min(thumbnail.Width - 1, thumbnail.Width / 2), Math.Min(thumbnail.Height - 1, thumbnail.Height / 2)].A > 0);

            var thumbnailFiles = Directory.GetFiles(Path.Combine(config.GeneratedPath, "thumbnails"), "*.png", SearchOption.AllDirectories);
            Assert.Single(thumbnailFiles);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetBlobImageThumbnailStreamAsync_ServesDetectedSvgWithSvgContentType()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-thumbnail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var svgBytes = Encoding.UTF8.GetBytes("\uFEFF  <?xml version=\"1.0\" encoding=\"UTF-8\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"20\" height=\"20\"><rect width=\"20\" height=\"20\" fill=\"red\" /></svg>");
            var generatedPath = Path.Combine(tempRoot, "generated");
            var services = new ServiceCollection();

            await using var provider = services.BuildServiceProvider();

            var service = new ThumbnailService(
                provider.GetRequiredService<IServiceScopeFactory>(),
                new StubJobService(),
                new CoveConfiguration
                {
                    GeneratedPath = generatedPath,
                    WriteImageThumbnails = true,
                },
                new ZipFileReader(),
                new BlobServiceWithContent(svgBytes, "image/png"),
                NullLogger<ThumbnailService>.Instance);

            var result = await service.GetBlobImageThumbnailStreamAsync("studio-svg", 640, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("image/svg+xml", result.Value.contentType);
            Assert.True(result.Value.supportsRangeRequests);

            await using var stream = result.Value.stream;
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer);
            Assert.Equal(svgBytes, buffer.ToArray());
            if (Directory.Exists(generatedPath))
                Assert.Empty(Directory.GetFiles(generatedPath, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class StubJobService : IJobService
    {
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => throw new NotSupportedException();

        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => Task.FromResult("blob-id");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class BlobServiceWithContent(byte[] bytes, string contentType) : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => Task.FromResult("blob-id");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>((new MemoryStream(bytes), contentType));

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }
}
