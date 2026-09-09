using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using IExtensionServiceExchange = Cove.Plugins.IExtensionServiceExchange;

namespace Cove.Api.Services;

public enum BulkDeletionEntityKind
{
    Video,
    Image,
    Audio,
    Text,
    Gallery,
    Performer,
    Tag,
    Studio,
    Group,
    Face,
}

public sealed record BulkDeletionJobStart(string JobId, int ItemCount);

/// <summary>
/// Queues every library bulk deletion through one observable contract. The originating principal is
/// snapshotted so a video job can re-authorize its final, locked descendant scope without depending on
/// an originating request or HttpContext.
/// </summary>
public sealed class BulkDeletionJobService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<BulkDeletionJobService>? logger = null)
{
    internal static int ResolveMaxParallelism(CoveConfiguration configuration, int processorCount)
        => configuration.MaxParallelTasks == -1
            ? Math.Max(1, processorCount)
            : Math.Max(1, configuration.MaxParallelTasks);

    public BulkDeletionJobStart Start(
        CovePrincipal? principal,
        BulkDeletionEntityKind entityKind,
        IReadOnlyCollection<int> entityIds,
        bool deleteFiles = false,
        bool deleteGenerated = false,
        Guid? duplicateSearchId = null)
    {
        var ids = entityIds.Where(id => id > 0).Distinct().ToArray();
        var entityName = EntityName(entityKind);
        var type = $"{entityName}-bulk-delete";
        var description = $"Deleting {ids.Length.ToString(CultureInfo.InvariantCulture)} {Pluralize(entityName, ids.Length)}";
        var executionContext = new BulkDeletionExecutionContext();
        var maxParallelism = ResolveMaxParallelism(config, Environment.ProcessorCount);
        var authorizationPrincipal = SnapshotPrincipal(principal);
        var owner = JobOwner.FromPrincipal(principal);

        async Task Work(IJobProgress progress, CancellationToken ct)
        {
            JobBatchResult? result = null;
            BulkPhysicalDeletionResult? physicalResult = null;
            try
            {
                var workIds = ids;
                if (entityKind == BulkDeletionEntityKind.Video && ids.Length > 1)
                {
                    using var normalizationScope = scopeFactory.CreateScope();
                    var normalizationDb = normalizationScope.ServiceProvider.GetRequiredService<CoveContext>();
                    workIds = await CollapseSelectedVideoDescendantsAsync(normalizationDb, ids, ct);
                }

                progress.DeclareUnitCount(workIds.Length);
                result = await jobService.RunBatchAsync(
                    workIds,
                    maxInFlight: maxParallelism,
                    async (id, unit, _) =>
                    {
                        try
                        {
                            using var scope = scopeFactory.CreateScope();
                            var deletionService = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                            var deleted = await deletionService.DeleteAsync(
                                entityKind,
                                id,
                                executionContext,
                                deleteFiles,
                                deleteGenerated,
                                CancellationToken.None,
                                authorizationPrincipal: authorizationPrincipal);
                            unit.Complete(
                                deleted ? JobUnitOutcome.Succeeded : JobUnitOutcome.Skipped,
                                deleted ? null : $"{DisplayName(entityKind)} no longer exists or cannot be deleted.");
                        }
                        catch (Exception ex)
                        {
                            logger?.LogWarning(ex, "Failed to delete {EntityKind} {EntityId} in a bulk deletion job.", entityKind, id);
                            throw;
                        }
                    },
                    progress,
                    unitIdFactory: (id, _) => id.ToString(CultureInfo.InvariantCulture),
                    labelFactory: _ => $"Deleting {entityName}",
                    ct: ct);
            }
            finally
            {
                try
                {
                    if (deleteFiles)
                    {
                        progress.Report(1d, "Deleting physical files");
                        using var scope = scopeFactory.CreateScope();
                        var deletionService = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                        physicalResult = await deletionService.DeleteTrackedPhysicalFilesAsync(
                            entityKind,
                            executionContext,
                            maxParallelism,
                            // Once metadata has committed, finish deleting the corresponding detached paths
                            // even if the user cancelled between units; otherwise cancellation strands files
                            // that can no longer be reached by a retry.
                            CancellationToken.None);
                    }
                }
                finally
                {
                    if (duplicateSearchId.HasValue)
                    {
                        using var scope = scopeFactory.CreateScope();
                        var cleanupDb = scope.ServiceProvider.GetRequiredService<CoveContext>();
                        await cleanupDb.DuplicateDeletionKeeperReservations
                            .IgnoreQueryFilters()
                            .Where(item => item.SearchId == duplicateSearchId.Value)
                            .ExecuteDeleteAsync(CancellationToken.None);
                    }
                }
            }
            if (result is null)
                return;
            var summary = AppendOutcomeIds(result.Summary, "failed", result.FailedUnitIds, result.FailedUnits);
            summary = AppendOutcomeIds(summary, "skipped", result.SkippedUnitIds, result.SkippedUnits);
            if (physicalResult is { Failed: > 0 })
                summary = $"{summary}; {physicalResult.Failed.ToString(CultureInfo.InvariantCulture)} physical files could not be deleted";
            progress.SetSummary(summary);
        }

        var jobId = owner is null
            ? jobService.Enqueue(type, description, Work)
            : jobService.EnqueueOwned(owner, type, description, Work);
        return new BulkDeletionJobStart(jobId, ids.Length);
    }

    private static CovePrincipal? SnapshotPrincipal(CovePrincipal? principal)
    {
        if (principal is null)
            return null;

        return new CovePrincipal
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
    }

    internal static async Task<int[]> CollapseSelectedVideoDescendantsAsync(
        CoveContext db,
        IReadOnlyCollection<int> videoIds,
        CancellationToken ct)
    {
        var selected = videoIds.Where(id => id > 0).ToHashSet();
        if (selected.Count < 2)
            return [.. selected];

        var parentsByVideoId = new Dictionary<int, int?>();
        var frontier = selected.ToArray();
        var visited = new HashSet<int>();
        while (frontier.Length > 0)
        {
            var current = frontier.Where(visited.Add).ToArray();
            if (current.Length == 0)
                break;

            var rows = await db.Videos
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(video => current.Contains(video.Id))
                .Select(video => new { video.Id, video.ParentVideoId })
                .ToListAsync(ct);
            foreach (var row in rows)
                parentsByVideoId[row.Id] = row.ParentVideoId;
            frontier = rows
                .Where(row => row.ParentVideoId.HasValue && !visited.Contains(row.ParentVideoId.Value))
                .Select(row => row.ParentVideoId!.Value)
                .Distinct()
                .ToArray();
        }

        return selected
            .Where(videoId => !HasSelectedAncestor(videoId, selected, parentsByVideoId))
            .Order()
            .ToArray();
    }

    private static bool HasSelectedAncestor(
        int videoId,
        IReadOnlySet<int> selected,
        IReadOnlyDictionary<int, int?> parentsByVideoId)
    {
        var visited = new HashSet<int> { videoId };
        var current = videoId;
        while (parentsByVideoId.TryGetValue(current, out var parentId) && parentId.HasValue && visited.Add(parentId.Value))
        {
            if (selected.Contains(parentId.Value))
                return true;
            current = parentId.Value;
        }
        return false;
    }

    private static string EntityName(BulkDeletionEntityKind kind) => kind switch
    {
        BulkDeletionEntityKind.Text => "text",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static string DisplayName(BulkDeletionEntityKind kind) => kind switch
    {
        BulkDeletionEntityKind.Text => "Text document",
        _ => kind.ToString(),
    };

    private static string Pluralize(string entityName, int count)
        => count == 1 ? entityName : entityName switch
        {
            "gallery" => "galleries",
            _ => $"{entityName}s",
        };

    private static string AppendOutcomeIds(
        string summary,
        string outcome,
        IReadOnlyList<string> ids,
        int total)
    {
        if (ids.Count == 0)
            return summary;

        const int displayedIdLimit = 10;
        var displayed = ids.Take(displayedIdLimit).ToArray();
        var omitted = Math.Max(0, total - displayed.Length);
        var suffix = omitted > 0
            ? $" (+{omitted.ToString(CultureInfo.InvariantCulture)} more)"
            : string.Empty;
        return $"{summary}; {outcome} IDs: {string.Join(", ", displayed)}{suffix}";
    }
}

public sealed class BulkDeletionExecutionContext
{
    private readonly ConcurrentDictionary<string, byte> _physicalFilePaths = new(FilesystemPaths.PathComparer);

    public Guid PhysicalDeletionBatchId { get; } = Guid.NewGuid();

    public void StagePhysicalFiles(CoveContext db, IEnumerable<string> paths)
    {
        foreach (var storedPath in paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(FilesystemPaths.ToStoredPath)
            .Distinct(FilesystemPaths.PathComparer))
        {
            var identity = PhysicalFileIdentitySnapshot.Capture(storedPath);
            db.PendingPhysicalFileDeletions.Add(new PendingPhysicalFileDeletion
            {
                BatchId = PhysicalDeletionBatchId,
                Path = storedPath,
                IdentityCaptured = identity.Captured,
                ExpectedExists = identity.Exists,
                ExpectedLength = identity.Length,
                ExpectedLastWriteTimeUtcTicks = identity.LastWriteTimeUtcTicks,
                ExpectedCreationTimeUtcTicks = identity.CreationTimeUtcTicks,
            });
        }
    }

    public void TrackPhysicalFile(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
            _physicalFilePaths.TryAdd(FilesystemPaths.ToStoredPath(path), 0);
    }

    public string[] GetPhysicalFiles() => [.. _physicalFilePaths.Keys];
}

public sealed record BulkPhysicalDeletionResult(int Deleted, int Failed);

public sealed class BulkEntityDeletionService(
    CoveContext db,
    CustomFieldService customFields,
    ImageDeletionService imageDeletionService,
    IThumbnailService thumbnailService,
    IBlobService blobService,
    IEventBus eventBus,
    FacePerformerPropagationService? facePerformerPropagationService = null,
    IEnumerable<IFaceLifecycleParticipant>? faceLifecycleParticipants = null,
    IExtensionServiceExchange? serviceExchange = null,
    ILogger<BulkEntityDeletionService>? logger = null,
    IBlobReferenceCounter? blobReferenceCounter = null,
    EntityHostDependencyService? hostDependencyService = null,
    PhysicalFileAccessCoordinator? physicalFileCoordinator = null,
    ISegmentSpanCacheInvalidator? segmentSpanCacheInvalidator = null,
    PhysicalFileDeletionService? physicalFileDeletionService = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null,
    IAuthorizationService? authorizationService = null)
{
    private readonly EntityHostDependencyService _hostDependencies = hostDependencyService ?? new EntityHostDependencyService(db);
    private readonly PhysicalFileDeletionService _physicalFileDeletion = physicalFileDeletionService
        ?? new PhysicalFileDeletionService(db, physicalFileCoordinator ?? PhysicalFileAccessCoordinator.Shared);

    public async Task<bool> DeleteAsync(
        BulkDeletionEntityKind kind,
        int id,
        BulkDeletionExecutionContext executionContext,
        bool deleteFiles,
        bool deleteGenerated,
        CancellationToken ct,
        bool publishEvent = true,
        CovePrincipal? authorizationPrincipal = null)
    {
        var deleted = kind switch
        {
            BulkDeletionEntityKind.Video => await DeleteVideoAsync(id, executionContext, deleteFiles, deleteGenerated, authorizationPrincipal, ct),
            BulkDeletionEntityKind.Image => await imageDeletionService.DeleteAsync(id, deleteFiles, deleteGenerated, executionContext, ct),
            BulkDeletionEntityKind.Audio => await DeleteAudioAsync(id, executionContext, deleteFiles, deleteGenerated, ct),
            BulkDeletionEntityKind.Text => await DeleteTextAsync(id, executionContext, deleteFiles, deleteGenerated, ct),
            BulkDeletionEntityKind.Gallery => await DeleteGalleryAsync(id, ct),
            BulkDeletionEntityKind.Performer => await DeleteSimpleAsync(db.Performers, id, CustomFieldEntityTypes.Performer, AffinityHostType.Performer, item => [item.ImageBlobId, item.ImageOverrideBlobId], ct),
            BulkDeletionEntityKind.Tag => await DeleteSimpleAsync(db.Tags, id, CustomFieldEntityTypes.Tag, AffinityHostType.Tag, item => [item.ImageBlobId, item.ImageOverrideBlobId], ct),
            BulkDeletionEntityKind.Studio => await DeleteSimpleAsync(db.Studios, id, CustomFieldEntityTypes.Studio, AffinityHostType.Studio, item => [item.ImageBlobId, item.ImageOverrideBlobId], ct),
            BulkDeletionEntityKind.Group => await DeleteGroupAsync(id, ct),
            BulkDeletionEntityKind.Face => await DeleteFaceAsync(id, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        if (deleted && publishEvent && kind != BulkDeletionEntityKind.Face)
            PublishDeleted(kind, id);
        return deleted;
    }

    private async Task<bool> DeleteVideoAsync(
        int id,
        BulkDeletionExecutionContext context,
        bool deleteFiles,
        bool deleteGenerated,
        CovePrincipal? authorizationPrincipal,
        CancellationToken ct)
    {
        VideoDeletionResult? committedDeletion = null;
        var executionStrategy = db.Database.CreateExecutionStrategy();
        var deleted = await executionStrategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            if (committedDeletion is not null
                && await db.VideoDeletionCommitMarkers.AsNoTracking().AnyAsync(
                    marker => marker.BatchId == context.PhysicalDeletionBatchId && marker.VideoId == id,
                    ct))
            {
                // Commit can succeed even when the provider loses its acknowledgement. The captured
                // result and transaction marker let the retry verify that exact operation and still
                // run every post-commit step.
                return true;
            }

            var blobReferenceTransaction = blobReferenceTransactions is null
                ? null
                : await blobReferenceTransactions.BeginAsync(db, ct);
            try
            {
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                    : null;
                var scopeIds = await VideoHierarchyQueries.ExpandAndLockDeletionScopeAsync(db, [id], ct);
                if (!scopeIds.Contains(id))
                    return false;

                var videos = await db.Videos
                    .IgnoreQueryFilters()
                    .Include(item => item.Files)
                    .Where(item => scopeIds.Contains(item.Id))
                    .ToArrayAsync(ct);
                if (!videos.Any(video => video.Id == id))
                    return false;
                await AuthorizeVideoDeletionScopeAsync(authorizationPrincipal, scopeIds, ct);
                var videoIds = videos.Select(video => video.Id).ToArray();
                var descendantIds = scopeIds.Where(videoId => videoId != id).ToArray();
                var physicalPaths = deleteFiles
                    ? videos.SelectMany(item => item.Files).Select(file => file.Path).ToArray()
                    : [];
                var allFiles = videos.SelectMany(item => item.Files).ToArray();
                if (allFiles.Length > 0)
                    db.VideoFiles.RemoveRange(allFiles);

                var hostCleanups = new List<EntityHostDependencyCleanup>(videos.Length);
                foreach (var deletedVideo in videos)
                {
                    hostCleanups.Add(await _hostDependencies.StageDeleteAsync(AffinityHostType.Video, deletedVideo.Id, ct));
                    await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Video, deletedVideo.Id, ct);
                }

                var blobIds = videos
                    .Select(item => item.ImageBlobId)
                    .Where(blobId => !string.IsNullOrWhiteSpace(blobId))
                    .Cast<string>()
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                // Assign before SaveChanges/Commit so an ambiguous commit retry can recover the exact
                // post-commit work even though the entity rows are no longer available to query.
                committedDeletion = new VideoDeletionResult(
                    videoIds,
                    descendantIds,
                    physicalPaths,
                    blobIds,
                    [.. hostCleanups]);
                db.Videos.RemoveRange(videos);
                db.VideoDeletionCommitMarkers.Add(new VideoDeletionCommitMarker
                {
                    BatchId = context.PhysicalDeletionBatchId,
                    VideoId = id,
                });
                context.StagePhysicalFiles(db, physicalPaths);
                await db.SaveChangesAsync(ct);
                if (transaction is not null)
                    await transaction.CommitAsync(ct);
                if (blobReferenceTransaction is not null)
                    await blobReferenceTransaction.CompleteAsync();
                return true;
            }
            finally
            {
                if (blobReferenceTransaction is not null)
                    await blobReferenceTransaction.DisposeAsync();
            }
        });

        if (!deleted || committedDeletion is null)
            return false;

        foreach (var path in committedDeletion.PhysicalPaths)
            context.TrackPhysicalFile(path);
        foreach (var hostCleanup in committedDeletion.HostCleanups)
            InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(
            committedDeletion.HostCleanups.SelectMany(cleanup => cleanup.BlobIds),
            ct);

        if (deleteGenerated)
        {
            foreach (var videoId in committedDeletion.VideoIds)
            {
                try
                {
                    await thumbnailService.DeleteVideoGeneratedFilesAsync(videoId, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Video {VideoId} was deleted, but its generated files could not be fully removed.", videoId);
                }
            }
        }
        foreach (var blobId in committedDeletion.BlobIds)
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated, ct);
        foreach (var descendantId in committedDeletion.DescendantIds)
            PublishDeleted(BulkDeletionEntityKind.Video, descendantId);
        try
        {
            await db.VideoDeletionCommitMarkers
                .Where(marker => marker.BatchId == context.PhysicalDeletionBatchId && marker.VideoId == id)
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Batch ids are unique per queued job, so an orphaned proof cannot be mistaken for a
            // future operation. Keep successful deletion and cleanup outcomes even if pruning fails.
            logger?.LogWarning(ex, "Video {VideoId} was deleted, but its commit marker could not be removed.", id);
        }
        return true;
    }

    private async Task AuthorizeVideoDeletionScopeAsync(
        CovePrincipal? principal,
        IReadOnlyCollection<int> videoIds,
        CancellationToken ct)
    {
        if (principal is null)
            return;
        if (authorizationService is null)
            throw new InvalidOperationException("Video deletion authorization is unavailable.");

        foreach (var chunk in videoIds.Chunk(4_000))
        {
            var entities = chunk
                .Select(videoId => EntityRef.Of(EntityKinds.Video, videoId))
                .ToArray();
            var decisions = await authorizationService.AuthorizeManyAsync(
                principal,
                Permissions.VideosDelete,
                entities,
                ct);
            for (var index = 0; index < decisions.Count; index++)
            {
                if (decisions[index].Allowed)
                    continue;

                throw new ForbiddenException(
                    decisions[index].Reason ?? "The video deletion scope is no longer authorized.",
                    Permissions.VideosDelete,
                    entities[index]);
            }
        }
    }

    private async Task<bool> DeleteAudioAsync(
        int id,
        BulkDeletionExecutionContext context,
        bool deleteFiles,
        bool deleteGenerated,
        CancellationToken ct)
    {
        var audio = await db.Audios.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (audio is null)
            return false;

        string[] physicalPaths = [];
        if (deleteFiles)
            physicalPaths = audio.Files.Select(file => file.Path).ToArray();

        if (audio.Files.Count > 0)
            db.AudioFiles.RemoveRange(audio.Files);
        var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Audio, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Audio, id, ct);

        var blobId = audio.ImageBlobId;
        db.Audios.Remove(audio);
        context.StagePhysicalFiles(db, physicalPaths);
        await db.SaveChangesAsync(ct);

        foreach (var path in physicalPaths)
            context.TrackPhysicalFile(path);
        InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(hostCleanup.BlobIds, ct);

        if (!string.IsNullOrWhiteSpace(blobId))
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated, ct);
        return true;
    }

    private async Task<bool> DeleteTextAsync(
        int id,
        BulkDeletionExecutionContext context,
        bool deleteFiles,
        bool deleteGenerated,
        CancellationToken ct)
    {
        var text = await db.TextDocuments.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == id, ct);
        if (text is null)
            return false;

        string[] physicalPaths = [];
        if (deleteFiles)
            physicalPaths = text.Files.Select(file => file.Path).ToArray();

        if (text.Files.Count > 0)
            db.TextFiles.RemoveRange(text.Files);
        var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Text, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Text, id, ct);

        var blobId = text.ImageBlobId;
        db.TextDocuments.Remove(text);
        context.StagePhysicalFiles(db, physicalPaths);
        await db.SaveChangesAsync(ct);

        foreach (var path in physicalPaths)
            context.TrackPhysicalFile(path);
        InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(hostCleanup.BlobIds, ct);

        if (!string.IsNullOrWhiteSpace(blobId))
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated, ct);
        return true;
    }

    private async Task<bool> DeleteSimpleAsync<TEntity>(
        DbSet<TEntity> entities,
        int id,
        string customFieldEntityType,
        AffinityHostType hostType,
        Func<TEntity, IEnumerable<string?>> blobIds,
        CancellationToken ct)
        where TEntity : BaseEntity
    {
        var entity = await entities.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (entity is null)
            return false;

        var ownedBlobIds = blobIds(entity)
            .Where(blobId => !string.IsNullOrWhiteSpace(blobId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hostCleanup = await _hostDependencies.StageDeleteAsync(hostType, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(customFieldEntityType, id, ct);
        entities.Remove(entity);
        await db.SaveChangesAsync(ct);
        InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(hostCleanup.BlobIds, ct);
        foreach (var blobId in ownedBlobIds)
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated: true, ct);
        return true;
    }

    private async Task<bool> DeleteGalleryAsync(int id, CancellationToken ct)
    {
        var gallery = await db.Galleries
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (gallery is null)
            return false;

        var ownedBlobIds = new[] { gallery.ImageBlobId, gallery.BackImageBlobId }
            .Where(blobId => !string.IsNullOrWhiteSpace(blobId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (gallery.Files.Count > 0)
            db.GalleryFiles.RemoveRange(gallery.Files);
        var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Gallery, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Gallery, id, ct);
        db.Galleries.Remove(gallery);
        await db.SaveChangesAsync(ct);

        InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(hostCleanup.BlobIds, ct);
        foreach (var blobId in ownedBlobIds)
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated: true, ct);
        return true;
    }

    private async Task<bool> DeleteGroupAsync(int id, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (group is null || DynamicGroupResolver.IsProtectedBuiltInGroup(group.QuerySourceKey))
            return false;

        var ownedBlobIds = new[] { group.FrontImageBlobId, group.BackImageBlobId }
            .Where(blobId => !string.IsNullOrWhiteSpace(blobId))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Group, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Group, id, ct);
        db.Groups.Remove(group);
        await db.SaveChangesAsync(ct);
        InvalidateSegmentCaches(hostCleanup);
        await CleanupDependencyBlobsBestEffortAsync(hostCleanup.BlobIds, ct);
        foreach (var blobId in ownedBlobIds)
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated: true, ct);
        return true;
    }

    private async Task<bool> DeleteFaceAsync(int id, CancellationToken ct)
    {
        if (facePerformerPropagationService is null)
            throw new InvalidOperationException("Face deletion services are unavailable.");

        var clearedEvidence = new List<ClearedFaceRunEvidence>();
        string? coverBlobId = null;
        string[] dependencyBlobIds = [];
        int[] dependencySegmentVideoIds = [];
        var deleted = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            clearedEvidence.Clear();
            var propagationHosts = new HashSet<(FaceAppearanceHostType HostType, int HostId)>();
            var blobReferenceTransaction = blobReferenceTransactions is null
                ? null
                : await blobReferenceTransactions.BeginAsync(db, ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                var deletion = await DeleteFaceCoreAsync(id, clearedEvidence, propagationHosts, ct);
                deleted = deletion.Deleted;
                coverBlobId = deletion.CoverBlobId;
                if (!deleted)
                    return;

                var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Face, id, ct);
                dependencyBlobIds = [.. hostCleanup.BlobIds];
                dependencySegmentVideoIds = [.. hostCleanup.SegmentVideoIds];
                await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Face, id, ct);
                await db.SaveChangesAsync(ct);
                foreach (var (hostType, hostId) in propagationHosts)
                    await facePerformerPropagationService.ReconcileHostUnscopedAsync(hostType, hostId, ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                if (blobReferenceTransaction is not null)
                    await blobReferenceTransaction.CompleteAsync();
            }
            finally
            {
                if (blobReferenceTransaction is not null)
                    await blobReferenceTransaction.DisposeAsync();
            }
        });

        if (!deleted)
            return false;

        await NotifyHostFacesClearedAsync(clearedEvidence, ct);
        foreach (var videoId in dependencySegmentVideoIds)
            segmentSpanCacheInvalidator?.InvalidateVideo(videoId);
        await CleanupDependencyBlobsBestEffortAsync(dependencyBlobIds, ct);
        if (!string.IsNullOrWhiteSpace(coverBlobId))
            await CleanupBlobBestEffortAsync(coverBlobId, deleteGenerated: true, ct);
        return true;
    }

    private async Task<FaceDeletionCoreResult> DeleteFaceCoreAsync(
        int id,
        ICollection<ClearedFaceRunEvidence> clearedEvidence,
        ISet<(FaceAppearanceHostType HostType, int HostId)> propagationHosts,
        CancellationToken ct)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (face is null)
            return new(false, null);

        var mergedFaces = await db.Faces.IgnoreQueryFilters().Where(item => item.MergedIntoFaceId == id).ToListAsync(ct);
        var detections = await db.Detections
            .IgnoreQueryFilters()
            .Where(detection => detection.RefId == id && detection.RefKind != null && detection.RefKind.ToLower() == "face")
            .ToListAsync(ct);
        var appearances = await db.FaceAppearances
            .IgnoreQueryFilters()
            .Where(appearance => appearance.FaceId == id)
            .ToListAsync(ct);
        foreach (var appearance in appearances)
            propagationHosts.Add((appearance.HostType, appearance.HostId));

        var mergedFaceIds = mergedFaces.Select(item => item.Id).ToArray();
        if (mergedFaceIds.Length > 0)
        {
            var restoredHosts = await db.FaceAppearances
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(appearance => mergedFaceIds.Contains(appearance.FaceId))
                .Select(appearance => new { appearance.HostType, appearance.HostId })
                .Distinct()
                .ToListAsync(ct);
            foreach (var host in restoredHosts)
                propagationHosts.Add((host.HostType, host.HostId));
        }

        var embeddings = await db.Embeddings
            .IgnoreQueryFilters()
            .Where(embedding => embedding.HostType == EmbeddingHostType.Face && embedding.HostId == id)
            .ToListAsync(ct);
        foreach (var participant in ActiveFaceLifecycleParticipants())
            await participant.OnDeletingAsync(face, ct);
        foreach (var mergedFace in mergedFaces)
            mergedFace.MergedIntoFaceId = face.MergedIntoFaceId;

        var faceModelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var detection in detections)
        {
            if (TryReadModelKey(detection.Extra, out var detectionKey))
                faceModelKeys.Add(detectionKey);
        }
        foreach (var embedding in embeddings)
        {
            if (TryReadModelKey(embedding.Meta, out var embeddingKey))
                faceModelKeys.Add(embeddingKey);
        }
        foreach (var host in detections.Select(detection => (detection.HostType, detection.HostId)).Distinct())
        {
            foreach (var modelKey in faceModelKeys)
                clearedEvidence.Add(new ClearedFaceRunEvidence(host.HostType, host.HostId, modelKey));
        }

        if (detections.Count > 0)
            db.Detections.RemoveRange(detections);
        if (appearances.Count > 0)
            db.FaceAppearances.RemoveRange(appearances);
        if (embeddings.Count > 0)
            db.Embeddings.RemoveRange(embeddings);
        db.Faces.Remove(face);
        return new(true, face.CoverBlobId);
    }

    private async Task NotifyHostFacesClearedAsync(
        IReadOnlyCollection<ClearedFaceRunEvidence> cleared,
        CancellationToken ct)
    {
        var participants = ActiveFaceLifecycleParticipants();
        if (cleared.Count == 0 || participants.Count == 0)
            return;

        foreach (var hostGroup in cleared.GroupBy(item => (item.HostType, item.HostId)))
        {
            var (hostType, hostId) = hostGroup.Key;
            var stillHasFaces = await db.Detections.AnyAsync(
                detection => detection.HostType == hostType
                    && detection.HostId == hostId
                    && detection.RefKind != null
                    && detection.RefKind.ToLower() == "face",
                ct);
            if (stillHasFaces)
                continue;

            var modelKeys = hostGroup.Select(item => item.ModelKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modelKeys.Count == 0)
                continue;

            var evidence = new FaceRunEvidenceCleared(hostType, hostId, modelKeys);
            foreach (var participant in participants)
                await participant.OnHostFacesClearedAsync(evidence, ct);
        }
    }

    private IReadOnlyList<IFaceLifecycleParticipant> ActiveFaceLifecycleParticipants()
        => (faceLifecycleParticipants ?? [])
            .Concat(serviceExchange?.GetAll<IFaceLifecycleParticipant>() ?? [])
            .Distinct()
            .ToArray();

    private static bool TryReadModelKey(JsonDocument? document, out string modelKey)
    {
        modelKey = string.Empty;
        if (document is null
            || document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("modelKey", out var element))
            return false;

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        modelKey = raw.Trim();
        return true;
    }

    private readonly record struct FaceDeletionCoreResult(bool Deleted, string? CoverBlobId);
    private readonly record struct ClearedFaceRunEvidence(DetectionHostType HostType, int HostId, string ModelKey);
    private sealed record VideoDeletionResult(
        int[] VideoIds,
        int[] DescendantIds,
        string[] PhysicalPaths,
        string[] BlobIds,
        EntityHostDependencyCleanup[] HostCleanups);

    private async Task CleanupBlobBestEffortAsync(string blobId, bool deleteGenerated, CancellationToken ct)
    {
        if (deleteGenerated)
        {
            try
            {
                if (blobReferenceCounter is not null
                    && await blobReferenceCounter.CountReferencesAsync(blobId, maximum: 1, ct) == 0)
                    await thumbnailService.DeleteBlobGeneratedFilesAsync(blobId, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "An entity was deleted, but generated files for blob {BlobId} could not be fully removed.", blobId);
            }
        }

        try
        {
            await blobService.DeleteBlobIfUnreferencedAsync(blobId, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "An entity was deleted, but unreferenced blob {BlobId} could not be removed.", blobId);
        }
    }

    private async Task CleanupDependencyBlobsBestEffortAsync(IEnumerable<string> blobIds, CancellationToken ct)
    {
        foreach (var blobId in blobIds.Distinct(StringComparer.Ordinal))
            await CleanupBlobBestEffortAsync(blobId, deleteGenerated: true, ct);
    }

    private void InvalidateSegmentCaches(EntityHostDependencyCleanup cleanup)
    {
        foreach (var videoId in cleanup.SegmentVideoIds)
            segmentSpanCacheInvalidator?.InvalidateVideo(videoId);
    }

    public async Task<BulkPhysicalDeletionResult> DeleteTrackedPhysicalFilesAsync(
        BulkDeletionEntityKind kind,
        BulkDeletionExecutionContext context,
        int maxParallelism,
        CancellationToken ct)
    {
        if (kind is not (BulkDeletionEntityKind.Video
            or BulkDeletionEntityKind.Image
            or BulkDeletionEntityKind.Audio
            or BulkDeletionEntityKind.Text))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Only file-backed entities can delete physical files.");
        return await _physicalFileDeletion.ProcessPendingAsync(
            context.PhysicalDeletionBatchId,
            maxParallelism,
            ct);
    }

    internal static string NormalizeCaseInsensitivePath(string path)
        => PhysicalFileDeletionService.NormalizeCaseInsensitivePath(path);

    private void PublishDeleted(BulkDeletionEntityKind kind, int id)
    {
        var (eventType, entityType) = kind switch
        {
            BulkDeletionEntityKind.Video => (EventType.VideoDeleted, "video"),
            BulkDeletionEntityKind.Image => (EventType.ImageDeleted, "image"),
            BulkDeletionEntityKind.Audio => (EventType.AudioDeleted, "audio"),
            BulkDeletionEntityKind.Text => (EventType.TextDeleted, "text"),
            BulkDeletionEntityKind.Gallery => (EventType.GalleryDeleted, "gallery"),
            BulkDeletionEntityKind.Performer => (EventType.PerformerDeleted, "performer"),
            BulkDeletionEntityKind.Tag => (EventType.TagDeleted, "tag"),
            BulkDeletionEntityKind.Studio => (EventType.StudioDeleted, "studio"),
            BulkDeletionEntityKind.Group => (EventType.GroupDeleted, "group"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        eventBus.Publish(new EntityEvent(eventType, entityType, id));
    }
}
