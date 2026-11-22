using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class EmbeddingAnnIndexReconcilerTests
{
    private const string SemanticD3 = "ix_embeddings_visual_semantic_asset_hnsw_d3";
    private const string SemanticD2 = "ix_embeddings_visual_semantic_asset_hnsw_d2";
    private const string AudioD2 = "ix_embeddings_audio_asset_hnsw_d2";

    [Fact]
    public async Task ReconcileAsync_WithoutEmbeddings_CreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal(0, result.DesiredCount);
        Assert.Empty(result.Created);
        Assert.Empty(await ReadAnnIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_BuildsOneIndexPerCoreFamilyAndDimension_AndThenNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            db.Embeddings.AddRange(
                Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f),
                Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 2, 0f, 1f, 0f),
                Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 1f, 0f),
                Section(Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 0f, 1f), 3),
                // Not a core-owned family: core does not index it.
                Asset(EmbeddingModality.Face, "face.embed.v1", "face.v1", 1, 1f, 0f, 0f, 0f));
            await db.SaveChangesAsync(ct);
        }

        var reconciler = CreateReconciler(database);
        var first = await reconciler.ReconcileAsync(ct);
        var second = await reconciler.ReconcileAsync(ct);
        var third = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal([AudioD2, SemanticD3], first.Created.Order(StringComparer.Ordinal));
        Assert.Empty(first.Failed);
        Assert.Empty(second.Created);
        Assert.Empty(third.Created);
        Assert.Equal([AudioD2, SemanticD3], await ReadAnnIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_FamilyWithOnlySectionRows_GetsNoIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            db.Embeddings.Add(Section(Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 1f, 0f), 1));
            await db.SaveChangesAsync(ct);
        }

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal(0, result.DesiredCount);
        Assert.Empty(await ReadAnnIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_IndexesEachDimension_AndRowsOfANewDimensionStillInsert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            db.Embeddings.AddRange(
                Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f),
                // The same family after a model change to a smaller embedding.
                Asset(EmbeddingModality.Visual, "visual.semantic.small.v1", EmbeddingKinds.VisualSemanticFamily, 2, 1f, 0f));
            await db.SaveChangesAsync(ct);
        }

        var result = await CreateReconciler(database).ReconcileAsync(ct);
        Assert.Equal([SemanticD2, SemanticD3], result.Created.Order(StringComparer.Ordinal));

        // Each index restricts "Dim", so a row of a dimension no index casts to is not evaluated by any.
        await using (var db = database.CreateContext())
        {
            db.Embeddings.Add(Asset(EmbeddingModality.Visual, "visual.semantic.large.v1", EmbeddingKinds.VisualSemanticFamily, 3, 1f, 0f, 0f, 0f));
            await db.SaveChangesAsync(ct);
        }
    }

    [Fact]
    public async Task ReconcileAsync_ValidLegacyIndexAtTheSameDimension_CountsAsCovered()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            db.Embeddings.Add(Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f));
            await db.SaveChangesAsync(ct);
            // The index V1_0 builds when embeddings exist at migration time.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE INDEX ix_embeddings_visual_semantic_asset_hnsw ON embeddings
                USING hnsw ((("Vector")::vector(3)) vector_cosine_ops)
                WHERE "Modality" = 1 AND "KindFamily" = 'semantic.v1' AND "SectionIndex" = 0
                """, ct);
        }

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal(1, result.DesiredCount);
        Assert.Empty(result.Created);
        Assert.Equal(["ix_embeddings_visual_semantic_asset_hnsw"], await ReadAnnIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_WhileAnotherPassHoldsTheLock_Skips()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            db.Embeddings.Add(Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f));
            await db.SaveChangesAsync(ct);
        }

        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync(ct);
        await using (var command = new NpgsqlCommand("SELECT pg_advisory_lock($1)", holder))
        {
            command.Parameters.AddWithValue(EmbeddingAnnIndexReconciler.AdvisoryLockKey);
            await command.ExecuteNonQueryAsync(ct);
        }

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Same(EmbeddingAnnIndexReconcileResult.Skipped, result);
        Assert.Empty(await ReadAnnIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task KnnAsync_UsesTheBuiltIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await using (var db = database.CreateContext())
        {
            for (var hostId = 1; hostId <= 20; hostId++)
                db.Embeddings.Add(Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, hostId, hostId, 1f, 0f));
            await db.SaveChangesAsync(ct);
        }
        await CreateReconciler(database).ReconcileAsync(ct);

        await using var search = database.CreateContext();
        await search.Database.OpenConnectionAsync(ct);
        // A 20-row table is cheaper to scan, so make the planner show whether the index matches at all.
        await search.Database.ExecuteSqlRawAsync("SET enable_seqscan = off", ct);
        var results = await new EmbeddingService(search, []).KnnAsync(
            new Vector(new[] { 1f, 1f, 0f }),
            5,
            new EmbeddingSearchOptions
            {
                HostType = EmbeddingHostType.Video,
                Modality = EmbeddingModality.Visual,
                KindFamily = EmbeddingKinds.VisualSemanticFamily,
                SectionIndex = 0,
            },
            ct);
        await search.Database.ExecuteSqlRawAsync("SELECT pg_stat_force_next_flush()", ct);

        Assert.Equal(5, results.Count);
        Assert.True(await ReadIndexScansAsync(database, SemanticD3, ct) > 0, "the KNN query did not use the ANN index");
    }

    [Theory]
    [InlineData("CREATE INDEX x ON public.embeddings USING hnsw (((\"Vector\")::vector(1024)) vector_cosine_ops) WHERE ...", 1024)]
    [InlineData("CREATE INDEX x ON public.embeddings USING btree (\"Kind\", \"Dim\")", null)]
    public void CastDimensions_ReadsTheDimensionTheIndexCastsTo(string definition, int? expected)
        => Assert.Equal(expected, EmbeddingAnnIndexReconciler.CastDimensions(definition));

    [Fact]
    public void BuildIndexSpec_RejectsDimensionsHnswCannotIndex()
    {
        var family = EmbeddingAnnIndexReconciler.Families[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbeddingAnnIndexReconciler.BuildIndexSpec(family, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbeddingAnnIndexReconciler.BuildIndexSpec(family, EmbeddingAnnIndexReconciler.MaxIndexedDimensions + 1));
        Assert.Equal("ix_embeddings_visual_semantic_asset_hnsw_d768", EmbeddingAnnIndexReconciler.BuildIndexSpec(family, 768).Name);
    }

    private static EmbeddingAnnIndexReconciler CreateReconciler(CovePostgresDatabase database)
        => new(database.ConnectionString, NullLogger<EmbeddingAnnIndexReconciler>.Instance);

    private static Embedding Asset(EmbeddingModality modality, string kind, string kindFamily, int hostId, params float[] vector) => new()
    {
        HostType = EmbeddingHostType.Video,
        HostId = hostId,
        Kind = kind,
        KindFamily = kindFamily,
        Modality = modality,
        Dim = vector.Length,
        Vector = new Vector(vector),
        SectionIndex = 0,
        SourceKey = "test",
    };

    private static Embedding Section(Embedding embedding, int sectionIndex)
    {
        embedding.SectionIndex = sectionIndex;
        return embedding;
    }

    private static async Task<IReadOnlyList<string>> ReadAnnIndexNamesAsync(CovePostgresDatabase database, CancellationToken ct)
    {
        await using var command = database.DataSource.CreateCommand("""
            SELECT index_class.relname
            FROM pg_index
            JOIN pg_class AS index_class ON index_class.oid = pg_index.indexrelid
            JOIN pg_am ON pg_am.oid = index_class.relam
            WHERE pg_index.indrelid = 'public.embeddings'::regclass AND pg_am.amname = 'hnsw'
            ORDER BY index_class.relname
            """);
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            names.Add(reader.GetString(0));
        return names;
    }

    private static async Task<long> ReadIndexScansAsync(CovePostgresDatabase database, string indexName, CancellationToken ct)
    {
        await using var command = database.DataSource.CreateCommand(
            "SELECT idx_scan FROM pg_stat_user_indexes WHERE indexrelname = $1");
        command.Parameters.AddWithValue(indexName);
        return (long)(await command.ExecuteScalarAsync(ct) ?? 0L);
    }
}
