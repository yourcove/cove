using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class GenerateJobService(
    IJobService jobService,
    IThumbnailService thumbnailService,
    IVideoAssetGenerator videoAssetGenerator,
    IFingerprintService fingerprintService,
    FileFingerprintWriter fingerprintWriter,
    NonVideoGenerationService nonVideoGenerationService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<GenerateJobService> logger)
{
    private const double SegmentPreviewDefaultDuration = 3.0;
    private const double SegmentPreviewMaxDuration = 5.0;
    private const double SegmentPreviewReuseOverlapRatio = 0.8;

    private readonly record struct SegmentPreviewClip(double StartSec, double EndSec, string Path);

    private sealed record VideoWorkItem(
        Video Video,
        VideoFile File,
        string Path,
        bool HasThumbnail,
        bool HasPreview,
        bool HasSprite,
        bool HasPhash,
        bool HasMd5);

    public string Start(GenerateOptionsDto options)
        => jobService.Enqueue(
            "generate",
            "Generating content",
            (progress, ct) => RunAsync(options, progress, ct));

    internal static bool RequiresPathsForExplicitNonVideoWork(GenerateOptionsDto options)
        => options.VideoIds is { Count: > 0 }
            && options.Paths is not { Count: > 0 }
            && (options.ImagePhashes
                || options.ImageThumbnails
                || options.GalleryThumbnails
                || options.AudioPhashes
                || options.TextPhashes);

    internal static bool ShouldGenerateDefaultVideoThumbnail(bool requested, string? imageBlobId)
        => requested && string.IsNullOrWhiteSpace(imageBlobId);

    internal static async Task ReportGenerateResultAsync(
        IJobUnit unit,
        Task<bool> generation,
        string failureMessage)
    {
        if (!await generation)
            unit.Complete(JobUnitOutcome.Failed, failureMessage);
    }

    internal static VideoFile? SelectVideoFile(Video video, IReadOnlyList<string> filterPaths)
    {
        var files = video.Files.OrderBy(file => file.Id);
        return filterPaths.Count == 0
            ? files.FirstOrDefault()
            : files.FirstOrDefault(file => GeneratePathFilter.Contains(GeneratePathFilter.Resolve(file), filterPaths));
    }

    private async Task RunAsync(GenerateOptionsDto options, IJobProgress progress, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var hasVideoSelection = options.VideoIds is { Count: > 0 };
        var hasPathSelection = options.Paths is { Count: > 0 };
        var allowNonVideoWork = !hasVideoSelection || hasPathSelection;
        var parallelism = config.MaxParallelTasks <= 0
            ? Environment.ProcessorCount
            : Math.Max(1, config.MaxParallelTasks);

        await GenerateVideosAsync(db, options, hasVideoSelection, parallelism, progress, ct);

        if (!allowNonVideoWork)
            return;

        await nonVideoGenerationService.GenerateAsync(db, options, parallelism, progress, ct);
    }

    private async Task GenerateVideosAsync(
        CoveContext db,
        GenerateOptionsDto options,
        bool hasVideoSelection,
        int parallelism,
        IJobProgress progress,
        CancellationToken ct)
    {
        var videoWorkRequested = hasVideoSelection
            || options.Thumbnails
            || options.Previews
            || options.Sprites
            || options.SegmentThumbnails
            || options.SegmentPreviews
            || options.Segments
            || options.Phashes
            || options.Md5;

        if (!videoWorkRequested)
            return;

        var query = db.Videos
            .Include(video => video.Files).ThenInclude(file => file.ParentFolder)
            .Include(video => video.Files).ThenInclude(file => file.Fingerprints)
            .AsSplitQuery();

        var videos = hasVideoSelection
            ? await query.Where(video => options.VideoIds!.Contains(video.Id)).ToListAsync(ct)
            : await query.ToListAsync(ct);

        List<string> filterPaths = hasVideoSelection
            ? []
            : GeneratePathFilter.Normalize(options.Paths);
        var workItems = videos
            .Select(video => CreateVideoWorkItem(video, filterPaths))
            .Where(item => item is not null)
            .Cast<VideoWorkItem>()
            .Where(item => NeedsVideoWork(item, options))
            .ToList();

        var segmentThumbnails = options.SegmentThumbnails || options.SegmentPreviews || options.Segments;
        var segmentPreviews = options.SegmentPreviews || options.Segments;
        var segmentsByVideoId = segmentThumbnails
            ? await LoadSegmentsAsync(db, workItems, ct)
            : [];

        await jobService.RunBatchAsync(
            workItems,
            parallelism,
            (item, unit, token) => GenerateVideoAsync(item, options, segmentThumbnails, segmentPreviews, segmentsByVideoId, unit, token),
            progress,
            unitIdFactory: (item, _) => item.Video.Id.ToString(),
            labelFactory: item => item.Video.Title,
            ct: ct);
    }

    private VideoWorkItem? CreateVideoWorkItem(Video video, IReadOnlyList<string> filterPaths)
    {
        var file = SelectVideoFile(video, filterPaths);
        if (file is null)
            return null;

        return new VideoWorkItem(
            video,
            file,
            GeneratePathFilter.Resolve(file),
            System.IO.File.Exists(thumbnailService.GetThumbnailPathForVideo(video.Id)),
            System.IO.File.Exists(thumbnailService.GetPreviewPath(video.Id)),
            System.IO.File.Exists(thumbnailService.GetSpritePath(video.Id))
                && System.IO.File.Exists(thumbnailService.GetSpriteVttPath(video.Id)),
            video.Files.Any(candidate => candidate.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value))),
            video.Files.Any(candidate => candidate.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value))));
    }

    private static bool NeedsVideoWork(VideoWorkItem item, GenerateOptionsDto options)
    {
        var generatedFileWork =
            (ShouldGenerateDefaultVideoThumbnail(options.Thumbnails, item.Video.ImageBlobId)
                && (options.Overwrite || !item.HasThumbnail))
            || (options.Previews && (options.Overwrite || !item.HasPreview))
            || (options.Sprites && (options.Overwrite || !item.HasSprite))
            || options.SegmentThumbnails
            || options.SegmentPreviews
            || options.Segments;

        return generatedFileWork
            || (options.Phashes && (options.Overwrite || !item.HasPhash))
            || (options.Md5 && (options.Overwrite || !item.HasMd5));
    }

    private static async Task<Dictionary<int, List<(double StartSec, double? EndSec)>>> LoadSegmentsAsync(
        CoveContext db,
        IReadOnlyCollection<VideoWorkItem> workItems,
        CancellationToken ct)
    {
        if (workItems.Count == 0)
            return [];

        var videoIds = workItems.Select(item => item.Video.Id).ToList();
        var segments = await db.Segments
            .AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && videoIds.Contains(segment.HostId))
            .Select(segment => new { segment.HostId, segment.StartSec, segment.EndSec })
            .ToListAsync(ct);

        return segments
            .GroupBy(segment => segment.HostId)
            .ToDictionary(
                group => group.Key,
                group => group
                    .GroupBy(segment => segment.StartSec)
                    .Select(segmentGroup => (
                        StartSec: segmentGroup.Key,
                        EndSec: segmentGroup
                            .Select(segment => segment.EndSec)
                            .Where(endSec => endSec.HasValue)
                            .OrderBy(endSec => endSec)
                            .FirstOrDefault()))
                    .OrderBy(segment => segment.StartSec)
                    .ToList());
    }

    private async Task GenerateVideoAsync(
        VideoWorkItem item,
        GenerateOptionsDto options,
        bool generateSegmentThumbnails,
        bool generateSegmentPreviews,
        IReadOnlyDictionary<int, List<(double StartSec, double? EndSec)>> segmentsByVideoId,
        IJobUnit unit,
        CancellationToken ct)
    {
        try
        {
            if (!System.IO.File.Exists(item.Path))
            {
                unit.Complete(JobUnitOutcome.Skipped, "Source file is unavailable");
                return;
            }

            await GeneratePrimaryVideoAssetsAsync(item, options, unit, ct);

            if (generateSegmentThumbnails && segmentsByVideoId.TryGetValue(item.Video.Id, out var segments))
                await GenerateSegmentAssetsAsync(item, segments, generateSegmentPreviews, options.Overwrite, unit, ct);

            if (options.Phashes && (options.Overwrite || !item.HasPhash))
            {
                var phash = await fingerprintService.ComputeVideoPhashAsync(item.Path, item.File.Duration, ct);
                if (!string.IsNullOrWhiteSpace(phash))
                    await fingerprintWriter.UpsertAsync(item.File.Id, "phash", phash, ct);
                else
                    unit.Complete(JobUnitOutcome.Failed, "Video perceptual hash generation failed");
            }

            if (options.Md5 && (options.Overwrite || !item.HasMd5))
            {
                var md5 = await fingerprintService.ComputeMd5Async(item.Path, ct);
                if (!string.IsNullOrWhiteSpace(md5))
                    await fingerprintWriter.UpsertAsync(item.File.Id, "md5", md5, ct);
                else
                    unit.Complete(JobUnitOutcome.Failed, "Video MD5 generation failed");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Skipped video {VideoId} during generate after an error", item.Video.Id);
            unit.Complete(JobUnitOutcome.Failed, ex.Message);
        }
    }

    private async Task GeneratePrimaryVideoAssetsAsync(
        VideoWorkItem item,
        GenerateOptionsDto options,
        IJobUnit unit,
        CancellationToken ct)
    {
        if (ShouldGenerateDefaultVideoThumbnail(options.Thumbnails, item.Video.ImageBlobId))
        {
            if (options.Overwrite || !item.HasThumbnail)
                await ReportGenerateResultAsync(
                    unit,
                    videoAssetGenerator.GenerateThumbnailFromFileAsync(item.Video.Id, item.File.Id, null, ct),
                    "Thumbnail generation failed");
        }

        if (options.Previews && (options.Overwrite || !item.HasPreview))
        {
            await ReportGenerateResultAsync(
                unit,
                videoAssetGenerator.GeneratePreviewFromFileAsync(
                    item.Video.Id,
                    item.File.Id,
                    options.Overwrite,
                    ct),
                "Preview generation failed");
        }

        if (options.Sprites && (options.Overwrite || !item.HasSprite))
        {
            await ReportGenerateResultAsync(
                unit,
                videoAssetGenerator.GenerateSpriteFromFileAsync(
                    item.Video.Id,
                    item.File.Id,
                    options.Overwrite,
                    ct),
                "Sprite generation failed");
        }
    }

    private async Task GenerateSegmentAssetsAsync(
        VideoWorkItem item,
        IEnumerable<(double StartSec, double? EndSec)> segments,
        bool generatePreviews,
        bool overwrite,
        IJobUnit unit,
        CancellationToken ct)
    {
        var generatedClips = new List<SegmentPreviewClip>();
        foreach (var segment in segments)
        {
            var screenshotSecond = Math.Max(0, segment.StartSec);
            if (item.File.Duration > 0)
                screenshotSecond = Math.Min(screenshotSecond, Math.Max(0, item.File.Duration - 0.1));

            var thumbnailPath = thumbnailService.GetTimestampedThumbnailPath(item.Video.Id, screenshotSecond);
            var previewPath = generatePreviews
                ? thumbnailService.GetSegmentAnimatedPreviewPath(item.Video.Id, screenshotSecond)
                : null;

            if (overwrite || !System.IO.File.Exists(thumbnailPath))
                await ReportGenerateResultAsync(
                    unit,
                    videoAssetGenerator.GenerateThumbnailFromFileAsync(
                        item.Video.Id,
                        item.File.Id,
                        screenshotSecond,
                        ct),
                    "Segment thumbnail generation failed");

            if (previewPath is not null)
                await GenerateSegmentPreviewAsync(
                    item,
                    segment.EndSec,
                    screenshotSecond,
                    previewPath,
                    generatedClips,
                    overwrite,
                    unit,
                    ct);
        }
    }

    private async Task GenerateSegmentPreviewAsync(
        VideoWorkItem item,
        double? segmentEnd,
        double screenshotSecond,
        string previewPath,
        List<SegmentPreviewClip> generatedClips,
        bool overwrite,
        IJobUnit unit,
        CancellationToken ct)
    {
        var (clipStart, clipEnd) = ResolveSegmentPreviewClip(screenshotSecond, segmentEnd, item.File.Duration);
        if (!overwrite && System.IO.File.Exists(previewPath))
        {
            AddSegmentPreviewClip(generatedClips, new SegmentPreviewClip(clipStart, clipEnd, previewPath));
            return;
        }

        var reusableClip = FindReusableSegmentPreviewClip(generatedClips, clipStart, clipEnd);
        if (reusableClip.HasValue)
        {
            CopySegmentPreviewAlias(reusableClip.Value.Path, previewPath);
            if (System.IO.File.Exists(previewPath))
            {
                AddSegmentPreviewClip(generatedClips, new SegmentPreviewClip(clipStart, clipEnd, previewPath));
                return;
            }
        }

        await ReportGenerateResultAsync(
            unit,
            videoAssetGenerator.GenerateSegmentPreviewFromFileAsync(
                item.Video.Id,
                item.File.Id,
                screenshotSecond,
                segmentEnd,
                overwrite,
                ct),
            "Segment preview generation failed");
        if (System.IO.File.Exists(previewPath))
            AddSegmentPreviewClip(generatedClips, new SegmentPreviewClip(clipStart, clipEnd, previewPath));
    }


    private static (double StartSec, double EndSec) ResolveSegmentPreviewClip(double startSec, double? endSec, double videoDuration)
    {
        if (videoDuration <= 0)
            return (Math.Max(0, startSec), Math.Max(0, startSec) + SegmentPreviewDefaultDuration);

        var clampedStart = Math.Max(0, Math.Min(startSec, Math.Max(0, videoDuration - 0.1)));
        var requestedDuration = endSec.HasValue && endSec.Value > clampedStart
            ? endSec.Value - clampedStart
            : SegmentPreviewDefaultDuration;
        var previewDuration = Math.Min(SegmentPreviewMaxDuration, Math.Max(0.5, requestedDuration));
        previewDuration = Math.Min(previewDuration, Math.Max(0.5, videoDuration - clampedStart));
        return (clampedStart, clampedStart + previewDuration);
    }

    private static SegmentPreviewClip? FindReusableSegmentPreviewClip(
        IEnumerable<SegmentPreviewClip> clips,
        double startSec,
        double endSec)
    {
        foreach (var clip in clips)
        {
            if (IsReusableSegmentPreviewClip(clip, startSec, endSec))
                return clip;
        }

        return null;
    }

    private static bool IsReusableSegmentPreviewClip(SegmentPreviewClip clip, double startSec, double endSec)
    {
        var overlap = Math.Min(clip.EndSec, endSec) - Math.Max(clip.StartSec, startSec);
        if (overlap <= 0)
            return false;

        var clipDuration = Math.Max(0.001, clip.EndSec - clip.StartSec);
        var requestedDuration = Math.Max(0.001, endSec - startSec);
        return overlap / Math.Min(clipDuration, requestedDuration) >= SegmentPreviewReuseOverlapRatio;
    }

    private static void AddSegmentPreviewClip(List<SegmentPreviewClip> clips, SegmentPreviewClip clip)
    {
        if (!clips.Any(existing => string.Equals(existing.Path, clip.Path, StringComparison.OrdinalIgnoreCase)))
            clips.Add(clip);
    }

    private static void CopySegmentPreviewAlias(string sourcePath, string targetPath)
    {
        if (string.Equals(sourcePath, targetPath, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(sourcePath))
            return;

        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
            Directory.CreateDirectory(targetDirectory);

        var tempPath = targetPath + ".tmp";
        try
        {
            System.IO.File.Copy(sourcePath, tempPath, overwrite: true);
            System.IO.File.Move(tempPath, targetPath, overwrite: true);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                try { System.IO.File.Delete(tempPath); } catch { }
            }
        }
    }
}
