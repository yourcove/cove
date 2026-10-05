using System.Runtime.CompilerServices;
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
    IVideoSourceHealthProbe sourceHealthProbe,
    FileFingerprintWriter fingerprintWriter,
    NonVideoGenerationService nonVideoGenerationService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<GenerateJobService> logger)
{
    private sealed record VideoWorkItem(
        Video Video,
        VideoFile File,
        string Path,
        bool HasThumbnail,
        bool HasVrCard,
        bool HasPreview,
        bool HasVrPreview,
        bool HasSprite,
        bool HasPhash,
        bool HasMd5);

    /// <summary>Work that needs frames decoded out of the source file. MD5 does not.</summary>
    private static bool NeedsDecodableSource(GenerateOptionsDto options)
        => options.Thumbnails || options.Previews || options.Sprites || options.VrStereo
            || options.Phashes;

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
        var primary = video.Files.SingleOrDefault(file => file.Id == video.PrimaryFileId);
        return primary is not null && (filterPaths.Count == 0 || GeneratePathFilter.Contains(GeneratePathFilter.Resolve(primary), filterPaths))
            ? primary
            : null;
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
            || options.VrStereo
            || options.Phashes
            || options.Md5;

        if (!videoWorkRequested)
            return;

        // Freeze eligible IDs on disk so concurrent metadata/assets changes cannot
        // change aggregate job totals between selection and processing.
        await using var workSet = await GenerationWorkSet.CreateAsync(ct);
        await foreach (var batch in ReadVideoWorkAsync(db, options, hasVideoSelection, ct))
            await workSet.AddAsync(batch.Select(item => item.Video.Id), ct);
        progress.DeclareUnitCount(workSet.Count);

        var reporter = new GenerateProgressReporter(progress, logger, workSet.Count);
        logger.LogInformation(
            "Generate starting: {Count} videos need work ({Assets}), {Parallelism} in parallel, overwrite={Overwrite}",
            workSet.Count, DescribeSelectedAssets(options), parallelism, options.Overwrite);

        if (workSet.Count == 0)
        {
            logger.LogInformation("Generate finished: nothing to do — every selected video already has the requested assets.");
            progress.Report(1d, "Nothing to generate");
            return;
        }

        var filterPaths = hasVideoSelection ? [] : GeneratePathFilter.Normalize(options.Paths);
        await foreach (var ids in workSet.ReadAsync(ct))
        {
            var workItems = await LoadVideoWorkAsync(db, ids, filterPaths, ct);
            var availableIds = workItems.Select(item => item.Video.Id).ToHashSet();
            foreach (var missingId in ids.Where(id => !availableIds.Contains(id)))
            {
                using var unit = progress.StartUnit(missingId.ToString());
                unit.Complete(JobUnitOutcome.Skipped, "Selected video or source file is no longer available");
                reporter.RecordSkipped();
            }
            await jobService.RunBatchAsync(
                workItems,
                parallelism,
                (item, unit, token) => GenerateVideoAsync(item, options, new RecordingJobUnit(unit), reporter, token),
                progress,
                unitIdFactory: (item, _) => item.Video.Id.ToString(),
                labelFactory: item => item.Video.Title,
                ct: ct);
        }

        reporter.ReportCompleted();
    }

    /// <summary>Names the asset kinds this run was asked for, so the log says what it is doing.</summary>
    internal static string DescribeSelectedAssets(GenerateOptionsDto options)
    {
        var selected = new List<string>(6);
        if (options.Thumbnails) selected.Add("covers");
        if (options.Previews) selected.Add("previews");
        if (options.Sprites) selected.Add("sprites");
        if (options.Phashes) selected.Add("phashes");
        if (options.Md5) selected.Add("md5");
        return selected.Count > 0 ? string.Join(", ", selected) : "nothing";
    }

    private async IAsyncEnumerable<List<VideoWorkItem>> ReadVideoWorkAsync(
        CoveContext db, GenerateOptionsDto options, bool hasVideoSelection,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var filterPaths = hasVideoSelection ? [] : GeneratePathFilter.Normalize(options.Paths);
        await foreach (var ids in GenerationSelection.VideosAsync(db, options, ct))
        {
            var work = (await LoadVideoWorkAsync(db, ids, filterPaths, ct))
                .Where(item => NeedsVideoWork(item, options)).ToList();
            if (work.Count > 0)
                yield return work;
        }
    }

    private async Task<List<VideoWorkItem>> LoadVideoWorkAsync(
        CoveContext db, int[] ids, IReadOnlyList<string> filterPaths, CancellationToken ct)
    {
        var videos = await db.Videos.AsNoTracking().Where(video => ids.Contains(video.Id))
            .Include(video => video.Files).ThenInclude(file => file.ParentFolder)
            .Include(video => video.Files).ThenInclude(file => file.Fingerprints)
            .AsSplitQuery().OrderBy(video => video.Id).ToListAsync(ct);
        return videos.Select(video => CreateVideoWorkItem(video, filterPaths)).OfType<VideoWorkItem>().ToList();
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
            !video.IsVr || videoAssetGenerator.HasVrCard(video.Id),
            System.IO.File.Exists(thumbnailService.GetPreviewPath(video.Id)),
            !video.IsVr || videoAssetGenerator.HasVrPreview(video.Id),
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
            || (options.VrStereo && item.Video.IsVr && (options.Overwrite || !item.HasVrCard))
            || (options.Previews && (options.Overwrite || !item.HasPreview))
            || (options.VrStereo && item.Video.IsVr && (options.Overwrite || !item.HasVrPreview))
            || (options.Sprites && (options.Overwrite || !item.HasSprite));

        return generatedFileWork
            || (options.Phashes && (options.Overwrite || !item.HasPhash))
            || (options.Md5 && (options.Overwrite || !item.HasMd5));
    }

    private async Task GenerateVideoAsync(
        VideoWorkItem item,
        GenerateOptionsDto options,
        RecordingJobUnit unit,
        GenerateProgressReporter reporter,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        try
        {
            if (!System.IO.File.Exists(item.Path))
            {
                unit.Complete(JobUnitOutcome.Skipped, "Source file is unavailable");
                return;
            }

            // Everything below decodes frames from the source. A truncated download cannot produce
            // any of them, and rediscovering that costs tens of seconds per asset per run, so the
            // source is judged once per video - not once per selected asset - and the verdict is
            // persisted so later runs skip straight past it.
            if (NeedsDecodableSource(options))
            {
                var unreadable = await ResolveUnreadableReasonAsync(item, ct);
                if (unreadable != null)
                {
                    unit.Complete(JobUnitOutcome.Failed, unreadable);
                    return;
                }
            }

            await GeneratePrimaryVideoAssetsAsync(item, options, unit, ct);

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
        finally
        {
            RecordVideoOutcome(item, unit, reporter, DateTime.UtcNow - startedAt);
        }
    }

    /// <summary>
    /// Feeds one video's result into the running tally and leaves a trace of it.
    ///
    /// A failure is logged at Warning with the reason the unit recorded, because that is the thing
    /// worth noticing while a long run is in flight; successes are Debug, since a full-library run
    /// would otherwise write a line per video across the whole library.
    /// </summary>
    private void RecordVideoOutcome(VideoWorkItem item, RecordingJobUnit unit, GenerateProgressReporter reporter, TimeSpan elapsed)
    {
        // A unit the work left untouched succeeded: RunBatchAsync marks it after this returns.
        switch (unit.Outcome)
        {
            case JobUnitOutcome.Failed:
                reporter.RecordFailed();
                logger.LogWarning(
                    "Generate failed for video {VideoId} ({Title}) after {Elapsed:F1}s: {Reason}",
                    item.Video.Id, item.Video.Title, elapsed.TotalSeconds, unit.Message ?? "no reason recorded");
                break;
            case JobUnitOutcome.Skipped:
                reporter.RecordSkipped();
                logger.LogDebug(
                    "Generate skipped video {VideoId} ({Title}): {Reason}",
                    item.Video.Id, item.Video.Title, unit.Message ?? "no reason recorded");
                break;
            default:
                reporter.RecordSucceeded();
                logger.LogDebug(
                    "Generated video {VideoId} ({Title}) in {Elapsed:F1}s",
                    item.Video.Id, item.Video.Title, elapsed.TotalSeconds);
                break;
        }
    }

    /// <summary>
    /// Forwards to the real job unit while remembering the last reason passed to Complete.
    /// IJobUnit deliberately exposes only the outcome, but the reason is the useful half when a
    /// video fails, so generation keeps its own copy to put in the log.
    /// </summary>
    private sealed class RecordingJobUnit(IJobUnit inner) : IJobUnit
    {
        public string? Message { get; private set; }

        public JobUnitOutcome? Outcome => inner.Outcome;

        public void Report(double progress, string? message = null) => inner.Report(progress, message);

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            Message = message ?? Message;
            inner.Complete(outcome, message);
        }

        // The batch runner owns the real unit's lifetime; disposing here would end it early.
        public void Dispose() { }
    }

    /// <summary>
    /// Returns why this source cannot be generated from, or null to proceed. Uses the stored verdict
    /// when it still applies to the file currently on disk; otherwise probes and stores the result.
    /// A file that changes size (an interrupted download that later completed) is re-evaluated.
    /// </summary>
    private async Task<string?> ResolveUnreadableReasonAsync(VideoWorkItem item, CancellationToken ct)
    {
        if (item.File.IsSourceKnownUnreadable)
            return item.File.SourceUnreadableReason ?? "Source file is unreadable";

        var reason = await sourceHealthProbe.GetUnreadableReasonAsync(item.Path, ct);

        // Only touch the database when the stored verdict actually changes. The overwhelmingly
        // common case is a healthy file with nothing recorded, and opening a scope per video just
        // to confirm that would add a scope and a query to every video in the library.
        var hasStoredVerdict = item.File.SourceUnreadableAt.HasValue;
        if (reason != null || hasStoredVerdict)
            await RecordSourceHealthAsync(item.File.Id, reason, ct);

        return reason;
    }

    /// <summary>
    /// Persists (or clears) the unreadable verdict for a file. Clearing matters as much as setting:
    /// a file that was truncated and has since been re-downloaded must become eligible again.
    /// </summary>
    private async Task RecordSourceHealthAsync(int fileId, string? reason, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var file = await db.VideoFiles.SingleOrDefaultAsync(candidate => candidate.Id == fileId, ct);
            if (file == null)
                return;

            if (reason == null)
            {
                if (file.SourceUnreadableAt == null)
                    return;
                file.SourceUnreadableAt = null;
                file.SourceUnreadableReason = null;
                file.SourceUnreadableSize = null;
            }
            else
            {
                file.SourceUnreadableAt = DateTime.UtcNow;
                file.SourceUnreadableReason = reason;
                file.SourceUnreadableSize = file.Size;
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Recording the verdict is an optimization; failing to store it only costs time later.
            logger.LogDebug(ex, "Could not record source health for file {FileId}", fileId);
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

        // The stereoscopic card a headset shows, beside the one-eye cover flat screens show.
        if (options.VrStereo && item.Video.IsVr && (options.Overwrite || !item.HasVrCard))
        {
            await ReportGenerateResultAsync(
                unit,
                videoAssetGenerator.GenerateVrCardFromFileAsync(
                    item.Video.Id,
                    item.File.Id,
                    options.Overwrite,
                    ct),
                "Stereoscopic card generation failed");
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

        // The stereoscopic clip a headset shows, beside the one-eye preview flat screens show.
        if (options.VrStereo && item.Video.IsVr && (options.Overwrite || !item.HasVrPreview))
        {
            await ReportGenerateResultAsync(
                unit,
                videoAssetGenerator.GenerateVrPreviewFromFileAsync(
                    item.Video.Id,
                    item.File.Id,
                    options.Overwrite,
                    ct),
                "Stereoscopic preview generation failed");
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
}
