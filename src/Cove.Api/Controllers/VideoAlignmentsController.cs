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
    ISegmentSpanCacheInvalidator segmentSpanCacheInvalidator, IEventBus eventBus, VideoGeneratedAssetCoordinator generatedAssetCoordinator,
    VideoTimelineDependencyService timeline, ILogger<VideoAlignmentsController> logger) : ControllerBase
{
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
            var dependenciesWithoutTimeline = await timeline.LoadAsync(videoId, principalAccessor.Current?.Has(Permissions.SegmentsRead) == true, ct);
            if (!await timeline.CanReadAsync(principalAccessor.Current, dependenciesWithoutTimeline, ct)) return Forbid();
            return Ok(new { sourceFileId = (int?)null, targetFileId = initialTarget.Id, sourceDuration = (double?)null, targetDuration = initialTarget.Duration,
                equivalent = false, canAlign = false, dependencyCounts = dependenciesWithoutTimeline.GroupBy(d => d.Kind).ToDictionary(g => g.Key, g => g.Count()), dependencyCount = dependenciesWithoutTimeline.Count });
        }
        var pair = await LoadFiles(videoId, video.PrimaryFileId.Value, request.TargetFileId, ct);
        if (pair is null) return BadRequest("The replacement must be another file attached to this video.");
        var (source, target) = pair.Value;
        var sourceHash = await EnsurePhash(source, ct);
        var targetHash = await EnsurePhash(target, ct);
        var equivalent = VideoFileEquivalence.AreEquivalent(source.Duration, sourceHash, target.Duration, targetHash);
        var dependencies = await timeline.LoadAsync(videoId, principalAccessor.Current?.Has(Permissions.SegmentsRead) == true, ct);
        if (!await timeline.CanReadAsync(principalAccessor.Current, dependencies, ct)) return Forbid();
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
        var dependencies = await timeline.LoadAsync(videoId, principalAccessor.Current?.Has(Permissions.SegmentsRead) == true, ct);
        if (!await timeline.CanReadAsync(principalAccessor.Current, dependencies, ct)) return Forbid();
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
        // Generated covers, sprites and previews are keyed by video id, so they stay valid when the new
        // primary holds the same footage. Deleting them would leave the list and hover previews blank
        // until the next generate run.
        var keepGeneratedAssets = false;
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
                keepGeneratedAssets = false;
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
                var dependencies = await timeline.LoadAsync(videoId, principalAccessor.Current?.Has(Permissions.SegmentsRead) == true, ct);
                affectedTimelineVideoIds = dependencies.Select(item => item.TimelineHostId).Where(id => id.HasValue).Select(id => id!.Value).Append(videoId).ToHashSet();
                var deletes = (request.DeleteDependencies ?? []).Select(d => $"{d.Kind}:{d.Id}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!await timeline.MayChangeAsync(principalAccessor.Current, videoId, dependencies, moves: request.Resolution == "align", deletesAll: request.Resolution == "delete", deletes, ct)) { result = Forbid(); return; }
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

                if (request.Resolution != "align")
                {
                    var source = video.PrimaryFileId.HasValue ? await db.VideoFiles.SingleOrDefaultAsync(f => f.Id == video.PrimaryFileId, ct) : null;
                    var equivalent = await HoldsTheSameContent(source, target, ct);
                    if (!equivalent && request.Resolution != "delete" && dependencies.Count > 0)
                    { result = Conflict("Timed dependencies require alignment or removal before changing the primary file."); return; }
                    keepGeneratedAssets = equivalent;
                }
                await timeline.ApplyAsync(principalAccessor.Current, dependencies, mapped, deletes, ct);
                await timeline.InvalidateDerivedDataAsync(videoId, dependencies, ct);
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
                if (!keepGeneratedAssets)
                {
                    try { await thumbnailService.DeleteVideoGeneratedFilesAsync(affectedVideoId, ct); }
                    catch (Exception ex) { logger.LogWarning(ex, "Could not delete generated assets after changing the primary timeline for video {VideoId}", affectedVideoId); }
                }

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

    /// <summary>
    /// True when both files hold the same footage (same length and appearance), so timed content still
    /// lines up and generated covers, sprites and previews remain correct for the new primary file.
    /// </summary>
    private async Task<bool> HoldsTheSameContent(VideoFile? source, VideoFile target, CancellationToken ct)
    {
        if (source is null) return false;

        var sourceHash = await EnsurePhash(source, ct);
        var targetHash = await EnsurePhash(target, ct);
        return VideoFileEquivalence.AreEquivalent(source.Duration, sourceHash, target.Duration, targetHash);
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
    private static AlignedRange? MapDependency(VideoTimedDependency dependency, IReadOnlyList<AlignmentAnchor> anchors)
        => VideoAlignment.MapRange(dependency.StartSec, dependency.EndSec ?? dependency.StartSec, anchors);
    private sealed record MappedDependency(VideoTimedDependency Dependency, AlignedRange? Mapped);
    private sealed record AlignmentComparison(string Kind, int Id, string? Title, double SourceSec, double TargetSec, string? SourceThumbnail, string? TargetThumbnail);
}

public record AnalyzeVideoAlignment(int SourceFileId, int TargetFileId);
public record ReviewVideoAlignment(int SourceFileId, int TargetFileId, double SourceDuration, double TargetDuration, List<AlignmentAnchor> Anchors);
