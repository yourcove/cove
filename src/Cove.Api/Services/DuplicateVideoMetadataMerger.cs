using System.Data;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Carries a duplicate's metadata onto the video that is kept before the duplicate is deleted through
/// the normal deletion pipeline. Unlike a library merge, files are never moved: the duplicate's files
/// leave with it. Anything tied to a file's timeline (user markers, clip group items) is moved only when
/// the two videos have the same running time, because a different cut would place it on the wrong frames.
/// </summary>
public sealed class DuplicateVideoMetadataMerger(
    CoveContext db,
    IEventBus eventBus,
    ISegmentSpanCacheInvalidator? segmentSpanCacheInvalidator = null)
{
    internal const double TimelineToleranceSeconds = 2;

    public async Task MergeAsync(int targetId, IReadOnlyCollection<int> sourceIds, CancellationToken ct)
    {
        var sources = sourceIds.Where(id => id > 0 && id != targetId).Distinct().Order().ToArray();
        if (sources.Length == 0)
            return;

        var movedSegments = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            movedSegments = false;
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
                : null;
            var videos = await db.Videos
                .Include(video => video.Files)
                .Include(video => video.VideoTags)
                .Include(video => video.VideoPerformers)
                .Include(video => video.VideoGalleries)
                .Include(video => video.Urls)
                .Include(video => video.RemoteIds)
                .Include(video => video.GroupItems)
                .Include(video => video.PlayHistory)
                .Where(video => video.Id == targetId || sources.Contains(video.Id))
                .OrderBy(video => video.Id)
                .AsSplitQuery()
                .ToListAsync(ct);
            var target = videos.SingleOrDefault(video => video.Id == targetId)
                ?? throw new InvalidOperationException("The video to keep no longer exists.");
            var sourceVideos = videos.Where(video => video.Id != targetId).ToArray();
            if (sourceVideos.Length == 0)
                return;

            foreach (var source in sourceVideos)
            {
                MergeScalars(target, source);
                MergeRelationships(target, source, SameTimeline(target, source));
            }
            var timelineSourceIds = sourceVideos.Where(source => SameTimeline(target, source)).Select(source => source.Id).ToArray();
            movedSegments = await MoveUserSegmentsAsync(target.Id, timelineSourceIds, ct);
            var allIds = sourceVideos.Select(source => source.Id).Append(target.Id).ToArray();
            await MergeRatingsAsync(target.Id, allIds, ct);
            await MergeAffinitiesAsync(target.Id, allIds, ct);
            await MergeBookmarksAsync(target.Id, allIds, ct);
            await MergeCustomFieldsAsync(target.Id, sourceVideos.Select(source => source.Id).ToArray(), ct);

            target.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (transaction is not null)
                await transaction.CommitAsync(ct);
        });

        db.ChangeTracker.Clear();
        if (movedSegments)
            segmentSpanCacheInvalidator?.InvalidateVideo(targetId);
        eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", targetId));
    }

    internal static bool SameTimeline(Video target, Video source)
    {
        var targetDuration = target.Files.Select(file => file.Duration).DefaultIfEmpty(0).Max();
        var sourceDuration = source.Files.Select(file => file.Duration).DefaultIfEmpty(0).Max();
        return targetDuration > 0 && sourceDuration > 0
            && Math.Abs(targetDuration - sourceDuration) <= TimelineToleranceSeconds;
    }

    private static void MergeScalars(Video target, Video source)
    {
        if (string.IsNullOrWhiteSpace(target.Title) && !string.IsNullOrWhiteSpace(source.Title))
            target.Title = source.Title;
        if (string.IsNullOrWhiteSpace(target.Details) && !string.IsNullOrWhiteSpace(source.Details))
            target.Details = source.Details;
        if (string.IsNullOrWhiteSpace(target.Code) && !string.IsNullOrWhiteSpace(source.Code))
            target.Code = source.Code;
        if (string.IsNullOrWhiteSpace(target.Director) && !string.IsNullOrWhiteSpace(source.Director))
            target.Director = source.Director;
        if (target.Date is null && source.Date is not null)
        {
            target.Date = source.Date;
            target.DatePrecision = source.DatePrecision;
        }
        target.StudioId ??= source.StudioId;
        target.Organized |= source.Organized;
    }

    private void MergeRelationships(Video target, Video source, bool sameTimeline)
    {
        var tagIds = target.VideoTags.Select(link => link.TagId).ToHashSet();
        foreach (var link in source.VideoTags.Where(link => tagIds.Add(link.TagId)))
            target.VideoTags.Add(new VideoTag { VideoId = target.Id, TagId = link.TagId });

        var performerIds = target.VideoPerformers.Select(link => link.PerformerId).ToHashSet();
        foreach (var link in source.VideoPerformers.Where(link => performerIds.Add(link.PerformerId)))
            target.VideoPerformers.Add(new VideoPerformer { VideoId = target.Id, PerformerId = link.PerformerId });

        var galleryIds = target.VideoGalleries.Select(link => link.GalleryId).ToHashSet();
        foreach (var link in source.VideoGalleries.Where(link => galleryIds.Add(link.GalleryId)))
            target.VideoGalleries.Add(new VideoGallery { VideoId = target.Id, GalleryId = link.GalleryId });

        var urls = target.Urls.Select(url => url.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in source.Urls.Where(url => urls.Add(url.Url)))
            target.Urls.Add(new VideoUrl { VideoId = target.Id, Url = url.Url });

        var remoteIds = target.RemoteIds
            .Select(remoteId => $"{remoteId.Endpoint.Trim().ToLowerInvariant()}\n{remoteId.RemoteId.Trim().ToLowerInvariant()}")
            .ToHashSet(StringComparer.Ordinal);
        foreach (var remoteId in source.RemoteIds.Where(remoteId =>
            remoteIds.Add($"{remoteId.Endpoint.Trim().ToLowerInvariant()}\n{remoteId.RemoteId.Trim().ToLowerInvariant()}")))
        {
            target.RemoteIds.Add(new VideoRemoteId { VideoId = target.Id, Endpoint = remoteId.Endpoint, RemoteId = remoteId.RemoteId });
        }

        var wholeVideoGroups = target.GroupItems
            .Where(item => item.StartSec is null && item.EndSec is null)
            .Select(item => item.GroupId)
            .ToHashSet();
        foreach (var item in source.GroupItems.ToArray())
        {
            var wholeVideo = item.StartSec is null && item.EndSec is null;
            if (wholeVideo ? !wholeVideoGroups.Add(item.GroupId) : !sameTimeline)
                continue;
            item.VideoId = target.Id;
            if (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase))
                item.HostId = target.Id;
        }

        foreach (var entry in source.PlayHistory.ToArray())
            entry.VideoId = target.Id;
    }

    private async Task<bool> MoveUserSegmentsAsync(int targetId, int[] sourceIds, CancellationToken ct)
    {
        if (sourceIds.Length == 0)
            return false;
        // Only markers a person placed are carried over; generated segments can be regenerated for the
        // kept file and would otherwise duplicate the keeper's own analysis.
        var segments = await db.Segments
            .Where(segment => segment.HostType == SegmentHostType.Video
                && sourceIds.Contains(segment.HostId)
                && segment.SourceKey == "user")
            .ToListAsync(ct);
        foreach (var segment in segments)
            segment.HostId = targetId;
        return segments.Count > 0;
    }

    private async Task MergeRatingsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var ratings = await db.Ratings
            .Where(rating => rating.HostType == RatingHostType.Video && allIds.Contains(rating.HostId))
            .ToListAsync(ct);
        foreach (var group in ratings.GroupBy(rating => new { rating.UserId, rating.Aspect }))
        {
            // A person's rating of the kept video stays authoritative; otherwise their most recent
            // rating of any copy moves over.
            var keeper = group
                .OrderByDescending(rating => rating.HostId == targetId)
                .ThenByDescending(rating => rating.UpdatedAt)
                .ThenBy(rating => rating.Id)
                .First();
            keeper.HostId = targetId;
            db.Ratings.RemoveRange(group.Where(rating => rating.Id != keeper.Id));
        }
    }

    private async Task MergeAffinitiesAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var affinities = await db.UserEntityAffinities
            .Where(affinity => affinity.HostType == AffinityHostType.Video && allIds.Contains(affinity.HostId))
            .ToListAsync(ct);
        foreach (var group in affinities.GroupBy(affinity => affinity.UserId))
        {
            var ordered = group.OrderByDescending(affinity => affinity.HostId == targetId).ThenBy(affinity => affinity.Id).ToArray();
            var keeper = ordered[0];
            var keeperWasTarget = keeper.HostId == targetId;
            keeper.HostId = targetId;
            keeper.IsFavorite = ordered.Any(affinity => affinity.IsFavorite);
            keeper.FavoritedAt = ordered.Min(affinity => affinity.FavoritedAt);
            keeper.IsBookmarked = ordered.Any(affinity => affinity.IsBookmarked);
            keeper.ViewCount = SumClamped(ordered.Select(affinity => affinity.ViewCount));
            keeper.CompleteCount = SumClamped(ordered.Select(affinity => affinity.CompleteCount));
            keeper.LikeCount = SumClamped(ordered.Select(affinity => affinity.LikeCount));
            keeper.TotalConsumedSec = ordered.Sum(affinity => affinity.TotalConsumedSec);
            keeper.LastConsumedAt = ordered.Max(affinity => affinity.LastConsumedAt);
            // A resume position belongs to the timeline it was recorded on, so only the keeper's own survives.
            if (!keeperWasTarget)
                keeper.LastPositionSec = null;
            db.UserEntityAffinities.RemoveRange(ordered.Skip(1));
        }
    }

    private async Task MergeBookmarksAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var bookmarks = await db.UserBookmarks
            .Where(bookmark => bookmark.HostType == AffinityHostType.Video && allIds.Contains(bookmark.HostId))
            .ToListAsync(ct);
        foreach (var group in bookmarks.GroupBy(bookmark => bookmark.UserId))
        {
            if (group.Any(bookmark => bookmark.HostId == targetId))
            {
                db.UserBookmarks.RemoveRange(group.Where(bookmark => bookmark.HostId != targetId));
                continue;
            }
            var createdAt = group.Min(bookmark => bookmark.CreatedAt);
            db.UserBookmarks.RemoveRange(group);
            db.UserBookmarks.Add(new UserBookmark
            {
                UserId = group.Key,
                HostType = AffinityHostType.Video,
                HostId = targetId,
                CreatedAt = createdAt,
            });
        }
    }

    private async Task MergeCustomFieldsAsync(int targetId, int[] sourceIds, CancellationToken ct)
    {
        var values = await db.CustomFieldValues
            .Where(value => value.EntityType == CustomFieldEntityTypes.Video
                && (value.EntityId == targetId || sourceIds.Contains(value.EntityId)))
            .ToListAsync(ct);
        var filledDefinitions = values.Where(value => value.EntityId == targetId).Select(value => value.DefinitionId).ToHashSet();
        // Fill only fields the keeper has no value for; a field's positions travel together so a
        // multi-valued field is never interleaved from two videos.
        foreach (var definition in values
            .Where(value => value.EntityId != targetId && !filledDefinitions.Contains(value.DefinitionId))
            .GroupBy(value => value.DefinitionId))
        {
            var donorId = definition.Min(value => value.EntityId);
            foreach (var value in definition.Where(value => value.EntityId == donorId))
                value.EntityId = targetId;
            filledDefinitions.Add(definition.Key);
        }
    }

    private static int SumClamped(IEnumerable<int> values)
        => (int)Math.Clamp(values.Aggregate(0L, (sum, value) => sum + value), int.MinValue, int.MaxValue);
}
