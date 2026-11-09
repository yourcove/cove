using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
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
    public async Task KeyIsTransparentOnSqlite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var options = new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options;
        await using var context = new CoveContext(options);

        var sql = context.Studios.IgnoreQueryFilters()
            .OrderBy(studio => NaturalSort.Key(studio.Name))
            .Select(studio => studio.Id)
            .ToQueryString();

        Assert.Contains("ORDER BY \"s\".\"Name\"", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeyIsTransparentOnInMemoryProvider()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new CoveContext(options);
        context.Studios.AddRange(new Studio { Name = "b" }, new Studio { Name = "a" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var names = await context.Studios.IgnoreQueryFilters()
            .OrderBy(studio => NaturalSort.Key(studio.Name))
            .Select(studio => studio.Name)
            .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b"], names);
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
