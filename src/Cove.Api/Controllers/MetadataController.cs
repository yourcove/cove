using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/metadata")]
public class MetadataController(
    IScanService scanService,
    IJobService jobService,
    IThumbnailService thumbnailService,
    IFingerprintService fingerprintService,
    IServiceScopeFactory scopeFactory,
    IHttpClientFactory httpClientFactory,
    CoveConfiguration config,
    ILogger<MetadataController> logger) : ControllerBase
{
    private static readonly JsonSerializerOptions MetadataExportJsonOptions = new(CoveJson.Default)
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
    };

    private const double SegmentPreviewDefaultDuration = 3.0;
    private const double SegmentPreviewMaxDuration = 5.0;
    private const double SegmentPreviewReuseOverlapRatio = 0.8;

    private static List<string> NormalizeFilterPaths(IEnumerable<string> paths) => paths
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(NormalizePathForComparison)
        .Where(path => path.Length > 0)
        .ToList();

    private static string NormalizePathForComparison(string path) => path
        .Trim()
        .Replace('\\', '/')
        .TrimEnd('/');

    private static bool IsUnderAnyPath(string candidatePath, IReadOnlyList<string> filterPaths)
    {
        if (filterPaths.Count == 0)
            return true;

        var normalizedCandidate = NormalizePathForComparison(candidatePath);
        return filterPaths.Any(path => normalizedCandidate.StartsWith(path, StringComparison.OrdinalIgnoreCase));
    }

    private static string ResolveFilePath(BaseFileEntity file) => file.ParentFolder != null
        ? Path.Combine(file.ParentFolder.Path, file.Basename)
        : file.Basename;

    private readonly record struct SegmentPreviewClip(double StartSec, double EndSec, string Path);

    [HttpPost("scan")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> StartScan([FromBody] ScanOptionsDto? opts)
    {
        var enableAllGenerators = opts?.ScanGenerators == true;
        var jobId = scanService.StartScan(new ScanOperationOptions
        {
            Paths = opts?.Paths,
            GenerateCovers = enableAllGenerators || opts?.ScanGenerateCovers == true,
            GeneratePreviews = enableAllGenerators || opts?.ScanGeneratePreviews == true,
            GenerateSprites = enableAllGenerators || opts?.ScanGenerateSprites == true,
            GeneratePhashes = enableAllGenerators || opts?.ScanGeneratePhashes == true,
            GenerateMd5 = enableAllGenerators || opts?.ScanGenerateMd5 == true,
            GenerateImageThumbnails = enableAllGenerators || opts?.ScanGenerateThumbnails == true,
            GenerateImagePhashes = enableAllGenerators || opts?.ScanGenerateImagePhashes == true,
            GenerateAudioPhashes = enableAllGenerators || opts?.ScanGenerateAudioPhashes == true,
            GenerateTextPhashes = enableAllGenerators || opts?.ScanGenerateTextPhashes == true,
            Rescan = opts?.Rescan == true,
        });
        return Ok(new { jobId });
    }

    /// <summary>
    /// Lists folders the user may target for a selective scan/generate. With no <paramref name="path"/>
    /// it returns the configured library roots; otherwise it returns the immediate subfolders of the
    /// given path. The path MUST be at or below a configured library root — anything else is rejected,
    /// so the folder picker can never drill outside the library.
    /// </summary>
    [HttpGet("library-folders")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<List<LibraryFolderDto>> GetLibraryFolders([FromQuery] string? path)
    {
        var roots = config.CovePaths
            .Select(covePath => covePath.Path)
            .Where(rootPath => !string.IsNullOrWhiteSpace(rootPath))
            .Select(rootPath => CanonicalizePath(rootPath!))
            .Where(rootPath => rootPath.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(path))
        {
            return Ok(roots
                .OrderBy(root => root, StringComparer.OrdinalIgnoreCase)
                .Select(root => new LibraryFolderDto(root, root, SafeHasSubdirectories(root)))
                .ToList());
        }

        var requested = CanonicalizePath(path);
        if (requested.Length == 0 || !roots.Any(root => IsAtOrUnderPath(requested, root)))
            return StatusCode(StatusCodes.Status403Forbidden, new { code = "OUTSIDE_LIBRARY", message = "Path is not within a configured library folder." });

        if (!Directory.Exists(requested))
            return Ok(new List<LibraryFolderDto>());

        try
        {
            return Ok(Directory.GetDirectories(requested)
                .Select(CanonicalizePath)
                .Where(dir => dir.Length > 0)
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
                .Select(dir => new LibraryFolderDto(dir[(dir.LastIndexOf('/') + 1)..], dir, SafeHasSubdirectories(dir)))
                .ToList());
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            logger.LogWarning(ex, "Failed to list subfolders of {Path}", requested);
            return Ok(new List<LibraryFolderDto>());
        }
    }

    private static string CanonicalizePath(string path)
    {
        try { return Path.GetFullPath(path.Trim()).Replace('\\', '/').TrimEnd('/'); }
        catch { return string.Empty; }
    }

    // Segment-aware containment check so "/library" does not match "/library-other".
    private static bool IsAtOrUnderPath(string candidate, string root)
        => candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);

    private static bool SafeHasSubdirectories(string dir)
    {
        try { return Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Any(); }
        catch { return false; }
    }

    [HttpPost("generate")]
    [RequiresPermission(Permissions.JobsRun)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "opts", PropertyName = "VideoIds")]
    public ActionResult<object> StartGenerate([FromBody] GenerateOptionsDto? opts)
    {
        var selectedVideoIds = opts?.VideoIds;
        var hasVideoSelection = selectedVideoIds is { Count: > 0 };
        var hasPathSelection = opts?.Paths is { Count: > 0 };
        var requestsExplicitNonVideoWork = opts?.ImagePhashes == true
            || opts?.ImageThumbnails == true
            || opts?.GalleryThumbnails == true
            || opts?.AudioPhashes == true
            || opts?.TextPhashes == true;

        if (hasVideoSelection && !hasPathSelection && requestsExplicitNonVideoWork)
        {
            return BadRequest(new
            {
                error = "Non-video generate options require paths when videoIds are supplied. Provide paths for image, gallery, audio, or text work, or run those options without videoIds."
            });
        }

        var jobId = jobService.Enqueue("generate", "Generating content", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            async Task UpsertFingerprintAsync(int fileId, string type, string value, CancellationToken token)
            {
                using var innerScope = scopeFactory.CreateScope();
                var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                var existing = await innerDb.FileFingerprints
                    .FirstOrDefaultAsync(fp => fp.FileId == fileId && fp.Type == type, token);
                if (existing != null)
                    existing.Value = value;
                else
                    innerDb.FileFingerprints.Add(new FileFingerprint { FileId = fileId, Type = type, Value = value });
                await innerDb.SaveChangesAsync(token);
            }

            var allowNonVideoWork = !hasVideoSelection || hasPathSelection;
            var generateNonVideoMd5 = opts?.Md5 == true && allowNonVideoWork;

            var videoWorkRequested = hasVideoSelection
                || opts?.Thumbnails == true
                || opts?.Previews == true
                || opts?.Sprites == true
                || opts?.SegmentThumbnails == true
                || opts?.SegmentPreviews == true
                || opts?.Markers == true
                || opts?.Phashes == true
                || opts?.Md5 == true;

            var videos = hasVideoSelection
                ? await dbCtx.Videos.Include(s => s.Files).ThenInclude(f => f.ParentFolder).Include(s => s.Files).ThenInclude(f => f.Fingerprints).Where(s => selectedVideoIds!.Contains(s.Id)).AsSplitQuery().ToListAsync(ct)
                : videoWorkRequested
                    ? await dbCtx.Videos.Include(s => s.Files).ThenInclude(f => f.ParentFolder).Include(s => s.Files).ThenInclude(f => f.Fingerprints).AsSplitQuery().ToListAsync(ct)
                    : new List<Video>();

            if (!hasVideoSelection && opts?.Paths is { Count: > 0 } paths)
            {
                var filterPaths = NormalizeFilterPaths(paths);
                videos = videos.Where(s =>
                {
                    var file = s.Files.OrderBy(f => f.Id).FirstOrDefault();
                    if (file == null) return false;
                    var filePath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);
                    return IsUnderAnyPath(filePath, filterPaths);
                }).ToList();
            }

            // Build work items (read-only snapshot) so we don't touch DbContext from parallel threads
            var workItems = videos.Select(s =>
            {
                var file = s.Files.OrderBy(f => f.Id).FirstOrDefault();
                return new
                {
                    Video = s,
                    File = file,
                    Path = file != null ? Path.Combine(file.ParentFolder?.Path ?? "", file.Basename) : "",
                    HasThumbnail = System.IO.File.Exists(thumbnailService.GetThumbnailPathForVideo(s.Id)),
                    HasPreview = System.IO.File.Exists(thumbnailService.GetPreviewPath(s.Id)),
                    HasSprite = System.IO.File.Exists(thumbnailService.GetSpritePath(s.Id))
                        && System.IO.File.Exists(thumbnailService.GetSpriteVttPath(s.Id)),
                    HasPhash = s.Files.Any(f => f.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value))),
                    HasMd5 = s.Files.Any(f => f.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value))),
                };
            }).Where(w => w.File != null).ToList();

            var overwrite = opts?.Overwrite == true;
            var generateVideoFiles = opts?.Thumbnails == true
                || opts?.Previews == true
                || opts?.Sprites == true
                || opts?.SegmentThumbnails == true
                || opts?.SegmentPreviews == true
                || opts?.Markers == true;
            var generateVideoPhashes = opts?.Phashes == true;
            var generateVideoMd5 = opts?.Md5 == true;

            workItems = workItems
                .Where(item => (generateVideoFiles && (
                        overwrite
                        || (opts?.Thumbnails == true && !item.HasThumbnail)
                        || (opts?.Previews == true && !item.HasPreview)
                        || (opts?.Sprites == true && !item.HasSprite)
                        || opts?.SegmentThumbnails == true
                        || opts?.SegmentPreviews == true
                        || opts?.Markers == true))
                    || (generateVideoPhashes && (overwrite || !item.HasPhash))
                    || (generateVideoMd5 && (overwrite || !item.HasMd5)))
                .ToList();

            var total = workItems.Count;
            var processed = 0;
            var maxParallel = config.MaxParallelTasks;
            var parallelism = maxParallel <= 0 ? Environment.ProcessorCount : Math.Max(1, maxParallel);
            var segmentPreviewsByVideoId = new Dictionary<int, List<(double StartSec, double? EndSec)>>();
            var generateSegmentThumbnails = opts?.SegmentThumbnails == true || opts?.SegmentPreviews == true || opts?.Markers == true;
            var generateSegmentPreviews = opts?.SegmentPreviews == true || opts?.Markers == true;

            if (generateSegmentThumbnails && workItems.Count > 0)
            {
                var workItemVideoIds = workItems.Select(item => item.Video.Id).ToList();
                segmentPreviewsByVideoId = (await dbCtx.Segments
                    .AsNoTracking()
                    .Where(segment => segment.HostType == SegmentHostType.Video && workItemVideoIds.Contains(segment.HostId))
                    .Select(segment => new { segment.HostId, segment.StartSec, segment.EndSec })
                    .ToListAsync(ct))
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

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
            {
                try
                {
                if (!System.IO.File.Exists(item.Path))
                    return;

                if (opts?.Thumbnails == true)
                {
                    var thumbPath = thumbnailService.GetThumbnailPathForVideo(item.Video.Id);
                    var thumbExists = System.IO.File.Exists(thumbPath);
                    if (opts?.Overwrite == true && thumbExists) System.IO.File.Delete(thumbPath);
                    if (opts?.Overwrite == true || !thumbExists)
                        await thumbnailService.GenerateVideoThumbnailAsync(item.Video.Id, null, token);
                }

                if (opts?.Previews == true)
                {
                    var previewPath = thumbnailService.GetPreviewPath(item.Video.Id);
                    if (opts?.Overwrite == true)
                    {
                        if (System.IO.File.Exists(previewPath)) System.IO.File.Delete(previewPath);
                    }
                    if (opts?.Overwrite == true || !System.IO.File.Exists(previewPath))
                        await thumbnailService.GenerateVideoPreviewAsync(item.Video.Id, token);
                }

                if (opts?.Sprites == true)
                {
                    var spritePath = thumbnailService.GetSpritePath(item.Video.Id);
                    var vttPath = thumbnailService.GetSpriteVttPath(item.Video.Id);
                    if (opts?.Overwrite == true)
                    {
                        if (System.IO.File.Exists(spritePath)) System.IO.File.Delete(spritePath);
                        if (System.IO.File.Exists(vttPath)) System.IO.File.Delete(vttPath);
                    }
                    if (opts?.Overwrite == true || !System.IO.File.Exists(spritePath) || !System.IO.File.Exists(vttPath))
                        await thumbnailService.GenerateVideoSpriteAsync(item.Video.Id, token);
                }

                if (generateSegmentThumbnails && segmentPreviewsByVideoId.TryGetValue(item.Video.Id, out var segmentPreviews))
                {
                    var duration = item.File!.Duration;
                    var generatedSegmentPreviewClips = new List<SegmentPreviewClip>();
                    foreach (var segmentPreview in segmentPreviews)
                    {
                        var screenshotSecond = Math.Max(0, segmentPreview.StartSec);
                        if (duration > 0)
                            screenshotSecond = Math.Min(screenshotSecond, Math.Max(0, duration - 0.1));

                        var segmentThumbnailPath = thumbnailService.GetTimestampedThumbnailPath(item.Video.Id, screenshotSecond);
                            if (overwrite && System.IO.File.Exists(segmentThumbnailPath))
                            System.IO.File.Delete(segmentThumbnailPath);

                        var segmentPreviewPath = generateSegmentPreviews
                            ? thumbnailService.GetSegmentAnimatedPreviewPath(item.Video.Id, screenshotSecond)
                            : null;

                        if (segmentPreviewPath != null && overwrite && System.IO.File.Exists(segmentPreviewPath))
                        {
                            System.IO.File.Delete(segmentPreviewPath);
                        }

                        if (!System.IO.File.Exists(segmentThumbnailPath))
                            await thumbnailService.GenerateVideoThumbnailAsync(item.Video.Id, screenshotSecond, token);

                        if (generateSegmentPreviews && segmentPreviewPath != null)
                        {
                            var (clipStart, clipEnd) = ResolveSegmentPreviewClip(screenshotSecond, segmentPreview.EndSec, duration);
                            if (System.IO.File.Exists(segmentPreviewPath))
                            {
                                AddSegmentPreviewClip(generatedSegmentPreviewClips, new SegmentPreviewClip(clipStart, clipEnd, segmentPreviewPath));
                                continue;
                            }

                            var reusableClip = FindReusableSegmentPreviewClip(generatedSegmentPreviewClips, clipStart, clipEnd);
                            if (reusableClip.HasValue)
                            {
                                CopySegmentPreviewAlias(reusableClip.Value.Path, segmentPreviewPath);
                                if (System.IO.File.Exists(segmentPreviewPath))
                                {
                                    AddSegmentPreviewClip(generatedSegmentPreviewClips, new SegmentPreviewClip(clipStart, clipEnd, segmentPreviewPath));
                                    continue;
                                }
                            }

                            await thumbnailService.GenerateSegmentAnimatedPreviewAsync(item.Video.Id, screenshotSecond, segmentPreview.EndSec, token);
                            if (System.IO.File.Exists(segmentPreviewPath))
                                AddSegmentPreviewClip(generatedSegmentPreviewClips, new SegmentPreviewClip(clipStart, clipEnd, segmentPreviewPath));
                        }
                    }
                }

                if (generateVideoPhashes && (overwrite || !item.HasPhash))
                {
                    var phash = await fingerprintService.ComputeVideoPhashAsync(item.Path, item.File!.Duration, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                        await UpsertFingerprintAsync(item.File!.Id, "phash", phash, token);
                }

                if (generateVideoMd5 && (overwrite || !item.HasMd5))
                {
                    var md5 = await fingerprintService.ComputeMd5Async(item.Path, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                        await UpsertFingerprintAsync(item.File!.Id, "md5", md5, token);
                }

                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Skipped video {VideoId} during generate after an error", item.Video.Id);
                }
                finally
                {
                    var current = Interlocked.Increment(ref processed);
                    progress.Report((double)current / total, $"Generating ({current}/{total}) {item.Video.Title ?? "Untitled"}");
                }
            });

            if (allowNonVideoWork && (opts?.ImagePhashes == true || opts?.ImageThumbnails == true || generateNonVideoMd5))
            {
                var imageFiles = await dbCtx.ImageFiles
                    .Include(f => f.ParentFolder)
                    .Include(f => f.Fingerprints)
                    .ToListAsync(ct);

                if (opts?.Paths is { Count: > 0 } imagePaths)
                {
                    var filterPaths = NormalizeFilterPaths(imagePaths);
                    imageFiles = imageFiles.Where(imageFile =>
                    {
                        var imagePath = imageFile.ParentFolder != null
                            ? Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename)
                            : imageFile.Basename;
                        return IsUnderAnyPath(imagePath, filterPaths);
                    }).ToList();
                }

                if (opts?.ImageIds is { Count: > 0 } imageIdFilter)
                {
                    var imageIdSet = imageIdFilter.ToHashSet();
                    imageFiles = imageFiles.Where(imageFile => imageFile.ImageId.HasValue && imageIdSet.Contains(imageFile.ImageId.Value)).ToList();
                }

                var imageTotal = imageFiles.Count;
                var imageProcessed = 0;

                await Parallel.ForEachAsync(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (imageFile, token) =>
                {
                    try
                    {
                    var imagePath = imageFile.ParentFolder != null
                        ? Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename)
                        : imageFile.Basename;

                    if (opts?.ImageThumbnails == true && imageFile.ImageId.HasValue)
                        await thumbnailService.GenerateImageThumbnailAsync(imageFile.ImageId.Value, overwrite: opts?.Overwrite == true, ct: token);

                    if (System.IO.File.Exists(imagePath))
                    {
                        var hasPhash = imageFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (opts?.ImagePhashes == true && (opts?.Overwrite == true || !hasPhash))
                        {
                            var phash = await fingerprintService.ComputeImagePhashAsync(imagePath, token);
                            if (!string.IsNullOrWhiteSpace(phash))
                                await UpsertFingerprintAsync(imageFile.Id, "phash", phash, token);
                        }

                        var hasMd5 = imageFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (generateNonVideoMd5 && (opts?.Overwrite == true || !hasMd5))
                        {
                            var md5 = await fingerprintService.ComputeMd5Async(imagePath, token);
                            if (!string.IsNullOrWhiteSpace(md5))
                                await UpsertFingerprintAsync(imageFile.Id, "md5", md5, token);
                        }
                    }

                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Skipped image {ImageId} during generate after an error", imageFile.ImageId);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref imageProcessed);
                        progress.Report((double)current / imageTotal, $"Generating image content ({current}/{imageTotal})");
                    }
                });
            }

            if (allowNonVideoWork && (opts?.GalleryThumbnails == true || generateNonVideoMd5))
            {
                var galleries = await dbCtx.Galleries
                    .Include(g => g.Folder)
                    .Include(g => g.Files).ThenInclude(f => f.ParentFolder)
                    .Include(g => g.Files).ThenInclude(f => f.Fingerprints)
                    .AsSplitQuery()
                    .ToListAsync(ct);

                if (opts?.Paths is { Count: > 0 } galleryPaths)
                {
                    var filterPaths = NormalizeFilterPaths(galleryPaths);
                    galleries = galleries.Where(gallery =>
                        (gallery.Folder != null && IsUnderAnyPath(gallery.Folder.Path, filterPaths))
                        || gallery.Files.Any(file => IsUnderAnyPath(ResolveFilePath(file), filterPaths)))
                        .ToList();
                }

                var galleryIds = galleries.Select(gallery => gallery.Id).ToList();
                var firstImageRows = new List<(int GalleryId, int ImageId)>();
                if (galleryIds.Count > 0)
                {
                    firstImageRows = (await dbCtx.Set<ImageGallery>()
                        .AsNoTracking()
                        .Where(link => galleryIds.Contains(link.GalleryId))
                        .Select(link => new { link.GalleryId, link.ImageId })
                        .ToListAsync(ct))
                        .Select(link => (link.GalleryId, link.ImageId))
                        .ToList();
                }
                var firstImageByGalleryId = firstImageRows
                    .GroupBy(link => link.GalleryId)
                    .ToDictionary(group => group.Key, group => group.Min(link => link.ImageId));

                var galleryTotal = galleries.Count;
                var galleryProcessed = 0;

                await Parallel.ForEachAsync(galleries, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (gallery, token) =>
                {
                    try
                    {
                    if (opts?.GalleryThumbnails == true)
                    {
                        var coverImageId = gallery.CoverImageId;
                        if (!coverImageId.HasValue && firstImageByGalleryId.TryGetValue(gallery.Id, out var firstImageId))
                            coverImageId = firstImageId;

                        if (coverImageId.HasValue)
                            await thumbnailService.GenerateImageThumbnailAsync(coverImageId.Value, overwrite: opts?.Overwrite == true, ct: token);
                    }

                    if (generateNonVideoMd5)
                    {
                        foreach (var galleryFile in gallery.Files)
                        {
                            var galleryPath = ResolveFilePath(galleryFile);
                            if (!System.IO.File.Exists(galleryPath))
                                continue;

                            var hasMd5 = galleryFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value));
                            if (opts?.Overwrite == true || !hasMd5)
                            {
                                var md5 = await fingerprintService.ComputeMd5Async(galleryPath, token);
                                if (!string.IsNullOrWhiteSpace(md5))
                                    await UpsertFingerprintAsync(galleryFile.Id, "md5", md5, token);
                            }
                        }
                    }

                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Skipped gallery {GalleryId} during generate after an error", gallery.Id);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref galleryProcessed);
                        progress.Report(galleryTotal == 0 ? 1d : (double)current / galleryTotal, $"Generating gallery content ({current}/{galleryTotal})");
                    }
                });
            }

            if (allowNonVideoWork && (opts?.AudioPhashes == true || generateNonVideoMd5))
            {
                var audioFiles = await dbCtx.AudioFiles
                    .Include(f => f.ParentFolder)
                    .Include(f => f.Fingerprints)
                    .ToListAsync(ct);

                if (opts?.Paths is { Count: > 0 } audioPaths)
                {
                    var filterPaths = NormalizeFilterPaths(audioPaths);
                    audioFiles = audioFiles.Where(audioFile =>
                    {
                        var audioPath = audioFile.ParentFolder != null
                            ? Path.Combine(audioFile.ParentFolder.Path, audioFile.Basename)
                            : audioFile.Basename;
                        return IsUnderAnyPath(audioPath, filterPaths);
                    }).ToList();
                }

                if (opts?.AudioIds is { Count: > 0 } audioIdFilter)
                {
                    var audioIdSet = audioIdFilter.ToHashSet();
                    audioFiles = audioFiles.Where(audioFile => audioFile.AudioId.HasValue && audioIdSet.Contains(audioFile.AudioId.Value)).ToList();
                }

                var audioTotal = audioFiles.Count;
                var audioProcessed = 0;

                await Parallel.ForEachAsync(audioFiles, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (audioFile, token) =>
                {
                    try
                    {
                    var audioPath = audioFile.ParentFolder != null
                        ? Path.Combine(audioFile.ParentFolder.Path, audioFile.Basename)
                        : audioFile.Basename;

                    if (System.IO.File.Exists(audioPath))
                    {
                        var hasPhash = audioFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (opts?.AudioPhashes == true && (opts?.Overwrite == true || !hasPhash))
                        {
                            var phash = await fingerprintService.ComputeAudioPhashAsync(audioPath, token);
                            if (!string.IsNullOrWhiteSpace(phash))
                                await UpsertFingerprintAsync(audioFile.Id, "phash", phash, token);
                        }

                        var hasMd5 = audioFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (generateNonVideoMd5 && (opts?.Overwrite == true || !hasMd5))
                        {
                            var md5 = await fingerprintService.ComputeMd5Async(audioPath, token);
                            if (!string.IsNullOrWhiteSpace(md5))
                                await UpsertFingerprintAsync(audioFile.Id, "md5", md5, token);
                        }
                    }

                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Skipped audio {AudioId} during generate after an error", audioFile.AudioId);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref audioProcessed);
                        progress.Report(audioTotal == 0 ? 1d : (double)current / audioTotal, $"Generating audio content ({current}/{audioTotal})");
                    }
                });
            }

            if (allowNonVideoWork && (opts?.TextPhashes == true || generateNonVideoMd5))
            {
                var textFiles = await dbCtx.TextFiles
                    .Include(f => f.ParentFolder)
                    .Include(f => f.Fingerprints)
                    .ToListAsync(ct);

                if (opts?.Paths is { Count: > 0 } textPaths)
                {
                    var filterPaths = NormalizeFilterPaths(textPaths);
                    textFiles = textFiles.Where(textFile =>
                    {
                        var textPath = textFile.ParentFolder != null
                            ? Path.Combine(textFile.ParentFolder.Path, textFile.Basename)
                            : textFile.Basename;
                        return IsUnderAnyPath(textPath, filterPaths);
                    }).ToList();
                }

                if (opts?.TextIds is { Count: > 0 } textIdFilter)
                {
                    var textIdSet = textIdFilter.ToHashSet();
                    textFiles = textFiles.Where(textFile => textFile.TextDocumentId.HasValue && textIdSet.Contains(textFile.TextDocumentId.Value)).ToList();
                }

                var textTotal = textFiles.Count;
                var textProcessed = 0;

                await Parallel.ForEachAsync(textFiles, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (textFile, token) =>
                {
                    try
                    {
                    var textPath = textFile.ParentFolder != null
                        ? Path.Combine(textFile.ParentFolder.Path, textFile.Basename)
                        : textFile.Basename;

                    if (System.IO.File.Exists(textPath))
                    {
                        var hasPhash = textFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (opts?.TextPhashes == true && (opts?.Overwrite == true || !hasPhash))
                        {
                            var phash = await fingerprintService.ComputeTextPhashAsync(textPath, token);
                            if (!string.IsNullOrWhiteSpace(phash))
                                await UpsertFingerprintAsync(textFile.Id, "phash", phash, token);
                        }

                        var hasMd5 = textFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value));
                        if (generateNonVideoMd5 && (opts?.Overwrite == true || !hasMd5))
                        {
                            var md5 = await fingerprintService.ComputeMd5Async(textPath, token);
                            if (!string.IsNullOrWhiteSpace(md5))
                                await UpsertFingerprintAsync(textFile.Id, "md5", md5, token);
                        }
                    }

                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogWarning(ex, "Skipped text {TextId} during generate after an error", textFile.TextDocumentId);
                    }
                    finally
                    {
                        var current = Interlocked.Increment(ref textProcessed);
                        progress.Report(textTotal == 0 ? 1d : (double)current / textTotal, $"Generating text content ({current}/{textTotal})");
                    }
                });
            }
        });

        return Ok(new { jobId });
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

    private static SegmentPreviewClip? FindReusableSegmentPreviewClip(IEnumerable<SegmentPreviewClip> clips, double startSec, double endSec)
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

    [HttpPost("clean")]
    [RequiresPermission(Permissions.LibraryClean)]
    public ActionResult<object> StartClean([FromBody] CleanOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("clean", "Cleaning library", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var files = await dbCtx.Set<BaseFileEntity>()
                .Include(f => f.ParentFolder)
                .ToListAsync(ct);

            var total = files.Count;
            var cleaned = 0;
            var filterPaths = opts?.Paths?.Count > 0 ? NormalizeFilterPaths(opts.Paths) : [];

            for (var i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Checking files ({i + 1}/{total})");

                var file = files[i];
                var filePath = Path.Combine(file.ParentFolder?.Path ?? "", file.Basename);

                if (filterPaths.Count > 0 && !IsUnderAnyPath(filePath, filterPaths))
                    continue;

                if (!System.IO.File.Exists(filePath))
                {
                    if (opts?.DryRun != true)
                    {
                        dbCtx.Set<BaseFileEntity>().Remove(file);
                        cleaned++;
                    }
                    else
                    {
                        logger.LogDebug("Dry run: would remove missing file {Path}", filePath);
                        cleaned++;
                    }
                }
            }

            if (opts?.DryRun != true)
                await dbCtx.SaveChangesAsync(ct);

            logger.LogInformation("Clean completed. {Cleaned} missing files {Action}", cleaned, opts?.DryRun == true ? "found" : "removed");
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("export")]
    [RequiresPermission(Permissions.SystemBackup)]
    public ActionResult<object> StartExport([FromBody] ExportOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("export", "Exporting metadata", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var exportPath = Path.Combine(config.GeneratedPath ?? Path.GetTempPath(), "export");
            Directory.CreateDirectory(exportPath);
            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var exportFile = Path.Combine(exportPath, $"cove-export-{timestamp}.json");

            var exportData = new Dictionary<string, object>();

            if (opts?.IncludeVideos != false)
            {
                progress.Report(0.1, "Exporting videos...");
                exportData["videos"] = await dbCtx.Videos
                    .Include(s => s.VideoTags).ThenInclude(st => st.Tag)
                    .Include(s => s.VideoPerformers).ThenInclude(sp => sp.Performer)
                    .Include(s => s.Studio)
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .AsNoTracking()
                    .AsSplitQuery()
                    .ToListAsync(ct);
            }

            if (opts?.IncludePerformers != false)
            {
                progress.Report(0.3, "Exporting performers...");
                exportData["performers"] = await dbCtx.Performers.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeStudios != false)
            {
                progress.Report(0.5, "Exporting studios...");
                exportData["studios"] = await dbCtx.Studios.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeTags != false)
            {
                progress.Report(0.6, "Exporting tags...");
                exportData["tags"] = await dbCtx.Tags.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeGalleries != false)
            {
                progress.Report(0.7, "Exporting galleries...");
                exportData["galleries"] = await dbCtx.Galleries.AsNoTracking().ToListAsync(ct);
            }

            if (opts?.IncludeGroups != false)
            {
                progress.Report(0.8, "Exporting groups...");
                exportData["groups"] = await dbCtx.Groups.AsNoTracking().ToListAsync(ct);
            }

            progress.Report(0.9, "Writing export file...");
            await System.IO.File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(exportData, MetadataExportJsonOptions), ct);

            logger.LogInformation("Export completed: {Path}", exportFile);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("import")]
    [RequiresPermission(Permissions.SystemRestore)]
    public ActionResult<object> StartImport([FromBody] ImportOptionsDto? opts)
    {
        var filePath = opts?.FilePath;
        if (string.IsNullOrEmpty(filePath) || !System.IO.File.Exists(filePath))
            return BadRequest(new { error = "Import file path is required and must exist" });

        var overwrite = opts?.DuplicateHandling ?? false;

        var jobId = jobService.Enqueue("import", "Importing metadata", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();

            progress.Report(0.05, "Reading import file...");
            var json = await System.IO.File.ReadAllTextAsync(filePath, ct);
            var importData = JsonSerializer.Deserialize<JsonElement>(json, CoveJson.Default);

            // Import tags first (no dependencies)
            if (importData.TryGetProperty("tags", out var tagsEl))
            {
                progress.Report(0.1, "Importing tags...");
                var importTags = JsonSerializer.Deserialize<List<Tag>>(tagsEl.GetRawText(), CoveJson.Default) ?? [];
                foreach (var tag in importTags)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Tags.FirstOrDefaultAsync(t => t.Name == tag.Name, ct);
                    if (existing != null)
                    {
                        if (overwrite) { existing.Description = tag.Description; existing.Favorite = tag.Favorite; }
                    }
                    else
                    {
                        dbCtx.Tags.Add(new Tag { Name = tag.Name, Description = tag.Description, Favorite = tag.Favorite });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import studios (may reference parent studios)
            if (importData.TryGetProperty("studios", out var studiosEl))
            {
                progress.Report(0.3, "Importing studios...");
                var importStudios = JsonSerializer.Deserialize<List<Studio>>(studiosEl.GetRawText(), CoveJson.Default) ?? [];
                foreach (var studio in importStudios)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Studios.FirstOrDefaultAsync(s => s.Name == studio.Name, ct);
                    if (existing != null)
                    {
                        if (overwrite) { existing.Details = studio.Details; }
                    }
                    else
                    {
                        dbCtx.Studios.Add(new Studio { Name = studio.Name, Details = studio.Details });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import performers
            if (importData.TryGetProperty("performers", out var performersEl))
            {
                progress.Report(0.5, "Importing performers...");
                var importPerformers = JsonSerializer.Deserialize<List<Performer>>(performersEl.GetRawText(), CoveJson.Default) ?? [];
                foreach (var performer in importPerformers)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Performers.FirstOrDefaultAsync(p => p.Name == performer.Name && p.Disambiguation == performer.Disambiguation, ct);
                    if (existing != null)
                    {
                        if (overwrite)
                        {
                            existing.Gender = performer.Gender;
                            existing.Birthdate = performer.Birthdate;
                            existing.Ethnicity = performer.Ethnicity;
                            existing.Country = performer.Country;
                            existing.Details = performer.Details;
                        }
                    }
                    else
                    {
                        dbCtx.Performers.Add(new Performer
                        {
                            Name = performer.Name, Disambiguation = performer.Disambiguation,
                            Gender = performer.Gender, Birthdate = performer.Birthdate,
                            Ethnicity = performer.Ethnicity, Country = performer.Country,
                            Details = performer.Details, Favorite = performer.Favorite
                        });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            // Import groups
            if (importData.TryGetProperty("groups", out var groupsEl))
            {
                progress.Report(0.7, "Importing groups...");
                var importGroups = JsonSerializer.Deserialize<List<Group>>(groupsEl.GetRawText(), CoveJson.Default) ?? [];
                foreach (var group in importGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    var existing = await dbCtx.Groups.FirstOrDefaultAsync(g => g.Name == group.Name, ct);
                    if (existing != null)
                    {
                        if (overwrite) { existing.Director = group.Director; existing.Synopsis = group.Synopsis; }
                    }
                    else
                    {
                        dbCtx.Groups.Add(new Group { Name = group.Name, Director = group.Director, Synopsis = group.Synopsis, Duration = group.Duration });
                    }
                }
                await dbCtx.SaveChangesAsync(ct);
            }

            progress.Report(1.0, "Import completed");
            logger.LogInformation("Metadata import completed from: {Path}", filePath);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("clean-generated")]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public ActionResult<object> CleanGenerated()
    {
        var jobId = jobService.Enqueue("clean-generated", "Cleaning generated files", async (progress, ct) =>
        {
            var generatedPath = config.GeneratedPath;
            if (string.IsNullOrEmpty(generatedPath) || !Directory.Exists(generatedPath))
            {
                logger.LogWarning("Generated path not configured or does not exist");
                return;
            }

            var dirs = new[] { "screenshots", "thumbnails", "previews", "sprites", "transcodes", "vtt" };
            var totalCleared = 0L;

            for (var i = 0; i < dirs.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / dirs.Length, $"Cleaning {dirs[i]}...");

                var dir = Path.Combine(generatedPath, dirs[i]);
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(file);
                    totalCleared += fi.Length;
                    fi.Delete();
                }
            }

            logger.LogInformation("Cleaned generated files. Freed {Size} bytes", totalCleared);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    [HttpPost("identify")]
    [RequiresPermission(Permissions.LibraryIdentify)]
    [RequiresEntityAccess(EntityKinds.Video, Permissions.VideosWrite, ActionArgumentName = "opts", PropertyName = "VideoIds")]
    public ActionResult<object> StartIdentify([FromBody] IdentifyOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("identify", "Identifying videos", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var metadataServerSvc = scope.ServiceProvider.GetService<MetadataServerService>();

            var videos = opts?.VideoIds?.Count > 0
                ? await dbCtx.Videos
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .Include(s => s.VideoTags)
                    .Include(s => s.VideoPerformers)
                    .Include(s => s.RemoteIds)
                    .Include(s => s.Urls)
                    .Where(s => opts.VideoIds.Contains(s.Id)).AsSplitQuery().ToListAsync(ct)
                : await dbCtx.Videos
                    .Include(s => s.Files).ThenInclude(f => f.Fingerprints)
                    .Include(s => s.VideoTags)
                    .Include(s => s.VideoPerformers)
                    .Include(s => s.RemoteIds)
                    .Include(s => s.Urls)
                    .AsSplitQuery().ToListAsync(ct);

            var identifyDefaults = config.Scraping.IdentifyDefaults;
            var sourceEndpoints = ResolveIdentifyMetadataServerEndpoints(opts?.Sources, config.Scraping.MetadataServers);
            var sourceOrder = sourceEndpoints?
                .Select((endpoint, index) => new { endpoint, index })
                .ToDictionary(item => item.endpoint, item => item.index, StringComparer.OrdinalIgnoreCase);

            // Build import config from identify options
            var importConfig = new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = opts?.SetCoverImage ?? true,
                SetTags = opts?.SetTags ?? true,
                SetPerformers = opts?.SetPerformers ?? true,
                SetStudio = opts?.SetStudio ?? true,
                OnlyExistingTags = !(opts?.CreateTags ?? identifyDefaults.CreateTags),
                OnlyExistingPerformers = !(opts?.CreatePerformers ?? identifyDefaults.CreatePerformers),
                OnlyExistingStudio = !(opts?.CreateStudios ?? identifyDefaults.CreateStudios),
                MarkOrganized = opts?.MarkOrganized ?? false,
                FieldStrategies = opts?.FieldStrategies,
                PerformerGenders = opts?.PerformerGenders,
                SkipSingleNamePerformers = opts?.SkipSingleNamePerformers ?? true,
            };

            var total = videos.Count;
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Identifying video {i + 1}/{total}");

                var video = videos[i];
                var fingerprints = video.Files.SelectMany(f => f.Fingerprints).ToList();
                if (fingerprints.Count == 0) continue;

                // Attempt MetadataServer identification
                if (metadataServerSvc != null && (sourceEndpoints == null || sourceEndpoints.Count > 0))
                {
                    try
                    {
                        IReadOnlyList<MetadataServerVideoMatchDto> matches;
                        if (sourceEndpoints == null)
                        {
                            matches = await metadataServerSvc.SearchVideosAsync(video, null, null, ct);
                        }
                        else
                        {
                            var orderedMatches = new List<MetadataServerVideoMatchDto>();
                            foreach (var endpoint in sourceEndpoints)
                            {
                                orderedMatches.AddRange(await metadataServerSvc.SearchVideosAsync(video, null, endpoint, ct));
                            }
                            matches = orderedMatches;
                        }

                        logger.LogDebug(
                            "Identify video {VideoId}: metadata servers returned {MatchCount} candidate match(es)",
                            video.Id, matches.Count);

                        if (matches.Count > 0)
                        {
                            // Evaluate every candidate once, capturing its scores and whether it cleared
                            // the auto-apply thresholds (and which guard rejected it). This is purely for
                            // diagnostics; the ranking/selection below is unchanged.
                            var evaluatedCandidates = matches
                                .Select(match =>
                                {
                                    var durationDifferenceSeconds = GetDurationDifferenceSeconds(video, match);
                                    var phashDistance = GetBestPhashDistance(video, match);
                                    var passed = MeetsIdentifyAutoApplyThresholds(match.MatchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out var failureReason);
                                    return new
                                    {
                                        Match = match,
                                        DurationDifferenceSeconds = durationDifferenceSeconds,
                                        PhashDistance = phashDistance,
                                        Passed = passed,
                                        FailureReason = failureReason,
                                    };
                                })
                                .ToList();

                            foreach (var candidate in evaluatedCandidates)
                            {
                                logger.LogDebug(
                                    "Identify video {VideoId}: candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} => {Result}{FailureSuffix}",
                                    video.Id,
                                    candidate.Match.Id,
                                    candidate.Match.Title,
                                    candidate.Match.Endpoint,
                                    candidate.Match.MetadataServerName,
                                    candidate.Match.MatchCount,
                                    candidate.DurationDifferenceSeconds,
                                    candidate.PhashDistance,
                                    candidate.Passed ? "PASSED" : "FAILED",
                                    candidate.Passed ? string.Empty : $" ({candidate.FailureReason})");
                            }

                            var rankedMatches = evaluatedCandidates
                                .Where(candidate => candidate.Passed)
                                .OrderBy(candidate => sourceOrder != null && sourceOrder.TryGetValue(candidate.Match.Endpoint, out var index) ? index : int.MaxValue)
                                .ThenByDescending(candidate => candidate.Match.MatchCount)
                                .ThenBy(candidate => candidate.PhashDistance ?? int.MaxValue)
                                .ThenBy(candidate => candidate.DurationDifferenceSeconds ?? double.MaxValue)
                                .ToList();

                            if (rankedMatches.Count == 0)
                            {
                                logger.LogDebug(
                                    "Identify video {VideoId}: {MatchCount} candidate(s) returned, 0 passed thresholds",
                                    video.Id, matches.Count);
                                continue;
                            }

                            // Skip multiple matches only when explicitly requested. By default we
                            // apply the top-ranked candidate rather than skipping the whole video.
                            if ((opts?.SkipMultipleMatches ?? false) && rankedMatches.Count > 1)
                            {
                                logger.LogDebug(
                                    "Identify video {VideoId}: skipping because {PassedCount} candidates passed thresholds and SkipMultipleMatches is enabled",
                                    video.Id, rankedMatches.Count);
                                continue;
                            }

                            var bestCandidate = rankedMatches[0];
                            var best = bestCandidate.Match;
                            logger.LogInformation(
                                "Identify video {VideoId}: selected candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} (best of {PassedCount} passing of {TotalCount} returned)",
                                video.Id,
                                best.Id,
                                best.Title,
                                best.Endpoint,
                                best.MetadataServerName,
                                best.MatchCount,
                                bestCandidate.DurationDifferenceSeconds,
                                bestCandidate.PhashDistance,
                                rankedMatches.Count,
                                matches.Count);

                            await metadataServerSvc.MergeVideoAsync(video, best.Endpoint, best.Id, importConfig, ct);
                            await dbCtx.SaveChangesAsync(ct);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "MetadataServer identify failed for video {VideoId}", video.Id);
                    }
                }
            }

            await dbCtx.SaveChangesAsync(ct);
            logger.LogInformation("Identify completed for {Count} videos", total);
        }, exclusive: false);

        return Ok(new { jobId });
    }

    private static List<string>? ResolveIdentifyMetadataServerEndpoints(List<string>? sources, IReadOnlyList<MetadataServerInstance> metadataServers)
    {
        if (sources == null || sources.Count == 0)
            return null;

        var endpoints = new List<string>();
        foreach (var source in sources.Select(source => source.Trim()).Where(source => source.Length > 0))
        {
            var server = metadataServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Endpoint, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Name, source, StringComparison.OrdinalIgnoreCase));

            if (server == null)
                continue;

            if (!endpoints.Contains(server.Endpoint, StringComparer.OrdinalIgnoreCase))
                endpoints.Add(server.Endpoint);
        }

        return endpoints;
    }

    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults)
        => MeetsIdentifyAutoApplyThresholds(matchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out _);

    // Same threshold logic, but also reports which specific guard rejected the candidate so the
    // identify loop can log it. The boolean result is identical to the parameterless overload.
    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults, out string? failureReason)
    {
        failureReason = null;

        // Primary signal: require enough matching fingerprint submissions. MatchCount already
        // counts oshash, md5, and phash (incl. close phash) matches, so this works for metadata
        // servers that don't publish phashes.
        if (identifyDefaults.AutoApplyMinFingerprintMatches is int minFingerprintMatches)
        {
            if (matchCount < minFingerprintMatches)
            {
                failureReason = $"matchCount {matchCount} < AutoApplyMinFingerprintMatches {minFingerprintMatches}";
                return false;
            }
        }

        // Secondary guard: only reject when both durations are known and disagree by more than the
        // tolerance. A missing duration must never block a match that cleared the fingerprint bar.
        if (identifyDefaults.AutoApplyMaxDurationDifferenceSeconds is int maxDurationDifferenceSeconds)
        {
            if (durationDifferenceSeconds.HasValue && durationDifferenceSeconds.Value > maxDurationDifferenceSeconds)
            {
                failureReason = $"durationDiff {durationDifferenceSeconds.Value:0.##}s > AutoApplyMaxDurationDifferenceSeconds {maxDurationDifferenceSeconds}";
                return false;
            }
        }

        // Optional phash tightness guard: only applies when a phash distance is actually computable.
        if (identifyDefaults.AutoApplyMaxPhashDistance is int maxPhashDistance)
        {
            if (phashDistance.HasValue && phashDistance.Value > maxPhashDistance)
            {
                failureReason = $"phashDistance {phashDistance.Value} > AutoApplyMaxPhashDistance {maxPhashDistance}";
                return false;
            }
        }

        return true;
    }

    private static double? GetDurationDifferenceSeconds(Video video, MetadataServerVideoMatchDto match)
    {
        var localDuration = video.Files.Select(file => (double?)file.Duration).Max();
        return localDuration.HasValue && match.Duration.HasValue
            ? Math.Abs(localDuration.Value - match.Duration.Value)
            : null;
    }

    private static int? GetBestPhashDistance(Video video, MetadataServerVideoMatchDto match)
    {
        var localPhashes = video.Files
            .SelectMany(file => file.Fingerprints)
            .Where(fingerprint => string.Equals(fingerprint.Type, "phash", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Value))
            .Select(fingerprint => fingerprint.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remotePhashes = match.Fingerprints
            .Where(fingerprint => string.Equals(fingerprint.Algorithm, "PHASH", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Hash))
            .Select(fingerprint => fingerprint.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (localPhashes.Count == 0 || remotePhashes.Count == 0)
            return null;

        int? bestDistance = null;
        foreach (var localPhash in localPhashes)
        {
            foreach (var remotePhash in remotePhashes)
            {
                var distance = MetadataServerService.ComputePhashHammingDistance(localPhash, remotePhash);
                bestDistance = bestDistance.HasValue ? Math.Min(bestDistance.Value, distance) : distance;
            }
        }

        return bestDistance;
    }

    [HttpPost("sync-fingerprints")]
    [RequiresPermission(Permissions.LibraryScan)]
    public ActionResult<object> SyncFingerprints([FromBody] SyncFingerprintsOptionsDto? opts)
    {
        var sourceUrl = opts?.SourceUrl ?? "http://localhost:3000/graphql";
        var apiKey = opts?.ApiKey;

        var jobId = jobService.Enqueue("sync-fingerprints", "Syncing fingerprints from source instance", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            // Use the pooled factory rather than `new HttpClient()` to avoid socket exhaustion.
            using var httpClient = httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrEmpty(apiKey))
                httpClient.DefaultRequestHeaders.Add("ApiKey", apiKey);

            // Step 1: Fetch all fingerprints from the source instance, paging through results
            progress.Report(0, "Fetching fingerprints from source instance...");
            var oshashToPhash = new Dictionary<string, string>();
            var page = 1;
            var perPage = 100;
            var totalVideos = 0;
            var fetched = 0;

            do
            {
                ct.ThrowIfCancellationRequested();

                var graphqlQuery = new
                {
                    query = @"query FindVideos($filter: FindFilterType!) {
                        findVideos(filter: $filter) {
                            count
                            videos {
                                files {
                                    fingerprints {
                                        type
                                        value
                                    }
                                }
                            }
                        }
                    }",
                    variables = new
                    {
                        filter = new { page, per_page = perPage, sort = "id", direction = "ASC" }
                    }
                };

                var jsonPayload = JsonSerializer.Serialize(graphqlQuery);
                var response = await httpClient.PostAsync(
                    sourceUrl,
                    new StringContent(jsonPayload, Encoding.UTF8, "application/json"),
                    ct);

                response.EnsureSuccessStatusCode();
                var responseJson = await response.Content.ReadAsStringAsync(ct);

                using var doc = JsonDocument.Parse(responseJson);
                var data = doc.RootElement.GetProperty("data").GetProperty("findVideos");
                totalVideos = data.GetProperty("count").GetInt32();

                foreach (var video in data.GetProperty("videos").EnumerateArray())
                {
                    foreach (var file in video.GetProperty("files").EnumerateArray())
                    {
                        string? oshash = null;
                        string? phash = null;

                        foreach (var fp in file.GetProperty("fingerprints").EnumerateArray())
                        {
                            var type = fp.GetProperty("type").GetString();
                            var value = fp.GetProperty("value").GetString();
                            if (type == "oshash") oshash = value;
                            else if (type == "phash") phash = value;
                        }

                        if (oshash != null && phash != null)
                            oshashToPhash.TryAdd(oshash, phash);
                    }
                }

                fetched += perPage;
                page++;
                progress.Report(Math.Min(0.5, (double)fetched / Math.Max(totalVideos, 1)),
                    $"Fetched {Math.Min(fetched, totalVideos)}/{totalVideos} videos from source...");
            }
            while (fetched < totalVideos);

            logger.LogInformation("Fetched {Count} oshashâ†’phash mappings from source instance", oshashToPhash.Count);

            // Step 2: Load all files with fingerprints from our DB
            progress.Report(0.5, "Loading local video fingerprints...");
            var localFiles = await dbCtx.Set<BaseFileEntity>()
                .Include(f => f.Fingerprints)
                .ToListAsync(ct);

            var updated = 0;
            var created = 0;
            var total = localFiles.Count;

            for (var i = 0; i < localFiles.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = localFiles[i];
                var localOshash = file.Fingerprints.FirstOrDefault(f => f.Type == "oshash")?.Value;
                if (localOshash == null) continue;

                // Normalize oshash to padded format for lookup (Go uses %016x, local may be unpadded)
                var normalizedLocal = localOshash.PadLeft(16, '0');
                if (!oshashToPhash.TryGetValue(normalizedLocal, out var sourcePhash))
                {
                    // Also try with the raw value for backward compatibility
                    if (!oshashToPhash.TryGetValue(localOshash, out sourcePhash))
                        continue;
                }

                // Also fix the local oshash to padded format if it's not already
                if (localOshash.Length < 16)
                {
                    var oshashFp = file.Fingerprints.First(f => f.Type == "oshash");
                    oshashFp.Value = normalizedLocal;
                }

                var existingPhash = file.Fingerprints.FirstOrDefault(f => f.Type == "phash");
                if (existingPhash != null)
                {
                    if (existingPhash.Value != sourcePhash)
                    {
                        existingPhash.Value = sourcePhash;
                        updated++;
                    }
                }
                else
                {
                    file.Fingerprints.Add(new FileFingerprint { FileId = file.Id, Type = "phash", Value = sourcePhash });
                    created++;
                }

                if ((i + 1) % 100 == 0)
                    progress.Report(0.5 + 0.5 * ((double)(i + 1) / total),
                        $"Processing files ({i + 1}/{total})...");
            }

            await dbCtx.SaveChangesAsync(ct);
            logger.LogInformation("Fingerprint sync completed. {Updated} updated, {Created} created from {Total} source mappings",
                updated, created, oshashToPhash.Count);
        }, exclusive: false);

        return Ok(new { jobId });
    }
}

