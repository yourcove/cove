using Cove.Core.Interfaces;
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

    [Fact]
    public async Task RelatedPerformerOccurrenceTagFilter_TranslatesForPostgres()
    {
        await using var db = CreatePostgresContext();
        var query = await RelatedFilterQuery.ApplyToVideosAsync(
            db,
            db.Videos,
            new RelatedFilterCriterion<PerformerFilter>
            {
                PerformerIdsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.Includes, Value = [11] },
                PerformerOccurrenceTagsCriterion = new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Value = [21, 22] },
            },
            TestContext.Current.CancellationToken);

        var sql = query.Select(video => video.Id).ToQueryString();

        Assert.Contains("tag_applications", sql, StringComparison.Ordinal);
        Assert.Contains("ContextId", sql, StringComparison.Ordinal);
        Assert.Contains("PerformerId", sql, StringComparison.Ordinal);
        Assert.Contains("TagId", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DistinctRelatedPerformerFilter_TranslatesForPostgres()
    {
        await using var db = CreatePostgresContext();
        static RelatedFilterCriterion<PerformerFilter> Gender(string value) => new()
        {
            ObjectFilter = new PerformerFilter
            {
                GenderCriterion = new StringCriterion { Modifier = CriterionModifier.Equals, Value = value },
            },
        };

        var query = await RelatedFilterQuery.ApplyDistinctVideoPerformersAsync(
            db,
            db.Videos,
            [Gender("Male"), Gender("Female"), Gender("Female")],
            TestContext.Current.CancellationToken);
        var sql = query.Select(video => video.Id).ToQueryString();

        Assert.Contains("video_performers", sql, StringComparison.Ordinal);
        Assert.Contains("<>", sql, StringComparison.Ordinal);
        Assert.Contains("Female", sql, StringComparison.Ordinal);
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
