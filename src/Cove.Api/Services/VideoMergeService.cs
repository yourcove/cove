using System.Data;
using Cove.Api.Helpers;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Helpers;
using Cove.Core.Interfaces;
using Cove.Core.Common;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>What happens to a merged copy's files.</summary>
public enum VideoMergeFileHandling
{
    /// <summary>The files move onto the kept video and the copy's record is deleted in the same transaction.</summary>
    Attach,
    /// <summary>
    /// The files stay with the copy, which the caller then deletes through the normal deletion pipeline.
    /// Timeline-bound items move only when the copy's primary file is equivalent to the kept one.
    /// </summary>
    Remove,
}

/// <summary>
/// One merge: fold the removed videos into the kept video. Absent metadata choices mean the default
/// policy; explicit choices require exactly one removed video.
/// </summary>
public sealed record VideoMergePlan(
    int KeptVideoId,
    IReadOnlyList<int> RemovedVideoIds,
    VideoMergeFileHandling FileHandling = VideoMergeFileHandling.Attach,
    VideoMergeMetadataDto? Metadata = null);

public enum VideoMergeOutcome
{
    Merged,
    KeptVideoNotFound,
    InvalidHierarchy,
    InvalidMetadata,
}

public sealed record VideoMergeResult(VideoMergeOutcome Outcome, string? Error = null)
{
    /// <summary>The removed videos that were folded into the kept video.</summary>
    public IReadOnlyList<int> MergedVideoIds { get; init; } = [];

    /// <summary>
    /// Removed videos whose user markers or timed group items could not be carried over because their
    /// primary file is not equivalent to the kept video's. Those items are removed with the copy.
    /// </summary>
    public IReadOnlyList<int> TimelineKeptVideoIds { get; init; } = [];
}

/// <summary>
/// The one place a video merge happens. The library merge endpoint and the duplicate resolver both build
/// a <see cref="VideoMergePlan"/> and call <see cref="MergeAsync"/>, so the same two copies merge the
/// same way whichever screen started it.
/// </summary>
/// <remarks>
/// The default policy, applied identically everywhere: empty kept fields are filled from the removed
/// copies and conflicts keep the kept value; lists are combined and deduplicated; ratings, favourites,
/// affinities, bookmarks and play history are carried over; segments, detections and group items move,
/// except that with <see cref="VideoMergeFileHandling.Remove"/> anything bound to a timeline moves only
/// when the two primary files are equivalent; child clips are re-parented; provenance is recorded for
/// every field that changed. Explicit metadata choices override the default for the fields they name.
/// </remarks>
public sealed class VideoMergeService(
    CoveContext db,
    CustomFieldService customFields,
    IBlobService blobService,
    IStreamService streamService,
    IEventBus eventBus,
    ITagProvenanceService? tagProvenanceService = null,
    IFieldProvenanceService? fieldProvenanceService = null,
    ISegmentSpanCacheInvalidator? segmentSpanCacheInvalidator = null,
    IAuthorizationService? authorizationService = null,
    ICurrentPrincipalAccessor? principalAccessor = null,
    BlobReferenceTransactionCoordinator? blobReferenceTransactions = null,
    ILogger<VideoMergeService>? logger = null)
{
    /// <summary>Provenance source for fields the merge filled by default rather than by an explicit choice.</summary>
    public const string DefaultPolicySourceKey = "merge";
    /// <summary>Provenance source for fields a person chose in the review.</summary>
    public const string ChosenSourceKey = "manual";

    /// <summary>The scalar fields a review can choose a side for, in the order the default policy applies them.</summary>
    private static readonly string[] MergeScalarFields =
        ["title", "code", "details", "director", "date", "studioId", "captions", "organized", "isVr", "cover"];

    public async Task<VideoMergeResult> MergeAsync(VideoMergePlan plan, CancellationToken ct)
    {
        var removedIds = plan.RemovedVideoIds.Where(id => id > 0 && id != plan.KeptVideoId).Distinct().Order().ToArray();
        if (plan.Metadata != null && removedIds.Length == 0)
            return new VideoMergeResult(VideoMergeOutcome.InvalidMetadata, "Metadata choices require at least one distinct source video.");
        var requestedIds = removedIds.Append(plan.KeptVideoId).ToArray();

        var equivalentIds = plan.FileHandling == VideoMergeFileHandling.Remove
            ? await FindTimelineEquivalentAsync(plan.KeptVideoId, removedIds, ct)
            : removedIds.ToHashSet();

        var outcome = VideoMergeOutcome.Merged;
        string? error = null;
        int[] mergedIds = [];
        var timelineKeptIds = new HashSet<int>();
        var createdCoverBlobs = new List<string>();
        var strategy = db.Database.CreateExecutionStrategy();
        try
        {
            await strategy.ExecuteAsync(async () =>
            {
                outcome = VideoMergeOutcome.Merged;
                error = null;
                mergedIds = [];
                db.ChangeTracker.Clear();
                await using var blobTransaction = blobReferenceTransactions == null ? null : await blobReferenceTransactions.BeginAsync(db, ct);
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                    : null;

                var visibleIds = await db.Videos.AsNoTracking()
                    .Where(video => requestedIds.Contains(video.Id))
                    .Select(video => video.Id)
                    .ToArrayAsync(ct);
                if (!visibleIds.Contains(plan.KeptVideoId))
                {
                    outcome = VideoMergeOutcome.KeptVideoNotFound;
                    return;
                }

                var lockedTargetTags = plan.Metadata?.TagIds == null ? [] :
                    (await EffectiveTagDtoLoader.LoadAsync(db, AffinityHostType.Video, [plan.KeptVideoId], ct))
                        .GetValueOrDefault(plan.KeptVideoId)?.Where(tag => !tag.CanRemove).Select(tag => tag.Id).ToArray() ?? [];
                var visibleTags = plan.Metadata == null ? [] : await db.Tags.Select(item => item.Id).ToArrayAsync(ct);
                var visiblePerformers = plan.Metadata == null ? [] : await db.Performers.Select(item => item.Id).ToArrayAsync(ct);
                var visibleGalleries = plan.Metadata == null ? [] : await db.Galleries.Select(item => item.Id).ToArrayAsync(ct);
                var readableStudioNames = await LoadReadableStudioNamesAsync(plan.KeptVideoId, removedIds, plan.Metadata, ct);

                using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
                var videos = await db.Videos
                    .Include(video => video.Files)
                    .Include(video => video.VideoTags)
                    .Include(video => video.VideoPerformers)
                    .Include(video => video.VideoGalleries)
                    .Include(video => video.Urls)
                    .Include(video => video.RemoteIds)
                    .Include(video => video.GroupItems)
                    .Include(video => video.ChildVideos)
                    .Include(video => video.PlayHistory)
                    .Where(video => visibleIds.Contains(video.Id))
                    .OrderBy(video => video.Id)
                    .AsSplitQuery()
                    .ToListAsync(ct);
                var target = videos.SingleOrDefault(video => video.Id == plan.KeptVideoId);
                if (target == null)
                {
                    outcome = VideoMergeOutcome.KeptVideoNotFound;
                    return;
                }
                var sources = videos.Where(video => video.Id != target.Id).ToArray();
                if (plan.Metadata != null)
                {
                    if (sources.Length != removedIds.Length)
                    {
                        outcome = VideoMergeOutcome.InvalidMetadata;
                        error = "Source video no longer exists or is unavailable.";
                        return;
                    }
                    error = await ValidateMetadataAsync(target, sources, plan.Metadata, visibleTags, visiblePerformers, visibleGalleries, ct);
                    if (error != null)
                    {
                        outcome = VideoMergeOutcome.InvalidMetadata;
                        return;
                    }
                }
                if (sources.Length == 0)
                {
                    // Nothing to fold in; the request named only videos that no longer exist.
                    return;
                }
                if (await DescendsFromAnySourceAsync(target, sources.Select(source => source.Id).ToHashSet(), ct))
                {
                    outcome = VideoMergeOutcome.InvalidHierarchy;
                    return;
                }

                var previousTagIds = target.VideoTags.Select(item => item.TagId).ToArray();
                var sourceIds = sources.Select(source => source.Id).ToArray();
                var allIds = sourceIds.Append(target.Id).ToArray();

                // Resolve chosen and default values while every original record and cover still exists.
                error = await ApplyScalarsAsync(target, sources, plan.Metadata, createdCoverBlobs, readableStudioNames, ct);
                if (error != null)
                {
                    outcome = VideoMergeOutcome.InvalidMetadata;
                    return;
                }
                await ApplyCustomFieldsAsync(target, sources, plan.Metadata?.CustomFields, ct);

                timelineKeptIds.Clear();
                var wholeVideoGroups = target.GroupItems
                    .Where(item => item.StartSec is null && item.EndSec is null)
                    .Select(item => item.GroupId)
                    .ToHashSet();
                foreach (var source in sources)
                    if (!MoveRelationships(target, source, wholeVideoGroups, timelineEquivalent: equivalentIds.Contains(source.Id)))
                        timelineKeptIds.Add(source.Id);
                foreach (var id in await MoveSegmentsAndDetectionsAsync(target.Id, sourceIds, equivalentIds, plan.FileHandling, ct))
                    timelineKeptIds.Add(id);
                await MergeRatingsAsync(target.Id, allIds, ct);
                await MergeAffinitiesAsync(target.Id, allIds, ct);
                await MergeBookmarksAsync(target.Id, allIds, ct);

                if (plan.FileHandling == VideoMergeFileHandling.Attach)
                {
                    foreach (var file in sources.SelectMany(source => source.Files))
                        file.VideoId = target.Id;
                    target.PrimaryFileId ??= sources.Select(source => source.PrimaryFileId).FirstOrDefault(id => id.HasValue);
                }
                if (plan.Metadata != null)
                    PruneCollections(target, plan.Metadata, visibleTags.Except(lockedTargetTags.Intersect(previousTagIds)).ToArray(), visiblePerformers, visibleGalleries);
                if (tagProvenanceService != null)
                    await tagProvenanceService.SyncTagSetAsync(AffinityHostType.Video, target.Id, previousTagIds,
                        target.VideoTags.Select(item => item.TagId).ToArray(), cancellationToken: ct);
                MetadataCollectionUpdater.Touch(target);
                // Persist the moves before any copy is deleted: deleting a video sweeps the engagement rows
                // still recorded against it in the database, which would take the re-pointed rows with them.
                await db.SaveChangesAsync(ct);

                if (plan.FileHandling == VideoMergeFileHandling.Attach)
                {
                    foreach (var source in sources)
                    {
                        if (tagProvenanceService != null)
                            await tagProvenanceService.RemoveForHostAsync(AffinityHostType.Video, source.Id, ct);
                        db.Videos.Remove(source);
                    }
                    await db.SaveChangesAsync(ct);
                }
                if (transaction != null)
                    await transaction.CommitAsync(ct);
                if (blobTransaction != null)
                    await blobTransaction.CompleteAsync();
                mergedIds = sourceIds;
            });
        }
        finally
        {
            foreach (var blobId in createdCoverBlobs)
            {
                try { await blobService.DeleteBlobIfUnreferencedAsync(blobId, CancellationToken.None); }
                catch (Exception ex) { logger?.LogWarning(ex, "Could not clean up an unreferenced merge cover"); }
            }
        }

        db.ChangeTracker.Clear();
        if (outcome != VideoMergeOutcome.Merged)
            return new VideoMergeResult(outcome, error);
        foreach (var id in requestedIds)
            segmentSpanCacheInvalidator?.InvalidateVideo(id);
        if (mergedIds.Length > 0)
        {
            eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", plan.KeptVideoId));
            if (plan.FileHandling == VideoMergeFileHandling.Attach)
                foreach (var id in mergedIds)
                    eventBus.Publish(new EntityEvent(EventType.VideoDeleted, "Video", id));
        }
        return new VideoMergeResult(VideoMergeOutcome.Merged) { MergedVideoIds = mergedIds, TimelineKeptVideoIds = timelineKeptIds.Order().ToArray() };
    }

    /// <summary>
    /// What a merge that removes the sources' files would do to their timeline-bound items, so a review can
    /// say before confirming which markers will not be carried over. Reads only; nothing is computed.
    /// </summary>
    public async Task<IReadOnlyList<VideoMergeAssessmentDto>> AssessAsync(int keptVideoId, IReadOnlyList<int> removedVideoIds, CancellationToken ct)
    {
        var removedIds = removedVideoIds.Where(id => id > 0 && id != keptVideoId).Distinct().Order().ToArray();
        if (removedIds.Length == 0)
            return [];
        var equivalentIds = await FindTimelineEquivalentAsync(keptVideoId, removedIds, ct);
        var markers = await db.Segments.AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && removedIds.Contains(segment.HostId) && segment.SourceKey == "user")
            .GroupBy(segment => segment.HostId)
            .Select(group => new { VideoId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.VideoId, row => row.Count, ct);
        var timedItems = await db.GroupItems.AsNoTracking()
            .Where(item => item.VideoId.HasValue && removedIds.Contains(item.VideoId.Value) && (item.StartSec != null || item.EndSec != null))
            .GroupBy(item => item.VideoId!.Value)
            .Select(group => new { VideoId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.VideoId, row => row.Count, ct);
        return removedIds
            .Select(id => new VideoMergeAssessmentDto(id, equivalentIds.Contains(id), markers.GetValueOrDefault(id) + timedItems.GetValueOrDefault(id)))
            .ToArray();
    }

    // ----- Timeline equivalence -----

    /// <summary>
    /// Decides, from stored fingerprints only, which removed copies share the kept video's timeline. Hashes
    /// are never computed here: decoding a file can take minutes, far too long for a request or for a
    /// resolver draining hundreds of groups. When a hash is missing on either side the durations decide,
    /// as the duplicate resolver always did before.
    /// </summary>
    private async Task<HashSet<int>> FindTimelineEquivalentAsync(int keptId, int[] removedIds, CancellationToken ct)
    {
        var equivalent = new HashSet<int>();
        if (removedIds.Length == 0)
            return equivalent;
        using var authorizationFilterSuppression = db.SuppressAuthorizationFilters();
        var ids = removedIds.Append(keptId).ToArray();
        var primaries = await db.Videos.AsNoTracking()
            .Where(video => ids.Contains(video.Id))
            .Select(video => new { video.Id, video.PrimaryFileId })
            .ToDictionaryAsync(video => video.Id, video => video.PrimaryFileId, ct);
        var files = await db.VideoFiles.AsNoTracking()
            .Where(file => file.VideoId.HasValue && ids.Contains(file.VideoId.Value))
            .OrderBy(file => file.Id)
            .ToListAsync(ct);
        var hashes = await VideoFileEquivalence.LoadStoredPhashesAsync(db, files.Select(file => file.Id).ToArray(), ct);
        VideoFile? PrimaryOf(int videoId)
            => files.FirstOrDefault(file => file.Id == primaries.GetValueOrDefault(videoId))
                ?? files.FirstOrDefault(file => file.VideoId == videoId);

        var kept = PrimaryOf(keptId);
        if (kept == null)
            return equivalent;
        foreach (var removedId in removedIds)
        {
            var removed = PrimaryOf(removedId);
            if (removed != null && VideoFileEquivalence.AreEquivalentOrSameLength(
                    removed.Duration, hashes.GetValueOrDefault(removed.Id), kept.Duration, hashes.GetValueOrDefault(kept.Id)))
                equivalent.Add(removedId);
        }
        return equivalent;
    }

    // ----- Validation -----

    /// <summary>
    /// Choices name a side, not a copy. With several removed copies the "source" side is the combined
    /// incoming record: for a scalar, the first copy (by id) that has a value; for lists, everything the
    /// copies have between them.
    /// </summary>
    private async Task<string?> ValidateMetadataAsync(Video target, Video[] sources, VideoMergeMetadataDto choices,
        int[] visibleTags, int[] visiblePerformers, int[] visibleGalleries, CancellationToken ct)
    {
        var compared = sources.Prepend(target).ToArray();
        if (choices.Fields?.Any(item => !MergeScalarFields.Contains(item.Key) || item.Value is not ("source" or "target")) == true
            || choices.CustomFields?.Any(item => item.Value is not ("source" or "target")) == true)
            return "Unknown metadata field or selection side.";
        // Tags, performers and galleries may come from anywhere the caller can read, as when editing one
        // video; the review adds them the same way. Remote IDs stay bound to the compared videos.
        if (!ValidSelection(choices.TagIds, visibleTags)
            || !ValidSelection(choices.PerformerIds, visiblePerformers)
            || !ValidSelection(choices.GalleryIds, visibleGalleries))
            return "Selected relationships must be readable items.";
        if (choices.Urls?.Any(url => string.IsNullOrWhiteSpace(url)) == true)
            return "Selected links must not be empty.";
        if (!ValidSelection(choices.RemoteIds?.Select(item => (item.Endpoint, item.RemoteId)),
                compared.SelectMany(video => video.RemoteIds).Select(item => (item.Endpoint, item.RemoteId)), RemoteIdKeyComparer.Instance))
            return "Selected links must belong to one of the compared videos.";
        if (choices.CustomFields != null)
        {
            var values = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, compared.Select(video => video.Id).ToArray(), ct);
            if (choices.CustomFields.Keys.Except(values.Values.SelectMany(item => item.Keys), StringComparer.OrdinalIgnoreCase).Any())
                return "Selected custom fields must belong to one of the compared videos.";
        }
        return null;
    }

    private static bool ValidSelection<T>(IEnumerable<T>? selected, IEnumerable<T> available, IEqualityComparer<T>? comparer = null)
        => selected == null || !selected.Except(available, comparer).Any();

    private async Task<bool> DescendsFromAnySourceAsync(Video target, HashSet<int> sourceIds, CancellationToken ct)
    {
        var ancestorId = target.ParentVideoId;
        var visited = new HashSet<int> { target.Id };
        while (ancestorId.HasValue)
        {
            if (!visited.Add(ancestorId.Value) || sourceIds.Contains(ancestorId.Value))
                return true;
            ancestorId = await db.Videos.AsNoTracking()
                .Where(video => video.Id == ancestorId.Value)
                .Select(video => video.ParentVideoId)
                .SingleOrDefaultAsync(ct);
        }
        return false;
    }

    /// <summary>
    /// Names of the copies' studios the caller may read, for provenance. Loaded only when a studio can
    /// actually move: when it is chosen from the source, or when the kept video has none to keep.
    /// </summary>
    private async Task<Dictionary<int, string>> LoadReadableStudioNamesAsync(int keptId, int[] removedIds, VideoMergeMetadataDto? choices, CancellationToken ct)
    {
        var names = new Dictionary<int, string>();
        var chosen = choices?.Fields?.GetValueOrDefault("studioId") == "source";
        if (!chosen && await db.Videos.AsNoTracking().AnyAsync(video => video.Id == keptId && video.StudioId.HasValue, ct))
            return names;
        var studioIds = await db.Videos.AsNoTracking()
            .Where(video => removedIds.Contains(video.Id) && video.StudioId.HasValue)
            .Select(video => video.StudioId!.Value)
            .Distinct()
            .ToArrayAsync(ct);
        if (studioIds.Length == 0)
            return names;
        foreach (var studio in await db.Studios.AsNoTracking().Where(studio => studioIds.Contains(studio.Id)).ToListAsync(ct))
            if (authorizationService == null || principalAccessor?.Current == null
                || (await authorizationService.AuthorizeAsync(principalAccessor.Current, Permissions.StudiosRead,
                    EntityRef.Of(EntityKinds.Studio, studio.Id), ct)).Allowed)
                names[studio.Id] = studio.Name;
        return names;
    }

    // ----- Scalars and custom fields -----

    private async Task<string?> ApplyScalarsAsync(Video target, Video[] sources, VideoMergeMetadataDto? choices,
        List<string> createdCoverBlobs, IReadOnlyDictionary<int, string> readableStudioNames, CancellationToken ct)
    {
        var chosen = new Dictionary<string, object?>();
        var filled = new Dictionary<string, object?>();
        foreach (var key in MergeScalarFields)
        {
            var side = choices?.Fields?.GetValueOrDefault(key);
            if (side == "target")
                continue;
            if (side == "source")
            {
                // The incoming side of the review is the first copy that has a value for this field.
                var donor = sources.FirstOrDefault(source => HasScalarValue(source, key)) ?? sources[0];
                var error = await CopyScalarAsync(target, donor, key, chosen, createdCoverBlobs, readableStudioNames, ct);
                if (error != null)
                    return error;
                continue;
            }
            // No choice for this field: fill an empty kept value from the first copy that has one.
            switch (key)
            {
                case "organized":
                    if (!target.Organized && sources.Any(source => source.Organized))
                    {
                        target.Organized = true;
                        filled[key] = true;
                    }
                    break;
                case "isVr":
                    break;
                default:
                    if (HasScalarValue(target, key))
                        break;
                    var donor = sources.FirstOrDefault(source => HasScalarValue(source, key));
                    if (donor != null)
                        await CopyScalarAsync(target, donor, key, filled, createdCoverBlobs, readableStudioNames, ct);
                    break;
            }
        }
        if (fieldProvenanceService != null)
        {
            if (chosen.Count > 0)
                await fieldProvenanceService.RecordManyAsync(AffinityHostType.Video, target.Id, chosen, ChosenSourceKey, cancellationToken: ct);
            if (filled.Count > 0)
                await fieldProvenanceService.RecordManyAsync(AffinityHostType.Video, target.Id, filled, DefaultPolicySourceKey, cancellationToken: ct);
        }
        return null;
    }

    private static bool HasScalarValue(Video video, string key) => key switch
    {
        "title" => !string.IsNullOrWhiteSpace(video.Title),
        "code" => !string.IsNullOrWhiteSpace(video.Code),
        "details" => !string.IsNullOrWhiteSpace(video.Details),
        "director" => !string.IsNullOrWhiteSpace(video.Director),
        "date" => video.Date.HasValue,
        "studioId" => video.StudioId.HasValue,
        "captions" => !string.IsNullOrWhiteSpace(video.Captions),
        "cover" => video.ImageBlobId != null,
        _ => true,
    };

    private async Task<string?> CopyScalarAsync(Video target, Video source, string key, Dictionary<string, object?> provenance,
        List<string> createdCoverBlobs, IReadOnlyDictionary<int, string> readableStudioNames, CancellationToken ct)
    {
        switch (key)
        {
            case "title": target.Title = source.Title; provenance[key] = target.Title; break;
            case "code": target.Code = source.Code; provenance[key] = target.Code; break;
            case "details": target.Details = source.Details; provenance[key] = target.Details; break;
            case "director": target.Director = source.Director; provenance[key] = target.Director; break;
            case "date": target.Date = source.Date; target.DatePrecision = source.DatePrecision; provenance[key] = PartialDate.Format(target.Date, target.DatePrecision); break;
            case "studioId":
                target.StudioId = source.StudioId;
                if (!source.StudioId.HasValue) provenance["studio"] = null;
                else if (readableStudioNames.TryGetValue(source.StudioId.Value, out var studioName)) provenance["studio"] = studioName;
                break;
            case "captions": target.Captions = source.Captions; provenance[key] = target.Captions; break;
            case "organized": target.Organized = source.Organized; provenance[key] = target.Organized; break;
            case "isVr": target.IsVr = source.IsVr; provenance[key] = target.IsVr; break;
            case "cover":
                if (source.ImageBlobId != null) target.ImageBlobId = source.ImageBlobId;
                else
                {
                    var screenshot = await streamService.GetVideoScreenshot(source.Id, null, ct);
                    if (screenshot == null) return "The selected source cover is unavailable. Reopen the comparison and try again.";
                    await using var image = screenshot.Value.stream;
                    target.ImageBlobId = await blobService.StoreBlobAsync(image, screenshot.Value.contentType, ct);
                    createdCoverBlobs.Add(target.ImageBlobId);
                }
                break;
        }
        return null;
    }

    private async Task ApplyCustomFieldsAsync(Video target, Video[] sources, Dictionary<string, string>? choices, CancellationToken ct)
    {
        var ids = sources.Select(source => source.Id).Append(target.Id).ToArray();
        var values = await customFields.GetValuesAsync(CustomFieldEntityTypes.Video, ids, ct);
        var result = new Dictionary<string, object>(values.GetValueOrDefault(target.Id) ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var (key, side) in choices ?? [])
        {
            if (side != "source") continue;
            var incoming = sources.Select(source => values.GetValueOrDefault(source.Id)).FirstOrDefault(item => item?.ContainsKey(key) == true);
            if (incoming != null) result[key] = incoming[key];
            else result.Remove(key);
            changed = true;
        }
        // Fill only keys the kept video has no value for, from the first copy that has one.
        foreach (var source in sources)
            foreach (var (key, value) in values.GetValueOrDefault(source.Id) ?? new Dictionary<string, object>())
            {
                if (result.ContainsKey(key) || choices?.ContainsKey(key) == true)
                    continue;
                result[key] = value;
                changed = true;
            }
        if (changed)
            await customFields.SaveValuesAsync(CustomFieldEntityTypes.Video, target.Id, result, ct);
    }

    // ----- Relationships, timeline items, engagement -----

    /// <param name="wholeVideoGroups">
    /// Groups the kept video already belongs to as a whole, shared across the copies so that two copies in
    /// the same group do not both bring a membership.
    /// </param>
    /// <returns>False when a timed group item could not be carried over because the timelines are not equivalent.</returns>
    private bool MoveRelationships(Video target, Video source, HashSet<int> wholeVideoGroups, bool timelineEquivalent)
    {
        var movedEverything = true;
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

        var remoteIds = target.RemoteIds.Select(remoteId => (remoteId.Endpoint, remoteId.RemoteId)).ToHashSet(RemoteIdKeyComparer.Instance);
        foreach (var remoteId in source.RemoteIds.Where(remoteId => remoteIds.Add((remoteId.Endpoint, remoteId.RemoteId))))
            remoteId.VideoId = target.Id;

        foreach (var item in source.GroupItems.ToArray())
        {
            var wholeVideo = item.StartSec is null && item.EndSec is null;
            if (wholeVideo && !wholeVideoGroups.Add(item.GroupId))
                continue;
            if (!wholeVideo && !timelineEquivalent)
            {
                movedEverything = false;
                continue;
            }
            item.VideoId = target.Id;
            if (string.Equals(item.HostType, "video", StringComparison.OrdinalIgnoreCase))
                item.HostId = target.Id;
        }

        foreach (var entry in source.PlayHistory.ToArray())
            entry.VideoId = target.Id;

        foreach (var child in source.ChildVideos.ToArray())
            if (child.Id != target.Id)
                child.ParentVideoId = target.Id;
        return movedEverything;
    }

    /// <returns>The copies whose user markers stayed behind because the timelines are not equivalent.</returns>
    private async Task<int[]> MoveSegmentsAndDetectionsAsync(int targetId, int[] sourceIds, HashSet<int> equivalentIds,
        VideoMergeFileHandling fileHandling, CancellationToken ct)
    {
        if (fileHandling == VideoMergeFileHandling.Attach)
        {
            // The files come along, so everything found on them stays meaningful.
            foreach (var segment in await db.Segments
                .Where(segment => segment.HostType == SegmentHostType.Video && sourceIds.Contains(segment.HostId)).ToListAsync(ct))
                segment.HostId = targetId;
            foreach (var detection in await db.Detections
                .Where(detection => detection.HostType == DetectionHostType.Video && sourceIds.Contains(detection.HostId)).ToListAsync(ct))
                detection.HostId = targetId;
            return [];
        }
        // The files leave with the copy. Only markers a person placed are worth carrying, and only onto an
        // equivalent timeline; generated segments and detections describe the departing file and can be
        // regenerated for the kept one.
        var markers = await db.Segments
            .Where(segment => segment.HostType == SegmentHostType.Video && sourceIds.Contains(segment.HostId) && segment.SourceKey == "user")
            .ToListAsync(ct);
        var kept = markers.Where(segment => !equivalentIds.Contains(segment.HostId)).Select(segment => segment.HostId).Distinct().ToArray();
        foreach (var segment in markers.Where(segment => equivalentIds.Contains(segment.HostId)))
            segment.HostId = targetId;
        // Shot boundaries belong to files. Any file of a copy that shows the kept video's timeline hands
        // its shots to the kept video's primary file before the copy is deleted with its files.
        var keptFileId = await db.Videos.IgnoreQueryFilters().Where(video => video.Id == targetId).Select(video => video.PrimaryFileId).FirstOrDefaultAsync(ct)
            ?? await db.VideoFiles.IgnoreQueryFilters()
                .Where(file => file.VideoId == targetId)
                .OrderBy(file => file.Id)
                .Select(file => (int?)file.Id)
                .FirstOrDefaultAsync(ct);
        if (keptFileId is int replacementFileId)
        {
            var copyFileIds = await db.VideoFiles.IgnoreQueryFilters()
                .Where(file => file.VideoId.HasValue && sourceIds.Contains(file.VideoId.Value))
                .Select(file => file.Id)
                .ToArrayAsync(ct);
            await VideoShotCarryOver.KeepOnReplacementAsync(db, copyFileIds, replacementFileId, ct);
        }
        return kept;
    }

    private async Task MergeRatingsAsync(int targetId, int[] allIds, CancellationToken ct)
    {
        var ratings = await db.Ratings
            .Where(rating => rating.HostType == RatingHostType.Video && allIds.Contains(rating.HostId))
            .ToListAsync(ct);
        foreach (var group in ratings.GroupBy(rating => new { rating.UserId, rating.Aspect }))
        {
            // A person's rating of the kept video stays authoritative; otherwise their most recent rating
            // of any copy moves over.
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

    private static int SumClamped(IEnumerable<int> values)
        => (int)Math.Clamp(values.Aggregate(0L, (sum, value) => sum + value), int.MinValue, int.MaxValue);

    // ----- Pruning to the review's selection -----

    private void PruneCollections(Video target, VideoMergeMetadataDto choices,
        int[] visibleTags, int[] visiblePerformers, int[] visibleGalleries)
    {
        // Fix up relationships moved by changing their foreign key before pruning the combined sets.
        db.ChangeTracker.DetectChanges();
        // The review can add relationships the compared videos did not have, as editing one video can.
        AddMissing(target.VideoTags, choices.TagIds, item => item.TagId, id => new VideoTag { VideoId = target.Id, TagId = id });
        AddMissing(target.VideoPerformers, choices.PerformerIds, item => item.PerformerId, id => new VideoPerformer { VideoId = target.Id, PerformerId = id });
        AddMissing(target.VideoGalleries, choices.GalleryIds, item => item.GalleryId, id => new VideoGallery { VideoId = target.Id, GalleryId = id });
        var urls = choices.Urls?.Select(url => url.Trim()).ToArray();
        AddMissing(target.Urls, urls, item => item.Url, url => new VideoUrl { VideoId = target.Id, Url = url }, StringComparer.OrdinalIgnoreCase);
        PruneCollection(target.VideoTags, choices.TagIds, item => item.TagId, visibleTags);
        PruneCollection(target.VideoPerformers, choices.PerformerIds, item => item.PerformerId, visiblePerformers);
        PruneCollection(target.VideoGalleries, choices.GalleryIds, item => item.GalleryId, visibleGalleries);
        PruneCollection(target.Urls, urls, item => item.Url, comparer: StringComparer.OrdinalIgnoreCase);
        PruneCollection(target.RemoteIds, choices.RemoteIds?.Select(item => (item.Endpoint, item.RemoteId)),
            item => (item.Endpoint, item.RemoteId), comparer: RemoteIdKeyComparer.Instance);
    }

    private static void AddMissing<T, TKey>(ICollection<T> items, IEnumerable<TKey>? selected, Func<T, TKey> key,
        Func<TKey, T> create, IEqualityComparer<TKey>? comparer = null)
    {
        if (selected == null) return;
        var existing = items.Select(key).ToHashSet(comparer);
        foreach (var id in selected.Distinct(comparer).Where(id => !existing.Contains(id)))
            items.Add(create(id));
    }

    private void PruneCollection<T, TKey>(ICollection<T> items, IEnumerable<TKey>? selected,
        Func<T, TKey> key, IEnumerable<TKey>? visible = null, IEqualityComparer<TKey>? comparer = null) where T : class
    {
        if (selected == null) return;
        var keep = selected.ToHashSet(comparer);
        var editable = visible?.ToHashSet(comparer);
        foreach (var item in items.Where(item => !keep.Contains(key(item)) && (editable == null || editable.Contains(key(item)))).ToArray())
        {
            items.Remove(item);
            db.Remove(item);
        }
    }

    private sealed class RemoteIdKeyComparer : IEqualityComparer<(string Endpoint, string RemoteId)>
    {
        public static RemoteIdKeyComparer Instance { get; } = new();

        public bool Equals((string Endpoint, string RemoteId) left, (string Endpoint, string RemoteId) right)
            => string.Equals(left.Endpoint, right.Endpoint, StringComparison.OrdinalIgnoreCase)
                && string.Equals(left.RemoteId, right.RemoteId, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Endpoint, string RemoteId) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Endpoint),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.RemoteId));
    }
}
