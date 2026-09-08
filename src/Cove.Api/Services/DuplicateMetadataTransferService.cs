using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Runs duplicate cleanup through Cove's ordinary deletion pipeline. Metadata is staged before the
/// source row and its durable physical-deletion outbox entries commit in the same transaction.
/// </summary>
public sealed class DuplicateMetadataTransferService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration configuration)
{
    public BulkDeletionJobStart StartDuplicateCleanup(
        CovePrincipal? principal,
        Guid searchId,
        IReadOnlyCollection<int> sourceIds,
        bool deleteFiles,
        bool deleteGenerated,
        bool overwriteMetadata)
    {
        var ids = sourceIds.Where(id => id > 0).Distinct().ToArray();
        var owner = JobOwner.FromPrincipal(principal);
        async Task Work(IJobProgress progress, CancellationToken ct)
        {
            var context = new BulkDeletionExecutionContext();
            var completed = 0;
            progress.DeclareUnitCount(ids.Length);
            try
            {
                foreach (var sourceId in ids)
                {
                    ct.ThrowIfCancellationRequested();
                    using var scope = scopeFactory.CreateScope();
                    var accessor = scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>();
                    accessor.Set(principal);
                    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    var targetId = await ResolveMetadataTargetAsync(db, searchId, sourceId, ct);
                    if (!targetId.HasValue)
                        throw new InvalidOperationException($"No keeper remains for duplicate video {sourceId.ToString(CultureInfo.InvariantCulture)}.");
                    var deletion = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                    if (await deletion.DeleteAsync(
                        BulkDeletionEntityKind.Video,
                        sourceId,
                        context,
                        deleteFiles,
                        deleteGenerated,
                        ct,
                        authorizationPrincipal: principal,
                        metadataTargetVideoId: targetId,
                        overwriteMetadata: overwriteMetadata))
                        completed++;
                    progress.Report((double)completed / Math.Max(1, ids.Length), $"Merged metadata and removed {completed.ToString(CultureInfo.InvariantCulture)} of {ids.Length.ToString(CultureInfo.InvariantCulture)} duplicate videos");
                }
            }
            finally
            {
                using var cleanupScope = scopeFactory.CreateScope();
                var cleanupDb = cleanupScope.ServiceProvider.GetRequiredService<CoveContext>();
                await cleanupDb.DuplicateDeletionKeeperReservations.IgnoreQueryFilters()
                    .Where(item => item.SearchId == searchId).ExecuteDeleteAsync(CancellationToken.None);
                if (deleteFiles)
                {
                    var deletion = cleanupScope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                    await deletion.DeleteTrackedPhysicalFilesAsync(
                        BulkDeletionEntityKind.Video,
                        context,
                        BulkDeletionJobService.ResolveMaxParallelism(configuration, Environment.ProcessorCount),
                        CancellationToken.None);
                }
            }
        }

        var jobId = owner is null
            ? jobService.Enqueue("video-bulk-delete", $"Merging metadata and deleting {ids.Length} duplicate videos", Work)
            : jobService.EnqueueOwned(owner, "video-bulk-delete", $"Merging metadata and deleting {ids.Length} duplicate videos", Work);
        return new BulkDeletionJobStart(jobId, ids.Length);
    }

    private static async Task<int?> ResolveMetadataTargetAsync(CoveContext db, Guid searchId, int sourceId, CancellationToken ct)
        => await db.DuplicateSearchItems
            .Where(item => item.VideoId == sourceId && item.Group != null && item.Group.SearchId == searchId)
            .SelectMany(item => item.Group!.Items.Where(candidate => candidate.Keep)
                .OrderByDescending(candidate => candidate.VideoId == item.Group.RecommendedVideoId)
                .ThenBy(candidate => candidate.VideoId)
                .Select(candidate => (int?)candidate.VideoId))
            .FirstOrDefaultAsync(ct);

    internal static async Task StageVideoTransferAsync(
        CoveContext db,
        int targetId,
        int sourceId,
        bool overwrite,
        CancellationToken ct)
    {
        if (targetId == sourceId)
            throw new InvalidOperationException("A duplicate keeper cannot be its own deletion source.");
        var records = await db.Videos.IgnoreQueryFilters()
            .Include(video => video.VideoTags)
            .Include(video => video.VideoPerformers)
            .Include(video => video.VideoGalleries)
            .Include(video => video.Urls)
            .Include(video => video.RemoteIds)
            .Include(video => video.GroupItems)
            .Include(video => video.ChildVideos)
            .Where(video => video.Id == targetId || video.Id == sourceId)
            .ToListAsync(ct);
        var target = records.SingleOrDefault(video => video.Id == targetId)
            ?? throw new InvalidOperationException($"Keeper video {targetId.ToString(CultureInfo.InvariantCulture)} no longer exists.");
        var source = records.SingleOrDefault(video => video.Id == sourceId);
        if (source is null)
            return;

        target.Title = Pick(target.Title, source.Title, overwrite);
        target.Code = Pick(target.Code, source.Code, overwrite);
        target.Details = Pick(target.Details, source.Details, overwrite);
        target.Director = Pick(target.Director, source.Director, overwrite);
        target.Date = overwrite ? source.Date ?? target.Date : target.Date ?? source.Date;
        target.StudioId = overwrite ? source.StudioId ?? target.StudioId : target.StudioId ?? source.StudioId;
        target.Captions = Pick(target.Captions, source.Captions, overwrite);
        target.ImageBlobId = Pick(target.ImageBlobId, source.ImageBlobId, overwrite);
        target.Organized |= source.Organized;
        target.IsVr |= source.IsVr;

        AddMissing(target.Urls, source.Urls, row => row.Url,
            row => new VideoUrl { VideoId = targetId, Url = row.Url }, StringComparer.OrdinalIgnoreCase);
        AddMissing(target.VideoTags, source.VideoTags, row => row.TagId,
            row => new VideoTag { VideoId = targetId, TagId = row.TagId });
        AddMissing(target.VideoPerformers, source.VideoPerformers, row => row.PerformerId,
            row => new VideoPerformer { VideoId = targetId, PerformerId = row.PerformerId });
        AddMissing(target.VideoGalleries, source.VideoGalleries, row => row.GalleryId,
            row => new VideoGallery { VideoId = targetId, GalleryId = row.GalleryId });
        AddMissing(target.RemoteIds, source.RemoteIds, row => $"{row.Endpoint}\u001f{row.RemoteId}",
            row => new VideoRemoteId { VideoId = targetId, Endpoint = row.Endpoint, RemoteId = row.RemoteId }, StringComparer.OrdinalIgnoreCase);

        var groupIds = target.GroupItems.Where(row => row.Kind == GroupItemKind.Video).Select(row => row.GroupId).ToHashSet();
        foreach (var row in source.GroupItems.Where(row => row.Kind == GroupItemKind.Video))
            if (groupIds.Add(row.GroupId))
                target.GroupItems.Add(new GroupItem
                {
                    GroupId = row.GroupId,
                    OrderIndex = row.OrderIndex,
                    Kind = GroupItemKind.Video,
                    HostType = "video",
                    HostId = targetId,
                    VideoId = targetId,
                });

        foreach (var child in source.ChildVideos.Where(child => child.Id != targetId).ToArray())
            child.ParentVideoId = targetId;
        if (target.ParentVideoId == sourceId)
            target.ParentVideoId = source.ParentVideoId == targetId ? null : source.ParentVideoId;

        await MergeCustomFieldsAsync(db, CustomFieldEntityTypes.Video, targetId, sourceId, overwrite, ct);
        await MergeSegmentsAsync(db, targetId, sourceId, ct);
        await MergeDetectionsAsync(db, targetId, sourceId, ct);
        await MergeEngagementAsync(db, AffinityHostType.Video, RatingHostType.Video, InteractionHostType.Video, targetId, sourceId, overwrite, ct);
        await MergeProvenanceAsync(db, targetId, sourceId, ct);
        target.UpdatedAt = DateTime.UtcNow;
    }

    private static void AddMissing<T, TKey>(ICollection<T> target, IEnumerable<T> source, Func<T, TKey> key, Func<T, T> clone, IEqualityComparer<TKey>? comparer = null)
    {
        var existing = target.Select(key).ToHashSet(comparer);
        foreach (var item in source)
            if (existing.Add(key(item))) target.Add(clone(item));
    }

    private static async Task MergeCustomFieldsAsync(CoveContext db, string entityType, int targetId, int sourceId, bool overwrite, CancellationToken ct)
    {
        var values = await db.CustomFieldValues.Where(value => value.EntityType == entityType && (value.EntityId == targetId || value.EntityId == sourceId)).ToListAsync(ct);
        var targets = values.Where(value => value.EntityId == targetId).GroupBy(value => value.DefinitionId).ToDictionary(group => group.Key, group => group.ToList());
        foreach (var group in values.Where(value => value.EntityId == sourceId).GroupBy(value => value.DefinitionId))
        {
            if (!targets.TryGetValue(group.Key, out var current) || overwrite)
            {
                if (current is not null) db.CustomFieldValues.RemoveRange(current);
                db.CustomFieldValues.AddRange(group.Select(value => new CustomFieldValue
                {
                    DefinitionId = value.DefinitionId, EntityType = entityType, EntityId = targetId, Position = value.Position,
                    TextValue = value.TextValue, NumberValue = value.NumberValue, BoolValue = value.BoolValue,
                    DateValue = value.DateValue, TimestampValue = value.TimestampValue, IntegerValue = value.IntegerValue,
                }));
            }
        }
    }

    private static async Task MergeSegmentsAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        var existing = (await db.Segments.Where(row => row.HostType == SegmentHostType.Video && row.HostId == targetId).ToListAsync(ct))
            .Select(SegmentKey).ToHashSet(StringComparer.Ordinal);
        foreach (var row in await db.Segments.Where(row => row.HostType == SegmentHostType.Video && row.HostId == sourceId).ToListAsync(ct))
            if (existing.Add(SegmentKey(row))) row.HostId = targetId; else db.Segments.Remove(row);
    }

    private static string SegmentKey(Segment row) => $"{row.StartSec:R}|{row.EndSec?.ToString("R", CultureInfo.InvariantCulture)}|{row.TagId}|{row.Kind}|{row.RefId}|{row.Title}";

    private static async Task MergeDetectionsAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        foreach (var row in await db.Detections.Where(row => row.HostType == DetectionHostType.Video && row.HostId == sourceId).ToListAsync(ct))
            row.HostId = targetId;
    }

    internal static async Task MergeEngagementAsync(CoveContext db, AffinityHostType affinityType, RatingHostType ratingType, InteractionHostType interactionType, int targetId, int sourceId, bool overwriteRatings, CancellationToken ct)
    {
        var affinities = await db.UserEntityAffinities.Where(row => row.HostType == affinityType && row.HostId == targetId).ToDictionaryAsync(row => row.UserId, ct);
        foreach (var source in await db.UserEntityAffinities.Where(row => row.HostType == affinityType && row.HostId == sourceId).ToListAsync(ct))
        {
            if (!affinities.TryGetValue(source.UserId, out var target)) { source.HostId = targetId; affinities[source.UserId] = source; continue; }
            target.LikeCount += source.LikeCount; target.DerivedLikeCount += source.DerivedLikeCount; target.ViewCount += source.ViewCount;
            target.CompleteCount += source.CompleteCount; target.TotalConsumedSec += source.TotalConsumedSec; target.InteractionCount += source.InteractionCount;
            target.PageVisitCount += source.PageVisitCount; target.OpenDetailCount += source.OpenDetailCount; target.OpenLightboxCount += source.OpenLightboxCount;
            target.NavigateCount += source.NavigateCount; target.PauseCount += source.PauseCount; target.SeekCount += source.SeekCount;
            target.PlayerControlCount += source.PlayerControlCount; target.SearchInteractionCount += source.SearchInteractionCount; target.FilterInteractionCount += source.FilterInteractionCount;
            target.ZoomCount += source.ZoomCount; target.IsFavorite |= source.IsFavorite; target.IsBookmarked |= source.IsBookmarked;
            target.FavoritedAt = Earlier(target.FavoritedAt, source.FavoritedAt); target.LastConsumedAt = Later(target.LastConsumedAt, source.LastConsumedAt);
            target.LastInteractedAt = Later(target.LastInteractedAt, source.LastInteractedAt); target.MaxDwellSec = Math.Max(target.MaxDwellSec, source.MaxDwellSec);
            db.UserEntityAffinities.Remove(source);
        }
        var ratings = (await db.Ratings.Where(row => row.HostType == ratingType && row.HostId == targetId).ToListAsync(ct)).ToDictionary(row => (row.UserId, row.Aspect));
        foreach (var source in await db.Ratings.Where(row => row.HostType == ratingType && row.HostId == sourceId).ToListAsync(ct))
        {
            if (!ratings.TryGetValue((source.UserId, source.Aspect), out var target)) { source.HostId = targetId; ratings[(source.UserId, source.Aspect)] = source; }
            else { if (overwriteRatings) target.Value = source.Value; db.Ratings.Remove(source); }
        }
        var bookmarkUsers = (await db.UserBookmarks.Where(row => row.HostType == affinityType && row.HostId == targetId).Select(row => row.UserId).ToListAsync(ct)).ToHashSet();
        foreach (var source in await db.UserBookmarks.Where(row => row.HostType == affinityType && row.HostId == sourceId).ToListAsync(ct))
            if (bookmarkUsers.Add(source.UserId)) source.HostId = targetId; else db.UserBookmarks.Remove(source);
        await db.Interactions.Where(row => row.HostType == interactionType && row.HostId == sourceId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.HostId, targetId), ct);
    }

    private static async Task MergeProvenanceAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        var fieldKeys = (await db.FieldProvenance.Where(row => row.HostType == AffinityHostType.Video && row.HostId == targetId).ToListAsync(ct))
            .Select(row => (row.FieldKey, row.SourceKey, row.SourceRunId, row.ModelKey)).ToHashSet();
        foreach (var row in await db.FieldProvenance.Where(row => row.HostType == AffinityHostType.Video && row.HostId == sourceId).ToListAsync(ct))
            if (fieldKeys.Add((row.FieldKey, row.SourceKey, row.SourceRunId, row.ModelKey))) row.HostId = targetId;
        var tagKeys = (await db.TagApplications.Where(row => row.HostType == AffinityHostType.Video && row.HostId == targetId).ToListAsync(ct))
            .Select(row => (row.ContextType, row.ContextId, row.TagId, row.SourceKey, row.SourceRunId, row.ModelKey)).ToHashSet();
        foreach (var row in await db.TagApplications.Where(row => row.HostType == AffinityHostType.Video && row.HostId == sourceId).ToListAsync(ct))
            if (tagKeys.Add((row.ContextType, row.ContextId, row.TagId, row.SourceKey, row.SourceRunId, row.ModelKey))) row.HostId = targetId;
    }

    private static string? Pick(string? target, string? source, bool overwrite)
        => overwrite ? First(source, target) : First(target, source);
    private static string? First(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) ? first : !string.IsNullOrWhiteSpace(second) ? second : null;
    private static DateTime? Earlier(DateTime? left, DateTime? right) => left is null ? right : right is null ? left : left < right ? left : right;
    private static DateTime? Later(DateTime? left, DateTime? right) => left is null ? right : right is null ? left : left > right ? left : right;
}
