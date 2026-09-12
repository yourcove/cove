using System.Data;
using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Core.Services;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/videos/{videoId:int}/alignments")]
[RequiresPermission(Permissions.VideosRead, Permissions.VideosWrite, Permissions.FilesRead, Permissions.SegmentsRead, Permissions.GroupsRead)]
[RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, RouteValueName = "videoId")]
public class VideoAlignmentsController(CoveContext db, VideoAlignmentExtractor extractor, IFingerprintService fingerprintService,
    ICurrentPrincipalAccessor principalAccessor, IAuditService audit, IThumbnailService thumbnailService,
    IAuthorizationService authorizationService, EntityHostDependencyService hostDependencies, CustomFieldService customFields,
    ISegmentSpanCacheInvalidator segmentSpanCacheInvalidator, IEventBus eventBus, VideoGeneratedAssetCoordinator generatedAssetCoordinator,
    ILogger<VideoAlignmentsController> logger) : ControllerBase
{
    private const int EquivalentPhashDistance = 8;
    private const double EquivalentDurationTolerance = 1;
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> FingerprintLocks = new();

    [HttpGet]
    public async Task<IActionResult> Get(int videoId, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return NotFound();
        var files = await db.VideoFiles.AsNoTracking().Where(f => f.VideoId == videoId).OrderBy(f => f.Id).ToListAsync(ct);
        return Ok(new { video.PrimaryFileId, files = files.Select(f => new { f.Id, f.Basename, f.Duration, available = FileAvailable(FilesystemPaths.ToNativePath(f.Path)) }) });
    }

    [HttpPost("assess")]
    public async Task<IActionResult> Assess(int videoId, [FromBody] AnalyzeVideoAlignment request, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(v => v.Id == videoId, ct);
        if (video is null) return NotFound();
        if (!video.PrimaryFileId.HasValue)
        {
            var initialTarget = await db.VideoFiles.SingleOrDefaultAsync(f => f.Id == request.TargetFileId && f.VideoId == videoId, ct);
            if (initialTarget is null) return BadRequest("The replacement must be a file attached to this video.");
            var dependenciesWithoutTimeline = await LoadDependencies(videoId, ct);
            if (!await CanReadDependencies(dependenciesWithoutTimeline, ct)) return Forbid();
            return Ok(new { sourceFileId = (int?)null, targetFileId = initialTarget.Id, sourceDuration = (double?)null, targetDuration = initialTarget.Duration,
                equivalent = false, canAlign = false, dependencyCounts = dependenciesWithoutTimeline.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()), dependencyCount = dependenciesWithoutTimeline.Count });
        }
        var pair = await LoadFiles(videoId, video.PrimaryFileId.Value, request.TargetFileId, ct);
        if (pair is null) return BadRequest("The replacement must be another file attached to this video.");
        var (source, target) = pair.Value;
        var sourceHash = await EnsurePhash(source, ct);
        var targetHash = await EnsurePhash(target, ct);
        var equivalent = !string.IsNullOrWhiteSpace(sourceHash) && !string.IsNullOrWhiteSpace(targetHash)
            && MetadataServerService.ComputePhashHammingDistance(sourceHash, targetHash) <= EquivalentPhashDistance
            && Math.Abs(source.Duration - target.Duration) <= EquivalentDurationTolerance;
        var dependencies = await LoadDependencies(videoId, ct);
        if (!await CanReadDependencies(dependencies, ct)) return Forbid();
        return Ok(new { sourceFileId = source.Id, targetFileId = target.Id, sourceDuration = source.Duration, targetDuration = target.Duration,
            equivalent, canAlign = FileAvailable(FilesystemPaths.ToNativePath(source.Path)) && FileAvailable(FilesystemPaths.ToNativePath(target.Path)),
            dependencyCounts = dependencies.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()), dependencyCount = dependencies.Count });
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> Analyze(int videoId, [FromBody] AnalyzeVideoAlignment request, CancellationToken ct)
    {
        var pair = await LoadFiles(videoId, request.SourceFileId, request.TargetFileId, ct);
        if (pair is null) return BadRequest("Select two different files belonging to this video.");
        var (source, target) = pair.Value;
        var sourcePath = FilesystemPaths.ToNativePath(source.Path);
        var targetPath = FilesystemPaths.ToNativePath(target.Path);
        if (!FileAvailable(sourcePath) || !FileAvailable(targetPath)) return Conflict("Both files must be available to find an alignment.");
        try
        {
            var sourceBefore = FileIdentity(sourcePath);
            var targetBefore = FileIdentity(targetPath);
            var sourceTask = extractor.ExtractAsync(sourcePath, source.Duration, ct);
            var targetTask = extractor.ExtractAsync(targetPath, target.Duration, ct);
            await Task.WhenAll(sourceTask, targetTask);
            var sourceExtracted = await sourceTask;
            var targetExtracted = await targetTask;
            if (!StillMatches(sourcePath, sourceBefore) || !StillMatches(targetPath, targetBefore)
                || await db.VideoFiles.CountAsync(f => f.VideoId == videoId && (f.Id == source.Id || f.Id == target.Id), ct) != 2)
                return Conflict("A selected file changed while it was being sampled. Try again.");
            var automatic = await extractor.FindAutomaticAlignmentAsync(sourceExtracted.Samples, targetExtracted.Samples,
                source.Duration, target.Duration, Math.Max(sourceExtracted.Step, targetExtracted.Step), ct);
            var anchors = automatic.Anchors;
            if (anchors.Count > 0 && VideoAlignment.Validate(anchors, source.Duration, target.Duration) is not null) anchors = [];
            return Ok(new { anchors, sampleStep = Math.Max(sourceExtracted.Step, targetExtracted.Step),
                message = anchors.Count > 0
                    ? "A consistent timeline offset was found across sampled positions. Review the affected content before applying."
                    : automatic.InternalEditsDetected
                        ? "The files appear to contain cuts within the timeline, so timed content cannot be aligned reliably. Choose another file or remove the timed content."
                        : "No unambiguous visual alignment was found." });
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StatusCode(408, "Alignment extraction timed out."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { logger.LogWarning(ex, "Ad hoc alignment failed for video {VideoId}", videoId); return Conflict("A selected video could not be read. Check both files and FFmpeg, then retry."); }
    }

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(int videoId, [FromBody] ReviewVideoAlignment request, CancellationToken ct)
    {
        var pair = await LoadFiles(videoId, request.SourceFileId, request.TargetFileId, ct);
        if (pair is null) return BadRequest("The selected files no longer belong to this video.");
        var error = VideoAlignment.Validate(request.Anchors, pair.Value.Source.Duration, pair.Value.Target.Duration);
        if (error is not null) return BadRequest(error);
        var dependencies = await LoadDependencies(videoId, ct);
        if (!await CanReadDependencies(dependencies, ct)) return Forbid();
        var mappedDependencies = dependencies.Select(d => new MappedDependency(d, MapDependency(d, request.Anchors))).ToList();
        var selectedComparisons = mappedDependencies
            .Where(item => item.Mapped is not null && item.Dependency.Kind == "segment").Take(5)
            .Concat(mappedDependencies.Where(item => item.Mapped is not null && item.Dependency.Kind == "clip").Take(5))
            .ToList();
        var comparisons = await Task.WhenAll(selectedComparisons.Select(item => BuildComparison(item, pair.Value.Source, pair.Value.Target, request.Anchors, ct)));
        return Ok(new { dependencies = mappedDependencies.Select(item => new { item.Dependency.Kind, item.Dependency.Id, item.Dependency.Title,
                item.Dependency.StartSec, item.Dependency.EndSec, mapped = item.Mapped }),
            comparisons,
            comparisonCounts = new { segment = mappedDependencies.Count(item => item.Dependency.Kind == "segment"), clip = mappedDependencies.Count(item => item.Dependency.Kind == "clip") },
            message = "Every dependency must map to one matching section or be selected for removal." });
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply(int videoId, [FromBody] VideoSetPrimaryFileDto request, CancellationToken ct)
    {
        IActionResult result = Problem("The primary file change could not be applied.");
        var changed = false;
        object? auditDetails = null;
        var affectedTimelineVideoIds = new HashSet<int> { videoId };
        try
        {
            await using var assetLease = await generatedAssetCoordinator.AcquireAsync(videoId, ct);
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                result = Problem("The primary file change could not be applied.");
                changed = false;
                auditDetails = null;
                affectedTimelineVideoIds = [videoId];
                await using var transaction = db.Database.IsRelational()
                    ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                    : null;
                var video = await db.Videos.SingleOrDefaultAsync(v => v.Id == videoId, ct);
                if (video is null) { result = NotFound(); return; }
                if (video.PrimaryFileId != request.ExpectedPrimaryFileId) { result = Conflict("The primary file changed while this dialog was open. Reopen it and try again."); return; }
                var target = await db.VideoFiles.SingleOrDefaultAsync(f => f.Id == request.FileId && f.VideoId == videoId, ct);
                if (target is null) { result = Conflict("The replacement file is no longer attached to this video."); return; }
                if (video.PrimaryFileId == target.Id) { result = Ok(new { video.PrimaryFileId }); return; }
                var dependencies = await LoadDependencies(videoId, ct);
                affectedTimelineVideoIds = dependencies.Select(item => item.TimelineHostId).Where(id => id.HasValue).Select(id => id!.Value).Append(videoId).ToHashSet();
                if (!await HasDependencyPermissions(videoId, dependencies, request.Resolution, request.DeleteDependencies, ct)) { result = Forbid(); return; }
                var deletes = (request.DeleteDependencies ?? []).Select(d => $"{d.Kind}:{d.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                var mapped = new Dictionary<string, AlignedRange>();
                if (request.Resolution == "align")
                {
                    if (!video.PrimaryFileId.HasValue) { result = Conflict("There is no current primary timeline to align."); return; }
                    var source = await db.VideoFiles.SingleOrDefaultAsync(f => f.Id == video.PrimaryFileId && f.VideoId == videoId, ct);
                    var error = source is null ? "The current primary file is unavailable." : VideoAlignment.Validate(request.Anchors ?? [], source.Duration, target.Duration);
                    if (error is not null) { result = Conflict(error); return; }
                    foreach (var dependency in dependencies)
                    {
                        var key = $"{dependency.Kind}:{dependency.Id}";
                        if (deletes.Contains(key)) continue;
                        if (MapDependency(dependency, request.Anchors!) is not { } range)
                        { result = Conflict($"{dependency.Kind} {dependency.Id} is unresolved. Add anchors that cover it or select it for removal."); return; }
                        mapped[key] = range;
                    }
                }
                else if (request.Resolution == "delete") deletes = dependencies.Select(d => $"{d.Kind}:{d.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                else if (dependencies.Count > 0)
                {
                    var source = video.PrimaryFileId.HasValue ? await db.VideoFiles.SingleOrDefaultAsync(f => f.Id == video.PrimaryFileId, ct) : null;
                    var sourceHash = source is null ? null : await EnsurePhash(source, ct);
                    var targetHash = await EnsurePhash(target, ct);
                    var equivalent = source is not null && !string.IsNullOrWhiteSpace(sourceHash) && !string.IsNullOrWhiteSpace(targetHash)
                        && Math.Abs(source.Duration - target.Duration) <= EquivalentDurationTolerance
                        && MetadataServerService.ComputePhashHammingDistance(sourceHash, targetHash) <= EquivalentPhashDistance;
                    if (!equivalent) { result = Conflict("Timed dependencies require alignment or removal before changing the primary file."); return; }
                }
                await ApplyDependencies(dependencies, mapped, deletes, ct);
                await InvalidateTimelineDerivedData(videoId, dependencies, ct);
                var previousId = video.PrimaryFileId;
                video.PrimaryFileId = target.Id;
                video.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
                if (transaction is not null) await transaction.CommitAsync(ct);
                changed = true;
                result = Ok(new { video.PrimaryFileId, mapped = mapped.Count, deleted = deletes.Count });
                auditDetails = new { previousFileId = previousId, primaryFileId = target.Id, request.Resolution, mapped = mapped.Count, deleted = deletes.Count };
            });
            if (!changed) return result;
            foreach (var affectedVideoId in affectedTimelineVideoIds)
            {
                try { await thumbnailService.DeleteVideoGeneratedFilesAsync(affectedVideoId, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Could not delete generated assets after changing the primary timeline for video {VideoId}", affectedVideoId); }
                generatedAssetCoordinator.Advance(affectedVideoId);
            }
            try
            {
                foreach (var affectedVideoId in affectedTimelineVideoIds)
                {
                    segmentSpanCacheInvalidator.InvalidateVideo(affectedVideoId);
                    eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "video", affectedVideoId));
                }
                await audit.LogAsync("video.primary-file.update", AuditOutcomes.Success, principalAccessor.Current, "video", videoId.ToString(), auditDetails, ct);
            }
            catch (Exception ex) { logger.LogWarning(ex, "Could not finish post-commit notifications after changing the primary file for video {VideoId}", videoId); }
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Conflict("The primary file changed while this dialog was open. Reopen it and try again.");
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set primary file for video {VideoId}", videoId);
            return Problem("The primary file change could not be applied.");
        }
    }

    private async Task<bool> HasDependencyPermissions(int videoId, List<TimedDependency> dependencies, string resolution, List<VideoTimedDependencyRef>? requestedDeletes, CancellationToken ct)
    {
        var principal = principalAccessor.Current;
        var deleteKeys = (requestedDeletes ?? []).Select(item => $"{item.Kind}:{item.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deletesAll = resolution == "delete";
        var changesSegments = resolution == "align" && dependencies.Any(item => item.Kind is "segment" or "detection");
        var deletesSegments = dependencies.Any(item => (item.Kind is "segment" or "detection") && (deletesAll || deleteKeys.Contains($"{item.Kind}:{item.Id}")));
        var changesGroupRanges = resolution == "align" && dependencies.Any(item => item.Kind == "group range");
        var deletesGroupRanges = dependencies.Any(item => item.Kind == "group range" && (deletesAll || deleteKeys.Contains($"{item.Kind}:{item.Id}")));
        var deletesClips = dependencies.Any(item => item.Kind == "clip" && (deletesAll || deleteKeys.Contains($"{item.Kind}:{item.Id}")));
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
            var deleting = deletesAll || deleteKeys.Contains($"{dependency.Kind}:{dependency.Id}");
            if (resolution != "align" && !deleting) continue;
            var permission = deleting ? Permissions.VideosDelete : Permissions.VideosWrite;
            if (!(await authorizationService.AuthorizeAsync(principal, permission, EntityRef.Of(EntityKinds.Video, dependency.Id), ct)).Allowed) return false;
        }
        foreach (var dependency in dependencies.Where(item => item.Kind == "group range" && item.RelatedEntityId.HasValue))
        {
            var deleting = deletesAll || deleteKeys.Contains($"{dependency.Kind}:{dependency.Id}");
            if (resolution != "align" && !deleting) continue;
            var permission = deleting ? Permissions.GroupsDelete : Permissions.GroupsWrite;
            if (!(await authorizationService.AuthorizeAsync(principal, permission, EntityRef.Of(EntityKinds.Group, dependency.RelatedEntityId!.Value), ct)).Allowed) return false;
        }
        return true;
    }

    private async Task ApplyDependencies(List<TimedDependency> dependencies, Dictionary<string, AlignedRange> mapped, HashSet<string> deletes, CancellationToken ct)
    {
        foreach (var dependency in dependencies)
        {
            var key = $"{dependency.Kind}:{dependency.Id}";
            if (dependency.Kind == "segment") { var e = await db.Segments.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) { await hostDependencies.StageDeleteAsync(AffinityHostType.Segment, e.Id, ct); db.Embeddings.RemoveRange(await db.Embeddings.IgnoreQueryFilters().Where(x => x.HostType == EmbeddingHostType.Segment && x.HostId == e.Id).ToListAsync(ct)); db.Segments.Remove(e); } else if (mapped.TryGetValue(key, out var r)) { e.StartSec = r.StartSec; e.EndSec = dependency.EndSec.HasValue ? r.EndSec : null; RemoveKeyframes(e); } }
            else if (dependency.Kind == "clip") { var e = await db.Videos.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) await StageClipDeletion(e.Id, ct); else if (mapped.TryGetValue(key, out var r)) { e.ClipStartSec = r.StartSec; e.ClipEndSec = r.EndSec; } }
            else if (dependency.Kind == "detection") { var e = await db.Detections.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) db.Detections.Remove(e); else if (mapped.TryGetValue(key, out var r)) e.ObservedAtSec = r.StartSec; }
            else if (dependency.Kind == "group range") { var e = await db.GroupItems.IgnoreQueryFilters().SingleAsync(x => x.Id == dependency.Id, ct); if (deletes.Contains(key)) db.GroupItems.Remove(e); else if (mapped.TryGetValue(key, out var r)) { var mappedOffset = dependency.RelativeClipId.HasValue && mapped.TryGetValue($"clip:{dependency.RelativeClipId.Value}", out var clipRange) ? clipRange.StartSec : 0; e.StartSec = r.StartSec - mappedOffset; e.EndSec = r.EndSec - mappedOffset; } }
        }
    }

    private async Task StageClipDeletion(int clipId, CancellationToken ct)
    {
        var scopeIds = await VideoHierarchyQueries.ExpandAndLockDeletionScopeAsync(db, [clipId], ct);
        foreach (var id in scopeIds)
            if (!(await authorizationService.AuthorizeAsync(principalAccessor.Current, Permissions.VideosDelete, EntityRef.Of(EntityKinds.Video, id), ct)).Allowed)
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

    private async Task InvalidateTimelineDerivedData(int videoId, List<TimedDependency> dependencies, CancellationToken ct)
    {
        var segmentIds = dependencies.Where(d => d.Kind == "segment").Select(d => d.Id).ToArray();
        var videoIds = dependencies.Select(d => d.TimelineHostId).Where(id => id.HasValue).Select(id => id!.Value).Append(videoId).Distinct().ToArray();
        var embeddings = await db.Embeddings.IgnoreQueryFilters().Where(e =>
            (e.HostType == EmbeddingHostType.Video && videoIds.Contains(e.HostId))
            || (e.HostType == EmbeddingHostType.Segment && segmentIds.Contains(e.HostId))).ToListAsync(ct);
        db.Embeddings.RemoveRange(embeddings);
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

    private async Task<List<TimedDependency>> LoadDependencies(int videoId, CancellationToken ct)
    {
        var result = new List<TimedDependency>();
        var timelineVideoIds = await VideoHierarchyQueries.ExpandDeletionScopeAsync(db, [videoId], ct);
        if (principalAccessor.Current?.Has(Permissions.SegmentsRead) == true)
            result.AddRange(await db.Segments.IgnoreQueryFilters().AsNoTracking().Where(s => s.HostType == SegmentHostType.Video && timelineVideoIds.Contains(s.HostId)).Select(s => new TimedDependency("segment", s.Id, s.Title, s.StartSec, s.EndSec, null, s.HostId, null)).ToListAsync(ct));
        var clips = await db.Videos.IgnoreQueryFilters().AsNoTracking().Where(v => timelineVideoIds.Contains(v.Id) && v.Id != videoId).Select(v => new { v.Id, v.Title, Start = v.ClipStartSec ?? 0, v.ClipEndSec }).ToListAsync(ct);
        result.AddRange(clips.Select(v => new TimedDependency("clip", v.Id, v.Title, v.Start, v.ClipEndSec, v.Id, v.Id, null)));
        result.AddRange(await db.Detections.IgnoreQueryFilters().AsNoTracking().Where(d => d.HostType == DetectionHostType.Video && timelineVideoIds.Contains(d.HostId) && d.ObservedAtSec.HasValue).Select(d => new TimedDependency("detection", d.Id, d.Class, d.ObservedAtSec!.Value, d.ObservedAtSec!.Value, null, d.HostId, null)).ToListAsync(ct));
        var clipStarts = clips.ToDictionary(clip => clip.Id, clip => clip.Start);
        var groupRanges = await db.GroupItems.IgnoreQueryFilters().AsNoTracking().Where(g => g.VideoId.HasValue && timelineVideoIds.Contains(g.VideoId.Value) && g.Kind == GroupItemKind.VideoRange).ToListAsync(ct);
        result.AddRange(groupRanges.Select(g => { var relativeClipId = g.VideoId != videoId ? g.VideoId : null; var offset = relativeClipId.HasValue ? clipStarts.GetValueOrDefault(relativeClipId.Value) : 0; return new TimedDependency("group range", g.Id, null, (g.StartSec ?? 0) + offset, g.EndSec + offset, g.GroupId, g.VideoId, relativeClipId); }));
        return result;
    }

    private async Task<bool> CanReadDependencies(List<TimedDependency> dependencies, CancellationToken ct)
    {
        foreach (var dependency in dependencies.Where(item => item.Kind == "clip"))
            if (!(await authorizationService.AuthorizeAsync(principalAccessor.Current, Permissions.VideosRead, EntityRef.Of(EntityKinds.Video, dependency.Id), ct)).Allowed) return false;
        foreach (var groupId in dependencies.Where(item => item.Kind == "group range" && item.RelatedEntityId.HasValue).Select(item => item.RelatedEntityId!.Value).Distinct())
            if (!(await authorizationService.AuthorizeAsync(principalAccessor.Current, Permissions.GroupsRead, EntityRef.Of(EntityKinds.Group, groupId), ct)).Allowed) return false;
        return true;
    }

    private async Task<string?> EnsurePhash(VideoFile file, CancellationToken ct)
    {
        var fingerprintLock = FingerprintLocks.GetOrAdd(file.Id, static _ => new SemaphoreSlim(1, 1));
        await fingerprintLock.WaitAsync(ct);
        try
        {
            var existing = await db.FileFingerprints.FirstOrDefaultAsync(f => f.FileId == file.Id && f.Type == "phash" && f.Value != "", ct);
            if (existing is not null) return existing.Value;
            var path = FilesystemPaths.ToNativePath(file.Path);
            if (!FileAvailable(path)) return null;
            var value = await fingerprintService.ComputeVideoPhashAsync(path, file.Duration, ct);
            if (string.IsNullOrWhiteSpace(value)) return null;
            db.FileFingerprints.Add(new FileFingerprint { FileId = file.Id, Type = "phash", Value = value });
            await db.SaveChangesAsync(ct);
            return value;
        }
        finally
        {
            fingerprintLock.Release();
        }
    }

    private async Task<(VideoFile Source, VideoFile Target)?> LoadFiles(int videoId, int sourceId, int targetId, CancellationToken ct)
    {
        if (sourceId == targetId || !await db.Videos.AnyAsync(v => v.Id == videoId, ct)) return null;
        var files = await db.VideoFiles.Where(f => f.VideoId == videoId && (f.Id == sourceId || f.Id == targetId)).ToListAsync(ct);
        return files.Count == 2 ? (files.Single(f => f.Id == sourceId), files.Single(f => f.Id == targetId)) : null;
    }

    private async Task<AlignmentComparison> BuildComparison(MappedDependency item, VideoFile source, VideoFile target, IReadOnlyList<AlignmentAnchor> anchors, CancellationToken ct)
    {
        var sourceSec = Midpoint(item.Dependency.StartSec, item.Dependency.EndSec);
        var targetSec = VideoAlignment.MapRange(sourceSec, sourceSec, anchors)?.StartSec ?? Midpoint(item.Mapped!.StartSec, item.Mapped.EndSec);
        var sourceFrame = TryExtractColorFrame(FilesystemPaths.ToNativePath(source.Path), sourceSec, ct);
        var targetFrame = TryExtractColorFrame(FilesystemPaths.ToNativePath(target.Path), targetSec, ct);
        await Task.WhenAll(sourceFrame, targetFrame);
        return new(item.Dependency.Kind, item.Dependency.Id, item.Dependency.Title, sourceSec, targetSec, await sourceFrame, await targetFrame);
    }

    private async Task<string?> TryExtractColorFrame(string path, double timeSec, CancellationToken ct)
    {
        try { return await extractor.ExtractColorFrameAsync(path, timeSec, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning(ex, "Could not capture an alignment review frame at {TimeSec}", timeSec);
            return null;
        }
    }

    private static double Midpoint(double startSec, double? endSec) => endSec.HasValue ? startSec + (endSec.Value - startSec) / 2 : startSec;
    private static (long Size, DateTime Modified) FileIdentity(string path) { var file = new FileInfo(path); return (file.Length, file.LastWriteTimeUtc); }
    private static bool StillMatches(string path, (long Size, DateTime Modified) before)
    { var file = new FileInfo(path); return file.Exists && file.Length == before.Size && Math.Abs((file.LastWriteTimeUtc - before.Modified).TotalMilliseconds) < 1; }
    private static bool FileAvailable(string path) { try { return System.IO.File.Exists(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; } }
    private static AlignedRange? MapDependency(TimedDependency dependency, IReadOnlyList<AlignmentAnchor> anchors)
        => VideoAlignment.MapRange(dependency.StartSec, dependency.EndSec ?? dependency.StartSec, anchors);
    private sealed record MappedDependency(TimedDependency Dependency, AlignedRange? Mapped);
    private sealed record AlignmentComparison(string Kind, int Id, string? Title, double SourceSec, double TargetSec, string? SourceThumbnail, string? TargetThumbnail);
    private sealed record TimedDependency(string Kind, int Id, string? Title, double StartSec, double? EndSec, int? RelatedEntityId, int? TimelineHostId, int? RelativeClipId);
}

public record AnalyzeVideoAlignment(int SourceFileId, int TargetFileId);
public record ReviewVideoAlignment(int SourceFileId, int TargetFileId, double SourceDuration, double TargetDuration, List<AlignmentAnchor> Anchors);
