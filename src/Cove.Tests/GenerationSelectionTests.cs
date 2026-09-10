using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class GenerationSelectionTests
{
    [Fact]
    public async Task VideoScopePreservesPathRulesAndExplicitIdPrecedenceAcrossPages()
    {
        await using var db = await CreateContextAsync();
        var outside = new Folder { Path = "/library/selected-sibling" };
        var inside = new Folder { Path = "/LIBRARY/Selected/Été" };
        db.Folders.AddRange(outside, inside);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var videos = Enumerable.Range(0, 601).Select(index => new Video
        {
            Title = $"video {index}",
            Files = [new VideoFile { ParentFolder = index == 600 ? inside : outside, Basename = $"{index}.mp4" }],
        }).ToArray();
        db.Videos.AddRange(videos);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var selected = await FlattenAsync(GenerationSelection.VideosAsync(db,
            new GenerateOptionsDto { Paths = ["\\library\\selected\\été\\"] }, TestContext.Current.CancellationToken));
        Assert.Equal([videos[^1].Id], selected);
        Assert.Empty(db.ChangeTracker.Entries());

        selected = await FlattenAsync(GenerationSelection.VideosAsync(db,
            new GenerateOptionsDto { VideoIds = [videos[0].Id], Paths = ["/missing"] }, TestContext.Current.CancellationToken));
        Assert.Equal([videos[0].Id], selected);
        selected = await FlattenAsync(GenerationSelection.VideosAsync(db, new GenerateOptionsDto(), TestContext.Current.CancellationToken));
        Assert.Equal(videos.Select(video => video.Id), selected);
    }

    [Fact]
    public async Task GalleryScopeMatchesFoldersAndArchiveBackingFiles()
    {
        await using var db = await CreateContextAsync();
        var folderGallery = new Gallery { Title = "folder", Folder = new Folder { Path = "/selected/folder" } };
        var archiveGallery = new Gallery
        {
            Title = "archive", Folder = new Folder { Path = "/virtual/elsewhere" },
            Files = [new GalleryFile { ParentFolder = new Folder { Path = "/selected" }, Basename = "archive.zip" }],
        };
        var unrelated = new Gallery { Title = "unrelated", Folder = new Folder { Path = "/selected-sibling" } };
        db.Galleries.AddRange(folderGallery, archiveGallery, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        var selected = await FlattenAsync(GenerationSelection.GalleriesAsync(db, ["/selected/"], TestContext.Current.CancellationToken));
        Assert.Equal(new[] { folderGallery.Id, archiveGallery.Id }.Order(), selected);
        Assert.Empty(db.ChangeTracker.Entries());
    }

    [Fact]
    public async Task SelectionHonorsCancellationBetweenBatches()
    {
        await using var db = await CreateContextAsync();
        db.Videos.AddRange(Enumerable.Range(0, 600).Select(index => new Video { Title = $"video {index}" }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var batches = GenerationSelection.VideosAsync(db, new GenerateOptionsDto(), cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await batches.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await batches.MoveNextAsync());
    }

    private static async Task<int[]> FlattenAsync(IAsyncEnumerable<int[]> batches)
    {
        var ids = new List<int>();
        await foreach (var batch in batches)
        {
            Assert.InRange(batch.Length, 1, GenerationSelection.BatchSize);
            ids.AddRange(batch);
        }
        return ids.ToArray();
    }

    private static async Task<CoveContext> CreateContextAsync()
    {
        var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return db;
    }
}
