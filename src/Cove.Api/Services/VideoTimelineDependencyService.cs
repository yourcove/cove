using System.Text.Json.Nodes;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Core.Services;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// One thing that holds a time on a video's timeline and must move when that timeline changes: a
/// segment, a detection, a clip (a child video cut from this one), or a timed range in a group.
/// </summary>
public sealed record VideoTimedDependency(
    string Kind, int Id, string? Title, double StartSec, double? EndSec,
    int? RelatedEntityId, int? TimelineHostId, int? RelativeClipId)
{
    public string Key => $"{Kind}:{Id}";

    /// <summary>
    /// Where the item ends on a timeline <paramref name="timelineDuration"/> long. A clip or group range with
    /// no end runs to the end of the video; any other item without one is an instant.
    /// </summary>
    public double EffectiveEnd(double timelineDuration) => EndSec ?? (Kind is "clip" or "group range" ? timelineDuration : StartSec);
}

/// <summary>
/// Moves a video's timed content onto a new timeline: loading it, checking who may change it, and
/// applying new times or removals. Shared by the primary-file dialog, which maps times through anchors
/// the user reviewed, and by cutting, which maps them through the cut itself.
///
/// Every check takes the principal explicitly rather than reading the request's, because a cut runs in a
/// background job long after its request, where there is no current user to ask.
/// </summary>
public sealed class VideoTimelineDependencyService(
    CoveContext db,
    IAuthorizationService authorizationService,
    EntityHostDependencyService hostDependencies,
    CustomFieldService customFields)
{
    /// <summary>Everything timed on this video's timeline, including content hosted on its clips.</summary>
    public async Task<List<VideoTimedDependency>> LoadAsync(int videoId, bool includeSegments, CancellationToken ct)
    {
        var result = new List<VideoTimedDependency>();
        var timelineVideoIds = await VideoHierarchyQueries.ExpandDeletionScopeAsync(db, [videoId], ct);
        if (includeSegments)
            result.AddRange(await db.Segments.IgnoreQueryFilters().AsNoTracking().Where(s => s.HostType == SegmentHostType.Video && timelineVideoIds.Contains(s.HostId)).Select(s => new VideoTimedDependency("segment", s.Id, s.Title, s.StartSec, s.EndSec, null, s.HostId, null)).ToListAsync(ct));
        var clips = await db.Videos.IgnoreQueryFilters().AsNoTracking().Where(v => timelineVideoIds.Contains(v.Id) && v.Id != videoId).Select(v => new { v.Id, v.Title, Start = v.ClipStartSec ?? 0, v.ClipEndSec }).ToListAsync(ct);
        result.AddRange(clips.Select(v => new VideoTimedDependency("clip", v.Id, v.Title, v.Start, v.ClipEndSec, v.Id, v.Id, null)));
        result.AddRange(await db.Detections.IgnoreQueryFilters().AsNoTracking().Where(d => d.HostType == DetectionHostType.Video && timelineVideoIds.Contains(d.HostId) && d.ObservedAtSec.HasValue).Select(d => new VideoTimedDependency("detection", d.Id, d.Class, d.ObservedAtSec!.Value, d.ObservedAtSec!.Value, null, d.HostId, null)).ToListAsync(ct));
        var clipStarts = clips.ToDictionary(clip => clip.Id, clip => clip.Start);
        var groupRanges = await db.GroupItems.IgnoreQueryFilters().AsNoTracking().Where(g => g.VideoId.HasValue && timelineVideoIds.Contains(g.VideoId.Value) && g.Kind == GroupItemKind.VideoRange).ToListAsync(ct);
        result.AddRange(groupRanges.Select(g => { var relativeClipId = g.VideoId != videoId ? g.VideoId : null; var offset = relativeClipId.HasValue ? clipStarts.GetValueOrDefault(relativeClipId.Value) : 0; return new VideoTimedDependency("group range", g.Id, null, (g.StartSec ?? 0) + offset, g.EndSec + offset, g.GroupId, g.VideoId, relativeClipId); }));
        return result;
    }

    /// <summary>True when <paramref name="principal"/> may see every clip and group the dependencies belong to.</summary>
    public async Task<bool> CanReadAsync(CovePrincipal? principal, IReadOnlyList<VideoTimedDependency> dependencies, CancellationToken ct)
    {
        foreach (var dependency in dependencies.Where(item => item.Kind == "clip"))
            if (!(await authorizationService.AuthorizeAsync(principal, Permissions.VideosRead, EntityRef.Of(EntityKinds.Video, dependency.Id), ct)).Allowed) return false;
        foreach (var groupId in dependencies.Where(item => item.Kind == "group range" && item.RelatedEntityId.HasValue).Select(item => item.RelatedEntityId!.Value).Distinct())
            if (!(await authorizationService.AuthorizeAsync(principal, Permissions.GroupsRead, EntityRef.Of(EntityKinds.Group, groupId), ct)).Allowed) return false;
        return true;
    }

    /// <summary>
    /// True when <paramref name="principal"/> may make this timeline change: move the dependencies it
    /// keeps (when <paramref name="moves"/>) and delete the ones in <paramref name="deleteKeys"/>, or all
    /// of them when <paramref name="deletesAll"/>.
    /// </summary>
    public async Task<bool> MayChangeAsync(
        CovePrincipal? principal, int videoId, IReadOnlyList<VideoTimedDependency> dependencies,
        bool moves, bool deletesAll, IReadOnlySet<string> deleteKeys, CancellationToken ct)
    {
        bool Deleting(VideoTimedDependency item) => deletesAll || deleteKeys.Contains(item.Key);
        var changesSegments = moves && dependencies.Any(item => item.Kind is "segment" or "detection");
        var deletesSegments = dependencies.Any(item => item.Kind is "segment" or "detection" && Deleting(item));
        var changesGroupRanges = moves && dependencies.Any(item => item.Kind == "group range");
        var deletesGroupRanges = dependencies.Any(item => item.Kind == "group range" && Deleting(item));
        var deletesClips = dependencies.Any(item => item.Kind == "clip" && Deleting(item));
        var timelineHostIds = dependencies.Where(item => item.Kind is "segment" or "detection").Select(item => item.TimelineHostId ?? videoId).Distinct();
        foreach (var hostId in timelineHostIds)
        {
            var hostEntity = EntityRef.Of(EntityKinds.Video, hostId);
            if (changesSegments && !(await authorizationService.AuthorizeAsync(principal, Permissions.SegmentsWrite, hostEntity, ct)).Allowed) return false;
            if (deletesSegments && !(await authorizationService.AuthorizeAsync(principal, Permissions.SegmentsDelete, hostEntity, ct)).Allowed) return false;
        }
        if (changesGroupRanges && principal?.Has(Permissions.GroupsWrite) != true) return false;
        if (deletesGroupRanges && principal?.Has(Permissions.GroupsDelete) != true) return false;
        if (deletesClips && principal?.Has(Permissions.VideosDelete) != true) return false;
        foreach (var dependency in dependencies.Where(item => item.Kind == "clip"))
        {
            var deleting = Deleting(dependency);
            if (!moves && !deleting) continue;
            var permission = deleting ? Permissions.VideosDelete : Permissions.VideosWrite;
            if (!(await authorizationService.AuthorizeAsync(principal, permission, EntityRef.Of(EntityKinds.Video, dependency.Id), ct)).Allowed) return false;
        }
        foreach (var dependency in dependencies.Where(item => item.Kind == "group range" && item.RelatedEntityId.HasValue))
        {
            var deleting = Deleting(dependency);
            if (!moves && !deleting) continue;
            var permission = deleting ? Permissions.GroupsDelete : Permissions.GroupsWrite;
            if (!(await authorizationService.AuthorizeAsync(principal, permission, EntityRef.Of(EntityKinds.Group, dependency.RelatedEntityId!.Value), ct)).Allowed) return false;
        }
        return true;
    }

    /// <summary>
    /// Writes the new times in <paramref name="mapped"/> and stages the removals in
    /// <paramref name="deletes"/>. Nothing is saved; the caller commits within its own transaction.
    ///
    /// An item with no end - a clip or group range running to the end of the video - keeps no end: its
    /// mapped range is only used for where it starts. (Writing the mapped end used to collapse such a clip
    /// to zero length, because an open end is mapped as an instant.)
    /// </summary>
    public async Task ApplyAsync(
        CovePrincipal? principal, IReadOnlyList<VideoTimedDependency> dependencies,
        IReadOnlyDictionary<string, AlignedRange> mapped, IReadOnlySet<string> deletes, CancellationToken ct)
    {
        foreach (var dependency in dependencies)
        {
            var key = dependency.Key;
            if (dependency.Kind == "segment") { var e = await db.Segments.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) { await hostDependencies.StageDeleteAsync(AffinityHostType.Segment, e.Id, ct); db.Embeddings.RemoveRange(await db.Embeddings.IgnoreQueryFilters().Where(x => x.HostType == EmbeddingHostType.Segment && x.HostId == e.Id).ToListAsync(ct)); db.Segments.Remove(e); } else if (mapped.TryGetValue(key, out var r)) { e.StartSec = r.StartSec; e.EndSec = dependency.EndSec.HasValue ? r.EndSec : null; RemoveKeyframes(e); } }
            else if (dependency.Kind == "clip") { var e = await db.Videos.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) await StageClipDeletionAsync(principal, e.Id, ct); else if (mapped.TryGetValue(key, out var r)) { e.ClipStartSec = r.StartSec; e.ClipEndSec = dependency.EndSec.HasValue ? r.EndSec : null; } }
            else if (dependency.Kind == "detection") { var e = await db.Detections.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) db.Detections.Remove(e); else if (mapped.TryGetValue(key, out var r)) e.ObservedAtSec = r.StartSec; }
            else if (dependency.Kind == "group range") { var e = await db.GroupItems.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) db.GroupItems.Remove(e); else if (mapped.TryGetValue(key, out var r)) { var mappedOffset = dependency.RelativeClipId.HasValue && mapped.TryGetValue($"clip:{dependency.RelativeClipId.Value}", out var clipRange) ? clipRange.StartSec : 0; e.StartSec = r.StartSec - mappedOffset; e.EndSec = dependency.EndSec.HasValue ? r.EndSec - mappedOffset : null; } }
        }
    }

    /// <summary>Drops embeddings computed from the old timeline: they describe footage at times that no longer hold it.</summary>
    public async Task InvalidateDerivedDataAsync(int videoId, IReadOnlyList<VideoTimedDependency> dependencies, CancellationToken ct)
    {
        var segmentIds = dependencies.Where(d => d.Kind == "segment").Select(d => d.Id).ToArray();
        var videoIds = dependencies.Select(d => d.TimelineHostId).Where(id => id.HasValue).Select(id => id!.Value).Append(videoId).Distinct().ToArray();
        var embeddings = await db.Embeddings.IgnoreQueryFilters().Where(e =>
            (e.HostType == EmbeddingHostType.Video && videoIds.Contains(e.HostId))
            || (e.HostType == EmbeddingHostType.Segment && segmentIds.Contains(e.HostId))).ToListAsync(ct);
        db.Embeddings.RemoveRange(embeddings);
    }

    private async Task StageClipDeletionAsync(CovePrincipal? principal, int clipId, CancellationToken ct)
    {
        var scopeIds = await VideoHierarchyQueries.ExpandAndLockDeletionScopeAsync(db, [clipId], ct);
        foreach (var id in scopeIds)
            if (!(await authorizationService.AuthorizeAsync(principal, Permissions.VideosDelete, EntityRef.Of(EntityKinds.Video, id), ct)).Allowed)
                throw new UnauthorizedAccessException("The clip deletion scope is not authorized.");
        var videos = await db.Videos.IgnoreQueryFilters().Include(v => v.Files).Where(v => scopeIds.Contains(v.Id)).ToListAsync(ct);
        // Flushes dependency changes staged so far too; the caller's serializable transaction keeps it atomic.
        await VideoHierarchyQueries.ReleasePrimaryFilesBeforeDeletionAsync(db, videos, ct);
        foreach (var video in videos)
        {
            await hostDependencies.StageDeleteAsync(AffinityHostType.Video, video.Id, ct);
            await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Video, video.Id, ct);
        }
        db.VideoFiles.RemoveRange(videos.SelectMany(v => v.Files));
        db.Videos.RemoveRange(videos);
    }

    private static void RemoveKeyframes(Segment segment)
    {
        if (segment.Payload is null) return;
        var node = JsonNode.Parse(segment.Payload.RootElement.GetRawText()) as JsonObject;
        if (node is null) return;
        var changed = node.Remove("keyframes");
        changed |= node.Remove("bestBbox");
        changed |= node.Remove("bestTimeSec");
        changed |= node.Remove("bestScore");
        if (changed) segment.Payload = System.Text.Json.JsonDocument.Parse(node.ToJsonString());
    }
}
