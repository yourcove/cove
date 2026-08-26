using Cove.Core.Entities;
using Cove.Core.Interfaces;
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

    [Theory]
    [InlineData("like_counter")]
    [InlineData("last_like_at")]
    public void GalleryAggregateCompoundSortTranslatesForPostgres(string key)
    {
        using var context = CreatePostgresContext();
        var repository = new GalleryRepository(context);
        var registry = repository.CreateGalleryMultiSortRegistry(currentUserId: 42);
        var compound = CompoundSortQuery<Gallery>.Create(
            context,
            context.Galleries.IgnoreQueryFilters(),
            userId: 42,
            affinityHostType: null,
            RatingHostType.Gallery,
            includeAffinity: false,
            includeRating: false);
        registry.Apply(compound,
        [
            new SortClause(key, Cove.Core.Enums.SortDirection.Desc),
            new SortClause("date", Cove.Core.Enums.SortDirection.Desc),
        ]);

        var sql = compound.Finish(gallery => gallery.Id).Take(25).Select(gallery => gallery.Id).ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(key == "like_counter" ? "user_entity_affinities" : "interactions", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JsonCustomFieldPathFilterAndSortTranslateForPostgres()
    {
        using var context = CreatePostgresContext();
        var criterion = new CustomFieldCriterion
        {
            Key = "structured_metadata",
            JsonPath = "/profile/score",
            Type = CustomFieldTypes.Number,
            Modifier = CriterionModifier.GreaterThan,
            Value = "15",
        };

        var filterSql = context.Videos
            .IgnoreQueryFilters()
            .ApplyCustomFieldCriterion(context, CustomFieldEntityTypes.Video, criterion)
            .Select(video => video.Id)
            .ToQueryString();
        var sortSql = context.Videos
            .IgnoreQueryFilters()
            .ApplyCustomFieldSort(context, CustomFieldEntityTypes.Video, "custom-json:number:structured_metadata:%2Fprofile%2Fscore", desc: false)
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains("custom_field_json_paths", filterSql, StringComparison.Ordinal);
        Assert.Contains("\"Filterable\"", filterSql, StringComparison.Ordinal);
        Assert.Contains("public.cove_json_pointer_number", filterSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'/profile/score'", filterSql, StringComparison.Ordinal);
        Assert.Contains("\"JsonValue\" IS NOT NULL", filterSql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"Sortable\"", sortSql, StringComparison.Ordinal);
        Assert.Contains("public.cove_json_pointer_number", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'/profile/score'", sortSql, StringComparison.Ordinal);
        Assert.Contains("\"JsonValue\" IS NOT NULL", sortSql, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonTextCustomFieldFilterUsesBoundedIndexKeyWhileSortUsesFullText()
    {
        using var context = CreatePostgresContext();
        var criterion = new CustomFieldCriterion
        {
            Key = "structured_metadata",
            JsonPath = "/profile/name",
            Type = CustomFieldTypes.Text,
            Modifier = CriterionModifier.Equals,
            Value = new string('x', CustomFieldJsonDbFunctions.TextIndexKeyByteLength + 100),
        };

        var filterSql = context.Videos
            .IgnoreQueryFilters()
            .ApplyCustomFieldCriterion(context, CustomFieldEntityTypes.Video, criterion)
            .Select(video => video.Id)
            .ToQueryString();
        var sortSql = context.Videos
            .IgnoreQueryFilters()
            .ApplyCustomFieldSort(context, CustomFieldEntityTypes.Video, "custom-json:text:structured_metadata:%2Fprofile%2Fname", desc: false)
            .Select(video => video.Id)
            .ToQueryString();

        Assert.Contains("public.cove_json_pointer_text_index_key", filterSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public.cove_json_pointer_text", filterSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'/profile/name'", filterSql, StringComparison.Ordinal);
        Assert.Contains(" = ", filterSql, StringComparison.Ordinal);
        Assert.DoesNotContain("public.cove_json_pointer_text_index_key", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("public.cove_json_pointer_text", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ORDER BY", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("LEFT JOIN", sortSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("'/profile/name'", sortSql, StringComparison.Ordinal);
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
