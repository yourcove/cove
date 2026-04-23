using System.IO.Compression;
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

    private sealed class StubJobService : IJobService
    {
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => throw new NotSupportedException();

        public bool Cancel(string jobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Cove.Core.Entities.Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }
}