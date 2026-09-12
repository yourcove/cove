using System.Data;
using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Resolves reviewed duplicate groups in the background. Queuing a group is a small transaction that
/// returns immediately, so a person can keep reviewing while earlier decisions are applied. Each search
/// has at most one worker, identified by the durable claim in <see cref="DuplicateSearch.DeletionJobId"/>,
/// which drains the search's queue one group at a time and releases the claim only after observing — under
/// the search row lock — that nothing else was queued in the meantime.
/// </summary>
public sealed class DuplicateResolutionService(
    CoveContext db,
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<DuplicateResolutionService>? logger = null)
{
    public const string RemoveAction = "remove";
    public const string MergeAction = "merge";
    internal const string WorkerJobType = "duplicate-resolution";
    internal const string LostWorkerError = "Resolution stopped before this group finished. Nothing further was removed; review it and resolve it again.";

    internal static readonly DuplicateGroupStatus[] ReviewableStatuses = [DuplicateGroupStatus.Unresolved, DuplicateGroupStatus.Failed];

    /// <summary>
    /// Queues the requested reviewable groups and starts the search's worker when none is running.
    /// Callers authorize the destructive action before calling; the worker re-authorizes every deletion.
    /// </summary>
    public async Task<DuplicateResolveResult> QueueAsync(
        Guid searchId,
        IReadOnlyCollection<int> groupIds,
        string action,
        bool deleteFiles,
        bool deleteGenerated,
        CovePrincipal? principal,
        CancellationToken ct)
    {
        var ids = groupIds.Distinct().ToArray();
        if (ids.Length == 0)
            return new DuplicateResolveResult(0, null);

        var queuedAt = DateTime.UtcNow;
        var claim = DuplicateSearchDeletionClaim.Create();
        var strategy = db.Database.CreateExecutionStrategy();
        var outcome = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            // Writing the search row first serializes this transaction with the worker's final
            // "is anything still queued" check, which takes the same row lock before it looks.
            var locked = await db.DuplicateSearches
                .Where(search => search.Id == searchId && search.Status == DuplicateSearchStatus.Completed)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.ExpiresAt, queuedAt.Add(DuplicateSearchJobService.ResultRetention)), ct);
            if (locked == 0)
                return (Queued: 0, StartWorker: false, JobId: (string?)null);

            await db.DuplicateSearchGroups
                .Where(group => group.SearchId == searchId
                    && ids.Contains(group.Id)
                    && (group.Status == DuplicateGroupStatus.Unresolved || group.Status == DuplicateGroupStatus.Failed))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(group => group.Status, DuplicateGroupStatus.Queued)
                    .SetProperty(group => group.ResolutionAction, action)
                    .SetProperty(group => group.DeleteFiles, deleteFiles)
                    .SetProperty(group => group.DeleteGenerated, deleteGenerated)
                    .SetProperty(group => group.QueuedAt, queuedAt)
                    .SetProperty(group => group.Error, (string?)null), ct);
            // Counting by this request's timestamp keeps a replayed transaction's result accurate.
            var queued = await db.DuplicateSearchGroups.CountAsync(group => group.SearchId == searchId
                && ids.Contains(group.Id)
                && group.Status == DuplicateGroupStatus.Queued
                && group.QueuedAt == queuedAt, ct);
            var claimed = queued > 0
                && await db.DuplicateSearches
                    .Where(search => search.Id == searchId && (search.DeletionJobId == null || search.DeletionJobId == claim))
                    .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, claim), ct) == 1;
            var currentJobId = await db.DuplicateSearches
                .Where(search => search.Id == searchId)
                .Select(search => search.DeletionJobId)
                .SingleAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return (Queued: queued, StartWorker: claimed, JobId: currentJobId);
        });

        if (!outcome.StartWorker)
            return new DuplicateResolveResult(outcome.Queued, outcome.JobId);

        var jobIdSource = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string jobId;
        try
        {
            jobId = jobService.EnqueueFor(
                JobOwner.FromPrincipal(principal),
                WorkerJobType,
                "Resolving duplicate videos",
                CreateWorker(scopeFactory, config, searchId, claim, jobIdSource.Task, SnapshotPrincipal(principal)),
                $"/duplicates?search={searchId:D}",
                // Resolution must feel immediate; it should not wait behind a long scan in the exclusive queue.
                exclusive: false);
        }
        catch
        {
            await ReleaseClaimAsync(db, searchId, claim, claim, DuplicateGroupStatus.Unresolved, null, CancellationToken.None);
            jobIdSource.TrySetCanceled();
            throw;
        }
        jobIdSource.TrySetResult(jobId);
        await db.DuplicateSearches
            .Where(search => search.Id == searchId && search.DeletionJobId == claim)
            .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, jobId), CancellationToken.None);
        return new DuplicateResolveResult(outcome.Queued, jobId);
    }

    /// <summary>
    /// Releases a worker claim whose job is no longer running (it crashed or was cancelled before it
    /// started), returning its groups to review. Returns true when anything changed.
    /// </summary>
    public async Task<bool> ReconcileLostWorkerAsync(DuplicateSearch search, CancellationToken ct)
    {
        var jobId = search.DeletionJobId;
        if (string.IsNullOrWhiteSpace(jobId) || jobId.StartsWith(DuplicateSearchDeletionClaim.Prefix, StringComparison.Ordinal))
            return false;
        var job = jobService.GetJob(jobId);
        if (job is { Status: JobStatus.Pending or JobStatus.Running })
            return false;
        return await ReleaseClaimAsync(db, search.Id, jobId, jobId, DuplicateGroupStatus.Failed, LostWorkerError, ct);
    }

    /// <summary>Releases the claims of a worker job that was cancelled before its callback ran.</summary>
    public async Task<int> ReleaseCancelledWorkerAsync(string jobId, CancellationToken ct)
    {
        var searchIds = await db.DuplicateSearches
            .Where(search => search.DeletionJobId == jobId)
            .Select(search => search.Id)
            .ToArrayAsync(ct);
        var released = 0;
        foreach (var searchId in searchIds)
        {
            if (await ReleaseClaimAsync(db, searchId, jobId, jobId, DuplicateGroupStatus.Unresolved, null, ct))
                released++;
        }
        return released;
    }

    private static async Task<bool> ReleaseClaimAsync(
        CoveContext context,
        Guid searchId,
        string claim,
        string alternateClaim,
        DuplicateGroupStatus returnStatus,
        string? error,
        CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = context.Database.IsRelational()
                ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            var released = await context.DuplicateSearches
                .Where(search => search.Id == searchId && (search.DeletionJobId == claim || search.DeletionJobId == alternateClaim))
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
            if (released == 1)
            {
                await context.DuplicateSearchGroups
                    .Where(group => group.SearchId == searchId
                        && (group.Status == DuplicateGroupStatus.Queued || group.Status == DuplicateGroupStatus.Processing))
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(group => group.Status, returnStatus)
                        .SetProperty(group => group.Error, error), ct);
                await context.DuplicateDeletionKeeperReservations
                    .IgnoreQueryFilters()
                    .Where(item => item.SearchId == searchId)
                    .ExecuteDeleteAsync(ct);
            }
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return released == 1;
        });
    }

    private static Func<IJobProgress, CancellationToken, Task> CreateWorker(
        IServiceScopeFactory scopeFactory,
        CoveConfiguration config,
        Guid searchId,
        string claim,
        Task<string> jobIdTask,
        CovePrincipal? principal)
        => async (progress, ct) =>
        {
            var jobId = await jobIdTask;
            var resolved = 0;
            var failed = 0;
            var removedVideos = 0;
            long removedBytes = 0;
            try
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    using var scope = CreatePrincipalScope(scopeFactory, principal);
                    var scopedDb = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    var next = await scopedDb.DuplicateSearchGroups
                        .AsNoTracking()
                        .Where(group => group.SearchId == searchId && group.Status == DuplicateGroupStatus.Queued)
                        .OrderBy(group => group.QueuedAt)
                        .ThenBy(group => group.Position)
                        .Select(group => new { group.Id, group.Position, group.ResolutionAction, group.DeleteFiles, group.DeleteGenerated })
                        .FirstOrDefaultAsync(ct);
                    if (next is null)
                    {
                        if (await TryFinishAsync(scopedDb, searchId, claim, jobId, ct))
                            break;
                        continue;
                    }

                    var remaining = await scopedDb.DuplicateSearchGroups
                        .CountAsync(group => group.SearchId == searchId && group.Status == DuplicateGroupStatus.Queued, ct);
                    progress.Report(
                        (double)(resolved + failed) / Math.Max(1, resolved + failed + remaining),
                        $"Resolving duplicate group {(next.Position + 1).ToString(CultureInfo.InvariantCulture)} ({remaining.ToString(CultureInfo.InvariantCulture)} queued)");

                    var started = await scopedDb.DuplicateSearchGroups
                        .Where(group => group.Id == next.Id && group.Status == DuplicateGroupStatus.Queued)
                        .ExecuteUpdateAsync(update => update.SetProperty(group => group.Status, DuplicateGroupStatus.Processing), ct);
                    if (started == 0)
                        continue;

                    try
                    {
                        var result = await ResolveGroupAsync(
                            scopeFactory,
                            config,
                            scope.ServiceProvider,
                            scopedDb,
                            searchId,
                            next.Id,
                            next.ResolutionAction == MergeAction,
                            next.DeleteFiles,
                            next.DeleteGenerated,
                            principal,
                            ct);
                        await scopedDb.DuplicateSearchGroups
                            .Where(group => group.Id == next.Id)
                            .ExecuteUpdateAsync(update => update
                                .SetProperty(group => group.Status, DuplicateGroupStatus.Resolved)
                                .SetProperty(group => group.ResolvedAt, DateTime.UtcNow)
                                .SetProperty(group => group.RemovedVideoCount, result.RemovedVideos)
                                .SetProperty(group => group.RemovedBytes, result.RemovedBytes)
                                .SetProperty(group => group.Error, result.Warning), CancellationToken.None);
                        resolved++;
                        removedVideos += result.RemovedVideos;
                        removedBytes += result.RemovedBytes;
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        await scopedDb.DuplicateSearchGroups
                            .Where(group => group.Id == next.Id)
                            .ExecuteUpdateAsync(update => update.SetProperty(group => group.Status, DuplicateGroupStatus.Unresolved), CancellationToken.None);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        scope.ServiceProvider.GetService<ILogger<DuplicateResolutionService>>()
                            ?.LogWarning(ex, "Failed to resolve duplicate group {GroupId} of search {SearchId}.", next.Id, searchId);
                        failed++;
                        var message = ex.Message;
                        await scopedDb.DuplicateSearchGroups
                            .Where(group => group.Id == next.Id)
                            .ExecuteUpdateAsync(update => update
                                .SetProperty(group => group.Status, DuplicateGroupStatus.Failed)
                                .SetProperty(group => group.Error, message[..Math.Min(message.Length, 2_000)]), CancellationToken.None);
                    }
                    finally
                    {
                        await scopedDb.DuplicateDeletionKeeperReservations
                            .IgnoreQueryFilters()
                            .Where(item => item.SearchId == searchId)
                            .ExecuteDeleteAsync(CancellationToken.None);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                using var scope = scopeFactory.CreateScope();
                await ReleaseClaimAsync(
                    scope.ServiceProvider.GetRequiredService<CoveContext>(),
                    searchId,
                    claim,
                    jobId,
                    DuplicateGroupStatus.Unresolved,
                    null,
                    CancellationToken.None);
                throw;
            }
            catch
            {
                using var scope = scopeFactory.CreateScope();
                await ReleaseClaimAsync(
                    scope.ServiceProvider.GetRequiredService<CoveContext>(),
                    searchId,
                    claim,
                    jobId,
                    DuplicateGroupStatus.Failed,
                    LostWorkerError,
                    CancellationToken.None);
                throw;
            }

            var summary = $"Resolved {resolved.ToString(CultureInfo.InvariantCulture)} duplicate groups, removed {removedVideos.ToString(CultureInfo.InvariantCulture)} videos ({FormatBytes(removedBytes)})";
            if (failed > 0)
                summary += $"; {failed.ToString(CultureInfo.InvariantCulture)} groups failed";
            progress.SetSummary(summary);
        };

    private static async Task<bool> TryFinishAsync(CoveContext context, Guid searchId, string claim, string jobId, CancellationToken ct)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = context.Database.IsRelational()
                ? await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            // Take the search row lock before looking, so a concurrent queue request either committed its
            // groups already (and is seen below) or will find the claim released and start a new worker.
            await context.DuplicateSearches
                .Where(search => search.Id == searchId)
                .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, search => search.DeletionJobId), ct);
            var queued = await context.DuplicateSearchGroups
                .AnyAsync(group => group.SearchId == searchId && group.Status == DuplicateGroupStatus.Queued, ct);
            if (!queued)
            {
                await context.DuplicateSearches
                    .Where(search => search.Id == searchId && (search.DeletionJobId == claim || search.DeletionJobId == jobId))
                    .ExecuteUpdateAsync(update => update.SetProperty(search => search.DeletionJobId, (string?)null), ct);
            }
            if (transaction is not null)
                await transaction.CommitAsync(ct);
            return !queued;
        });
    }

    private static async Task<GroupResolution> ResolveGroupAsync(
        IServiceScopeFactory scopeFactory,
        CoveConfiguration config,
        IServiceProvider services,
        CoveContext scopedDb,
        Guid searchId,
        int groupId,
        bool merge,
        bool deleteFiles,
        bool deleteGenerated,
        CovePrincipal? principal,
        CancellationToken ct)
    {
        var keeperIds = await scopedDb.DuplicateSearchItems
            .Where(item => item.GroupId == groupId && item.Keep)
            .Select(item => item.VideoId)
            .OrderBy(id => id)
            .ToArrayAsync(ct);
        if (keeperIds.Length == 0)
            throw new InvalidOperationException("No video in this group is marked to keep.");
        var removeIds = await DuplicateSearchJobService
            .EffectiveUnkeptVideoIds(scopedDb, searchId, scopedDb.DuplicateSearchGroups.Where(group => group.Id == groupId))
            .OrderBy(id => id)
            .ToArrayAsync(ct);
        if (removeIds.Length == 0)
            return new GroupResolution(0, 0, null);

        // Reserve the keepers so no other deletion path can remove them while their duplicates go.
        try
        {
            await scopedDb.DuplicateDeletionKeeperReservations
                .IgnoreQueryFilters()
                .Where(item => item.SearchId == searchId)
                .ExecuteDeleteAsync(ct);
            scopedDb.DuplicateDeletionKeeperReservations.AddRange(keeperIds.Select(videoId =>
                new DuplicateDeletionKeeperReservation { SearchId = searchId, VideoId = videoId }));
            await scopedDb.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException("A video marked to keep was deleted before its duplicates could be removed.");
        }
        finally
        {
            scopedDb.ChangeTracker.Clear();
        }

        var bytesByVideoId = await scopedDb.VideoFiles
            .Where(file => file.VideoId.HasValue && removeIds.Contains(file.VideoId.Value))
            .GroupBy(file => file.VideoId!.Value)
            .Select(group => new { VideoId = group.Key, Bytes = group.Sum(file => file.Size) })
            .ToDictionaryAsync(row => row.VideoId, row => row.Bytes, ct);

        if (merge)
        {
            // Like video deletion, a system-initiated resolution (no principal) is not entity-authorized.
            if (principal is not null)
            {
                var authorization = services.GetRequiredService<Cove.Core.Auth.IAuthorizationService>();
                var decision = await authorization.AuthorizeAsync(principal, Permissions.VideosWrite, EntityRef.Of(EntityKinds.Video, keeperIds[0]), ct);
                if (!decision.Allowed)
                    throw new UnauthorizedAccessException("You do not have permission to change the video being kept.");
            }
            using var mergeScope = CreatePrincipalScope(scopeFactory, principal);
            await mergeScope.ServiceProvider.GetRequiredService<DuplicateVideoMetadataMerger>()
                .MergeAsync(keeperIds[0], removeIds, ct);
        }

        var context = new BulkDeletionExecutionContext();
        var removed = 0;
        long removedBytes = 0;
        string? warning = null;
        try
        {
            foreach (var videoId in removeIds)
            {
                ct.ThrowIfCancellationRequested();
                using var unitScope = CreatePrincipalScope(scopeFactory, principal);
                var deletion = unitScope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                if (await deletion.DeleteAsync(
                    BulkDeletionEntityKind.Video,
                    videoId,
                    context,
                    deleteFiles,
                    deleteGenerated,
                    CancellationToken.None,
                    authorizationPrincipal: principal))
                {
                    removed++;
                    removedBytes += bytesByVideoId.GetValueOrDefault(videoId);
                }
            }
        }
        finally
        {
            if (deleteFiles)
            {
                using var fileScope = CreatePrincipalScope(scopeFactory, principal);
                var physical = await fileScope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>()
                    .DeleteTrackedPhysicalFilesAsync(
                        BulkDeletionEntityKind.Video,
                        context,
                        BulkDeletionJobService.ResolveMaxParallelism(config, Environment.ProcessorCount),
                        CancellationToken.None);
                if (physical.Failed > 0)
                    warning = $"{physical.Failed.ToString(CultureInfo.InvariantCulture)} file(s) could not be deleted from disk and will be retried.";
            }
        }
        return new GroupResolution(removed, removedBytes, warning);
    }

    private static IServiceScope CreatePrincipalScope(IServiceScopeFactory scopeFactory, CovePrincipal? principal)
    {
        var scope = scopeFactory.CreateScope();
        scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>().Set(principal);
        return scope;
    }

    internal static CovePrincipal? SnapshotPrincipal(CovePrincipal? principal)
        => principal is null
            ? null
            : new CovePrincipal
            {
                UserId = principal.UserId,
                Username = principal.Username,
                Kind = principal.Kind,
                Roles = principal.Roles.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Permissions = principal.Permissions.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ReadRestrictedEntityKinds = principal.ReadRestrictedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ReadGrantedEntityKinds = principal.ReadGrantedEntityKinds.ToHashSet(StringComparer.OrdinalIgnoreCase),
                ClaimsPrincipal = principal.ClaimsPrincipal,
                TokenId = principal.TokenId,
                Ip = principal.Ip,
                UserAgent = principal.UserAgent,
            };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture)} {units[unit]}";
    }

    private sealed record GroupResolution(int RemovedVideos, long RemovedBytes, string? Warning);
}
