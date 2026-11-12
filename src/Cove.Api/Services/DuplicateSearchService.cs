using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
    /// <summary>
    /// The match type that reviews the files attached to one video instead of separate videos. Its groups
    /// hold <see cref="DuplicateSearchFileItem"/> members and resolve by changing files, never whole videos.
    /// </summary>
    internal const string FilesMatchType = "files";
    internal const int MaximumScopePathCharacters = 1_048_576;
    internal static readonly TimeSpan ResultRetention = TimeSpan.FromDays(7);

    public async Task<DuplicateSearchStarted> StartAsync(
        JobOwner? owner,
        CovePrincipal? principal,
        DuplicateSearchStartRequest request,
        IReadOnlyCollection<int>? candidateVideoIds,
        CancellationToken ct)
    {
        var ids = candidateVideoIds is null ? null : DuplicateSearchLimits.NormalizeIds(candidateVideoIds);
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

        var description = matchType == FilesMatchType
            ? "Finding videos with more than one file"
            : $"Finding duplicate videos by {DescribeMatchType(matchType)}";
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
            "files" or "same-video" or "same_video" => FilesMatchType,
            _ => "fingerprint",
        };

    internal static string[] NormalizeScopePaths(IReadOnlyList<string>? paths)
    {
        var normalizedPaths = new List<string>();
        var distinctPaths = new HashSet<string>(FilesystemPaths.PathComparer);
        var retainedCharacters = 0;
        foreach (var rawPath in paths ?? [])
        {
            DuplicateSearchLimits.CheckFieldLength(rawPath?.Length ?? 0);
            var path = (rawPath ?? string.Empty).Trim().Replace('\\', '/').TrimEnd('/');
            if (path.Length == 0 || !distinctPaths.Add(path))
                continue;

            retainedCharacters += path.Length;
            if (retainedCharacters > MaximumScopePathCharacters)
                throw new InvalidOperationException($"Duplicate search scope paths exceed the {MaximumScopePathCharacters:N0}-character total limit. Use fewer or shorter paths and try again.");
            normalizedPaths.Add(path);
        }
        return normalizedPaths.ToArray();
    }

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
        FilesMatchType => "files on the same video",
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
    /// <summary>Left-hand fingerprints one job unit compares against their duration windows.</summary>
    private const int PHashComparisonChunkSize = 4_096;
    /// <summary>Matches a comparison unit collects before merging them into the shared grouping.</summary>
    private const int PHashMatchFlushSize = 4_096;
    /// <summary>
    /// A comparison is one XOR and one population count, so this is minutes of work rather than a memory
    /// bound. The count is known exactly before any comparison runs, so a search that would exceed it
    /// fails at once instead of part-way through.
    /// </summary>
    internal const long MaximumPHashComparisons = 500_000_000_000;
    /// <summary>The comparison limit in force; tests lower it to reach the failure without a huge library.</summary>
    internal long PHashComparisonLimit { get; init; } = MaximumPHashComparisons;

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
            var candidates = candidateVideoIds is null
                ? await ResolveCandidatesAsync(search, ct)
                : ExplicitCandidates(DuplicateSearchLimits.NormalizeIds(candidateVideoIds));
            search.CandidateCount = await candidates.CountAsync(ct);
            await db.SaveChangesAsync(ct);

            if (search.MatchType == DuplicateSearchJobService.FilesMatchType)
            {
                progress.Report(0.1, "Finding videos with more than one file");
                var fileGroups = await FindMultiFileGroupsAsync(candidates, ct);
                ct.ThrowIfCancellationRequested();
                progress.Report(0.9, "Choosing files to keep");
                var fileGroupCount = await PersistFileGroupsAsync(
                    searchId,
                    fileGroups,
                    DuplicateKeeperRules.Deserialize(search.KeeperRulesJson),
                    progress,
                    ct);
                progress.Report(1, $"Found {fileGroupCount.ToString(CultureInfo.InvariantCulture)} videos with more than one file");
                return;
            }

            progress.Report(0.03, "Loading pairs marked as not duplicates");
            var ignored = await LoadIgnoredPairsAsync(candidates, ct);

            List<List<int>> groups;
            switch (search.MatchType)
            {
                case "phash":
                    groups = await FindPhashGroupsAsync(candidates, search.Distance, search.DurationDifference, ignored, progress, ct);
                    break;
                case "title":
                    progress.Report(0.1, "Comparing video titles");
                    groups = await FindTitleGroupsAsync(candidates, ignored, ct);
                    break;
                case "remoteId":
                    progress.Report(0.1, "Comparing remote IDs");
                    groups = await FindRemoteIdGroupsAsync(candidates, ignored, ct);
                    break;
                default:
                    progress.Report(0.1, "Comparing file fingerprints");
                    groups = await FindFingerprintGroupsAsync(candidates, ignored, ct);
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

    private IQueryable<int> ExplicitCandidates(int[] ids)
        => db.Videos.AsNoTracking().Where(video => ids.Contains(video.Id)).Select(video => video.Id);

    /// <summary>
    /// Describes the videos in scope as a query rather than loading their IDs, so every later step filters
    /// in the database and a search over the whole library holds no per-video state for its scope.
    /// </summary>
    private async Task<IQueryable<int>> ResolveCandidatesAsync(DuplicateSearch search, CancellationToken ct)
    {
        var includes = search.IncludePaths ?? [];
        var excludes = search.ExcludePaths ?? [];
        foreach (var path in includes.Concat(excludes))
            DuplicateSearchLimits.CheckFieldLength(path.Length);
        var minimumDuration = search.MinimumDuration;
        var videos = db.Videos.AsNoTracking();
        if (minimumDuration > 0)
            videos = videos.Where(video => video.MaxDuration >= minimumDuration);
        // Title and remote-ID matching also apply to metadata-only videos, so an unscoped search
        // must not require a file.
        if (includes.Length == 0 && excludes.Length == 0)
            return videos.Select(video => video.Id);

        // A file's path is its folder's path plus its name, so a file is at or below a scope exactly when
        // its folder is. Matching folders here keeps the platform's path comparison in one place, and
        // there are far fewer folders than files.
        var includedFolderIds = new List<int>();
        var excludedFolderIds = new List<int>();
        var folders = db.Folders.AsNoTracking().Select(folder => new { folder.Id, folder.Path }).AsAsyncEnumerable();
        await foreach (var folder in folders.WithCancellation(ct))
        {
            var path = (folder.Path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            if (includes.Any(scope => DuplicateSearchJobService.IsAtOrBelow(path, scope)))
                includedFolderIds.Add(folder.Id);
            if (excludes.Any(scope => DuplicateSearchJobService.IsAtOrBelow(path, scope)))
                excludedFolderIds.Add(folder.Id);
        }

        var scopedVideoIds = WhereInScope(
                db.VideoFiles.AsNoTracking().Where(file => file.VideoId.HasValue),
                includes,
                includedFolderIds.ToArray(),
                excludes,
                excludedFolderIds.ToArray(),
                FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase)
            .Select(file => file.VideoId!.Value);
        return videos.Where(video => scopedVideoIds.Contains(video.Id)).Select(video => video.Id);
    }

    /// <summary>
    /// Keeps the files in one of the included folders and in none of the excluded ones. A scope may also
    /// name one file directly, which is matched with the same case rule the folders were matched with.
    /// </summary>
    internal static IQueryable<VideoFile> WhereInScope(
        IQueryable<VideoFile> files,
        string[] includes,
        int[] includedFolderIds,
        string[] excludes,
        int[] excludedFolderIds,
        bool ignoreCase)
    {
        if (ignoreCase)
        {
            includes = includes.Select(path => path.ToUpperInvariant()).ToArray();
            excludes = excludes.Select(path => path.ToUpperInvariant()).ToArray();
        }
        if (includes.Length > 0)
        {
            files = ignoreCase
                ? files.Where(file => includedFolderIds.Contains(file.ParentFolderId) || includes.Contains(file.Path.ToUpper()))
                : files.Where(file => includedFolderIds.Contains(file.ParentFolderId) || includes.Contains(file.Path));
        }
        if (excludes.Length > 0)
        {
            files = ignoreCase
                ? files.Where(file => !excludedFolderIds.Contains(file.ParentFolderId) && !excludes.Contains(file.Path.ToUpper()))
                : files.Where(file => !excludedFolderIds.Contains(file.ParentFolderId) && !excludes.Contains(file.Path));
        }
        return files;
    }

    private async Task<HashSet<(int Low, int High)>> LoadIgnoredPairsAsync(IQueryable<int> candidates, CancellationToken ct)
    {
        var result = new HashSet<(int Low, int High)>();
        var pairs = db.DuplicateIgnoredPairs
            .AsNoTracking()
            .Where(pair => candidates.Contains(pair.LowVideoId) && candidates.Contains(pair.HighVideoId))
            .Select(pair => new { pair.LowVideoId, pair.HighVideoId })
            .AsAsyncEnumerable();
        await foreach (var pair in pairs.WithCancellation(ct))
            result.Add((pair.LowVideoId, pair.HighVideoId));
        return result;
    }

    private async Task<List<List<int>>> FindFingerprintGroupsAsync(
        IQueryable<int> candidates,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
    {
        var keys = new List<DuplicateKeyCandidate>();
        var query = db.VideoFiles
            .Where(file => file.VideoId.HasValue && candidates.Contains(file.VideoId.Value))
            .SelectMany(
                file => file.Fingerprints.Where(fingerprint =>
                    (fingerprint.Type == "oshash" || fingerprint.Type == "md5")
                    && fingerprint.Value != ""),
                (file, fingerprint) => new { SourceId = fingerprint.Id, VideoId = file.VideoId!.Value, fingerprint.Type, Value = fingerprint.Value.Length > DuplicateSearchLimits.MaximumFieldCharacters ? fingerprint.Value.Substring(0, DuplicateSearchLimits.MaximumFieldCharacters + 1) : fingerprint.Value })
            .AsNoTracking();
        await foreach (var row in ReadCandidatePagesAsync(query, row => row.SourceId, row => row.Value.Length, ct))
            keys.Add(DuplicateKeyCandidate.Create(row.VideoId, $"{row.Type}\n{row.Value}"));

        return GroupBuckets(DuplicateKeyCandidate.TakeBuckets(keys), ignored);
    }

    private async Task<List<List<int>>> FindTitleGroupsAsync(
        IQueryable<int> candidates,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
    {
        var keys = new List<DuplicateKeyCandidate>();
        var query = db.Videos
            .Where(video => candidates.Contains(video.Id) && video.Title != null && video.Title != "")
            .Select(video => new { SourceId = video.Id, VideoId = video.Id, Title = video.Title!.Length > DuplicateSearchLimits.MaximumFieldCharacters ? video.Title!.Substring(0, DuplicateSearchLimits.MaximumFieldCharacters + 1) : video.Title! })
            .AsNoTracking();
        await foreach (var row in ReadCandidatePagesAsync(query, row => row.SourceId, row => row.Title.Length, ct))
        {
            var title = NormalizeTitle(row.Title);
            if (title.Length > 0)
                keys.Add(DuplicateKeyCandidate.CreateIgnoringCase(row.VideoId, title));
        }

        return GroupBuckets(DuplicateKeyCandidate.TakeBuckets(keys), ignored);
    }

    private async Task<List<List<int>>> FindRemoteIdGroupsAsync(
        IQueryable<int> candidates,
        HashSet<(int Low, int High)> ignored,
        CancellationToken ct)
    {
        var keys = new List<DuplicateKeyCandidate>();
        var query = db.Set<VideoRemoteId>()
            .Where(remoteId => candidates.Contains(remoteId.VideoId) && remoteId.RemoteId != "")
            .Select(remoteId => new
            {
                SourceId = remoteId.Id,
                remoteId.VideoId,
                Endpoint = remoteId.Endpoint.Length > DuplicateSearchLimits.MaximumFieldCharacters ? remoteId.Endpoint.Substring(0, DuplicateSearchLimits.MaximumFieldCharacters + 1) : remoteId.Endpoint,
                RemoteId = remoteId.RemoteId.Length > DuplicateSearchLimits.MaximumFieldCharacters ? remoteId.RemoteId.Substring(0, DuplicateSearchLimits.MaximumFieldCharacters + 1) : remoteId.RemoteId
            })
            .AsNoTracking();
        await foreach (var row in ReadCandidatePagesAsync(query, row => row.SourceId, row => Math.Max(row.Endpoint.Length, row.RemoteId.Length), ct))
            keys.Add(DuplicateKeyCandidate.CreateIgnoringCase(row.VideoId, $"{row.Endpoint.Trim()}\n{row.RemoteId.Trim()}"));

        return GroupBuckets(DuplicateKeyCandidate.TakeBuckets(keys), ignored);
    }

    private async Task<List<List<int>>> FindPhashGroupsAsync(
        IQueryable<int> candidates,
        int maxDistance,
        double maxDurationDifference,
        HashSet<(int Low, int High)> ignored,
        IJobProgress progress,
        CancellationToken ct)
    {
        progress.Report(0.04, "Loading visual fingerprints");
        var fingerprints = new List<DuplicatePHashCandidate>();
        var query = db.VideoFiles
            .Where(file => file.VideoId.HasValue && candidates.Contains(file.VideoId.Value))
            .SelectMany(
                file => file.Fingerprints.Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != ""),
                (file, fingerprint) => new { SourceId = fingerprint.Id, VideoId = file.VideoId!.Value, file.Duration, Value = fingerprint.Value.Length > DuplicateSearchLimits.MaximumFieldCharacters ? fingerprint.Value.Substring(0, DuplicateSearchLimits.MaximumFieldCharacters + 1) : fingerprint.Value })
            .AsNoTracking();
        await foreach (var row in ReadCandidatePagesAsync(query, row => row.SourceId, row => row.Value.Length, ct))
        {
            if (TryParsePHash(row.Value, out var hash))
                fingerprints.Add(new DuplicatePHashCandidate(row.VideoId, row.Duration, hash));
        }

        var window = PHashWindow.Take(fingerprints);
        if (window.Count < 2)
            return [];

        maxDistance = Math.Clamp(maxDistance, 0, 64);
        maxDurationDifference = Math.Max(0, maxDurationDifference);
        var comparisons = window.CountComparisons(maxDurationDifference);
        if (comparisons > PHashComparisonLimit)
        {
            throw new InvalidOperationException(
                $"The visual search would compare {comparisons.ToString("N0", CultureInfo.InvariantCulture)} pairs of fingerprints, more than the limit of {PHashComparisonLimit.ToString("N0", CultureInfo.InvariantCulture)}. Every fingerprint is compared with the others whose duration is within the tolerance, so use a smaller duration tolerance, raise the minimum duration, or limit the search to fewer folders, and try again.");
        }

        var grouping = new DuplicateUnionFind();
        var chunkCount = (window.Count + PHashComparisonChunkSize - 1) / PHashComparisonChunkSize;
        progress.DeclareUnitCount(chunkCount);
        var result = await jobService.RunBatchAsync(
            Enumerable.Range(0, chunkCount),
            BulkDeletionJobService.ResolveMaxParallelism(config, Environment.ProcessorCount),
            (chunk, unit, innerCt) =>
            {
                // Matches are rare next to comparisons, so each unit batches its own and takes the shared
                // lock only to merge them. Memory stays proportional to the videos that have a duplicate.
                var pending = new List<(int Left, int Right)>();
                var start = chunk * PHashComparisonChunkSize;
                window.Compare(
                    start,
                    Math.Min(window.Count, start + PHashComparisonChunkSize),
                    maxDistance,
                    maxDurationDifference,
                    (leftVideoId, rightVideoId) =>
                    {
                        var pair = leftVideoId < rightVideoId ? (leftVideoId, rightVideoId) : (rightVideoId, leftVideoId);
                        if (ignored.Contains(pair))
                            return;
                        pending.Add(pair);
                        if (pending.Count >= PHashMatchFlushSize)
                            Flush();
                    },
                    innerCt);
                Flush();
                unit.Complete(JobUnitOutcome.Succeeded);
                return Task.CompletedTask;

                void Flush()
                {
                    lock (grouping)
                    {
                        foreach (var (left, right) in pending)
                            grouping.Union(left, right);
                    }
                    pending.Clear();
                }
            },
            progress,
            unitIdFactory: (_, position) => position.ToString(CultureInfo.InvariantCulture),
            labelFactory: _ => "Comparing visual fingerprints",
            ct: ct);
        if (result.FailedUnits > 0)
            throw new InvalidOperationException($"{result.FailedUnits.ToString(CultureInfo.InvariantCulture)} pHash comparison units failed.");
        return grouping.ToGroups();
    }

    private async Task<int> PersistGroupsAsync(
        Guid searchId,
        IReadOnlyList<List<int>> groups,
        IReadOnlyList<DuplicateKeeperRule> rules,
        IJobProgress progress,
        CancellationToken ct)
    {
        await DeleteExistingGroupsAsync(searchId, ct);

        // Keeper facts carry paths and scores, so they are loaded for one batch of groups at a time
        // instead of for every duplicate the search found.
        var position = 0;
        var videoCount = 0;
        for (var batchStart = 0; batchStart < groups.Count; batchStart += PersistGroupBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(0.9 + 0.1 * batchStart / groups.Count, "Saving duplicate groups");
            var batch = groups.Skip(batchStart).Take(PersistGroupBatchSize).ToArray();
            // Groups are connected components, so no video belongs to two of them.
            var batchVideoIds = batch.SelectMany(group => group).Distinct().ToArray();
            videoCount += batchVideoIds.Length;
            var facts = await DuplicateKeeperRules.LoadFactsAsync(db, batchVideoIds, rules, ct);
            var definitions = PreparePersistedGroups(
                    batch,
                    MaximumPersistedGroupSize,
                    ids => DuplicateKeeperRules.Choose(ids, facts, rules))
                .Select(bounded => new PersistedGroupDefinition(
                    new DuplicateSearchGroup
                    {
                        SearchId = searchId,
                        Position = position++,
                        Status = DuplicateGroupStatus.Unresolved,
                        DecisionSource = "auto",
                        DecisionRule = bounded.Choice.DecisionRule,
                    },
                    bounded.VideoIds,
                    bounded.Choice.KeeperId))
                .ToList();

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

        await CompleteSearchAsync(searchId, position, videoCount, ct);
        return position;
    }

    /// <summary>
    /// Groups the files of every candidate video that has more than one, leaving out pairs a person chose to
    /// keep side by side. A video's files can split into several groups when some of its pairs were kept.
    /// </summary>
    private async Task<List<DuplicateFileGroup>> FindMultiFileGroupsAsync(IQueryable<int> candidates, CancellationToken ct)
    {
        var filesByVideoId = new Dictionary<int, List<int>>();
        var videoIdByFileId = new Dictionary<int, int>();
        // Only videos that actually have several files are read file by file.
        var query = db.VideoFiles
            .AsNoTracking()
            .Where(file => file.VideoId.HasValue
                && candidates.Contains(file.VideoId.Value)
                && db.VideoFiles.Any(other => other.VideoId == file.VideoId && other.Id != file.Id))
            .Select(file => new { file.Id, VideoId = file.VideoId!.Value });
        await foreach (var file in ReadCandidatePagesAsync(query, file => file.Id, _ => 0, ct))
        {
            if (!filesByVideoId.TryGetValue(file.VideoId, out var files))
                filesByVideoId[file.VideoId] = files = [];
            files.Add(file.Id);
            videoIdByFileId[file.Id] = file.VideoId;
        }

        var candidateFileIds = videoIdByFileId.Keys.ToHashSet();
        var ignored = new HashSet<(int Low, int High)>();
        foreach (var chunk in candidateFileIds.Order().Chunk(QueryChunkSize))
        {
            var pairs = db.DuplicateIgnoredFilePairs
                .AsNoTracking()
                .Where(pair => chunk.Contains(pair.LowFileId))
                .Select(pair => new { pair.LowFileId, pair.HighFileId })
                .AsAsyncEnumerable();
            await foreach (var pair in pairs.WithCancellation(ct))
            {
                if (candidateFileIds.Contains(pair.HighFileId))
                    ignored.Add((pair.LowFileId, pair.HighFileId));
            }
        }

        // Each video is its own bucket, so a group can never mix files from different videos.
        return GroupBuckets(filesByVideoId.Values, ignored)
            .Select(fileIds => new DuplicateFileGroup(videoIdByFileId[fileIds[0]], fileIds.ToArray()))
            .OrderBy(group => group.VideoId)
            .ThenBy(group => group.FileIds[0])
            .ToList();
    }

    /// <summary>
    /// Saves file groups with their chosen keeper. Unlike video groups they are never split into chunks:
    /// chunks share a keeper through the cross-group rule that only video resolution understands, and a
    /// video with more files than a page can show is still one decision about one video.
    /// </summary>
    private async Task<int> PersistFileGroupsAsync(
        Guid searchId,
        IReadOnlyList<DuplicateFileGroup> groups,
        IReadOnlyList<DuplicateKeeperRule> rules,
        IJobProgress progress,
        CancellationToken ct)
    {
        await DeleteExistingGroupsAsync(searchId, ct);

        var fileCount = 0;
        for (var batchStart = 0; batchStart < groups.Count; batchStart += PersistGroupBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            progress.Report(0.9 + 0.1 * batchStart / groups.Count, "Saving groups");
            var batchGroups = groups.Skip(batchStart).Take(PersistGroupBatchSize).ToArray();
            // A file belongs to one video, and each group holds files of a single video.
            var batchFileIds = batchGroups.SelectMany(group => group.FileIds).Distinct().ToArray();
            fileCount += batchFileIds.Length;
            var facts = await DuplicateKeeperRules.LoadFileFactsAsync(db, batchFileIds, ct);
            var batch = batchGroups
                .Select((group, offset) =>
                {
                    var choice = DuplicateKeeperRules.Choose(group.FileIds, facts, rules);
                    return (Group: group, Choice: choice, Entity: new DuplicateSearchGroup
                    {
                        SearchId = searchId,
                        Position = batchStart + offset,
                        Status = DuplicateGroupStatus.Unresolved,
                        DecisionSource = "auto",
                        DecisionRule = choice.DecisionRule,
                    });
                })
                .ToList();

            db.DuplicateSearchGroups.AddRange(batch.Select(entry => entry.Entity));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();

            db.DuplicateSearchFileItems.AddRange(batch.SelectMany(entry => entry.Group.FileIds.Select(fileId => new DuplicateSearchFileItem
            {
                GroupId = entry.Entity.Id,
                FileId = fileId,
                VideoId = entry.Group.VideoId,
                Keep = fileId == entry.Choice.KeeperId,
            })));
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }

        // A files search counts the files under review, since every group belongs to a single video.
        await CompleteSearchAsync(searchId, groups.Count, fileCount, ct);
        return groups.Count;
    }

    private async Task DeleteExistingGroupsAsync(Guid searchId, CancellationToken ct)
    {
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
    }

    private async Task CompleteSearchAsync(Guid searchId, int groupCount, int memberCount, CancellationToken ct)
    {
        var search = await db.DuplicateSearches.FirstAsync(item => item.Id == searchId, ct);
        search.Status = DuplicateSearchStatus.Completed;
        search.GroupCount = groupCount;
        search.VideoCount = memberCount;
        search.CompletedAt = DateTime.UtcNow;
        search.ExpiresAt = DateTime.UtcNow.Add(DuplicateSearchJobService.ResultRetention);
        search.Error = null;
        await db.SaveChangesAsync(ct);
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
        var window = PHashWindow.Take(candidates.ToList());
        var grouping = new DuplicateUnionFind();
        window.Compare(
            0,
            window.Count,
            Math.Clamp(maxDistance, 0, 64),
            Math.Max(0, maxDurationDifference),
            (leftVideoId, rightVideoId) =>
            {
                var pair = leftVideoId < rightVideoId ? (leftVideoId, rightVideoId) : (rightVideoId, leftVideoId);
                if (ignored?.Contains(pair) != true)
                    grouping.Union(pair.Item1, pair.Item2);
            },
            CancellationToken.None);
        return new PhashGroupingResult(grouping.ToGroups(), window.CountComparisons(Math.Max(0, maxDurationDifference)));
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
        var ignoredVideoIds = new HashSet<int>();
        foreach (var pair in ignored)
        {
            ignoredVideoIds.Add(pair.Low);
            ignoredVideoIds.Add(pair.High);
        }

        var grouping = new DuplicateUnionFind();
        foreach (var bucket in buckets)
        {
            var ids = bucket.Distinct().Order().ToArray();
            if (ids.Length < 2)
                continue;
            var touchesIgnored = ids.Length <= MaximumPairwiseBucketSize && ids.Any(ignoredVideoIds.Contains);
            if (!touchesIgnored)
            {
                for (var index = 1; index < ids.Length; index++)
                    grouping.Union(ids[0], ids[index]);
                continue;
            }
            for (var left = 0; left < ids.Length; left++)
            {
                for (var right = left + 1; right < ids.Length; right++)
                {
                    if (!ignored.Contains((ids[left], ids[right])))
                        grouping.Union(ids[left], ids[right]);
                }
            }
        }
        return grouping.ToGroups();
    }

    internal static string NormalizeTitle(string title)
        => string.Join(' ', title.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static List<int[]> SplitOversizedGroups(
        IEnumerable<IEnumerable<int>> groups,
        int maximumGroupSize = MaximumPersistedGroupSize)
        => PreparePersistedGroups(
                groups,
                maximumGroupSize,
                ids => new DuplicateKeeperChoice(ids.Min(), DuplicateKeeperRules.TieBreakRule))
            .Select(group => group.VideoIds)
            .ToList();

    private static List<BoundedDuplicateGroup> PreparePersistedGroups(
        IEnumerable<IEnumerable<int>> groups,
        int maximumGroupSize,
        Func<int[], DuplicateKeeperChoice> keeperSelector)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumGroupSize, 2);
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
                continue;
            }

            // Each persisted chunk shares the logical group's keeper. The global kept-video rule can
            // therefore delete every other member even though the UI pages groups in bounded rows.
            foreach (var chunk in ids.Where(id => id != choice.KeeperId).Chunk(maximumGroupSize - 1))
                result.Add(new BoundedDuplicateGroup([choice.KeeperId, .. chunk], choice));
        }
        return result;
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

    /// <summary>
    /// Fingerprints ordered by duration, held as parallel arrays so a comparison reads two adjacent
    /// machine words. Every fingerprint is compared with the later ones whose duration is within the
    /// tolerance, which finds exactly the pairs within both the tolerance and the bit distance.
    /// </summary>
    private sealed class PHashWindow
    {
        private readonly double[] _durations;
        private readonly ulong[] _hashes;
        private readonly int[] _videoIds;

        private PHashWindow(double[] durations, ulong[] hashes, int[] videoIds)
        {
            _durations = durations;
            _hashes = hashes;
            _videoIds = videoIds;
        }

        public int Count => _hashes.Length;

        /// <summary>Sorts and empties <paramref name="fingerprints"/> so the list does not outlive the arrays built from it.</summary>
        public static PHashWindow Take(List<DuplicatePHashCandidate> fingerprints)
        {
            fingerprints.Sort(static (left, right) =>
            {
                var byDuration = left.Duration.CompareTo(right.Duration);
                return byDuration != 0 ? byDuration : left.VideoId.CompareTo(right.VideoId);
            });
            var durations = new double[fingerprints.Count];
            var hashes = new ulong[fingerprints.Count];
            var videoIds = new int[fingerprints.Count];
            for (var index = 0; index < fingerprints.Count; index++)
            {
                (videoIds[index], durations[index], hashes[index]) = fingerprints[index];
            }
            fingerprints.Clear();
            fingerprints.TrimExcess();
            return new PHashWindow(durations, hashes, videoIds);
        }

        /// <summary>The number of pairs <see cref="Compare"/> visits over the whole window, in one linear pass.</summary>
        public long CountComparisons(double maxDurationDifference)
        {
            long total = 0;
            var end = 0;
            for (var left = 0; left < _durations.Length; left++)
            {
                end = Math.Max(end, left + 1);
                while (end < _durations.Length && _durations[end] - _durations[left] <= maxDurationDifference)
                    end++;
                total += end - left - 1;
            }
            return total;
        }

        public void Compare(
            int start,
            int end,
            int maxDistance,
            double maxDurationDifference,
            Action<int, int> match,
            CancellationToken ct)
        {
            var durations = _durations;
            var hashes = _hashes;
            var videoIds = _videoIds;
            for (var left = start; left < end; left++)
            {
                ct.ThrowIfCancellationRequested();
                var leftHash = hashes[left];
                var leftDuration = durations[left];
                var leftVideoId = videoIds[left];
                for (var right = left + 1; right < durations.Length && durations[right] - leftDuration <= maxDurationDifference; right++)
                {
                    if (BitOperations.PopCount(leftHash ^ hashes[right]) <= maxDistance && videoIds[right] != leftVideoId)
                        match(leftVideoId, videoIds[right]);
                }
            }
        }
    }

    /// <summary>Reads a query in keyset pages so no reader stays open across the work done per row.</summary>
    private static async IAsyncEnumerable<T> ReadCandidatePagesAsync<T>(
        IQueryable<T> query,
        System.Linq.Expressions.Expression<Func<T, int>> identity,
        Func<T, int> largestField,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var getId = identity.Compile();
        int? after = null;
        while (true)
        {
            var pageQuery = query;
            if (after.HasValue)
            {
                System.Linq.Expressions.Expression<Func<int>> cursor = () => after.Value;
                var predicate = System.Linq.Expressions.Expression.Lambda<Func<T, bool>>(
                    System.Linq.Expressions.Expression.GreaterThan(identity.Body, cursor.Body), identity.Parameters);
                pageQuery = pageQuery.Where(predicate);
            }
            var page = await pageQuery.OrderBy(identity).Take(DuplicateSearchLimits.PageSize).ToListAsync(ct);
            if (page.Count == 0) yield break;
            after = getId(page[^1]);
            foreach (var row in page)
            {
                ct.ThrowIfCancellationRequested();
                DuplicateSearchLimits.CheckFieldLength(largestField(row));
                yield return row;
            }
        }
    }

    private sealed record BoundedDuplicateGroup(int[] VideoIds, DuplicateKeeperChoice Choice);
    private sealed record DuplicateFileGroup(int VideoId, int[] FileIds);
    private sealed record PersistedGroupDefinition(DuplicateSearchGroup Entity, int[] VideoIds, int KeeperId);
}

/// <summary>
/// One candidate row of an exact search: the video and a 128-bit digest of the key it is matched on.
/// Holding the digest instead of the key keeps a search at 24 bytes per row whatever the length of a
/// title or fingerprint, and rows with equal keys become adjacent once sorted.
/// </summary>
internal readonly record struct DuplicateKeyCandidate(ulong KeyHigh, ulong KeyLow, int VideoId) : IComparable<DuplicateKeyCandidate>
{
    internal static DuplicateKeyCandidate Create(int videoId, string key)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(MemoryMarshal.AsBytes(key.AsSpan()), digest);
        return new DuplicateKeyCandidate(
            BinaryPrimitives.ReadUInt64LittleEndian(digest),
            BinaryPrimitives.ReadUInt64LittleEndian(digest[sizeof(ulong)..]),
            videoId);
    }

    /// <summary>
    /// Matches keys without regard to case, as <see cref="StringComparer.OrdinalIgnoreCase"/> does, with one
    /// difference: upper-casing folds the archaic long s (U+017F) into "S", which that comparer keeps apart.
    /// </summary>
    internal static DuplicateKeyCandidate CreateIgnoringCase(int videoId, string key)
        => Create(videoId, key.ToUpperInvariant());

    public int CompareTo(DuplicateKeyCandidate other)
    {
        var byHigh = KeyHigh.CompareTo(other.KeyHigh);
        if (byHigh != 0)
            return byHigh;
        var byLow = KeyLow.CompareTo(other.KeyLow);
        return byLow != 0 ? byLow : VideoId.CompareTo(other.VideoId);
    }

    /// <summary>Sorts and empties <paramref name="candidates"/>, returning the videos of every key that more than one row has.</summary>
    internal static List<int[]> TakeBuckets(List<DuplicateKeyCandidate> candidates)
    {
        candidates.Sort();
        var buckets = new List<int[]>();
        var start = 0;
        for (var index = 1; index <= candidates.Count; index++)
        {
            if (index < candidates.Count
                && candidates[index].KeyHigh == candidates[start].KeyHigh
                && candidates[index].KeyLow == candidates[start].KeyLow)
                continue;
            if (index - start > 1)
            {
                var videoIds = new int[index - start];
                for (var offset = 0; offset < videoIds.Length; offset++)
                    videoIds[offset] = candidates[start + offset].VideoId;
                buckets.Add(videoIds);
            }
            start = index;
        }
        candidates.Clear();
        candidates.TrimExcess();
        return buckets;
    }
}

/// <summary>
/// Collects matching pairs into connected groups as they arrive, so memory follows the number of videos
/// that have a duplicate rather than the number of matching pairs.
/// </summary>
internal sealed class DuplicateUnionFind
{
    private readonly Dictionary<int, int> _parent = [];

    public void Union(int left, int right)
    {
        _parent.TryAdd(left, left);
        _parent.TryAdd(right, right);
        var leftRoot = Find(left);
        var rightRoot = Find(right);
        if (leftRoot == rightRoot)
            return;

        // Stable roots keep the resulting groups deterministic regardless of parallel edge order.
        if (leftRoot < rightRoot)
            _parent[rightRoot] = leftRoot;
        else
            _parent[leftRoot] = rightRoot;
    }

    public List<List<int>> ToGroups()
        => _parent.Keys
            .GroupBy(Find)
            .Select(group => group.OrderBy(id => id).ToList())
            .Where(group => group.Count > 1)
            .OrderBy(group => group[0])
            .ToList();

    private int Find(int id)
    {
        var root = id;
        while (_parent[root] != root)
            root = _parent[root];
        while (_parent[id] != id)
        {
            var next = _parent[id];
            _parent[id] = root;
            id = next;
        }
        return root;
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
