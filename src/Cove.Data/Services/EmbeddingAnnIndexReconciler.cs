using System.Globalization;
using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cove.Data.Services;

/// <summary>
/// Creates the asset-level (<c>SectionIndex = 0</c>) HNSW indexes over <c>embeddings</c> that
/// <see cref="EmbeddingService"/>'s KNN query is written to use, for each core-owned embedding family and
/// each dimension that family has rows in.
/// </summary>
/// <remarks>
/// The V1_0 migration only builds these indexes when embeddings already exist at migration time, so a
/// library that runs AI analysis later would otherwise never get them. Each index built here also
/// restricts <c>"Dim"</c>: the index casts the vector to one dimension, and a row of another dimension
/// that matched the predicate would fail to insert. The KNN query emits <c>"Dim" = N</c> as a literal, so
/// the planner still matches it. A valid index from V1_0 counts for its family at the dimension it casts
/// to, so existing installs are left as they are.
/// </remarks>
public sealed partial class EmbeddingAnnIndexReconciler
{
    /// <summary>pgvector's HNSW limit for the <c>vector</c> type.</summary>
    public const int MaxIndexedDimensions = 2000;

    /// <summary>
    /// How long a failed build waits before it is tried again. Each attempt reads the whole table twice,
    /// so a build that cannot succeed (pgvector without HNSW, too little memory) must not repeat every pass.
    /// </summary>
    public static readonly TimeSpan RetryFailedBuildAfter = TimeSpan.FromHours(6);

    internal const long AdvisoryLockKey = 0x434F5645414E4E31; // COVEANN1

    /// <summary>The embedding families core owns the ANN indexes for, with the index names V1_0 used.</summary>
    public static readonly IReadOnlyList<EmbeddingAnnFamily> Families =
    [
        new(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticFamily, "visual_semantic"),
        new(EmbeddingModality.Visual, EmbeddingKinds.VisualFeatureFamily, "visual_feature"),
        new(EmbeddingModality.Audio, "audio.v1", "audio"),
    ];

    private readonly string _connectionString;
    private readonly ILogger<EmbeddingAnnIndexReconciler> _logger;

    // (Kind, Dim) pairs already resolved to the family they belong to, or to none, so later passes only
    // look at pairs that are new. Only touched under the advisory lock by one pass at a time.
    private readonly Dictionary<(string Kind, int Dim), EmbeddingAnnIndexSpec?> _resolvedPairs = [];
    private readonly Dictionary<string, DateTimeOffset> _retryFailedAt = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    public EmbeddingAnnIndexReconciler(
        string connectionString,
        ILogger<EmbeddingAnnIndexReconciler> logger,
        TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Enlist = false,
            Multiplexing = false,
            Pooling = false,
            ApplicationName = "Cove embedding ANN index reconciler",
        }.ConnectionString;
        _logger = logger;
    }

    /// <summary>
    /// Builds every missing index. Another instance that already holds the lock makes this a no-op. A
    /// family whose build fails is logged and skipped; the others still get built.
    /// </summary>
    public async Task<EmbeddingAnnIndexReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = CoveNpgsql.CreateDedicatedConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        if (!await TryAcquireLockAsync(connection, cancellationToken))
            return EmbeddingAnnIndexReconcileResult.Skipped;

        try
        {
            var desired = await ReadDesiredIndexesAsync(connection, cancellationToken);
            var existing = await ReadExistingIndexesAsync(connection, cancellationToken);

            var created = new List<string>();
            var failed = new List<string>();
            foreach (var spec in desired.OrderBy(spec => spec.Name, StringComparer.Ordinal))
            {
                if (IsCovered(spec, existing))
                    continue;
                if (_retryFailedAt.TryGetValue(spec.Name, out var retryAt) && _timeProvider.GetUtcNow() < retryAt)
                    continue;

                if (existing.TryGetValue(spec.Name, out var leftover))
                {
                    // An interrupted CREATE INDEX CONCURRENTLY leaves an invalid index that is still
                    // updated on every write but never used. Remove it and build again.
                    _logger.LogInformation("Removing incomplete embedding ANN index {IndexName}", leftover.Name);
                    await DropIndexAsync(connection, leftover.Name, cancellationToken);
                }

                if (await TryCreateIndexAsync(connection, spec, cancellationToken))
                {
                    _retryFailedAt.Remove(spec.Name);
                    created.Add(spec.Name);
                }
                else
                {
                    _retryFailedAt[spec.Name] = _timeProvider.GetUtcNow() + RetryFailedBuildAfter;
                    failed.Add(spec.Name);
                }
            }

            return new EmbeddingAnnIndexReconcileResult(desired.Count, created, failed);
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    /// <summary>The managed index for <paramref name="family"/> at <paramref name="dimensions"/>.</summary>
    public static EmbeddingAnnIndexSpec BuildIndexSpec(EmbeddingAnnFamily family, int dimensions)
    {
        if (dimensions is < 1 or > MaxIndexedDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, $"HNSW indexes support 1 to {MaxIndexedDimensions} dimensions.");
        return new EmbeddingAnnIndexSpec(family, dimensions, $"ix_embeddings_{family.NameStem}_asset_hnsw_d{dimensions}");
    }

    /// <summary>
    /// The (family, dimension) pairs that have asset-level rows. The distinct (Kind, Dim) pairs come from
    /// a loose index scan of <c>IX_embeddings_Kind_Dim</c>, which reads one index entry per pair, so a pass
    /// that finds nothing new stays cheap on a table of any size.
    /// </summary>
    private async Task<IReadOnlyCollection<EmbeddingAnnIndexSpec>> ReadDesiredIndexesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var pairs = new List<(string Kind, int Dim)>();
        await using (var command = CreateCommand(connection, """
            WITH RECURSIVE pairs AS (
                (SELECT "Kind", "Dim" FROM public.embeddings ORDER BY "Kind", "Dim" LIMIT 1)
                UNION ALL
                SELECT next_pair."Kind", next_pair."Dim"
                FROM pairs
                CROSS JOIN LATERAL (
                    SELECT "Kind", "Dim"
                    FROM public.embeddings AS embedding
                    WHERE (embedding."Kind", embedding."Dim") > (pairs."Kind", pairs."Dim")
                    ORDER BY embedding."Kind", embedding."Dim"
                    LIMIT 1
                ) AS next_pair
            )
            SELECT "Kind", "Dim" FROM pairs;
            """))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                pairs.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        var desired = new HashSet<EmbeddingAnnIndexSpec>();
        foreach (var pair in pairs)
        {
            if (!_resolvedPairs.TryGetValue(pair, out var spec))
            {
                spec = await ResolvePairAsync(connection, pair.Kind, pair.Dim, cancellationToken);
                _resolvedPairs[pair] = spec;
            }
            if (spec != null)
                desired.Add(spec);
        }
        return desired;
    }

    /// <summary>
    /// Finds which managed family a (Kind, Dim) pair belongs to. A kind belongs to one family, so a single
    /// row of the pair says which. The family must also have asset-level rows at that dimension, since
    /// those are all the index covers.
    /// </summary>
    private async Task<EmbeddingAnnIndexSpec?> ResolvePairAsync(
        NpgsqlConnection connection,
        string kind,
        int dimensions,
        CancellationToken cancellationToken)
    {
        int modality;
        string? kindFamily;
        await using (var command = CreateCommand(connection, """
            SELECT "Modality", "KindFamily"
            FROM public.embeddings
            WHERE "Kind" = @kind AND "Dim" = @dim
            LIMIT 1;
            """))
        {
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("dim", dimensions);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return null;
            modality = reader.GetInt32(0);
            kindFamily = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var family = Families.FirstOrDefault(candidate =>
            (int)candidate.Modality == modality && string.Equals(candidate.KindFamily, kindFamily, StringComparison.Ordinal));
        if (family == null)
            return null;

        if (dimensions is < 1 or > MaxIndexedDimensions)
        {
            _logger.LogWarning(
                "Embeddings of kind {Kind} have {Dimensions} dimensions; pgvector's HNSW index supports at most {MaxDimensions}, so {KindFamily} searches at that size stay exact",
                kind, dimensions, MaxIndexedDimensions, family.KindFamily);
            return null;
        }

        await using (var command = CreateCommand(connection, """
            SELECT EXISTS (
                SELECT 1
                FROM public.embeddings
                WHERE "Kind" = @kind AND "Dim" = @dim
                  AND "Modality" = @modality AND "KindFamily" = @family AND "SectionIndex" = 0
            );
            """))
        {
            command.Parameters.AddWithValue("kind", kind);
            command.Parameters.AddWithValue("dim", dimensions);
            command.Parameters.AddWithValue("modality", (int)family.Modality);
            command.Parameters.AddWithValue("family", family.KindFamily);
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)
                return null;
        }

        return BuildIndexSpec(family, dimensions);
    }

    private static async Task<Dictionary<string, ExistingIndex>> ReadExistingIndexesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ExistingIndex>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, """
            SELECT index_class.relname,
                   index_metadata.indisvalid,
                   index_metadata.indisready,
                   pg_get_indexdef(index_class.oid)
            FROM pg_index AS index_metadata
            JOIN pg_class AS index_class ON index_class.oid = index_metadata.indexrelid
            JOIN pg_class AS table_class ON table_class.oid = index_metadata.indrelid
            JOIN pg_namespace AS table_namespace ON table_namespace.oid = table_class.relnamespace
            WHERE table_namespace.nspname = 'public'
              AND table_class.relname = 'embeddings'
              AND left(index_class.relname, char_length('ix_embeddings_')) = 'ix_embeddings_';
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var index = new ExistingIndex(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3));
            result[index.Name] = index;
        }
        return result;
    }

    private static bool IsCovered(EmbeddingAnnIndexSpec spec, IReadOnlyDictionary<string, ExistingIndex> existing)
    {
        if (existing.TryGetValue(spec.Name, out var managed) && managed.IsUsable)
            return true;

        // V1_0's index has no "Dim" condition; it serves the dimension its expression casts to.
        return existing.TryGetValue(spec.Family.LegacyIndexName, out var legacy)
            && legacy.IsUsable
            && CastDimensions(legacy.Definition) == spec.Dimensions;
    }

    internal static int? CastDimensions(string indexDefinition)
    {
        var match = VectorCastPattern().Match(indexDefinition);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private async Task<bool> TryCreateIndexAsync(
        NpgsqlConnection connection,
        EmbeddingAnnIndexSpec spec,
        CancellationToken cancellationToken)
    {
        // Every value below is a fixed internal constant or an integer, never user input. The predicate
        // has to use literals so the planner can match it against the literals EmbeddingService emits.
        var sql = $"""
            CREATE INDEX CONCURRENTLY IF NOT EXISTS {QuoteIdentifier(spec.Name)}
            ON public.embeddings
            USING hnsw ((("Vector")::vector({spec.Dimensions})) vector_cosine_ops)
            WHERE "Modality" = {(int)spec.Family.Modality}
              AND "KindFamily" = '{spec.Family.KindFamily}'
              AND "SectionIndex" = 0
              AND "Dim" = {spec.Dimensions};
            """;

        _logger.LogInformation(
            "Building embedding ANN index {IndexName} for {KindFamily} at {Dimensions} dimensions; searches stay exact until it is ready",
            spec.Name, spec.Family.KindFamily, spec.Dimensions);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            await using (var command = CreateCommand(connection, sql))
                await command.ExecuteNonQueryAsync(cancellationToken);

            var built = (await ReadExistingIndexesAsync(connection, cancellationToken)).GetValueOrDefault(spec.Name);
            if (built is not { IsUsable: true })
                throw new InvalidOperationException($"PostgreSQL did not validate embedding ANN index {spec.Name}.");

            _logger.LogInformation(
                "Built embedding ANN index {IndexName} in {Elapsed}",
                spec.Name, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                exception,
                "Could not build embedding ANN index {IndexName}; {KindFamily} searches stay exact. Trying again in {RetryAfter}",
                spec.Name, spec.Family.KindFamily, RetryFailedBuildAfter);
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var leftover = (await ReadExistingIndexesAsync(connection, cleanupTimeout.Token)).GetValueOrDefault(spec.Name);
                if (leftover is { IsUsable: false })
                    await DropIndexAsync(connection, spec.Name, cleanupTimeout.Token);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Could not remove incomplete embedding ANN index {IndexName}", spec.Name);
            }
            return false;
        }
    }

    private static async Task DropIndexAsync(NpgsqlConnection connection, string indexName, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, $"DROP INDEX CONCURRENTLY IF EXISTS public.{QuoteIdentifier(indexName)};");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> TryAcquireLockAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, "SELECT pg_try_advisory_lock(@key);");
        command.Parameters.AddWithValue("key", AdvisoryLockKey);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task ReleaseLockAsync(NpgsqlConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
            return;
        try
        {
            await using var command = CreateCommand(connection, "SELECT pg_advisory_unlock(@key);");
            command.Parameters.AddWithValue("key", AdvisoryLockKey);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Could not explicitly release the embedding ANN index advisory lock");
        }
    }

    // Index builds on a large table take minutes, so no command timeout.
    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
        => new(sql, connection) { CommandTimeout = 0 };

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    [GeneratedRegex(@"::vector\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex VectorCastPattern();

    private sealed record ExistingIndex(string Name, bool IsValid, bool IsReady, string Definition)
    {
        public bool IsUsable => IsValid && IsReady;
    }
}

/// <param name="NameStem">The part of V1_0's index name between <c>ix_embeddings_</c> and <c>_asset_hnsw</c>.</param>
public sealed record EmbeddingAnnFamily(EmbeddingModality Modality, string KindFamily, string NameStem)
{
    public string LegacyIndexName => $"ix_embeddings_{NameStem}_asset_hnsw";
}

public sealed record EmbeddingAnnIndexSpec(EmbeddingAnnFamily Family, int Dimensions, string Name);

public sealed record EmbeddingAnnIndexReconcileResult(int DesiredCount, IReadOnlyList<string> Created, IReadOnlyList<string> Failed)
{
    public static EmbeddingAnnIndexReconcileResult Skipped { get; } = new(0, [], []);
}
