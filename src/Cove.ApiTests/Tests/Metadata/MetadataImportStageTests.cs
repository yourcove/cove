using Cove.ApiTests.Infrastructure;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Entities;

namespace Cove.ApiTests.Tests.Metadata;

public sealed class MetadataImportStageTests(MetadataImportPostgresFixture fixture) : IClassFixture<MetadataImportPostgresFixture>
{
    [Fact]
    public async Task StagingUsesPostgresTemporaryTables()
    {
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        using var input = new MemoryStream("{\"tags\":[{\"name\":\"temporary\"}]}"u8.ToArray());
        var stage = await MetadataImportStage.CreateAsync(db, input, TestContext.Current.CancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_class WHERE relnamespace = pg_my_temp_schema() AND relname LIKE 'cove_import_%'";
        Assert.True(Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)) > 0);
    }

    [Fact]
    public async Task BatchesAndReplaysSupportedEntities()
    {
        await using var db = await CreateContextAsync();
        var tags = Enumerable.Range(0, MetadataImportStage.BatchSize * 3 + 1)
            .Select(index => new { name = $"tag {index}" });
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { tags }));
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        for (var replay = 0; replay < 2; replay++)
        {
            var count = 0;
            await foreach (var batch in stage.ReadBatchesAsync<Tag>("tags", TestContext.Current.CancellationToken))
            {
                Assert.InRange(batch.Count, 1, MetadataImportStage.BatchSize);
                foreach (var tag in batch)
                    Assert.Equal($"tag {count++}", tag.Name);
            }
            Assert.Equal(MetadataImportStage.BatchSize * 3 + 1, count);
        }
    }

    [Fact]
    public async Task ResolvesDuplicateIdentitiesAcrossBatchesUsingFirstSpellingAndLastMetadata()
    {
        await using var db = await CreateContextAsync();
        var studios = new[] { new { name = " First studio ", details = "old" } }
            .Concat(Enumerable.Range(0, MetadataImportStage.BatchSize * 2)
                .Select(index => new { name = $"studio {index}", details = "other" }))
            .Append(new { name = "FIRST STUDIO", details = "new" });
        using var stream = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { studios }));
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        var all = new List<Studio>();
        await foreach (var batch in stage.ReadBatchesAsync<Studio>("studios", TestContext.Current.CancellationToken))
            all.AddRange(batch);
        Assert.Equal(MetadataImportStage.BatchSize * 2 + 1, all.Count);
        Assert.Equal(" First studio ", all[0].Name);
        Assert.Equal("new", all[0].Details);
    }

    [Fact]
    public async Task LastDuplicateSectionWinsIncludingNull()
    {
        await using var db = await CreateContextAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(
            "{\"tags\":[{\"name\":\"discard\"}],\"tags\":[{\"name\":\"keep\"}],\"groups\":[{}],\"groups\":null}"));
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        var tags = new List<Tag>();
        await foreach (var batch in stage.ReadBatchesAsync<Tag>("tags", TestContext.Current.CancellationToken))
            tags.AddRange(batch);
        Assert.Equal("keep", Assert.Single(tags).Name);
        await foreach (var batch in stage.ReadBatchesAsync<Group>("groups", TestContext.Current.CancellationToken))
            Assert.Fail("A final null section must clear earlier entries.");
    }
    [Theory]
    [InlineData("{\"studios\":[null],\"studios\":[]}")]
    [InlineData("{\"studios\":{},\"studios\":[]}")]
    [InlineData("{\"studios\":false,\"studios\":[]}")]
    public async Task IgnoresInvalidValuesInSupersededSections(string json)
    {
        await using var db = await CreateContextAsync();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        await foreach (var batch in stage.ReadBatchesAsync<Studio>("studios", TestContext.Current.CancellationToken))
            Assert.Fail("The final section is empty.");
    }
    [Fact]
    public async Task NormalizesExplicitNullIdentityNames()
    {
        await using var db = await CreateContextAsync();
        using var stream = new MemoryStream("{\"studios\":[{\"name\":null}],\"performers\":[{\"name\":null}]}"u8.ToArray());
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        await foreach (var batch in stage.ReadBatchesAsync<Studio>("studios", TestContext.Current.CancellationToken))
            Assert.Equal(EntityNameRules.NormalizeCanonicalName(null), Assert.Single(batch).Name);
        await foreach (var batch in stage.ReadBatchesAsync<Performer>("performers", TestContext.Current.CancellationToken))
            Assert.Equal(EntityNameRules.NormalizeCanonicalName(null), Assert.Single(batch).Name);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransactionCompletionDropsStaging(bool commit)
    {
        await using var db = await CreateContextAsync();
        using var stream = new MemoryStream("{\"tags\":[{\"name\":\"cleanup\"}]}"u8.ToArray());
        var stage = await MetadataImportStage.CreateAsync(db, stream, TestContext.Current.CancellationToken);
        await stage.BuildTargetIndexAsync(db, TestContext.Current.CancellationToken);
        if (commit) await db.Database.CommitTransactionAsync(TestContext.Current.CancellationToken);
        else await db.Database.RollbackTransactionAsync(TestContext.Current.CancellationToken);
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT count(*) FROM pg_class WHERE relnamespace=pg_my_temp_schema() AND relname LIKE 'cove_import_%'";
        Assert.Equal(0L, Convert.ToInt64(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
    }

    [Fact]
    public async Task LongOrdinalIdentitiesPreserveDeduplicationAndIndexedLookup()
    {
        await using var db = await CreateContextAsync();
        var prefix = new string('x', 4000);
        var first = prefix + "É";
        var last = prefix + "é";
        var distinct = prefix + "e\u0301";
        using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
        {
            studios = new[]
            {
                new { name = first, details = "first" },
                new { name = last, details = "last" },
                new { name = distinct, details = "distinct" },
            },
        }));
        var stage = await MetadataImportStage.CreateAsync(db, input, TestContext.Current.CancellationToken);
        await foreach (var batch in stage.ReadBatchesAsync<Studio>("studios", TestContext.Current.CancellationToken))
        {
            Assert.Equal(2, batch.Count);
            Assert.Equal(first, batch[0].Name);
            Assert.Equal("last", batch[0].Details);
            Assert.Equal(distinct, batch[1].Name);
        }
        var tags = new[] { new Tag { Id = 1, Name = first }, new Tag { Id = 2, Name = distinct } };
        await stage.AddTagsAsync(tags, TestContext.Current.CancellationToken);
        await stage.AddTagsAsync(tags, TestContext.Current.CancellationToken);
        var matches = await stage.FindTargetIdsAsync("tags", [last, distinct], TestContext.Current.CancellationToken);
        Assert.Equal(1, Assert.Single(matches[last]));
        Assert.Equal(2, Assert.Single(matches[distinct]));
    }

    private async Task<CoveContext> CreateContextAsync()
    {
        var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        return db;
    }
}
