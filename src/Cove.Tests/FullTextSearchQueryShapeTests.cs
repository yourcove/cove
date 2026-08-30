using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class FullTextSearchQueryShapeTests
{
    [Fact]
    public void ImageRelationshipAndPathSearch_UnionsCandidateIdsInsteadOfDistinctEntityRows()
    {
        using var db = CreatePostgresContext();
        var query = new ImageRepository(db).ApplyImageSearch(db.Images, "anal sex");

        var sql = query.Select(image => image.Id).ToQueryString();

        Assert.Contains("FROM image_tags AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM image_performers AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM files AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Details\",", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedRelationshipAndPathSearch_UnionsCandidateIdsInsteadOfDistinctEntityRows()
    {
        using var db = CreatePostgresContext();
        var audioBase = db.Audios.Where(audio => audio.Id > 100);
        var text = FullTextSearchHelpers.Apply(db, audioBase, "needle", audio => audio.Title, audio => audio.Details);
        var relational = FullTextSearchHelpers.ApplyRelationalMatches(text, audioBase, "needle",
            tagSelectors: [audio => audio.AudioTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [audio => audio.AudioPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        var query = FullTextSearchHelpers.ApplyFilePathMatch(relational, audioBase, "needle", audio => audio.Files);

        var sql = query.Select(audio => audio.Id).ToQueryString();

        Assert.Contains("FROM audio_tags AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM audio_performers AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM files AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Details\",", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImageCandidateIdSearch_PreservesTextRelationshipAndPathMatchesOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using var db = new CoveContext(options);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        var folder = new Folder { Path = "/library" };
        db.Images.AddRange(
            new Image { Title = "Needle title" },
            new Image { Title = "Tag relation", ImageTags = { new ImageTag { Tag = new Tag { Name = "Needle" } } } },
            new Image { Title = "Performer relation", ImagePerformers = { new ImagePerformer { Performer = new Performer { Name = "Needle Artist" } } } },
            new Image
            {
                Title = "Path relation",
                Files = { new ImageFile { Basename = "needle.jpg", Path = "/library/needle.jpg", ParentFolder = folder, Format = "jpg" } },
            },
            new Image { Title = "Unrelated" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var imageBase = db.Images.AsNoTracking().Where(image => image.Title != "Unrelated");
        var query = new ImageRepository(db).ApplyImageSearch(imageBase, "needle");

        var titles = await query.OrderBy(image => image.Title).Select(image => image.Title!).ToArrayAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["Needle title", "Path relation", "Performer relation", "Tag relation"], titles);

        var restricted = new ImageRepository(db).ApplyImageSearch(
            db.Images.Where(image => image.Title == "Unrelated"), "needle");
        Assert.Empty(await restricted.ToArrayAsync(TestContext.Current.CancellationToken));
    }

    private static CoveContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=query_shape;Username=query_shape;Password=query_shape",
                npgsql => npgsql.UseVector())
            .Options;

        return new CoveContext(options);
    }
}
