using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class VideoSearchQueryShapeTests
{
    [Fact]
    public void ResolvedTagSearch_UsesIndexedArraysWithoutRepeatedRelationshipSubqueries()
    {
        using var db = CreatePostgresContext();
        var search = new VideoTextSearch(db, "needle", [new("needle", [11], [11], [22], [22], [], [], [], [], [], [])],
            pathVideos: [33]);
        var sql = search.Order(search.Apply(db.Videos))
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains("&&", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM video_tags AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM video_performers AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ts_rank", sql, StringComparison.Ordinal);
        Assert.Contains("v.\"Id\" = ANY (", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM files AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT DISTINCT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Captions\",", sql, StringComparison.Ordinal);
    }

    // Past the candidate cap the search checks each video's files with a correlated EXISTS. An
    // uncorrelated `Id IN (files subquery)` beside the OR was planned as a per-row scan of every
    // matching file and did not finish on a two-million-video library.
    [Fact]
    public void PathFallback_WithoutResolvedCandidates_ChecksEachVideosFiles()
    {
        using var db = CreatePostgresContext();
        var search = new VideoTextSearch(db, "red blue", [
            new("red", [], [], [], [], [], [], [], [], [], []),
            new("blue", [], [], [], [], [], [], [], [], [], []),
        ]);
        var sql = search.Apply(db.Videos).Select(video => video.Id).ToQueryString();

        Assert.Contains("FROM files AS", sql, StringComparison.Ordinal);
        Assert.Contains("EXISTS (", sql, StringComparison.Ordinal);
        Assert.Contains("f.\"VideoId\" = v.\"Id\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("v.\"Id\" IN (", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void GalleryAndGroupMatches_TestResolvedVideoIdsWithoutLinkTableSubqueries()
    {
        using var db = CreatePostgresContext();
        var galleryMembers = new[] { (Owner: 31, Video: 101), (Owner: 32, Video: 102) }.ToLookup(row => row.Owner, row => row.Video);
        var groupMembers = new[] { (Owner: 41, Video: 201), (Owner: 42, Video: 202) }.ToLookup(row => row.Owner, row => row.Video);
        var search = new VideoTextSearch(db, "needle", [new("needle", [], [], [], [], [], [], [31], [31, 32], [41], [41, 42])],
            galleryMembers, groupMembers);
        var sql = search.Order(search.Apply(db.Videos))
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains("v.\"Id\" = ANY (", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM video_galleries AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM group_items AS", sql, StringComparison.Ordinal);
    }

    // Above VideoTextSearch.MemberSubqueryThreshold the member ids are not loaded; the search then tests
    // the link tables as it did before, which is slower but bounded in parameter size.
    [Fact]
    public void GalleryAndGroupMatches_WithoutResolvedMembers_TestTheLinkTables()
    {
        using var db = CreatePostgresContext();
        var search = new VideoTextSearch(db, "needle", [new("needle", [], [], [], [], [], [], [31], [31, 32], [41], [41, 42])]);
        var sql = search.Apply(db.Videos).Select(video => video.Id).ToQueryString();

        Assert.Contains("FROM video_galleries AS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM group_items AS", sql, StringComparison.Ordinal);
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

    public static TheoryData<MultiIdCriterion, string> PerformerOccurrenceTagCriteria => new()
    {
        { new MultiIdCriterion { Modifier = CriterionModifier.IsNull }, "v.\"Id\" NOT IN (" },
        { new MultiIdCriterion { Modifier = CriterionModifier.Excludes, Value = [21] }, "v.\"Id\" NOT IN (" },
        { new MultiIdCriterion { Modifier = CriterionModifier.IncludesAll, Excludes = [21] }, "v.\"Id\" NOT IN (" },
        { new MultiIdCriterion { Modifier = CriterionModifier.NotNull }, "AND EXISTS (" },
    };

    // Correlated NOT EXISTS over tag_applications is estimated at a row or two and plans as a nested-loop anti join;
    // exclusions must stay NOT IN so PostgreSQL hashes the application host ids instead.
    [Theory]
    [MemberData(nameof(PerformerOccurrenceTagCriteria))]
    public void PerformerOccurrenceTagCriterion_TranslatesExclusionsToHashableNotIn(MultiIdCriterion criterion, string expectedPredicate)
    {
        using var db = CreatePostgresContext();
        var sql = PerformerOccurrenceTagQuery.Apply(db, db.Videos, Cove.Core.Entities.AffinityHostType.Video, criterion, [])
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains(expectedPredicate, sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT EXISTS", sql, StringComparison.Ordinal);
        Assert.Contains("FROM tag_applications AS", sql, StringComparison.Ordinal);
        Assert.Contains("\"ContextType\" = 'performer'", sql, StringComparison.Ordinal);
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
