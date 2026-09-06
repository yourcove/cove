using System.IO.Compression;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class ScanGalleryProcessorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedArchiveReplacementIsRetrySafe(bool failAfterCommit)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"cove-gallery-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var archivePath = Path.Combine(root, "gallery.zip");
        CreateArchive(archivePath, "replacement.png");

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(ct);
            var interceptor = failAfterCommit
                ? (IInterceptor)new CommitAmbiguityInterceptor()
                : new PreCommitFailureInterceptor();
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite(connection)
                .ReplaceService<Microsoft.EntityFrameworkCore.Storage.IExecutionStrategyFactory, TestRetryingExecutionStrategyFactory>()
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new CoveContext(options);
            await db.Database.EnsureCreatedAsync(ct);

            var folder = new Folder { Path = root };
            var virtualFolder = new Folder { Path = $"{archivePath}#virtual" };
            var galleryFile = new GalleryFile
            {
                Basename = Path.GetFileName(archivePath),
                ParentFolder = folder,
                Size = 1,
                ModTime = DateTime.UtcNow.AddMinutes(-2),
            };
            var gallery = new Gallery
            {
                Files = [galleryFile],
            };
            db.Galleries.Add(gallery);
            await db.SaveChangesAsync(ct);
            var originalFile = new ImageFile
            {
                Basename = "original.png",
                ParentFolder = virtualFolder,
                ZipFileId = galleryFile.Id,
                Size = 1,
                ModTime = DateTime.UtcNow.AddMinutes(-2),
                Format = "png",
            };
            var originalImage = new Image { Title = "original", Files = [originalFile] };
            gallery.ImageGalleries.Add(new ImageGallery { Image = originalImage });
            await db.SaveChangesAsync(ct);
            var originalGalleryFileId = galleryFile.Id;

            switch (interceptor)
            {
                case CommitAmbiguityInterceptor commitAmbiguity:
                    commitAmbiguity.Arm();
                    break;
                case PreCommitFailureInterceptor preCommitFailure:
                    preCommitFailure.Arm(() => Task.CompletedTask);
                    break;
            }

            var info = new FileInfo(archivePath);
            var processor = new ScanGalleryProcessor(
                new ZipGalleryReader(new ZipFileReader()),
                new ScanFolderResolver(NullLogger.Instance),
                NullLogger.Instance);
            var result = await processor.ProcessAsync(
                db,
                archivePath,
                galleryId: null,
                ct,
                new FileStat(info.Length, info.LastWriteTimeUtc, info.LastWriteTimeUtc),
                parentFolderId: folder.Id,
                contentChanged: true);

            db.ChangeTracker.Clear();
            var persisted = await db.Galleries
                .Include(item => item.Files)
                .Include(item => item.ImageGalleries)
                .ThenInclude(item => item.Image)
                .ThenInclude(image => image!.Files)
                .SingleAsync(item => item.Id == gallery.Id, ct);
            Assert.Equal(gallery.Id, result.Id);
            Assert.Equal(originalGalleryFileId, Assert.Single(persisted.Files).Id);
            var replacement = Assert.Single(persisted.ImageGalleries).Image!;
            Assert.Equal("replacement", replacement.Title);
            Assert.Equal("replacement.png", Assert.Single(replacement.Files).Basename);
            Assert.Single(await db.Images.ToArrayAsync(ct));
            Assert.Single(await db.ImageFiles.ToArrayAsync(ct));
            Assert.Single(await db.Set<ImageGallery>().ToArrayAsync(ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void CreateArchive(string path, string entryName)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        using var entry = archive.CreateEntry(entryName).Open();
        entry.Write([1, 2, 3]);
    }
}
