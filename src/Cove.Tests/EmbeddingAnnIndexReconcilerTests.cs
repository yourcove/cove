using Cove.Core.Entities;
using Cove.Core.Interfaces;
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
    private const string LegacySemantic = "ix_embeddings_visual_semantic_asset_hnsw";

    // The shape V1_0 creates when embeddings exist at migration time: no "Dim" condition.
    private const string CreateLegacySemanticD3 = """
        CREATE INDEX ix_embeddings_visual_semantic_asset_hnsw ON embeddings
        USING hnsw ((("Vector")::vector(3)) vector_cosine_ops)
        WHERE "Modality" = 1 AND "KindFamily" = 'semantic.v1' AND "SectionIndex" = 0
        """;

    [Fact]
    public async Task ReconcileAsync_WithoutEmbeddings_CreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.False(result.Skipped);
        Assert.Equal(0, result.DesiredCount);
        Assert.Empty(result.Created);
        Assert.Empty(await ReadHnswIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_BuildsOneIndexPerCoreFamilyAndDimension_AndThenNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await AddAsync(database, ct,
            Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f),
            Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 2, 0f, 1f, 0f),
            Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 1f, 0f),
            Section(Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 0f, 1f), 3),
            // Not a core-owned family: core does not index it.
            Asset(EmbeddingModality.Face, "face.embed.v1", "face.v1", 1, 1f, 0f, 0f, 0f));

        var reconciler = CreateReconciler(database);
        var first = await reconciler.ReconcileAsync(ct);
        var second = await reconciler.ReconcileAsync(ct);
        var third = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal([AudioD2, SemanticD3], first.Created.Order(StringComparer.Ordinal));
        Assert.Empty(first.Failed);
        Assert.Equal(2, second.DesiredCount);
        Assert.Empty(second.Created);
        Assert.Empty(third.Created);
        Assert.Equal([AudioD2, SemanticD3], await ReadHnswIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_FamilyWithOnlySectionRows_IsCheckedAgainOnceAssetRowsCanHaveArrived()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var clock = new ManualTimeProvider();
        var reconciler = CreateReconciler(database, clock);
        await AddAsync(database, ct, Section(Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 1f, 0f), 1));

        var sectionsOnly = await reconciler.ReconcileAsync(ct);
        // The analysis stores the asset-level row after its sections.
        await AddAsync(database, ct, Asset(EmbeddingModality.Audio, "audio.embed.v1", "audio.v1", 1, 0f, 1f));
        var tooSoon = await reconciler.ReconcileAsync(ct);
        clock.Advance(EmbeddingAnnIndexReconciler.MinAssetRowRecheck);
        var rechecked = await reconciler.ReconcileAsync(ct);

        Assert.Equal(0, sectionsOnly.DesiredCount);
        Assert.Empty(tooSoon.Created);
        Assert.Equal([AudioD2], rechecked.Created);
    }

    [Fact]
    public async Task ReconcileAsync_IndexesEachDimension_AndRowsOfANewDimensionStillInsert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await AddAsync(database, ct,
            Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f),
            // The same family after a model change to a smaller embedding.
            Asset(EmbeddingModality.Visual, "visual.semantic.small.v1", EmbeddingKinds.VisualSemanticFamily, 2, 1f, 0f));

        var result = await CreateReconciler(database).ReconcileAsync(ct);
        Assert.Equal([SemanticD2, SemanticD3], result.Created.Order(StringComparer.Ordinal));

        // Each index restricts "Dim", so no index evaluates a row of a dimension it does not cast to.
        await AddAsync(database, ct,
            Asset(EmbeddingModality.Visual, "visual.semantic.large.v1", EmbeddingKinds.VisualSemanticFamily, 3, 1f, 0f, 0f, 0f));
    }

    [Fact]
    public async Task ReconcileAsync_ReplacesAValidLegacyIndex_SoRowsOfAnotherDimensionInsert()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await AddAsync(database, ct, Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f));
        await ExecuteAsync(database, CreateLegacySemanticD3, ct);
        var otherDimension = Asset(EmbeddingModality.Visual, "visual.semantic.small.v1", EmbeddingKinds.VisualSemanticFamily, 2, 1f, 0f);

        // With V1_0's index in place a row of another dimension cannot be stored.
        await using (var db = database.CreateContext())
        {
            db.Embeddings.Add(otherDimension);
            var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(ct));
            Assert.Contains("expected 3 dimensions", exception.InnerException?.Message, StringComparison.Ordinal);
        }

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal([SemanticD3], result.Created);
        Assert.Equal([LegacySemantic], result.ReplacedLegacy);
        Assert.Equal([SemanticD3], await ReadHnswIndexNamesAsync(database, ct));
        otherDimension.Id = 0;
        await AddAsync(database, ct, otherDimension);
    }

    [Fact]
    public async Task ReconcileAsync_DropsInvalidIndexes_AndRebuildsTheManagedOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await AddAsync(database, ct, Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f));
        await CreateReconciler(database).ReconcileAsync(ct);
        await ExecuteAsync(database, CreateLegacySemanticD3, ct);
        // What an interrupted CREATE INDEX CONCURRENTLY leaves behind.
        await ExecuteAsync(database, $"""
            UPDATE pg_index SET indisvalid = false
            WHERE indexrelid IN ('{SemanticD3}'::regclass, '{LegacySemantic}'::regclass)
            """, ct);

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal([LegacySemantic, SemanticD3], result.DroppedInvalid.Order(StringComparer.Ordinal));
        Assert.Equal([SemanticD3], result.Created);
        Assert.Equal([SemanticD3], await ReadHnswIndexNamesAsync(database, ct));
    }

    [Fact]
    public async Task ReconcileAsync_FailedBuild_LeavesNoIndexAndWaitsBeforeTryingAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var clock = new ManualTimeProvider();
        var reconciler = CreateReconciler(database, clock);
        // A row whose Dim disagrees with its vector: the index cannot cast it, so the build fails.
        var broken = Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f);
        broken.Dim = 3;
        await AddAsync(database, ct, broken);

        var failed = await reconciler.ReconcileAsync(ct);
        var backingOff = await reconciler.ReconcileAsync(ct);
        clock.Advance(EmbeddingAnnIndexReconciler.RetryFailedBuildAfter);
        var retried = await reconciler.ReconcileAsync(ct);

        Assert.Equal([SemanticD3], failed.Failed);
        Assert.Empty(await ReadHnswIndexNamesAsync(database, ct));
        Assert.Equal(1, backingOff.DesiredCount);
        Assert.Empty(backingOff.Failed);
        Assert.Equal([SemanticD3], retried.Failed);
    }

    [Fact]
    public async Task ReconcileAsync_WhileAnotherPassHoldsTheLock_SkipsUntilItIsReleased()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        await AddAsync(database, ct, Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, 1, 1f, 0f, 0f));
        var reconciler = CreateReconciler(database);

        await using var holder = new NpgsqlConnection(database.ConnectionString);
        await holder.OpenAsync(ct);
        await LockAsync(holder, "pg_advisory_lock", ct);
        var whileHeld = await reconciler.ReconcileAsync(ct);
        var namesWhileHeld = await ReadHnswIndexNamesAsync(database, ct);
        await LockAsync(holder, "pg_advisory_unlock", ct);
        var afterRelease = await reconciler.ReconcileAsync(ct);

        Assert.True(whileHeld.Skipped);
        Assert.Empty(namesWhileHeld);
        Assert.Equal([SemanticD3], afterRelease.Created);
    }

    [Fact]
    public async Task KnnAsync_UsesTheBuiltIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var rows = Enumerable.Range(1, 20)
            .Select(hostId => Asset(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticKind, EmbeddingKinds.VisualSemanticFamily, hostId, hostId, 1f, 0f))
            .ToArray();
        await AddAsync(database, ct, rows);
        await CreateReconciler(database).ReconcileAsync(ct);

        await using var search = database.CreateContext();
        await search.Database.OpenConnectionAsync(ct);
        // 20 rows are cheaper to scan; take the other plans away to show whether the index matches at all.
        await search.Database.ExecuteSqlRawAsync("SET enable_seqscan = off; SET enable_bitmapscan = off; SET enable_sort = off", ct);
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

    [Theory]
    [InlineData("ix_embeddings_visual_semantic_asset_hnsw", true)]
    [InlineData("ix_embeddings_audio_asset_hnsw_d192", true)]
    [InlineData("ix_embeddings_visual_feature_asset_hnsw_d1024", true)]
    [InlineData("ix_embeddings_face_asset_hnsw_d512", false)]
    [InlineData("ix_embeddings_audio_asset_hnsw_d0192", false)]
    [InlineData("IX_embeddings_Kind_Dim", false)]
    [InlineData("ix_embeddings_performance_feature_hnsw", false)]
    public void IsManagedOrLegacyName_MatchesOnlyNamesItOwns(string name, bool expected)
        => Assert.Equal(expected, EmbeddingAnnIndexReconciler.IsManagedOrLegacyName(name));

    [Fact]
    public void BuildIndexSpec_RejectsDimensionsHnswCannotIndex()
    {
        var family = EmbeddingAnnIndexReconciler.Families[0];
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbeddingAnnIndexReconciler.BuildIndexSpec(family, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EmbeddingAnnIndexReconciler.BuildIndexSpec(family, EmbeddingAnnIndexReconciler.MaxIndexedDimensions + 1));
        Assert.Equal("ix_embeddings_visual_semantic_asset_hnsw_d768", EmbeddingAnnIndexReconciler.BuildIndexSpec(family, 768).Name);
    }

    [Fact]
    public async Task ReconcileAsync_DimensionAboveTheHnswLimit_IsLeftExact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var wide = new float[EmbeddingAnnIndexReconciler.MaxIndexedDimensions + 1];
        wide[0] = 1f;
        await AddAsync(database, ct, Asset(EmbeddingModality.Visual, "visual.semantic.wide.v1", EmbeddingKinds.VisualSemanticFamily, 1, wide));

        var result = await CreateReconciler(database).ReconcileAsync(ct);

        Assert.Equal(0, result.DesiredCount);
        Assert.Empty(result.Failed);
        Assert.Empty(await ReadHnswIndexNamesAsync(database, ct));
    }

    private static EmbeddingAnnIndexReconciler CreateReconciler(CovePostgresDatabase database, TimeProvider? clock = null)
        => new(
            new CoveConfiguration { DatabaseConnectionString = database.ConnectionString },
            NullLogger<EmbeddingAnnIndexReconciler>.Instance,
            clock);

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

    private static async Task AddAsync(CovePostgresDatabase database, CancellationToken ct, params Embedding[] embeddings)
    {
        await using var db = database.CreateContext();
        db.Embeddings.AddRange(embeddings);
        await db.SaveChangesAsync(ct);
    }

    private static async Task ExecuteAsync(CovePostgresDatabase database, string sql, CancellationToken ct)
    {
        await using var command = database.DataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task LockAsync(NpgsqlConnection connection, string function, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand($"SELECT {function}($1)", connection);
        command.Parameters.AddWithValue(EmbeddingAnnIndexReconciler.AdvisoryLockKey);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<string>> ReadHnswIndexNamesAsync(CovePostgresDatabase database, CancellationToken ct)
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

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
