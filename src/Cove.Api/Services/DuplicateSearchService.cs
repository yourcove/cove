using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class DuplicateSearchJobService(
    CoveContext db,
    IJobService jobService,
    IServiceScopeFactory scopeFactory)
{
    internal const int MaximumPHashDistance = 64;
    private static readonly TimeSpan ResultRetention = TimeSpan.FromDays(7);

    public async Task<DuplicateSearchStartDto> StartAsync(
        JobOwner? owner,
        CovePrincipal? principal,
        DuplicateSearchRequestDto request,
        IReadOnlyCollection<int>? candidateVideoIds,
        CancellationToken ct)
    {
        var ids = candidateVideoIds?.Where(id => id > 0).Distinct().ToArray();
        var matchType = NormalizeMatchType(request.MatchType);
        var fingerprintAlgorithm = NormalizeFingerprintAlgorithm(request.FingerprintAlgorithm);
        var folderMode = NormalizeFolderMode(request.FolderMode);
        var folderPaths = NormalizeFolderPaths(request.FolderPaths);
        var rankingMode = string.Equals(request.RankingMode, "custom", StringComparison.OrdinalIgnoreCase) ? "custom" : "balanced";
        var preferredCodecs = NormalizeStringList(request.PreferredCodecs, ["av1", "hevc", "h264", "vp9", "mpeg4"]);
        var keeperRules = NormalizeKeeperRules(request.KeeperRules);
        var search = new DuplicateSearch
        {
            OwnerKey = owner?.Key,
            MatchType = matchType,
            FingerprintAlgorithm = fingerprintAlgorithm,
            Distance = Math.Clamp(request.Distance, 0, MaximumPHashDistance),
            DurationDifference = Math.Max(0, request.DurationDiff ?? 10),
            MinimumDuration = Math.Clamp(request.MinimumDuration, 0, 86_400_000),
            FolderMode = folderMode,
            FolderPathsJson = JsonSerializer.Serialize(folderPaths),
            RankingMode = rankingMode,
            PreferredCodecsJson = JsonSerializer.Serialize(preferredCodecs),
            KeeperRulesJson = JsonSerializer.Serialize(keeperRules),
            CandidateCount = ids?.Length ?? 0,
            Status = DuplicateSearchStatus.Pending,
            ExpiresAt = DateTime.UtcNow.Add(ResultRetention),
        };
        db.DuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);

        var resultUrl = $"/duplicates?search={search.Id:D}";
        var work = CreateExecutionWork(scopeFactory, search.Id, ids, principal);

        var description = $"Finding duplicate videos by {DescribeMatchType(matchType)}";
        var jobId = owner is null
            ? jobService.EnqueueWithResult("duplicate-search", description, work, resultUrl)
            : jobService.EnqueueOwned(owner, "duplicate-search", description, work, resultUrl);
        search.JobId = jobId;
        // The job is already observable at this point. Persist its durable link even if the request
        // disconnects after receiving the enqueue side effect.
        await db.SaveChangesAsync(CancellationToken.None);
        return new DuplicateSearchStartDto(search.Id, jobId, search.CandidateCount);
    }

    private static Func<IJobProgress, CancellationToken, Task> CreateExecutionWork(
        IServiceScopeFactory executionScopeFactory,
        Guid searchId,
        IReadOnlyCollection<int>? candidateVideoIds,
        CovePrincipal? principal)
        => async (progress, jobCt) =>
        {
            using var scope = executionScopeFactory.CreateScope();
            var scopedPrincipalAccessor = scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>();
            var previousPrincipal = scopedPrincipalAccessor.Current;
            scopedPrincipalAccessor.Set(principal);
            var execution = scope.ServiceProvider.GetRequiredService<DuplicateSearchExecutionService>();
            try
            {
                await execution.ExecuteAsync(searchId, candidateVideoIds, progress, jobCt);
            }
            finally
            {
                scopedPrincipalAccessor.Set(previousPrincipal);
            }
        };

    /// <summary>
    /// Releases a terminal or lost deletion job only when at least one unwanted video still exists.
    /// A completed deletion retains its job id so keeper decisions cannot later be rewritten as if
    /// the destructive action had never happened.
    /// </summary>
    public async Task<bool> ReconcileDeletionAsync(
        Guid searchId,
        string expectedJobId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedJobId))
            return false;

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // The conditional write both proves that this reconciliation still owns the claim and
            // locks the search row until its keeper reservations have been removed. A newer claim
            // cannot appear between the ownership check and reservation cleanup.
            var owned = await db.DuplicateSearches
                .Where(search => search.Id == searchId && search.DeletionJobId == expectedJobId)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, expectedJobId), ct);
            if (owned == 0)
            {
                var current = await db.DuplicateSearches
                    .Where(search => search.Id == searchId)
                    .Select(search => new { search.DeletionJobId })
                    .SingleOrDefaultAsync(ct);
                await transaction.CommitAsync(ct);
                // A retry after an ambiguously acknowledged successful release only needs its caller
                // to reload. Most importantly, it must not touch reservations belonging to a newer job.
                return current is not null && current.DeletionJobId != expectedJobId;
            }

            var unwantedVideoIds = EffectiveUnkeptVideoIds(db, searchId);
            var hasUnwantedVideos = await db.Videos.AnyAsync(video => unwantedVideoIds.Contains(video.Id), ct);
            if (hasUnwantedVideos)
            {
                var released = await db.DuplicateSearches
                    .Where(search => search.Id == searchId && search.DeletionJobId == expectedJobId)
                    .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
                if (released != 1)
                    throw new DbUpdateConcurrencyException("The duplicate deletion claim changed while it was being reconciled.");
            }

            await db.DuplicateDeletionKeeperReservations
                .IgnoreQueryFilters()
                .Where(item => item.SearchId == searchId)
                .ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
            return hasUnwantedVideos;
        });
    }

    public async Task<bool> ReconcileTerminalDeletionAsync(DuplicateSearch search, CancellationToken ct)
    {
        var deletionJobId = search.DeletionJobId;
        if (string.IsNullOrWhiteSpace(deletionJobId)
            || deletionJobId.StartsWith(DuplicateSearchDeletionClaim.Prefix, StringComparison.Ordinal))
            return false;

        var job = jobService.GetJob(deletionJobId);
        if (job is { Status: JobStatus.Pending or JobStatus.Running }
            || job is { CompletedAt: null })
            return false;

        return await ReconcileDeletionAsync(search.Id, deletionJobId, ct);
    }

    /// <summary>
    /// Releases the durable claim and keeper constraints for a deletion job that was cancelled while
    /// still queued. Its callback never ran, so the normal callback-finally cleanup cannot execute.
    /// </summary>
    public async Task<int> ReleaseCancelledPendingDeletionAsync(string deletionJobId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(deletionJobId))
            return 0;

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var owned = await db.DuplicateSearches
                .Where(search => search.DeletionJobId == deletionJobId)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, deletionJobId), ct);
            if (owned == 0)
            {
                await transaction.CommitAsync(ct);
                return 0;
            }

            var searchIds = await db.DuplicateSearches
                .Where(search => search.DeletionJobId == deletionJobId)
                .Select(search => search.Id)
                .ToArrayAsync(ct);
            await db.DuplicateDeletionKeeperReservations
                .IgnoreQueryFilters()
                .Where(item => searchIds.Contains(item.SearchId))
                .ExecuteDeleteAsync(ct);
            var released = await db.DuplicateSearches
                .Where(search => searchIds.Contains(search.Id) && search.DeletionJobId == deletionJobId)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
            if (released != owned)
                throw new DbUpdateConcurrencyException("A duplicate deletion claim changed while cancellation was being reconciled.");
            await transaction.CommitAsync(ct);
            return released;
        });
    }

    /// <summary>
    /// Releases one pre-enqueue reservation only while the supplied request token still owns it.
    /// The row lock is held through keeper cleanup so a later deletion claim cannot lose its guards.
    /// </summary>
    public async Task<bool> ReleaseDeletionClaimAsync(Guid searchId, string expectedClaim, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedClaim))
            return false;

        var executionStrategy = db.Database.CreateExecutionStrategy();
        return await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var released = await db.DuplicateSearches
                .Where(search => search.Id == searchId && search.DeletionJobId == expectedClaim)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
            if (released == 1)
            {
                await db.DuplicateDeletionKeeperReservations
                    .IgnoreQueryFilters()
                    .Where(item => item.SearchId == searchId)
                    .ExecuteDeleteAsync(ct);
            }
            await transaction.CommitAsync(ct);
            return released == 1;
        });
    }

    internal static IQueryable<int> EffectiveUnkeptVideoIds(CoveContext context, Guid searchId)
    {
        var keptVideoIds = context.DuplicateSearchItems
            .Where(item => item.Group != null && item.Group.SearchId == searchId && item.Keep)
            .Select(item => item.VideoId);
        return context.DuplicateSearchItems
            .Where(item => item.Group != null
                && item.Group.SearchId == searchId
                && !item.Keep
                && item.Group.Items.Any(keeper => keeper.Keep)
                && !keptVideoIds.Contains(item.VideoId))
            .Select(item => item.VideoId)
            .Distinct();
    }

    internal static string NormalizeMatchType(string? matchType)
        => matchType?.Trim().ToLowerInvariant() switch
        {
            "phash" or "visual" => "phash",
            "title" => "title",
            "remoteid" or "remote-id" or "remote_id" => "remoteId",
            _ => "fingerprint",
        };

    internal static string NormalizeFingerprintAlgorithm(string? algorithm)
        => algorithm?.Trim().ToLowerInvariant() is "md5" or "oshash" ? algorithm.Trim().ToLowerInvariant() : "any";

    internal static string NormalizeFolderMode(string? mode)
        => mode?.Trim().ToLowerInvariant() is "include" or "exclude" ? mode.Trim().ToLowerInvariant() : "all";

    internal static string[] NormalizeFolderPaths(IReadOnlyList<string>? paths)
        => (paths ?? [])
            .Select(path => path.Trim().Replace('\\', '/').TrimEnd('/'))
            .Where(path => path.Length > 0)
            .Distinct(FilesystemPaths.PathComparer)
            .Take(100)
            .ToArray();

    private static string[] NormalizeStringList(IReadOnlyList<string>? values, string[] fallback)
    {
        var normalized = (values ?? []).Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(20).ToArray();
        return normalized.Length > 0 ? normalized : fallback;
    }

    private static string[] NormalizeKeeperRules(IReadOnlyList<string>? values)
    {
        string[] valid = ["metadata", "resolution", "duration", "codec", "bitrate", "size", "oldest", "newest"];
        var requested = NormalizeStringList(values, []);
        var normalized = requested.Where(value => valid.Contains(value, StringComparer.OrdinalIgnoreCase)).ToArray();
        return normalized.Length > 0 ? normalized : ["resolution", "codec", "bitrate", "duration", "metadata", "oldest"];
    }

    private static string DescribeMatchType(string matchType) => matchType switch
    {
        "phash" => "visual pHash",
        "title" => "title",
        "remoteId" => "remote ID",
        _ => "file fingerprint",
    };
}

public sealed class DuplicateSearchExecutionService(
    CoveContext db,
    IJobService jobService,
    CoveConfiguration config)
{
    private const int QueryChunkSize = 4_000;
    private const int PersistGroupBatchSize = 250;
    private const int PersistItemBatchSize = 1_000;
    internal const int MaximumPersistedGroupSize = 50;
    internal const int MaximumPersistedGroupCount = 25_000;
    internal const long MaximumPHashComparisons = 50_000_000;
    internal const int MaximumPHashMatches = MaximumPersistedGroupCount;
    private static readonly TimeSpan ResultRetention = TimeSpan.FromDays(7);

    public async Task ExecuteAsync(
        Guid searchId,
        IReadOnlyCollection<int>? candidateVideoIds,
        IJobProgress progress,
        CancellationToken ct)
    {
        try
        {
            // Retention cleanup belongs to background execution; a large cascade must never delay the
            // request whose only responsibility is to durably enqueue this search.
            var now = DateTime.UtcNow;
            var expiredSearches = db.DuplicateSearches
                .Where(item => item.Id != searchId && item.ExpiresAt < now);
            if (db.Database.IsRelational())
            {
                await expiredSearches.ExecuteDeleteAsync(ct);
            }
            else
            {
                // EF's in-memory provider cannot translate ExecuteDelete. Keeping this fallback also
                // makes the execution service usable by lightweight embedders and deterministic tests.
                db.DuplicateSearches.RemoveRange(await expiredSearches.ToListAsync(ct));
                await db.SaveChangesAsync(ct);
            }

            var search = await db.DuplicateSearches.FirstOrDefaultAsync(item => item.Id == searchId, ct)
                ?? throw new InvalidOperationException("The duplicate search no longer exists.");
            search.Status = DuplicateSearchStatus.Running;
            search.StartedAt = DateTime.UtcNow;
            search.Error = null;
            await db.SaveChangesAsync(ct);

            progress.Report(0.01, "Loading visible videos");
            var ids = candidateVideoIds is null
                ? await ResolveCandidateIdsAsync(search, ct)
                : candidateVideoIds.Where(id => id > 0).Distinct().ToArray();
            search.CandidateCount = ids.Length;
            await db.SaveChangesAsync(ct);
            List<List<int>> groups;
            switch (search.MatchType)
            {
                case "phash":
                    groups = await FindPhashGroupsAsync(ids, search.Distance, search.DurationDifference, progress, ct);
                    break;
                case "title":
                    progress.Report(0.1, "Loading video titles");
                    groups = await FindTitleGroupsAsync(ids, ct);
                    break;
                case "remoteId":
                    progress.Report(0.1, "Loading remote IDs");
                    groups = await FindRemoteIdGroupsAsync(ids, ct);
                    break;
                default:
                    progress.Report(0.1, "Loading file fingerprints");
                    groups = await FindFingerprintGroupsAsync(ids, search.FingerprintAlgorithm, ct);
                    break;
            }

            ct.ThrowIfCancellationRequested();
            progress.Report(0.92, "Saving duplicate groups");
            var persistedGroupCount = await PersistGroupsAsync(searchId, groups, ct);
            progress.Report(1, $"Found {persistedGroupCount.ToString(CultureInfo.InvariantCulture)} duplicate groups");
        }
        catch (OperationCanceledException)
        {
            await SetTerminalStatusAsync(searchId, DuplicateSearchStatus.Cancelled, null);
            throw;
        }
        catch (Exception ex)
        {
            await SetTerminalStatusAsync(searchId, DuplicateSearchStatus.Failed, ex.Message);
            throw;
        }
    }

    private async Task<int[]> ResolveCandidateIdsAsync(DuplicateSearch search, CancellationToken ct)
    {
        // Title and remote-ID matching also apply to metadata-only records. Requiring a file for
        // the default, unscoped search silently excluded those videos from every match mode.
        if (search.FolderMode == "all" && search.MinimumDuration <= 0)
            return await db.Videos.AsNoTracking().Select(video => video.Id).ToArrayAsync(ct);

        var paths = DeserializeList(search.FolderPathsJson);
        var rows = await db.Videos
            .SelectMany(video => video.Files.Select(file => new { video.Id, file.Duration, file.Path }))
            .AsNoTracking()
            .ToListAsync(ct);
        return rows
            .Where(row => row.Duration >= search.MinimumDuration)
            .Where(row => search.FolderMode == "all" || paths.Length == 0 ||
                (search.FolderMode == "include") == paths.Any(path => IsAtOrBelow(row.Path, path)))
            .Select(row => row.Id)
            .Distinct()
            .ToArray();
    }

    internal static bool IsAtOrBelow(string candidate, string folder)
    {
        var normalizedCandidate = candidate.Replace('\\', '/').TrimEnd('/');
        var normalizedFolder = folder.Replace('\\', '/').TrimEnd('/');
        return normalizedCandidate.Equals(normalizedFolder, FilesystemPaths.PathComparison)
            || normalizedCandidate.StartsWith(normalizedFolder + "/", FilesystemPaths.PathComparison);
    }

    internal static string[] DeserializeList(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]") ?? []; }
        catch (JsonException) { return []; }
    }

    private async Task<List<List<int>>> FindFingerprintGroupsAsync(int[] candidateVideoIds, string algorithm, CancellationToken ct)
    {
        var rows = new List<DuplicateFingerprintCandidate>();
        foreach (var chunk in candidateVideoIds.Chunk(QueryChunkSize))
        {
            rows.AddRange(await db.VideoFiles
                .Where(file => file.VideoId.HasValue && chunk.Contains(file.VideoId.Value))
                .SelectMany(
                    file => file.Fingerprints.Where(fingerprint =>
                        (algorithm == "any" ? fingerprint.Type == "oshash" || fingerprint.Type == "md5" : fingerprint.Type == algorithm)
                        && fingerprint.Value != ""),
                    (file, fingerprint) => new DuplicateFingerprintCandidate(
                        file.VideoId!.Value,
                        fingerprint.Type,
                        fingerprint.Value))
                .AsNoTracking()
                .ToListAsync(ct));
        }

        return DistinctGroups(rows
            .GroupBy(row => (row.Type, row.Value))
            .Select(group => group.Select(row => row.VideoId)));
    }

    private async Task<List<List<int>>> FindTitleGroupsAsync(int[] candidateVideoIds, CancellationToken ct)
    {
        var rows = new List<DuplicateTitleCandidate>();
        foreach (var chunk in candidateVideoIds.Chunk(QueryChunkSize))
        {
            rows.AddRange(await db.Videos
                .Where(video => chunk.Contains(video.Id) && video.Title != null && video.Title != "")
                .Select(video => new DuplicateTitleCandidate(video.Id, video.Title!))
                .AsNoTracking()
                .ToListAsync(ct));
        }

        return DistinctGroups(rows
            .GroupBy(row => row.Title.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(row => row.VideoId)));
    }

    private async Task<List<List<int>>> FindRemoteIdGroupsAsync(int[] candidateVideoIds, CancellationToken ct)
    {
        var rows = new List<DuplicateRemoteIdCandidate>();
        foreach (var chunk in candidateVideoIds.Chunk(QueryChunkSize))
        {
            rows.AddRange(await db.Set<VideoRemoteId>()
                .Where(remoteId => chunk.Contains(remoteId.VideoId) && remoteId.RemoteId != "")
                .Select(remoteId => new DuplicateRemoteIdCandidate(
                    remoteId.VideoId,
                    remoteId.Endpoint,
                    remoteId.RemoteId))
                .AsNoTracking()
                .ToListAsync(ct));
        }

        return DistinctGroups(rows
            .GroupBy(
                row => $"{row.Endpoint.Trim()}\n{row.RemoteId.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(row => row.VideoId)));
    }

    private async Task<List<List<int>>> FindPhashGroupsAsync(
        int[] candidateVideoIds,
        int maxDistance,
        double maxDurationDifference,
        IJobProgress progress,
        CancellationToken ct)
    {
        progress.Report(0.02, "Loading visual fingerprints");
        var candidates = new List<DuplicatePHashCandidate>();
        foreach (var chunk in candidateVideoIds.Chunk(QueryChunkSize))
        {
            var rows = await db.VideoFiles
                .Where(file => file.VideoId.HasValue && chunk.Contains(file.VideoId.Value))
                .SelectMany(
                    file => file.Fingerprints.Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != ""),
                    (file, fingerprint) => new { VideoId = file.VideoId!.Value, file.Duration, fingerprint.Value })
                .AsNoTracking()
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (TryParsePHash(row.Value, out var hash))
                    candidates.Add(new DuplicatePHashCandidate(row.VideoId, row.Duration, hash));
            }
        }

        var sorted = candidates.OrderBy(candidate => candidate.Duration).ThenBy(candidate => candidate.VideoId).ToArray();
        progress.DeclareUnitCount(sorted.Length);
        if (sorted.Length < 2)
            return [];

        var index = new PHashMultiIndex(sorted, Math.Clamp(maxDistance, 0, 64));
        var matches = new ConcurrentDictionary<(int Left, int Right), byte>();
        long comparisonCount = 0;
        var matchCount = 0;
        var complexityExceeded = 0;
        var matchLimitExceeded = 0;
        var result = await jobService.RunBatchAsync(
            Enumerable.Range(0, sorted.Length),
            BulkDeletionJobService.ResolveMaxParallelism(config, Environment.ProcessorCount),
            (leftIndex, unit, innerCt) =>
            {
                if (Volatile.Read(ref complexityExceeded) != 0 || Volatile.Read(ref matchLimitExceeded) != 0)
                    throw new DuplicateSearchComplexityException();
                CompareCandidate(
                    index,
                    leftIndex,
                    Math.Clamp(maxDistance, 0, 64),
                    Math.Max(0, maxDurationDifference),
                    (left, right) =>
                    {
                        if (Volatile.Read(ref matchLimitExceeded) != 0)
                            throw new DuplicateSearchComplexityException();
                        var pair = left.VideoId < right.VideoId
                            ? (left.VideoId, right.VideoId)
                            : (right.VideoId, left.VideoId);
                        if (matches.TryAdd(pair, 0)
                            && Interlocked.Increment(ref matchCount) > MaximumPHashMatches)
                        {
                            Interlocked.Exchange(ref matchLimitExceeded, 1);
                            throw new DuplicateSearchComplexityException();
                        }
                    },
                    () =>
                    {
                        if (Interlocked.Increment(ref comparisonCount) > MaximumPHashComparisons)
                        {
                            Interlocked.Exchange(ref complexityExceeded, 1);
                            throw new DuplicateSearchComplexityException();
                        }
                    },
                    innerCt);
                unit.Complete(JobUnitOutcome.Succeeded);
                return Task.CompletedTask;
            },
            progress,
            unitIdFactory: (_, position) => position.ToString(CultureInfo.InvariantCulture),
            labelFactory: _ => "Comparing visual fingerprints",
            ct: ct);
        if (Volatile.Read(ref complexityExceeded) != 0)
        {
            throw new InvalidOperationException(
                $"The visual search exceeded {MaximumPHashComparisons.ToString("N0", CultureInfo.InvariantCulture)} comparisons. Reduce the pHash distance or duration delta and try again.");
        }
        if (Volatile.Read(ref matchLimitExceeded) != 0)
        {
            throw new InvalidOperationException(
                $"The visual search found more than {MaximumPHashMatches.ToString("N0", CultureInfo.InvariantCulture)} direct matches. Reduce the pHash distance or duration delta and try again.");
        }
        if (result.FailedUnits > 0)
            throw new InvalidOperationException($"{result.FailedUnits.ToString(CultureInfo.InvariantCulture)} pHash comparison units failed.");
        return BuildConnectedGroups(matches.Keys);
    }

    private async Task<int> PersistGroupsAsync(Guid searchId, IReadOnlyList<List<int>> groups, CancellationToken ct)
    {
        var allVideoIds = groups.SelectMany(group => group).Distinct().ToArray();
        var scores = new Dictionary<int, DuplicateKeeperScore>();
        foreach (var chunk in allVideoIds.Chunk(QueryChunkSize))
        {
            var videos = await db.Videos
                .Where(video => chunk.Contains(video.Id))
                .Include(video => video.Files)
                .Include(video => video.VideoTags)
                .Include(video => video.VideoPerformers)
                .Include(video => video.VideoGalleries)
                .Include(video => video.Urls)
                .Include(video => video.RemoteIds)
                .AsNoTracking()
                .ToListAsync(ct);
            foreach (var video in videos)
            {
                var file = video.Files.OrderByDescending(file => (long)file.Width * file.Height).ThenByDescending(file => file.Size).FirstOrDefault();
                var metadataCount = new object?[] { video.Title, video.Code, video.Details, video.Director, video.Date, video.StudioId, video.Captions, video.ImageBlobId }
                    .Count(value => value is not null && !string.IsNullOrWhiteSpace(value.ToString()))
                    + video.VideoTags.Count + video.VideoPerformers.Count + video.VideoGalleries.Count + video.Urls.Count + video.RemoteIds.Count;
                var score = new DuplicateKeeperScore(
                    video.Id,
                    video.MaxResolution,
                    video.MaxFileSize,
                    file?.BitRate ?? 0,
                    file?.Duration ?? 0,
                    NormalizeCodec(file?.VideoCodec),
                    metadataCount,
                    video.CreatedAt);
                scores[score.VideoId] = score;
            }
        }
        var searchOptions = await db.DuplicateSearches.AsNoTracking().SingleAsync(item => item.Id == searchId, ct);
        var preferredCodecs = DeserializeList(searchOptions.PreferredCodecsJson);
        var keeperRules = DeserializeList(searchOptions.KeeperRulesJson);
        var boundedGroups = PreparePersistedGroups(
            groups,
            MaximumPersistedGroupSize,
            MaximumPersistedGroupCount,
            ids => ChooseKeeper(ids, scores, searchOptions.RankingMode, preferredCodecs, keeperRules));

        var existingGroups = db.DuplicateSearchGroups.Where(group => group.SearchId == searchId);
        if (db.Database.IsRelational())
        {
            await existingGroups.ExecuteDeleteAsync(ct);
        }
        else
        {
            db.DuplicateSearchGroups.RemoveRange(await existingGroups.ToListAsync(ct));
            await db.SaveChangesAsync(ct);
        }
        for (var batchStart = 0; batchStart < boundedGroups.Count; batchStart += PersistGroupBatchSize)
        {
            var batchEnd = Math.Min(batchStart + PersistGroupBatchSize, boundedGroups.Count);
            var definitions = new List<PersistedGroupDefinition>(batchEnd - batchStart);
            for (var position = batchStart; position < batchEnd; position++)
            {
                ct.ThrowIfCancellationRequested();
                var videoIds = boundedGroups[position].VideoIds;
                var keeperId = boundedGroups[position].KeeperId;
                var risk = CalculateRisk(videoIds, scores);
                var runnerUp = RankVideos(videoIds, scores, searchOptions.RankingMode, preferredCodecs, keeperRules).Skip(1).FirstOrDefault();
                definitions.Add(new PersistedGroupDefinition(
                    new DuplicateSearchGroup
                    {
                        SearchId = searchId,
                        Position = position,
                        RecommendedVideoId = keeperId,
                        RecommendationReason = runnerUp == 0
                            ? "Only one candidate."
                            : $"Recommended #{keeperId.ToString(CultureInfo.InvariantCulture)} over #{runnerUp.ToString(CultureInfo.InvariantCulture)} using {searchOptions.RankingMode} ranking.",
                        RiskScore = risk.Score,
                        RiskNotesJson = JsonSerializer.Serialize(risk.Notes),
                    },
                    videoIds,
                    keeperId));
            }

            db.DuplicateSearchGroups.AddRange(definitions.Select(definition => definition.Entity));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            var pendingItems = new List<DuplicateSearchItem>(PersistItemBatchSize);
            foreach (var definition in definitions)
            {
                foreach (var videoId in definition.VideoIds)
                {
                    pendingItems.Add(new DuplicateSearchItem
                    {
                        GroupId = definition.Entity.Id,
                        VideoId = videoId,
                        Keep = videoId == definition.KeeperId,
                    });
                    if (pendingItems.Count == PersistItemBatchSize)
                    {
                        db.DuplicateSearchItems.AddRange(pendingItems);
                        await db.SaveChangesAsync(ct);
                        db.ChangeTracker.Clear();
                        pendingItems.Clear();
                    }
                }
            }
            if (pendingItems.Count > 0)
            {
                db.DuplicateSearchItems.AddRange(pendingItems);
                await db.SaveChangesAsync(ct);
                db.ChangeTracker.Clear();
            }
        }

        var search = await db.DuplicateSearches.FirstAsync(item => item.Id == searchId, ct);
        search.Status = DuplicateSearchStatus.Completed;
        search.GroupCount = boundedGroups.Count;
        search.VideoCount = allVideoIds.Length;
        search.CompletedAt = DateTime.UtcNow;
        search.ExpiresAt = DateTime.UtcNow.Add(ResultRetention);
        search.Error = null;
        await db.SaveChangesAsync(ct);
        return boundedGroups.Count;
    }

    private async Task SetTerminalStatusAsync(Guid searchId, DuplicateSearchStatus status, string? error)
    {
        db.ChangeTracker.Clear();
        var search = await db.DuplicateSearches.FirstOrDefaultAsync(item => item.Id == searchId, CancellationToken.None);
        if (search is null)
            return;
        search.Status = status;
        search.Error = string.IsNullOrWhiteSpace(error) ? null : error[..Math.Min(error.Length, 2_000)];
        search.CompletedAt = DateTime.UtcNow;
        search.ExpiresAt = DateTime.UtcNow.Add(ResultRetention);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    internal static PhashGroupingResult FindPhashGroupsForTests(
        IReadOnlyCollection<DuplicatePHashCandidate> candidates,
        int maxDistance,
        double maxDurationDifference)
    {
        var sorted = candidates.OrderBy(candidate => candidate.Duration).ThenBy(candidate => candidate.VideoId).ToArray();
        var index = new PHashMultiIndex(sorted, Math.Clamp(maxDistance, 0, 64));
        var matches = new HashSet<(int Left, int Right)>();
        long comparisons = 0;
        for (var leftIndex = 0; leftIndex < sorted.Length; leftIndex++)
        {
            CompareCandidate(
                index,
                leftIndex,
                Math.Clamp(maxDistance, 0, 64),
                Math.Max(0, maxDurationDifference),
                (left, right) => matches.Add(left.VideoId < right.VideoId
                    ? (left.VideoId, right.VideoId)
                    : (right.VideoId, left.VideoId)),
                () => comparisons++,
                CancellationToken.None);
        }
        return new PhashGroupingResult(
            BuildConnectedGroups(matches),
            comparisons);
    }

    private static List<List<int>> BuildConnectedGroups(IEnumerable<(int Left, int Right)> matches)
    {
        var parent = new Dictionary<int, int>();
        foreach (var (left, right) in matches)
        {
            parent.TryAdd(left, left);
            parent.TryAdd(right, right);
            Union(parent, left, right);
        }

        return parent.Keys
            .GroupBy(id => Find(parent, id))
            .Select(group => group.OrderBy(id => id).ToList())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0])
            .ToList();
    }

    private static int Find(IDictionary<int, int> parent, int id)
    {
        var root = id;
        while (parent[root] != root)
            root = parent[root];
        while (parent[id] != id)
        {
            var next = parent[id];
            parent[id] = root;
            id = next;
        }
        return root;
    }

    private static void Union(IDictionary<int, int> parent, int left, int right)
    {
        var leftRoot = Find(parent, left);
        var rightRoot = Find(parent, right);
        if (leftRoot == rightRoot)
            return;

        // Stable roots keep the resulting groups deterministic regardless of parallel edge order.
        if (leftRoot < rightRoot)
            parent[rightRoot] = leftRoot;
        else
            parent[leftRoot] = rightRoot;
    }

    internal static List<int[]> SplitOversizedGroups(
        IEnumerable<IEnumerable<int>> groups,
        int maximumGroupSize = MaximumPersistedGroupSize,
        int maximumGroupCount = MaximumPersistedGroupCount)
        => PreparePersistedGroups(
                groups,
                maximumGroupSize,
                maximumGroupCount,
                ids => ids[0])
            .Select(group => group.VideoIds)
            .ToList();

    private static List<BoundedDuplicateGroup> PreparePersistedGroups(
        IEnumerable<IEnumerable<int>> groups,
        int maximumGroupSize,
        int maximumGroupCount,
        Func<int[], int> keeperSelector)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGroupSize, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGroupCount, 1);
        var result = new List<BoundedDuplicateGroup>();
        foreach (var group in groups)
        {
            var ids = group.Distinct().OrderBy(id => id).ToArray();
            if (ids.Length < 2)
                continue;

            var keeperId = keeperSelector(ids);
            if (!ids.Contains(keeperId))
                throw new InvalidOperationException("The duplicate-group keeper must belong to its group.");

            if (ids.Length <= maximumGroupSize)
            {
                result.Add(new BoundedDuplicateGroup(ids, keeperId));
                ThrowIfTooManyGroups(result.Count, maximumGroupCount);
                continue;
            }

            // Each persisted chunk shares the logical group's keeper. The global kept-video rule can
            // therefore delete every other member even though the UI pages groups in bounded rows.
            foreach (var chunk in ids.Where(id => id != keeperId).Chunk(maximumGroupSize - 1))
            {
                result.Add(new BoundedDuplicateGroup([keeperId, .. chunk], keeperId));
                ThrowIfTooManyGroups(result.Count, maximumGroupCount);
            }
        }
        return result;
    }

    private static void ThrowIfTooManyGroups(int groupCount, int maximumGroupCount)
    {
        if (groupCount > maximumGroupCount)
        {
            throw new InvalidOperationException(
                $"The search found more than {maximumGroupCount.ToString("N0", CultureInfo.InvariantCulture)} duplicate groups. Narrow the search and try again.");
        }
    }

    private static void CompareCandidate(
        PHashMultiIndex index,
        int leftIndex,
        int maxDistance,
        double maxDurationDifference,
        Action<DuplicatePHashCandidate, DuplicatePHashCandidate> match,
        Action? comparisonCounter,
        CancellationToken ct)
    {
        var left = index.Candidates[leftIndex];
        HashSet<int>? seen = index.SegmentCount > 1 ? [] : null;
        for (var segment = 0; segment < index.SegmentCount; segment++)
        {
            ct.ThrowIfCancellationRequested();
            var bucket = index.GetBucket(segment, left.Hash);
            var position = bucket.BinarySearch(leftIndex);
            if (position < 0)
                continue;
            for (var bucketPosition = position + 1; bucketPosition < bucket.Count; bucketPosition++)
            {
                var rightIndex = bucket[bucketPosition];
                var right = index.Candidates[rightIndex];
                if (right.Duration - left.Duration > maxDurationDifference)
                    break;
                if (seen is not null && !seen.Add(rightIndex))
                    continue;
                if (left.VideoId == right.VideoId)
                    continue;

                comparisonCounter?.Invoke();
                if (BitOperations.PopCount(left.Hash ^ right.Hash) <= maxDistance)
                    match(left, right);
            }
        }
    }

    private static List<List<int>> DistinctGroups(IEnumerable<IEnumerable<int>> groups)
    {
        return groups
            .Select(group => group.Distinct().OrderBy(id => id).ToArray())
            .Where(group => group.Length > 1)
            .DistinctBy(group => string.Join(',', group))
            .OrderBy(group => group[0])
            .ThenBy(group => group.Length)
            .Select(group => group.ToList())
            .ToList();
    }

    private static bool TryParsePHash(string value, out ulong hash)
    {
        hash = 0;
        var normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[2..];
        return normalized.Length is > 0 and <= 16
            && normalized.All(Uri.IsHexDigit)
            && ulong.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out hash);
    }

    private sealed class PHashMultiIndex
    {
        private readonly Dictionary<(int Segment, ulong Value), List<int>> _buckets = [];
        private readonly int[] _offsets;
        private readonly int[] _widths;

        public PHashMultiIndex(DuplicatePHashCandidate[] candidates, int maxDistance)
        {
            Candidates = candidates;
            SegmentCount = maxDistance >= 64 ? 1 : maxDistance + 1;
            _offsets = new int[SegmentCount];
            _widths = new int[SegmentCount];
            if (maxDistance < 64)
            {
                var baseWidth = 64 / SegmentCount;
                var remainder = 64 % SegmentCount;
                var offset = 0;
                for (var segment = 0; segment < SegmentCount; segment++)
                {
                    _offsets[segment] = offset;
                    _widths[segment] = baseWidth + (segment < remainder ? 1 : 0);
                    offset += _widths[segment];
                }
            }

            for (var candidateIndex = 0; candidateIndex < candidates.Length; candidateIndex++)
            {
                for (var segment = 0; segment < SegmentCount; segment++)
                {
                    var key = (segment, SegmentValue(candidates[candidateIndex].Hash, segment));
                    if (!_buckets.TryGetValue(key, out var bucket))
                    {
                        bucket = [];
                        _buckets[key] = bucket;
                    }
                    bucket.Add(candidateIndex);
                }
            }
        }

        public DuplicatePHashCandidate[] Candidates { get; }
        public int SegmentCount { get; }

        public List<int> GetBucket(int segment, ulong hash)
            => _buckets[(segment, SegmentValue(hash, segment))];

        private ulong SegmentValue(ulong hash, int segment)
        {
            var width = _widths[segment];
            var mask = width == 64 ? ulong.MaxValue : width == 0 ? 0 : (1UL << width) - 1;
            return (hash >> _offsets[segment]) & mask;
        }
    }

    private sealed record DuplicateFingerprintCandidate(int VideoId, string Type, string Value);
    private sealed record DuplicateTitleCandidate(int VideoId, string Title);
    private sealed record DuplicateRemoteIdCandidate(int VideoId, string Endpoint, string RemoteId);
    private static int ChooseKeeper(int[] ids, IReadOnlyDictionary<int, DuplicateKeeperScore> scores, string rankingMode, string[] preferredCodecs, string[] keeperRules)
        => RankVideos(ids, scores, rankingMode, preferredCodecs, keeperRules).First();

    private static IEnumerable<int> RankVideos(IEnumerable<int> ids, IReadOnlyDictionary<int, DuplicateKeeperScore> scores, string rankingMode, string[] preferredCodecs, string[] keeperRules)
    {
        var ordered = ids.Select(id => scores.GetValueOrDefault(id) ?? new DuplicateKeeperScore(id, 0, 0, 0, 0, string.Empty, 0, DateTime.MaxValue));
        IOrderedEnumerable<DuplicateKeeperScore>? result = null;
        if (string.Equals(rankingMode, "custom", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var rule in keeperRules)
            {
                Func<DuplicateKeeperScore, IComparable> selector = rule switch
                {
                    "metadata" => score => score.MetadataCount,
                    "resolution" => score => score.Resolution,
                    "duration" => score => score.Duration,
                    "codec" => score => CodecPreference(score.Codec, preferredCodecs),
                    "bitrate" => score => score.BitRate,
                    "size" => score => score.FileSize,
                    "newest" => score => score.CreatedAt,
                    "oldest" => score => -score.CreatedAt.Ticks,
                    _ => score => 0,
                };
                result = result is null ? ordered.OrderByDescending(selector) : result.ThenByDescending(selector);
            }
        }
        result ??= ordered.OrderByDescending(score => score.Resolution)
            .ThenByDescending(score => score.BitRate * CodecQualityFactor(score.Codec))
            .ThenByDescending(score => score.Duration)
            .ThenByDescending(score => score.MetadataCount)
            .ThenBy(score => score.FileSize)
            .ThenBy(score => score.CreatedAt);
        return result.ThenBy(score => score.VideoId).Select(score => score.VideoId);
    }

    private static (int Score, string[] Notes) CalculateRisk(int[] ids, IReadOnlyDictionary<int, DuplicateKeeperScore> scores)
    {
        var values = ids.Select(id => scores.GetValueOrDefault(id)).Where(score => score is not null).Cast<DuplicateKeeperScore>().ToArray();
        if (values.Length < 2) return (0, []);
        var factor = 1;
        var notes = new List<string>();
        if (values.Length > 4) notes.Add($"{values.Length.ToString(CultureInfo.InvariantCulture)} videos in one group");
        var shortest = values.Min(score => score.Duration);
        var longest = values.Max(score => score.Duration);
        if (shortest > 0 && shortest < 30) { factor += 2; notes.Add("short clip"); }
        if (longest > 0 && (longest - shortest) / longest > .25) { factor++; notes.Add("durations differ by over 25%"); }
        if (values.Select(score => score.Resolution).Distinct().Count() > 1) { factor++; notes.Add("mixed resolutions"); }
        return ((values.Length - 1) * factor, [.. notes]);
    }

    private static int CodecPreference(string codec, string[] preferredCodecs)
    {
        var index = Array.FindIndex(preferredCodecs, value => string.Equals(NormalizeCodec(value), codec, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? -1_000 : preferredCodecs.Length - index;
    }

    private static double CodecQualityFactor(string codec) => codec switch
    {
        "av1" => 1.55,
        "hevc" => 1.35,
        "vp9" => 1.25,
        "h264" => 1,
        _ => .9,
    };

    private static string NormalizeCodec(string? value)
    {
        var codec = (value ?? string.Empty).Trim().ToLowerInvariant().Replace(".", "").Replace("_", "").Replace("-", "");
        return codec switch { "h265" => "hevc", "avc" or "avc1" => "h264", "vp90" => "vp9", "av01" => "av1", _ => codec };
    }

    private sealed record DuplicateKeeperScore(int VideoId, int Resolution, long FileSize, long BitRate, double Duration, string Codec, int MetadataCount, DateTime CreatedAt);
    private sealed record BoundedDuplicateGroup(int[] VideoIds, int KeeperId);
    private sealed record PersistedGroupDefinition(DuplicateSearchGroup Entity, int[] VideoIds, int KeeperId);
    private sealed class DuplicateSearchComplexityException : Exception
    {
    }
}

internal readonly record struct DuplicatePHashCandidate(int VideoId, double Duration, ulong Hash);
internal sealed record PhashGroupingResult(IReadOnlyList<List<int>> Groups, long ComparisonCount);

internal static class DuplicateSearchDeletionClaim
{
    // Recovery also releases durable keeper reservations, so it is safe to run only after the
    // migration that introduces that table, not merely after the initial search-result tables.
    public const string MigrationId = "20260824154058_AddDurableDeletionOutbox";
    public const string Prefix = "~";

    public static string Create() => Prefix + Guid.NewGuid().ToString("N")[..31];
}

public sealed class DuplicateSearchRecoveryService(
    IServiceScopeFactory scopeFactory,
    ILogger<DuplicateSearchRecoveryService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Do not make host readiness wait for retention cascades on a large result set.
        await Task.Yield();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(stoppingToken);
            if (!appliedMigrations.Contains(DuplicateSearchDeletionClaim.MigrationId, StringComparer.Ordinal))
                return;

            await RecoverAsync(db, DateTime.UtcNow, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to recover durable duplicate-search jobs during startup.");
        }
    }

    internal static async Task RecoverAsync(CoveContext db, DateTime now, CancellationToken ct)
    {
        await db.DuplicateSearches
            .Where(search => search.Status == DuplicateSearchStatus.Pending || search.Status == DuplicateSearchStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(search => search.Status, DuplicateSearchStatus.Interrupted)
                .SetProperty(search => search.CompletedAt, now)
                .SetProperty(search => search.Error, "The server stopped before this search completed."), ct);
        await db.DuplicateSearches
            .Where(search => search.DeletionJobId != null
                && search.DeletionJobId.StartsWith(DuplicateSearchDeletionClaim.Prefix))
            .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);

        // In-memory deletion jobs do not survive a restart. Release their keeper constraints so the
        // reconciled search can be queued again; the physical-file outbox is recovered separately.
        await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ExecuteDeleteAsync(ct);

        var claimedSearchIds = await db.DuplicateSearches
            .Where(search => search.DeletionJobId != null
                && !search.DeletionJobId.StartsWith(DuplicateSearchDeletionClaim.Prefix))
            .Select(search => search.Id)
            .ToArrayAsync(ct);
        foreach (var chunk in claimedSearchIds.Chunk(4_000))
        {
            var incompleteSearchIds = await db.DuplicateSearchItems
                .Where(item => item.Group != null
                    && chunk.Contains(item.Group.SearchId)
                    && !item.Keep
                    && item.Group.Items.Any(keeper => keeper.Keep)
                    && !db.DuplicateSearchItems.Any(keeper => keeper.Group != null
                        && keeper.Group.SearchId == item.Group.SearchId
                        && keeper.VideoId == item.VideoId
                        && keeper.Keep))
                .Join(db.Videos, item => item.VideoId, video => video.Id, (item, _) => item.Group!.SearchId)
                .Distinct()
                .ToArrayAsync(ct);
            if (incompleteSearchIds.Length > 0)
            {
                await db.DuplicateSearches
                    .Where(search => incompleteSearchIds.Contains(search.Id)
                        && search.DeletionJobId != null
                        && !search.DeletionJobId.StartsWith(DuplicateSearchDeletionClaim.Prefix))
                    .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
            }
        }

        await db.DuplicateSearches.Where(search => search.ExpiresAt < now).ExecuteDeleteAsync(ct);
        await db.ImageDuplicateSearches
            .Where(search => search.Status == DuplicateSearchStatus.Pending || search.Status == DuplicateSearchStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(search => search.Status, DuplicateSearchStatus.Interrupted)
                .SetProperty(search => search.CompletedAt, now)
                .SetProperty(search => search.Error, "The server stopped before this image search completed."), ct);
        await db.ImageDuplicateSearches
            .Where(search => search.CleanupJobId != null && search.CleanupJobId.StartsWith(DuplicateSearchDeletionClaim.Prefix))
            .ExecuteUpdateAsync(update => update.SetProperty(search => search.CleanupJobId, (string?)null), ct);
        await db.ImageDuplicateKeeperReservations.IgnoreQueryFilters().ExecuteDeleteAsync(ct);
        await db.ImageDuplicateSearches.Where(search => search.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }
}
