using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using Cove.Core.Auth;
using Cove.Core.Common;
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
    internal const int MaximumPHashDistance = 16;
    internal static readonly TimeSpan ResultRetention = TimeSpan.FromDays(7);

    public async Task<DuplicateSearchStarted> StartAsync(
        JobOwner? owner,
        CovePrincipal? principal,
        DuplicateSearchStartRequest request,
        IReadOnlyCollection<int>? candidateVideoIds,
        CancellationToken ct)
    {
        var ids = candidateVideoIds?.Where(id => id > 0).Distinct().ToArray();
        var matchType = NormalizeMatchType(request.MatchType);
        var search = new DuplicateSearch
        {
            OwnerKey = owner?.Key,
            MatchType = matchType,
            Distance = Math.Clamp(request.Distance, 0, MaximumPHashDistance),
            DurationDifference = Math.Clamp(request.DurationDiff ?? 5, 0, 3_600),
            IncludePaths = NormalizeScopePaths(request.IncludePaths),
            ExcludePaths = NormalizeScopePaths(request.ExcludePaths),
            MinimumDuration = Math.Clamp(request.MinimumDuration, 0, 86_400),
            KeeperRulesJson = DuplicateKeeperRules.Serialize(DuplicateKeeperRules.Normalize(request.KeeperRules)),
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
        return new DuplicateSearchStarted(search.Id, jobId, search.CandidateCount);
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
    /// Videos a resolution of the given groups would remove: members not kept in their group, in a group
    /// that still keeps someone, and not kept by any other group of the same search. Oversized logical
    /// groups are persisted as chunks sharing one keeper, so the cross-group rule is what lets every
    /// chunk remove its non-keepers without ever removing a video another chunk keeps.
    /// </summary>
    internal static IQueryable<int> EffectiveUnkeptVideoIds(
        CoveContext context,
        Guid searchId,
        IQueryable<DuplicateSearchGroup>? groups = null)
    {
        var keptVideoIds = context.DuplicateSearchItems
            .Where(item => item.Group != null && item.Group.SearchId == searchId && item.Keep)
            .Select(item => item.VideoId);
        var groupIds = (groups ?? context.DuplicateSearchGroups.Where(group => group.SearchId == searchId))
            .Select(group => group.Id);
        return context.DuplicateSearchItems
            .Where(item => item.Group != null
                && item.Group.SearchId == searchId
                && groupIds.Contains(item.GroupId)
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

    internal static string[] NormalizeScopePaths(IReadOnlyList<string>? paths)
        => (paths ?? [])
            .Select(path => (path ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/'))
            .Where(path => path.Length > 0)
            .Distinct(FilesystemPaths.PathComparer)
            .ToArray();

    internal static bool IsAtOrBelow(string candidatePath, string folder)
    {
        var candidate = candidatePath.Replace('\\', '/');
        return candidate.Equals(folder, FilesystemPaths.PathComparison)
            || candidate.StartsWith(folder + "/", FilesystemPaths.PathComparison);
    }

    private static string DescribeMatchType(string matchType) => matchType switch
    {
        "phash" => "visual similarity",
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
    /// <summary>Buckets larger than this are unioned linearly; per-pair ignore checks would be quadratic.</summary>
    private const int MaximumPairwiseBucketSize = 200;
    internal const int MaximumPersistedGroupSize = 50;
    internal const int MaximumPersistedGroupCount = 25_000;
    internal const long MaximumPHashComparisons = 50_000_000;
    internal const int MaximumPHashMatches = MaximumPersistedGroupCount;

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
                .Where(item => item.Id != searchId && item.ExpiresAt < now && item.DeletionJobId == null);
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

            progress.Report(0.01, "Loading videos in scope");
            var ids = candidateVideoIds is null
                ? await ResolveCandidateIdsAsync(search, ct)
                : candidateVideoIds.Where(id => id > 0).Distinct().ToArray();
            search.CandidateCount = ids.Length;
            await db.SaveChangesAsync(ct);

            progress.Report(0.03, "Loading pairs marked as not duplicates");
            var ignored = await LoadIgnoredPairsAsync(ids, ct);

            List<List<int>> groups;
            switch (search.MatchType)
            {
                case "phash":
                    groups = await FindPhashGroupsAsync(ids, search.Distance, search.DurationDifference, ignored, progress, ct);
                    break;
                case "title":
                    progress.Report(0.1, "Comparing video titles");
                    groups = await FindTitleGroupsAsync(ids, ignored, ct);
                    break;
                case "remoteId":
                    progress.Report(0.1, "Comparing remote IDs");
                    groups = await FindRemoteIdGroupsAsync(ids, ignored, ct);
                    break;
                default:
                    progress.Report(0.1, "Comparing file fingerprints");
                    groups = await FindFingerprintGroupsAsync(ids, ignored, ct);
                    break;
            }

            ct.ThrowIfCancellationRequested();
            progress.Report(0.9, "Choosing keepers");
            var persistedGroupCount = await PersistGroupsAsync(
                searchId,
                groups,
                DuplicateKeeperRules.Deserialize(search.KeeperRulesJson),
                progress,
                ct);
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
        var includes = search.IncludePaths ?? [];
        var excludes = search.ExcludePaths ?? [];
        if (includes.Length == 0 && excludes.Length == 0 && search.MinimumDuration <= 0)
        {
            // Title and remote-ID matching also apply to metadata-only videos, so an unscoped search
            // must not require a file.
            return await db.Videos.AsNoTracking().Select(video => video.Id).ToArrayAsync(ct);
        }

        var minimumDuration = search.MinimumDuration;
        var videoQuery = db.Videos.AsNoTracking();
        if (minimumDuration > 0)
            videoQuery = videoQuery.Where(video => video.MaxDuration >= minimumDuration);
        if (includes.Length == 0 && excludes.Length == 0)
            return await videoQuery.Select(video => video.Id).ToArrayAsync(ct);

        var files = await videoQuery
            .SelectMany(video => video.Files.Select(file => new { VideoId = video.Id, file.Path }))
            .ToListAsync(ct);
        return files
            .Where(file => (includes.Length == 0 || includes.Any(folder => DuplicateSearchJobService.IsAtOrBelow(file.Path, folder)))
                && !excludes.Any(folder => DuplicateSearchJobService.IsAtOrBelow(file.Path, folder)))
            .Select(file => file.VideoId)
            .Distinct()
            .ToArray();
    }

    private async Task<HashSet<(int Low, int High)>> LoadIgnoredPairsAsync(int[] candidateIds, CancellationToken ct)
    {
        var candidates = candidateIds.ToHashSet();
        var pairs = await db.DuplicateIgnoredPairs
            .AsNoTracking()
            .Select(pair => new { pair.LowVideoId, pair.HighVideoId })
            .ToListAsync(ct);
        return pairs
            .Where(pair => candidates.Contains(pair.LowVideoId) && candidates.Contains(pair.HighVideoId))
            .Select(pair => (pair.LowVideoId, pair.HighVideoId))
            .ToHashSet();
    }

    private async Task<List<List<int>>> FindFingerprintGroupsAsync(
        int[] candidateVideoIds,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
    {
        var rows = new List<DuplicateFingerprintCandidate>();
        foreach (var chunk in candidateVideoIds.Chunk(QueryChunkSize))
        {
            rows.AddRange(await db.VideoFiles
                .Where(file => file.VideoId.HasValue && chunk.Contains(file.VideoId.Value))
                .SelectMany(
                    file => file.Fingerprints.Where(fingerprint =>
                        (fingerprint.Type == "oshash" || fingerprint.Type == "md5")
                        && fingerprint.Value != ""),
                    (file, fingerprint) => new DuplicateFingerprintCandidate(
                        file.VideoId!.Value,
                        fingerprint.Type,
                        fingerprint.Value))
                .AsNoTracking()
                .ToListAsync(ct));
        }

        return GroupBuckets(
            rows.GroupBy(row => (row.Type, row.Value)).Select(bucket => bucket.Select(row => row.VideoId)),
            ignored);
    }

    private async Task<List<List<int>>> FindTitleGroupsAsync(
        int[] candidateVideoIds,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
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

        return GroupBuckets(
            rows.GroupBy(row => NormalizeTitle(row.Title), StringComparer.OrdinalIgnoreCase)
                .Where(bucket => bucket.Key.Length > 0)
                .Select(bucket => bucket.Select(row => row.VideoId)),
            ignored);
    }

    private async Task<List<List<int>>> FindRemoteIdGroupsAsync(
        int[] candidateVideoIds,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
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

        return GroupBuckets(
            rows.GroupBy(
                    row => $"{row.Endpoint.Trim()}\n{row.RemoteId.Trim()}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(bucket => bucket.Select(row => row.VideoId)),
            ignored);
    }

    private async Task<List<List<int>>> FindPhashGroupsAsync(
        int[] candidateVideoIds,
        int maxDistance,
        double maxDurationDifference,
        HashSet<(int Low, int High)> ignored,
        IJobProgress progress,
        CancellationToken ct)
    {
        progress.Report(0.04, "Loading visual fingerprints");
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
                        if (ignored.Contains(pair))
                            return;
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
                $"The visual search exceeded {MaximumPHashComparisons.ToString("N0", CultureInfo.InvariantCulture)} comparisons. Use a stricter accuracy or a smaller duration tolerance and try again.");
        }
        if (Volatile.Read(ref matchLimitExceeded) != 0)
        {
            throw new InvalidOperationException(
                $"The visual search found more than {MaximumPHashMatches.ToString("N0", CultureInfo.InvariantCulture)} direct matches. Use a stricter accuracy or a smaller duration tolerance and try again.");
        }
        if (result.FailedUnits > 0)
            throw new InvalidOperationException($"{result.FailedUnits.ToString(CultureInfo.InvariantCulture)} pHash comparison units failed.");
        return BuildConnectedGroups(matches.Keys);
    }

    private async Task<int> PersistGroupsAsync(
        Guid searchId,
        IReadOnlyList<List<int>> groups,
        IReadOnlyList<DuplicateKeeperRule> rules,
        IJobProgress progress,
        CancellationToken ct)
    {
        var allVideoIds = groups.SelectMany(group => group).Distinct().ToArray();
        var facts = await DuplicateKeeperRules.LoadFactsAsync(db, allVideoIds, rules, ct);
        var boundedGroups = PreparePersistedGroups(
            groups,
            MaximumPersistedGroupSize,
            MaximumPersistedGroupCount,
            ids => DuplicateKeeperRules.Choose(ids, facts, rules));

        progress.Report(0.94, "Saving duplicate groups");
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
                var bounded = boundedGroups[position];
                definitions.Add(new PersistedGroupDefinition(
                    new DuplicateSearchGroup
                    {
                        SearchId = searchId,
                        Position = position,
                        Status = DuplicateGroupStatus.Unresolved,
                        DecisionSource = "auto",
                        DecisionRule = bounded.Choice.DecisionRule,
                    },
                    bounded.VideoIds,
                    bounded.Choice.KeeperId));
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
        search.ExpiresAt = DateTime.UtcNow.Add(DuplicateSearchJobService.ResultRetention);
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
        search.ExpiresAt = DateTime.UtcNow.Add(DuplicateSearchJobService.ResultRetention);
        await db.SaveChangesAsync(CancellationToken.None);
    }

    internal static PhashGroupingResult FindPhashGroupsForTests(
        IReadOnlyCollection<DuplicatePHashCandidate> candidates,
        int maxDistance,
        double maxDurationDifference,
        IReadOnlySet<(int Low, int High)>? ignored = null)
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
                (left, right) =>
                {
                    var pair = left.VideoId < right.VideoId
                        ? (left.VideoId, right.VideoId)
                        : (right.VideoId, left.VideoId);
                    if (ignored?.Contains(pair) != true)
                        matches.Add(pair);
                },
                () => comparisons++,
                CancellationToken.None);
        }
        return new PhashGroupingResult(
            BuildConnectedGroups(matches),
            comparisons);
    }

    /// <summary>
    /// Connects every pair of videos that share a bucket (a fingerprint, a title, a remote ID) unless the
    /// pair was marked as not duplicates. Components rather than raw buckets become groups, so an MD5 match
    /// and an OSHash match of the same videos collapse into one group.
    /// </summary>
    internal static List<List<int>> GroupBuckets(
        IEnumerable<IEnumerable<int>> buckets,
        IReadOnlySet<(int Low, int High)> ignored)
    {
        var edges = new List<(int Left, int Right)>();
        var ignoredVideoIds = ignored.SelectMany(pair => new[] { pair.Low, pair.High }).ToHashSet();
        foreach (var bucket in buckets)
        {
            var ids = bucket.Distinct().Order().ToArray();
            if (ids.Length < 2)
                continue;
            var touchesIgnored = ids.Length <= MaximumPairwiseBucketSize && ids.Any(ignoredVideoIds.Contains);
            if (!touchesIgnored)
            {
                for (var index = 1; index < ids.Length; index++)
                    edges.Add((ids[0], ids[index]));
                continue;
            }
            for (var left = 0; left < ids.Length; left++)
            {
                for (var right = left + 1; right < ids.Length; right++)
                {
                    if (!ignored.Contains((ids[left], ids[right])))
                        edges.Add((ids[left], ids[right]));
                }
            }
        }
        return BuildConnectedGroups(edges);
    }

    internal static string NormalizeTitle(string title)
        => string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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
                ids => new DuplicateKeeperChoice(ids.Min(), DuplicateKeeperRules.TieBreakRule))
            .Select(group => group.VideoIds)
            .ToList();

    private static List<BoundedDuplicateGroup> PreparePersistedGroups(
        IEnumerable<IEnumerable<int>> groups,
        int maximumGroupSize,
        int maximumGroupCount,
        Func<int[], DuplicateKeeperChoice> keeperSelector)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGroupSize, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGroupCount, 1);
        var result = new List<BoundedDuplicateGroup>();
        foreach (var group in groups)
        {
            var ids = group.Distinct().OrderBy(id => id).ToArray();
            if (ids.Length < 2)
                continue;

            var choice = keeperSelector(ids);
            if (!ids.Contains(choice.KeeperId))
                throw new InvalidOperationException("The duplicate-group keeper must belong to its group.");

            if (ids.Length <= maximumGroupSize)
            {
                result.Add(new BoundedDuplicateGroup(ids, choice));
                ThrowIfTooManyGroups(result.Count, maximumGroupCount);
                continue;
            }

            // Each persisted chunk shares the logical group's keeper. The global kept-video rule can
            // therefore delete every other member even though the UI pages groups in bounded rows.
            foreach (var chunk in ids.Where(id => id != choice.KeeperId).Chunk(maximumGroupSize - 1))
            {
                result.Add(new BoundedDuplicateGroup([choice.KeeperId, .. chunk], choice));
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
    private sealed record BoundedDuplicateGroup(int[] VideoIds, DuplicateKeeperChoice Choice);
    private sealed record PersistedGroupDefinition(DuplicateSearchGroup Entity, int[] VideoIds, int KeeperId);
    private sealed class DuplicateSearchComplexityException : Exception
    {
    }
}

internal readonly record struct DuplicatePHashCandidate(int VideoId, double Duration, ulong Hash);
internal sealed record PhashGroupingResult(IReadOnlyList<List<int>> Groups, long ComparisonCount);

internal static class DuplicateSearchDeletionClaim
{
    // Recovery also resets group review state, so it is safe to run only after the migration that
    // introduces the per-group resolution columns.
    public const string MigrationId = DuplicateResolutionMigration.Id;
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

    internal const string InterruptedResolutionError = "Cove restarted before this group was resolved. Nothing was removed after the restart; review it and resolve it again.";

    internal static async Task RecoverAsync(CoveContext db, DateTime now, CancellationToken ct)
    {
        await db.DuplicateSearches
            .Where(search => search.Status == DuplicateSearchStatus.Pending || search.Status == DuplicateSearchStatus.Running)
            .ExecuteUpdateAsync(update => update
                .SetProperty(search => search.Status, DuplicateSearchStatus.Interrupted)
                .SetProperty(search => search.CompletedAt, now)
                .SetProperty(search => search.Error, "The server stopped before this search completed."), ct);

        // Resolution workers are in-memory jobs, so none survive a restart. Groups they still owned
        // return to review with an explanation instead of silently resuming destructive work.
        await db.DuplicateSearchGroups
            .Where(group => group.Status == DuplicateGroupStatus.Queued || group.Status == DuplicateGroupStatus.Processing)
            .ExecuteUpdateAsync(update => update
                .SetProperty(group => group.Status, DuplicateGroupStatus.Failed)
                .SetProperty(group => group.Error, InterruptedResolutionError), ct);
        await db.DuplicateSearches
            .Where(search => search.DeletionJobId != null)
            .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
        await db.DuplicateDeletionKeeperReservations.IgnoreQueryFilters().ExecuteDeleteAsync(ct);

        await db.DuplicateSearches.Where(search => search.ExpiresAt < now).ExecuteDeleteAsync(ct);
    }
}
