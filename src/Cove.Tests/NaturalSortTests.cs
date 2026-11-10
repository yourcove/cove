using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Cove.Tests;

public class NaturalSortTests
{
    [Fact]
    public void KeyTranslatesToNaturalCollationOnPostgres()
    {
        using var context = CreatePostgresContext();

        var sql = context.Studios.IgnoreQueryFilters()
            .OrderBy(studio => NaturalSort.Key(studio.Name))
            .Select(studio => studio.Id)
            .ToQueryString();

        Assert.Contains($"ORDER BY s.\"Name\" COLLATE {NaturalSort.CollationName}", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CompoundStringClauseUsesNaturalCollationOnPostgres()
    {
        using var context = CreatePostgresContext();
        var compound = CompoundSortQuery<Video>.Create(
            context,
            context.Videos.IgnoreQueryFilters(),
            userId: null,
            affinityHostType: null,
            ratingHostType: null,
            includeAffinity: false,
            includeRating: false);
        compound.Append(video => video.UpdatedAt, descending: true);
        compound.Append(video => video.Title, descending: false);

        var sql = compound.Finish(video => video.Id, descending: true).Select(video => video.Id).ToQueryString();

        Assert.Contains($"v.\"Title\" COLLATE {NaturalSort.CollationName}", sql, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"UpdatedAt\" COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void PostgresModelDeclaresTheNaturalCollation()
    {
        using var context = CreatePostgresContext();

        var script = context.Database.GenerateCreateScript();

        Assert.Contains($"CREATE COLLATION {NaturalSort.CollationName}", script, StringComparison.Ordinal);
        Assert.Contains(NaturalSort.IcuLocale, script, StringComparison.Ordinal);
        Assert.Contains($"CREATE INDEX \"IX_images_MinPath_natural\" ON images (\"MinPath\" COLLATE {NaturalSort.CollationName})", script, StringComparison.Ordinal);
    }

    [Fact]
    [RequiresPostgres]
    public async Task KeyOrdersDigitRunsByValueOnPostgres()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using var context = database.CreateContext();
        context.Studios.AddRange(
            new Studio { Name = "b" },
            new Studio { Name = "Studio 10" },
            new Studio { Name = "a" },
            new Studio { Name = "Studio 2" });
        await context.SaveChangesAsync(ct);

        var names = await context.Studios.IgnoreQueryFilters()
            .OrderBy(studio => NaturalSort.Key(studio.Name))
            .Select(studio => studio.Name)
            .ToListAsync(ct);

        Assert.Equal(["a", "b", "Studio 2", "Studio 10"], names);
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
