using Cove.Core.Entities;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class CoveContextDenormalizedIdArrayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_NewMediaGraphs_StoreGeneratedPerformerAndTagIds(bool saveAsynchronously)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var performer = new Performer { Name = "New performer" };
        var tag = new Tag { Name = "New tag" };
        var video = new Video { Title = "New video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });
        var image = new Image { Title = "New image" };
        image.ImagePerformers.Add(new ImagePerformer { Performer = performer });
        image.ImageTags.Add(new ImageTag { Tag = tag });
        var gallery = new Gallery { Title = "New gallery" };
        gallery.GalleryPerformers.Add(new GalleryPerformer { Performer = performer });
        gallery.GalleryTags.Add(new GalleryTag { Tag = tag });
        context.AddRange(video, image, gallery);

        if (saveAsynchronously)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var savedVideo = await context.Videos.SingleAsync();
        var savedImage = await context.Images.SingleAsync();
        var savedGallery = await context.Galleries.SingleAsync();
        Assert.Equal([performer.Id], savedVideo.PerformerIds);
        Assert.Equal([tag.Id], savedVideo.TagIds);
        Assert.Equal([performer.Id], savedImage.PerformerIds);
        Assert.Equal([tag.Id], savedImage.TagIds);
        Assert.Equal([performer.Id], savedGallery.PerformerIds);
        Assert.Equal([tag.Id], savedGallery.TagIds);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveChanges_ExistingVideoWithNewRelationships_ReplacesTemporaryIds(bool saveAsynchronously)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connection)
            .Options;

        await using var context = new CoveContext(options);
        await context.Database.EnsureCreatedAsync();

        var existingPerformer = new Performer { Name = "Existing performer" };
        var existingTag = new Tag { Name = "Existing tag" };
        var video = new Video { Title = "Existing video" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = existingPerformer });
        video.VideoTags.Add(new VideoTag { Tag = existingTag });
        context.Videos.Add(video);
        await context.SaveChangesAsync();

        var performer = new Performer { Name = "New performer" };
        var tag = new Tag { Name = "New tag" };
        video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
        video.VideoTags.Add(new VideoTag { Tag = tag });

        if (saveAsynchronously)
            await context.SaveChangesAsync();
        else
            context.SaveChanges();
        context.ChangeTracker.Clear();

        var saved = await context.Videos.SingleAsync();
        Assert.Equal([existingPerformer.Id, performer.Id], saved.PerformerIds);
        Assert.Equal([existingTag.Id, tag.Id], saved.TagIds);
    }

}
