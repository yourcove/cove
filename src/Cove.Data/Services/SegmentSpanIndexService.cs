using System.Diagnostics;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Cove.Data.Services;

/// <summary>
/// Keeps the stored span index (<see cref="StoredSegmentSpan"/>) in step with the segments. A video's
/// rows for a profile are stale when the video's version counter (incremented by the database triggers
/// in <see cref="SegmentSpanIndexSqlDefinitions"/>) or the profile's version differs from what the rows
/// were built from. Stale videos are re-resolved with <see cref="SegmentSpanResolver"/> and their rows
/// replaced; nothing else writes the index.
/// </summary>
public sealed class SegmentSpanIndexService(
    CoveContext db,
    SegmentSpanResolver resolver,
    SegmentSpanIndexSignal signal,
    ILogger<SegmentSpanIndexService>? logger = null,
    IMemoryCache? cache = null,
    LibraryWriteSignal? writes = null)
{
    /// <summary>Videos resolved and written per transaction.</summary>
    public const int BatchSize = 200;

    private static readonly TimeSpan FreshnessMemory = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Marks the profile as one whose spans are kept stored. The first registration is what makes the
    /// background materializer start building the profile's rows. The rows are kept until the profile
    /// is deleted, which removes them with it.
    /// </summary>
    public async Task RegisterProfileAsync(int profileId, CancellationToken ct)
    {
        // Nearly every call finds the profile already registered; only the first one writes.
        if (await db.SegmentSpanIndexedProfiles.AsNoTracking().AnyAsync(indexed => indexed.ProfileId == profileId, ct))
            return;

        var written = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO segment_span_indexed_profiles ("ProfileId")
            SELECT profile."Id" FROM segment_display_profiles AS profile WHERE profile."Id" = {profileId}
            ON CONFLICT ("ProfileId") DO NOTHING
            """, ct);
        if (written > 0)
            signal.Notify();
    }

    /// <summary>How many videos' stored spans are stale for the profile, counted up to <paramref name="limit"/>.</summary>
    public async Task<int> CountPendingAsync(int profileId, int limit, CancellationToken ct)
    {
        var profileVersion = await LoadProfileVersionAsync(profileId, ct);
        if (profileVersion is null)
            return 0;

        return await StaleVideos(profileId, profileVersion.Value).Take(limit).CountAsync(ct);
    }

    /// <summary>
    /// Brings the profile's stored spans up to date within a time budget and returns how many videos
    /// are still stale. A request calls this before it queries the index: after ordinary edits only a
    /// few videos are stale and the caller reads its own writes, while a first build or a bulk import
    /// leaves the remainder to the background materializer and reports it as pending.
    /// </summary>
    public async Task<int> EnsureFreshAsync(int profileId, TimeSpan budget, int pendingCountLimit, CancellationToken ct)
    {
        // Finding nothing stale means comparing every video's version with its state, which grows with
        // the library. Paging through a list asks again and again, so a clean answer is remembered
        // briefly. Any write made through the context ends it at once (the token is taken before the
        // check, so a write that lands during it is not missed); a write that bypasses the context is
        // picked up when the entry lapses, no later than the background pass would have got to it.
        var profileVersion = await LoadProfileVersionAsync(profileId, ct);
        var freshKey = $"segment-span-index-fresh:{profileId}:{profileVersion}";
        if (cache is not null && cache.TryGetValue(freshKey, out _))
            return 0;
        var writeToken = writes?.GetChangeToken();

        await RegisterProfileAsync(profileId, ct);

        var stopwatch = Stopwatch.StartNew();
        var first = true;
        while (true)
        {
            var batch = await ReconcileBatchCoreAsync(profileId, BatchSize, ct, settleDelay: null);
            if (first && batch.Stale == 0)
            {
                if (cache is not null && profileVersion is not null)
                {
                    var options = new MemoryCacheEntryOptions().SetAbsoluteExpiration(FreshnessMemory);
                    if (writeToken is not null)
                        options.AddExpirationToken(writeToken);
                    cache.Set(freshKey, true, options);
                }

                return 0;
            }

            first = false;
            if (batch.Stale < BatchSize || stopwatch.Elapsed >= budget)
                break;
        }

        // Whatever is left (the budget ran out, or a video could not be resolved) is reported, not hidden.
        var pending = await CountPendingAsync(profileId, pendingCountLimit, ct);
        if (pending > 0)
            signal.Notify();
        return pending;
    }

    /// <summary>
    /// Re-resolves up to <paramref name="maxVideos"/> stale videos of the profile and replaces their
    /// stored spans. Returns the number of videos brought up to date; fewer than requested means none
    /// are left (or, with a settle delay, none that have settled), or that some could not be resolved.
    /// </summary>
    public async Task<int> ReconcileBatchAsync(int profileId, int maxVideos, CancellationToken ct, TimeSpan? settleDelay = null)
        => (await ReconcileBatchCoreAsync(profileId, maxVideos, ct, settleDelay)).Reconciled;

    private async Task<(int Stale, int Reconciled)> ReconcileBatchCoreAsync(
        int profileId, int maxVideos, CancellationToken ct, TimeSpan? settleDelay)
    {
        using var unfiltered = db.SuppressAuthorizationFilters();

        var profile = await db.SegmentDisplayProfiles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == profileId, ct);
        if (profile is null)
            return (0, 0);

        // The versions are read before the segments. A write that lands in between leaves the stored
        // version behind the live one, so the video is simply resolved again on the next pass.
        var staleVideos = StaleVideos(profile.Id, profile.Version);
        if (settleDelay is { } delay)
        {
            // The background pass skips videos still being written; a request never waits. The cut-off is
            // taken from the database's clock, the one that stamped ChangedAt, so a skewed app clock cannot
            // make a settled video look unsettled.
            staleVideos = staleVideos.Where(version => version.ChangedAt <= DateTime.UtcNow - delay);
        }

        var stale = await staleVideos
            .OrderBy(version => version.VideoId)
            .Take(maxVideos)
            .Select(version => new StaleVideo(version.VideoId, version.Version))
            .ToListAsync(ct);
        if (stale.Count == 0)
            return (0, 0);

        try
        {
            await ReconcileVideosAsync(profile, stale, ct);
            return (stale.Count, stale.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One video that cannot be resolved or written must not hold back the rest of its batch,
            // or every later video of the profile: retry them one at a time and leave only it stale.
            logger?.LogWarning(exception, "Storing spans for a batch of {Count} videos failed; retrying them one at a time.", stale.Count);
        }

        var reconciled = 0;
        foreach (var video in stale)
        {
            try
            {
                await ReconcileVideosAsync(profile, [video], ct);
                reconciled++;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger?.LogError(exception, "Storing spans for video {VideoId} under profile {ProfileId} failed; it stays pending.", video.VideoId, profile.Id);
            }
        }

        return (stale.Count, reconciled);
    }

    private async Task ReconcileVideosAsync(SegmentDisplayProfile profile, IReadOnlyList<StaleVideo> stale, CancellationToken ct)
    {
        var videoIds = stale.Select(item => item.VideoId).ToArray();
        var versions = stale.Select(item => item.Version).ToArray();
        var resolution = await resolver.ResolveForIndexAsync(videoIds, profile, ct);

        // The index is written on the context's connection but outside its transaction pipeline: these
        // rows are derived data that no list cache depends on, so committing them must not signal a
        // library write (which expires every cached list page) once per batch of a long build.
        await db.Database.OpenConnectionAsync(ct);
        try
        {
            var connection = (NpgsqlConnection)db.Database.GetDbConnection();
            await using var transaction = await connection.BeginTransactionAsync(ct);

            // Writing the state rows first serializes concurrent reconcilers of the same video (a request
            // and the background materializer): the second waits on the row lock, then finds the state
            // already at its versions and leaves the first one's rows alone. Only videos whose state
            // changed are returned and rewritten; a video deleted since the read is skipped. A result
            // from an older read can still overwrite a newer one, but then the stored version is behind
            // the live one again and the video is simply resolved once more.
            //
            // The video rows are locked first, in id order. The span rows' foreign key would lock them
            // anyway, after the state rows; a concurrent video delete takes the same locks the other
            // way round (video, then its cascading state row), which would deadlock.
            await using (var lockVideos = new NpgsqlCommand(
                """SELECT 1 FROM videos WHERE "Id" = ANY(@videoIds) ORDER BY "Id" FOR KEY SHARE""", connection, transaction))
            {
                lockVideos.Parameters.AddWithValue("videoIds", videoIds);
                await lockVideos.ExecuteNonQueryAsync(ct);
            }

            var writable = new HashSet<int>();
            await using (var lockStates = new NpgsqlCommand("""
                INSERT INTO segment_span_video_states AS state ("ProfileId", "VideoId", "ProfileVersion", "ResolvedVersion")
                SELECT @profileId, item.video_id, @profileVersion, item.version
                FROM unnest(@videoIds, @versions) AS item(video_id, version)
                WHERE EXISTS (SELECT 1 FROM videos AS video WHERE video."Id" = item.video_id)
                ORDER BY item.video_id
                ON CONFLICT ("ProfileId", "VideoId") DO UPDATE
                    SET "ProfileVersion" = EXCLUDED."ProfileVersion", "ResolvedVersion" = EXCLUDED."ResolvedVersion"
                    WHERE state."ProfileVersion" <> EXCLUDED."ProfileVersion"
                       OR state."ResolvedVersion" < EXCLUDED."ResolvedVersion"
                RETURNING state."VideoId"
                """, connection, transaction))
            {
                lockStates.Parameters.AddWithValue("profileId", profile.Id);
                lockStates.Parameters.AddWithValue("profileVersion", profile.Version);
                lockStates.Parameters.AddWithValue("videoIds", videoIds);
                lockStates.Parameters.AddWithValue("versions", versions);
                await using var reader = await lockStates.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                    writable.Add(reader.GetInt32(0));
            }

            if (writable.Count > 0)
            {
                await using (var deleteSpans = new NpgsqlCommand(
                    """DELETE FROM segment_spans WHERE "ProfileId" = @profileId AND "VideoId" = ANY(@videoIds)""", connection, transaction))
                {
                    deleteSpans.Parameters.AddWithValue("profileId", profile.Id);
                    deleteSpans.Parameters.AddWithValue("videoIds", writable.ToArray());
                    await deleteSpans.ExecuteNonQueryAsync(ct);
                }

                await WriteSpansAsync(connection, profile.Id, resolution, writable, ct);
            }

            await transaction.CommitAsync(ct);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Refreshes the query planner's statistics for the stored span tables. While a profile is first
    /// built the statistics still describe the table without it, so the planner expects almost none of
    /// its rows and replaces each video's spans by scanning every span the build has already written.
    /// Autovacuum catches up eventually, but on its own schedule; the materializer calls this as it goes.
    /// </summary>
    public async Task RefreshStatisticsAsync(CancellationToken ct)
        => await db.Database.ExecuteSqlRawAsync("ANALYZE public.segment_spans, public.segment_span_video_states", ct);

    /// <summary>
    /// How many rows of the video versions have been inserted, updated or deleted, according to
    /// PostgreSQL's statistics: a cheap way to tell that nothing has changed since an earlier read. Null
    /// when the statistics are unavailable.
    /// </summary>
    public async Task<long?> ReadVideoVersionChangeCounterAsync(CancellationToken ct)
    {
        try
        {
            return await db.Database.SqlQuery<long?>($"""
                SELECT (n_tup_ins + n_tup_upd + n_tup_del)::bigint AS "Value"
                FROM pg_stat_user_tables
                WHERE schemaname = 'public' AND relname = 'segment_span_video_versions'
                """).FirstOrDefaultAsync(ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Without the counter the background pass simply searches every profile, as it would anyway.
            logger?.LogDebug(exception, "Reading the change counter of the video versions failed.");
            return null;
        }
    }

    private sealed record StaleVideo(int VideoId, long Version);

    private Task<int?> LoadProfileVersionAsync(int profileId, CancellationToken ct)
        => db.SegmentDisplayProfiles.AsNoTracking()
            .Where(profile => profile.Id == profileId)
            .Select(profile => (int?)profile.Version)
            .FirstOrDefaultAsync(ct);

    private IQueryable<SegmentSpanVideoVersion> StaleVideos(int profileId, int profileVersion)
        => db.SegmentSpanVideoVersions.AsNoTracking()
            .Where(version => !db.SegmentSpanVideoStates.Any(state =>
                state.ProfileId == profileId
                && state.VideoId == version.VideoId
                && state.ProfileVersion == profileVersion
                && state.ResolvedVersion == version.Version));

    private static async Task WriteSpansAsync(
        NpgsqlConnection connection, int profileId, SegmentSpanIndexResolution resolution, HashSet<int> writableVideoIds, CancellationToken ct)
    {
        if (!resolution.Videos.Any(video => video.Spans.Count > 0 && writableVideoIds.Contains(video.VideoId)))
            return;

        await using var writer = await connection.BeginBinaryImportAsync("""
            COPY segment_spans (
                "ProfileId", "VideoId", "Ordinal", "SpanKey", "StartSec", "EndSec", "DurationSec",
                "SourceKey", "Kind", "TagId", "Label", "ColorHint", "Lane", "CollapsedToInstant",
                "SegmentIds", "SegmentCount", "RefIds", "EntityRefIds", "Titles", "SourceRunIds", "ColorHints",
                "HasImage", "HasPayload", "MinConfidence", "MaxConfidence",
                "MinSegmentCreatedAt", "MaxSegmentCreatedAt", "MinSegmentUpdatedAt", "MaxSegmentUpdatedAt")
            FROM STDIN (FORMAT BINARY)
            """, ct);

        foreach (var (videoId, spans) in resolution.Videos)
        {
            if (!writableVideoIds.Contains(videoId))
                continue;

            for (var ordinal = 0; ordinal < spans.Count; ordinal++)
            {
                var span = spans[ordinal];
                var facts = SpanFacts.From(span, resolution.SegmentFacts);

                await writer.StartRowAsync(ct);
                await writer.WriteAsync(profileId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(videoId, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(ordinal, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(span.SpanKey, NpgsqlDbType.Text, ct);
                await writer.WriteAsync(span.StartSec, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(span.EndSec, NpgsqlDbType.Double, ct);
                await writer.WriteAsync(span.EndSec - span.StartSec, NpgsqlDbType.Double, ct);
                await WriteNullableAsync(writer, span.SourceKey, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, span.Kind, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, span.TagId, NpgsqlDbType.Integer, ct);
                await WriteNullableAsync(writer, span.TagName, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, span.ColorHint, NpgsqlDbType.Text, ct);
                await WriteNullableAsync(writer, span.Lane, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(span.CollapsedToInstant, NpgsqlDbType.Boolean, ct);
                await writer.WriteAsync(span.SegmentIds.ToArray(), NpgsqlDbType.Array | NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(span.SegmentIds.Count, NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(facts.RefIds, NpgsqlDbType.Array | NpgsqlDbType.Bigint, ct);
                await writer.WriteAsync(
                    facts.RefIds.Where(refId => refId is >= int.MinValue and <= int.MaxValue).Select(refId => (int)refId).ToArray(),
                    NpgsqlDbType.Array | NpgsqlDbType.Integer, ct);
                await writer.WriteAsync(facts.Titles, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await writer.WriteAsync(facts.SourceRunIds, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await writer.WriteAsync(facts.ColorHints, NpgsqlDbType.Array | NpgsqlDbType.Text, ct);
                await writer.WriteAsync(facts.HasImage, NpgsqlDbType.Boolean, ct);
                await writer.WriteAsync(facts.HasPayload, NpgsqlDbType.Boolean, ct);
                await WriteNullableAsync(writer, facts.MinConfidence, NpgsqlDbType.Real, ct);
                await WriteNullableAsync(writer, facts.MaxConfidence, NpgsqlDbType.Real, ct);
                await writer.WriteAsync(facts.MinCreatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(facts.MaxCreatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(facts.MinUpdatedAt, NpgsqlDbType.TimestampTz, ct);
                await writer.WriteAsync(facts.MaxUpdatedAt, NpgsqlDbType.TimestampTz, ct);
            }
        }

        await writer.CompleteAsync(ct);
    }

    private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken ct)
        => value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value, type, ct);

    private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken ct)
        where T : struct
        => value is null ? writer.WriteNullAsync(ct) : writer.WriteAsync(value.Value, type, ct);

    private sealed record SpanFacts(
        long[] RefIds,
        string[] Titles,
        string[] SourceRunIds,
        string[] ColorHints,
        bool HasImage,
        bool HasPayload,
        float? MinConfidence,
        float? MaxConfidence,
        DateTime MinCreatedAt,
        DateTime MaxCreatedAt,
        DateTime MinUpdatedAt,
        DateTime MaxUpdatedAt)
    {
        public static SpanFacts From(ResolvedSpan span, IReadOnlyDictionary<int, SegmentSpanIndexSegmentFacts> segmentFacts)
        {
            var refIds = new SortedSet<long>();
            var titles = new SortedSet<string>(StringComparer.Ordinal);
            var sourceRunIds = new SortedSet<string>(StringComparer.Ordinal);
            var colorHints = new SortedSet<string>(StringComparer.Ordinal);
            var hasImage = false;
            var hasPayload = false;
            float? minConfidence = null;
            float? maxConfidence = null;
            var minCreatedAt = DateTime.MaxValue;
            var maxCreatedAt = DateTime.MinValue;
            var minUpdatedAt = DateTime.MaxValue;
            var maxUpdatedAt = DateTime.MinValue;
            var any = false;

            foreach (var segmentId in span.SegmentIds)
            {
                if (!segmentFacts.TryGetValue(segmentId, out var facts))
                    continue;

                any = true;
                if (facts.RefId is { } refId)
                    refIds.Add(refId);
                AddText(titles, facts.Title);
                AddText(sourceRunIds, facts.SourceRunId);
                AddText(colorHints, facts.ColorHint);
                hasImage |= facts.HasImage;
                hasPayload |= facts.HasPayload;
                if (facts.Confidence is { } confidence)
                {
                    minConfidence = minConfidence is { } low ? Math.Min(low, confidence) : confidence;
                    maxConfidence = maxConfidence is { } high ? Math.Max(high, confidence) : confidence;
                }

                if (facts.CreatedAt < minCreatedAt) minCreatedAt = facts.CreatedAt;
                if (facts.CreatedAt > maxCreatedAt) maxCreatedAt = facts.CreatedAt;
                if (facts.UpdatedAt < minUpdatedAt) minUpdatedAt = facts.UpdatedAt;
                if (facts.UpdatedAt > maxUpdatedAt) maxUpdatedAt = facts.UpdatedAt;
            }

            if (!any)
                minCreatedAt = maxCreatedAt = minUpdatedAt = maxUpdatedAt = DateTime.UnixEpoch;

            return new SpanFacts(
                [.. refIds],
                [.. titles],
                [.. sourceRunIds.OrderBy(value => value, StringComparer.OrdinalIgnoreCase)],
                [.. colorHints],
                hasImage,
                hasPayload,
                minConfidence,
                maxConfidence,
                AsUtc(minCreatedAt),
                AsUtc(maxCreatedAt),
                AsUtc(minUpdatedAt),
                AsUtc(maxUpdatedAt));
        }

        private static void AddText(SortedSet<string> values, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value.Trim());
        }

        private static DateTime AsUtc(DateTime value)
            => value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
    }
}

/// <summary>
/// Wakes the background materializer when a request registers a profile or leaves stale videos behind,
/// so it does not wait for its idle poll.
/// </summary>
public sealed class SegmentSpanIndexSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Notify()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // A wake-up is already pending.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
        => await _wake.WaitAsync(timeout, ct);
}
