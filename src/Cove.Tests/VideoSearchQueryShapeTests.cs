using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class VideoSearchQueryShapeTests
{
    [Fact]
    public void RelationalSearch_UnionsRelationshipIdsInsteadOfDistinctVideoRows()
    {
        using var db = CreatePostgresContext();
        var repository = new VideoRepository(db);

        var sql = repository.ApplyVideoSearch(db.Videos, "anal sex")
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains("FROM video_tags AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM video_performers AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM files AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Captions\",", sql, StringComparison.Ordinal);
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
