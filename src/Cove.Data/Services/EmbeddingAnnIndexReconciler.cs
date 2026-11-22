using System.Globalization;
using System.Text.RegularExpressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Cove.Data.Services;

/// <summary>
/// Builds the asset-level (<c>SectionIndex = 0</c>) HNSW indexes over <c>embeddings</c> that
/// <see cref="EmbeddingService"/>'s KNN query is written for: one per core-owned embedding family and
/// dimension that has asset-level rows.
/// </summary>
/// <remarks>
/// The V1_0 migration only builds these indexes when embeddings already exist at migration time, so a
/// library that runs AI analysis later would otherwise never get them.
/// <para>
/// Each index restricts <c>"Dim"</c> as well as casting the vector to that dimension. Without the
/// restriction, as in V1_0's indexes, inserting a row of the family with another dimension fails ("expected
/// N dimensions") and the index cannot be built over mixed dimensions. The KNN query emits <c>"Dim" = N</c>
/// as a literal, so the planner still matches it. Once the replacement is valid, a V1_0 index of the same
/// family and dimension is dropped.
/// </para>
/// </remarks>
internal sealed partial class EmbeddingAnnIndexReconciler
{
    /// <summary>pgvector's HNSW limit for the <c>vector</c> type.</summary>
    internal const int MaxIndexedDimensions = 2000;

    /// <summary>
    /// How long a build that failed waits before it is tried again. Each attempt reads the whole table
    /// twice, so a build that cannot succeed must not repeat every pass. Kept in memory: a restart tries
    /// again on its first pass.
    /// </summary>
    internal static readonly TimeSpan RetryFailedBuildAfter = TimeSpan.FromHours(6);

    /// <summary>
    /// Bounds for re-checking a family and dimension whose kinds exist but that has no asset-level rows yet
    /// (for example while the first analysis has stored section rows only). With no such row the check
    /// reads every row of the family, so it backs off from the first to the second.
    /// </summary>
    internal static readonly TimeSpan MinAssetRowRecheck = TimeSpan.FromMinutes(15);
    internal static readonly TimeSpan MaxAssetRowRecheck = TimeSpan.FromHours(1);

    internal const long AdvisoryLockKey = 0x434F5645414E4E31; // COVEANN1

    /// <summary>
    /// The embedding families core owns the ANN indexes for, with the name stems V1_0 used. There is no
    /// <see cref="EmbeddingKinds"/> constant for the audio family; it is the value V1_0 indexes.
    /// </summary>
    internal static readonly IReadOnlyList<EmbeddingAnnFamily> Families =
    [
        new(EmbeddingModality.Visual, EmbeddingKinds.VisualSemanticFamily, "visual_semantic"),
        new(EmbeddingModality.Visual, EmbeddingKinds.VisualFeatureFamily, "visual_feature"),
        new(EmbeddingModality.Audio, "audio.v1", "audio"),
    ];

    private readonly CoveConfiguration _configuration;
    private readonly ILogger<EmbeddingAnnIndexReconciler> _logger;
    private readonly TimeProvider _timeProvider;

    // State kept between passes. A pass runs only while holding the advisory lock, and the maintenance
    // service runs one pass at a time, so these are never used concurrently.
    // (Kind, Dim) → the managed family it belongs to, or null. A kind's family does not change.
    private readonly Dictionary<(string Kind, int Dim), EmbeddingAnnFamily?> _pairFamilies = [];
    private readonly Dictionary<string, (DateTimeOffset At, TimeSpan Delay)> _assetRowRecheck = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _retryFailedBuildAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _interruptedBuilds = new(StringComparer.Ordinal);
    private bool _buildWithoutParallelWorkers;

    /// <summary>
    /// Builds of one index interrupted in a row before it counts as failed. A lost connection is usually a
    /// restart or a restore, but a build that takes the server down with it (out of memory) would otherwise
    /// be retried every pass.
    /// </summary>
    internal const int InterruptedBuildsBeforeBackOff = 2;

    public EmbeddingAnnIndexReconciler(
        CoveConfiguration configuration,
        ILogger<EmbeddingAnnIndexReconciler> logger,
        TimeProvider? timeProvider = null)
    {
        _configuration = configuration;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Builds every missing index and drops the V1_0 indexes they replace. Returns
    /// <see cref="EmbeddingAnnIndexReconcileResult.LockHeldElsewhere"/> when another pass holds the lock. A
    /// build that fails is logged and retried after <see cref="RetryFailedBuildAfter"/>; losing the
    /// connection or being cancelled ends the pass with an exception instead, and the next pass carries on.
    /// </summary>
    public async Task<EmbeddingAnnIndexReconcileResult> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = new NpgsqlConnectionStringBuilder(_configuration.DatabaseConnectionString)
        {
            Enlist = false,
            Multiplexing = false,
            Pooling = false,
            ApplicationName = "Cove embedding ANN index reconciler",
        }.ConnectionString;

        await using var connection = CoveNpgsql.CreateDedicatedConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        // pgvector reports a build that outgrows maintenance_work_mem only as a notice.
        connection.Notice += (_, args) => _logger.LogInformation(
            "PostgreSQL notice while maintaining embedding ANN indexes: {Message} {Detail} {Hint}",
            args.Notice.MessageText, args.Notice.Detail, args.Notice.Hint);
        // A build takes minutes on a large library; a role- or database-level timeout would cancel it every time.
        await ExecuteAsync(connection, "SET statement_timeout = 0; SET lock_timeout = 0;", cancellationToken);

        if (!await TryAcquireLockAsync(connection, cancellationToken))
        {
            _logger.LogDebug("Embedding ANN index check skipped: another pass holds the lock");
            return EmbeddingAnnIndexReconcileResult.LockHeldElsewhere;
        }

        try
        {
            var dropped = await DropInvalidIndexesAsync(connection, cancellationToken);
            var specs = await ReadIndexSpecsAsync(connection, cancellationToken);

            _buildWithoutParallelWorkers = false;
            var desired = 0;
            var created = new List<string>();
            var failed = new List<string>();
            var waiting = new List<string>();
            foreach (var spec in specs)
            {
                var existing = await ReadIndexesAsync(connection, cancellationToken);
                if (existing.TryGetValue(spec.Name, out var managed) && managed.IsUsable)
                {
                    desired++;
                    continue;
                }
                if (!await HasAssetRowsAsync(connection, spec, cancellationToken))
                    continue;
                desired++;
                if (_retryFailedBuildAt.TryGetValue(spec.Name, out var retryAt) && _timeProvider.GetUtcNow() < retryAt)
                {
                    waiting.Add(spec.Name);
                    continue;
                }

                if (await TryCreateIndexAsync(connection, spec, cancellationToken))
                {
                    _retryFailedBuildAt.Remove(spec.Name);
                    _interruptedBuilds.Remove(spec.Name);
                    created.Add(spec.Name);
                }
                else
                {
                    _retryFailedBuildAt[spec.Name] = _timeProvider.GetUtcNow() + RetryFailedBuildAfter;
                    failed.Add(spec.Name);
                }
            }

            var replaced = await DropReplacedLegacyIndexesAsync(connection, cancellationToken);

            var result = new EmbeddingAnnIndexReconcileResult(false, desired, created, failed, waiting, replaced, dropped);
            if (created.Count + failed.Count + waiting.Count + replaced.Count + dropped.Count > 0)
                _logger.LogInformation("Embedding ANN indexes: {Summary}", result.Summary);
            else
                _logger.LogDebug("Embedding ANN indexes: {Summary}", result.Summary);
            return result;
        }
        finally
        {
            await ReleaseLockAsync(connection);
        }
    }

    /// <summary>The managed index for <paramref name="family"/> at <paramref name="dimensions"/>.</summary>
    internal static EmbeddingAnnIndexSpec BuildIndexSpec(EmbeddingAnnFamily family, int dimensions)
    {
        if (dimensions is < 1 or > MaxIndexedDimensions)
            throw new ArgumentOutOfRangeException(nameof(dimensions), dimensions, $"HNSW indexes support 1 to {MaxIndexedDimensions} dimensions.");
        return new EmbeddingAnnIndexSpec(family, dimensions, $"ix_embeddings_{family.NameStem}_asset_hnsw_d{dimensions}");
    }

    /// <summary>Whether <paramref name="indexName"/> is an index this class builds or replaces.</summary>
    internal static bool IsManagedOrLegacyName(string indexName)
        => Families.Any(family => indexName == family.LegacyIndexName)
           || (ManagedNamePattern().Match(indexName) is { Success: true } match
               && Families.Any(family => family.NameStem == match.Groups[1].Value));

    internal static int? CastDimensions(string indexDefinition)
    {
        var match = VectorCastPattern().Match(indexDefinition);
        return match.Success ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>
    /// The managed index for each (family, dimension) that has embeddings. The distinct (Kind, Dim) pairs
    /// come from a loose index scan of <c>IX_embeddings_Kind_Dim</c>, which reads one index entry per pair,
    /// so a pass that finds nothing new stays cheap on a table of any size.
    /// </summary>
    private async Task<IReadOnlyList<EmbeddingAnnIndexSpec>> ReadIndexSpecsAsync(
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

        var specs = new Dictionary<string, EmbeddingAnnIndexSpec>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (!_pairFamilies.TryGetValue(pair, out var family))
            {
                var resolved = await ResolveFamilyAsync(connection, pair.Kind, pair.Dim, cancellationToken);
                // Its rows were deleted since the scan: nothing learned, so ask again next pass.
                if (!resolved.Found)
                    continue;
                family = resolved.Family;
                _pairFamilies[pair] = family;
            }
            if (family != null)
            {
                var spec = BuildIndexSpec(family, pair.Dim);
                specs.TryAdd(spec.Name, spec);
            }
        }
        return specs.Values.OrderBy(spec => spec.Name, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The managed family a (Kind, Dim) pair belongs to, or null; not found when its rows are gone. A kind
    /// is produced by one model and stored under one family, so one row of the pair says which.
    /// </summary>
    private async Task<(bool Found, EmbeddingAnnFamily? Family)> ResolveFamilyAsync(
        NpgsqlConnection connection,
        string kind,
        int dimensions,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, """
            SELECT "Modality", "KindFamily"
            FROM public.embeddings
            WHERE "Kind" = @kind AND "Dim" = @dim
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("dim", dimensions);
        int modality;
        string? kindFamily;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                return (false, null);
            modality = reader.GetInt32(0);
            kindFamily = reader.IsDBNull(1) ? null : reader.GetString(1);
        }

        var family = Families.FirstOrDefault(candidate =>
            (int)candidate.Modality == modality && string.Equals(candidate.KindFamily, kindFamily, StringComparison.Ordinal));
        if (family != null && dimensions is < 1 or > MaxIndexedDimensions)
        {
            _logger.LogWarning(
                "Embeddings of kind {Kind} have {Dimensions} dimensions; pgvector's HNSW index supports at most {MaxDimensions}, so {KindFamily} searches at that size stay exact",
                kind, dimensions, MaxIndexedDimensions, family.KindFamily);
            return (true, null);
        }
        return (true, family);
    }

    /// <summary>
    /// Whether the family has asset-level rows at the spec's dimension: the index covers only those. A
    /// "no" is not cached for good (the first analysis may simply not have stored one yet), but it is
    /// re-checked with a growing delay because answering it reads every row of the family.
    /// </summary>
    private async Task<bool> HasAssetRowsAsync(
        NpgsqlConnection connection,
        EmbeddingAnnIndexSpec spec,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_assetRowRecheck.TryGetValue(spec.Name, out var recheck) && now < recheck.At)
            return false;

        if (await ProbeAssetRowsAsync(connection, spec.Family, spec.Dimensions, cancellationToken))
        {
            _assetRowRecheck.Remove(spec.Name);
            return true;
        }

        var delay = recheck.Delay == default
            ? MinAssetRowRecheck
            : TimeSpan.FromTicks(Math.Min(recheck.Delay.Ticks * 2, MaxAssetRowRecheck.Ticks));
        _assetRowRecheck[spec.Name] = (now + delay, delay);
        return false;
    }

    private static async Task<bool> ProbeAssetRowsAsync(
        NpgsqlConnection connection,
        EmbeddingAnnFamily family,
        int dimensions,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, """
            SELECT EXISTS (
                SELECT 1
                FROM public.embeddings
                WHERE "Modality" = @modality AND "KindFamily" = @family AND "SectionIndex" = 0 AND "Dim" = @dim
            );
            """);
        command.Parameters.AddWithValue("modality", (int)family.Modality);
        command.Parameters.AddWithValue("family", family.KindFamily);
        command.Parameters.AddWithValue("dim", dimensions);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private async Task<bool> TryCreateIndexAsync(
        NpgsqlConnection connection,
        EmbeddingAnnIndexSpec spec,
        CancellationToken cancellationToken)
    {
        // Only fixed family constants and integers are interpolated, never user input. The predicate has to
        // use literals so the planner can match it against the literals EmbeddingService emits.
        var sql = $"""
            CREATE INDEX CONCURRENTLY {QuoteIdentifier(spec.Name)}
            ON public.embeddings
            USING hnsw ((("Vector")::vector({spec.Dimensions})) vector_cosine_ops)
            WHERE "Modality" = {(int)spec.Family.Modality}
              AND "KindFamily" = {QuoteLiteral(spec.Family.KindFamily)}
              AND "SectionIndex" = 0
              AND "Dim" = {spec.Dimensions};
            """;

        _logger.LogInformation(
            "Building embedding ANN index {IndexName} for {KindFamily} at {Dimensions} dimensions; these searches stay exact until it is ready",
            spec.Name, spec.Family.KindFamily, spec.Dimensions);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            try
            {
                if (_buildWithoutParallelWorkers)
                    await ExecuteAsync(connection, "SET max_parallel_maintenance_workers = 0;", cancellationToken);
                await ExecuteAsync(connection, sql, cancellationToken);
            }
            catch (PostgresException exception) when (!_buildWithoutParallelWorkers && IsSharedMemoryExhausted(exception))
            {
                // Parallel HNSW builds share the graph through /dev/shm, which containers often keep small
                // (Docker's default is 64 MB). Build it in this process alone instead.
                _logger.LogWarning(
                    "Building {IndexName} with parallel workers ran out of shared memory ({Message}); retrying without them. Giving the database container more shared memory (shm_size) makes this faster",
                    spec.Name, exception.MessageText);
                // The rest of this pass builds without them too; the setting ends with the session.
                _buildWithoutParallelWorkers = true;
                await DropIfInvalidAsync(connection, spec.Name, cancellationToken);
                await ExecuteAsync(connection, "SET max_parallel_maintenance_workers = 0;", cancellationToken);
                await ExecuteAsync(connection, sql, cancellationToken);
            }

            var built = (await ReadIndexesAsync(connection, cancellationToken)).GetValueOrDefault(spec.Name);
            if (built is not { IsUsable: true })
                throw new InvalidOperationException($"PostgreSQL did not validate embedding ANN index {spec.Name}.");

            _logger.LogInformation(
                "Built embedding ANN index {IndexName} in {Elapsed}",
                spec.Name, System.Diagnostics.Stopwatch.GetElapsedTime(started));
            return true;
        }
        catch (Exception exception) when (IsInterruption(exception, connection, cancellationToken)
                                          && !cancellationToken.IsCancellationRequested
                                          && exception is not OperationCanceledException)
        {
            var interruptions = _interruptedBuilds.GetValueOrDefault(spec.Name) + 1;
            _interruptedBuilds[spec.Name] = interruptions;
            if (interruptions >= InterruptedBuildsBeforeBackOff)
            {
                _interruptedBuilds.Remove(spec.Name);
                _retryFailedBuildAt[spec.Name] = _timeProvider.GetUtcNow() + RetryFailedBuildAfter;
                _logger.LogWarning(
                    exception,
                    "Building embedding ANN index {IndexName} lost its connection {Count} times in a row; trying again in {RetryAfter}. If PostgreSQL restarted, it may be running out of memory (maintenance_work_mem)",
                    spec.Name, interruptions, RetryFailedBuildAfter);
            }
            throw;
        }
        catch (Exception exception) when (!IsInterruption(exception, connection, cancellationToken))
        {
            _logger.LogWarning(
                exception,
                "Could not build embedding ANN index {IndexName}; {KindFamily} searches stay exact. Trying again in {RetryAfter}",
                spec.Name, spec.Family.KindFamily, RetryFailedBuildAfter);
            try
            {
                using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await DropIfInvalidAsync(connection, spec.Name, cleanupTimeout.Token);
            }
            catch (Exception cleanupException)
            {
                // The next pass drops it.
                _logger.LogWarning(cleanupException, "Could not remove incomplete embedding ANN index {IndexName}", spec.Name);
            }
            return false;
        }
    }

    /// <summary>
    /// Whether a failed build was interrupted rather than impossible: cancelled, the connection lost, or the
    /// server shutting down or terminating the session (a restore does this). Those end the pass without
    /// a back-off; any invalid index left behind is dropped at the start of the next pass.
    /// </summary>
    private static bool IsInterruption(Exception exception, NpgsqlConnection connection, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested
           || exception is OperationCanceledException
           || connection.FullState != System.Data.ConnectionState.Open
           || exception is NpgsqlException and not PostgresException
           || exception is PostgresException postgres
              && (postgres.SqlState is PostgresErrorCodes.QueryCanceled
                      or PostgresErrorCodes.AdminShutdown
                      or PostgresErrorCodes.CrashShutdown
                      or PostgresErrorCodes.CannotConnectNow
                  || postgres.SqlState.StartsWith("08", StringComparison.Ordinal));

    private static bool IsSharedMemoryExhausted(PostgresException exception)
        => exception.SqlState.StartsWith("53", StringComparison.Ordinal)
           && exception.MessageText.Contains("shared memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Drops every invalid index under a managed or V1_0 name. An interrupted <c>CREATE INDEX
    /// CONCURRENTLY</c> leaves one behind, and it is still updated on every write but never used.
    /// </summary>
    private async Task<IReadOnlyList<string>> DropInvalidIndexesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var dropped = new List<string>();
        // An index still being built (by hand, say) is invalid until it finishes; leave it alone.
        foreach (var index in (await ReadIndexesAsync(connection, cancellationToken)).Values.Where(index => !index.IsUsable && !index.IsBeingBuilt))
        {
            _logger.LogInformation("Removing incomplete embedding ANN index {IndexName}", index.Name);
            await DropIndexAsync(connection, index.Name, cancellationToken);
            dropped.Add(index.Name);
        }
        return dropped;
    }

    /// <summary>
    /// Drops each V1_0 index whose family and dimension now have a valid managed index, or no asset-level
    /// rows. (A managed index whose rows are all gone is kept: it is small and rejects nothing.)
    /// </summary>
    private async Task<IReadOnlyList<string>> DropReplacedLegacyIndexesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var replaced = new List<string>();
        var existing = await ReadIndexesAsync(connection, cancellationToken);
        foreach (var family in Families)
        {
            if (!existing.TryGetValue(family.LegacyIndexName, out var legacy)
                || !legacy.IsUsable
                || CastDimensions(legacy.Definition) is not { } dimensions
                || dimensions is < 1 or > MaxIndexedDimensions)
                continue;

            if (existing.TryGetValue(BuildIndexSpec(family, dimensions).Name, out var replacement) && replacement.IsUsable)
            {
                _logger.LogInformation(
                    "Dropping embedding ANN index {LegacyIndexName}: {IndexName} replaces it and also lets rows of other dimensions be stored",
                    legacy.Name, replacement.Name);
            }
            else if (!await ProbeAssetRowsAsync(connection, family, dimensions, cancellationToken))
            {
                // Nothing left for it to index, but it would still reject the first asset row of a model
                // with another dimension.
                _logger.LogInformation(
                    "Dropping embedding ANN index {LegacyIndexName}: {KindFamily} has no asset-level rows at {Dimensions} dimensions any more",
                    legacy.Name, family.KindFamily, dimensions);
            }
            else
            {
                continue;
            }

            await DropIndexAsync(connection, legacy.Name, cancellationToken);
            replaced.Add(legacy.Name);
        }
        return replaced;
    }

    private static async Task DropIfInvalidAsync(NpgsqlConnection connection, string indexName, CancellationToken cancellationToken)
    {
        if ((await ReadIndexesAsync(connection, cancellationToken)).GetValueOrDefault(indexName) is { IsUsable: false })
            await DropIndexAsync(connection, indexName, cancellationToken);
    }

    /// <summary>The indexes on <c>embeddings</c> under a managed or V1_0 name.</summary>
    private static async Task<Dictionary<string, ExistingIndex>> ReadIndexesAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ExistingIndex>(StringComparer.Ordinal);
        await using var command = CreateCommand(connection, """
            SELECT index_class.relname,
                   index_metadata.indisvalid,
                   index_metadata.indisready,
                   pg_get_indexdef(index_class.oid),
                   EXISTS (SELECT 1 FROM pg_stat_progress_create_index AS progress WHERE progress.index_relid = index_class.oid)
            FROM pg_index AS index_metadata
            JOIN pg_class AS index_class ON index_class.oid = index_metadata.indexrelid
            JOIN pg_class AS table_class ON table_class.oid = index_metadata.indrelid
            JOIN pg_namespace AS table_namespace ON table_namespace.oid = table_class.relnamespace
            WHERE table_namespace.nspname = 'public'
              AND table_class.relname = 'embeddings';
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var name = reader.GetString(0);
            if (IsManagedOrLegacyName(name))
                result[name] = new ExistingIndex(name, reader.GetBoolean(1), reader.GetBoolean(2), reader.GetString(3), reader.GetBoolean(4));
        }
        return result;
    }

    private static async Task DropIndexAsync(NpgsqlConnection connection, string indexName, CancellationToken cancellationToken)
        => await ExecuteAsync(connection, $"DROP INDEX CONCURRENTLY IF EXISTS public.{QuoteIdentifier(indexName)};", cancellationToken);

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, sql);
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
        if (connection.FullState != System.Data.ConnectionState.Open)
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

    // Builds take minutes on a large library, so no client-side timeout either.
    private static NpgsqlCommand CreateCommand(NpgsqlConnection connection, string sql)
        => new(sql, connection) { CommandTimeout = 0 };

    private static string QuoteIdentifier(string identifier)
        => $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    [GeneratedRegex(@"::vector\((\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex VectorCastPattern();

    [GeneratedRegex(@"^ix_embeddings_([a-z_]+)_asset_hnsw_d[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex ManagedNamePattern();

    private sealed record ExistingIndex(string Name, bool IsValid, bool IsReady, string Definition, bool IsBeingBuilt)
    {
        public bool IsUsable => IsValid && IsReady;
    }
}

/// <param name="NameStem">The part of V1_0's index name between <c>ix_embeddings_</c> and <c>_asset_hnsw</c>.</param>
internal sealed record EmbeddingAnnFamily(EmbeddingModality Modality, string KindFamily, string NameStem)
{
    public string LegacyIndexName => $"ix_embeddings_{NameStem}_asset_hnsw";
}

internal sealed record EmbeddingAnnIndexSpec(EmbeddingAnnFamily Family, int Dimensions, string Name);

internal sealed record EmbeddingAnnIndexReconcileResult(
    bool Skipped,
    int DesiredCount,
    IReadOnlyList<string> Created,
    IReadOnlyList<string> Failed,
    IReadOnlyList<string> WaitingToRetry,
    IReadOnlyList<string> ReplacedLegacy,
    IReadOnlyList<string> DroppedInvalid)
{
    /// <summary>Nothing was done because another pass holds the advisory lock.</summary>
    public static EmbeddingAnnIndexReconcileResult LockHeldElsewhere { get; } = new(true, 0, [], [], [], [], []);

    public string Summary =>
        $"{DesiredCount} wanted; {Created.Count} built, {Failed.Count} failed, {WaitingToRetry.Count} waiting to retry, {ReplacedLegacy.Count} V1_0 index(es) replaced, {DroppedInvalid.Count} incomplete removed";
}
