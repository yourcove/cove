using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Cove.Tests;

public class CompoundSortQueryShapeTests
{
    [Fact]
    public void CompoundEngagementSortJoinsEachUserScopedTableOnce()
    {
        using var context = CreatePostgresContext();
        var compound = CompoundSortQuery<Video>.Create(
            context,
            context.Videos.IgnoreQueryFilters(),
            userId: 42,
            AffinityHostType.Video,
            RatingHostType.Video,
            includeAffinity: true,
            includeRating: true);
        compound.AppendRating(descending: true);
        compound.AppendAffinityInt(nameof(UserEntityAffinity.ViewCount), descending: true);

        var sql = compound.Finish(video => video.Id).Take(25).Select(video => video.Id).ToQueryString();

        Assert.Equal(1, CountOccurrences(sql, "user_entity_affinities"));
        Assert.Equal(1, CountOccurrences(sql, "ratings"));
        Assert.DoesNotContain("(SELECT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompoundMetadataSortDoesNotJoinEngagementTables()
    {
        using var context = CreatePostgresContext();
        var compound = CompoundSortQuery<Video>.Create(
            context,
            context.Videos.IgnoreQueryFilters(),
            userId: 42,
            AffinityHostType.Video,
            RatingHostType.Video,
            includeAffinity: false,
            includeRating: false);
        compound.Append(video => video.UpdatedAt, descending: true);
        compound.Append(video => video.Title, descending: false);

        var sql = compound.Finish(video => video.Id).Take(25).Select(video => video.Id).ToQueryString();

        Assert.DoesNotContain("user_entity_affinities", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ratings", sql, StringComparison.Ordinal);
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

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(fragment, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += fragment.Length;
        }

        return count;
    }
}
