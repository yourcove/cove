using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

internal sealed class ScanVideoProcessor(
    CoveConfiguration config,
    IFingerprintService fingerprintService,
    IThumbnailService thumbnailService,
    IMediaProbeService mediaProbeService,
    ScanFolderResolver folderResolver,
    ScanFileIdentityService fileIdentity,
    ILogger logger)
{
    internal async Task<(VideoFile File, bool Relinked, bool Moved)> ProcessAsync(
        CoveContext db,
        string path,
        int? videoId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool syncCaptions = true,
        bool knownNew = false,
        ConcurrentDictionary<string, IReadOnlyList<string>>? captionFilesByDir = null,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null,
        string? videoProbeJson = null)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        // When the scan index already established this is a brand-new file, the lookup is
        // guaranteed to miss — skip the round-trip and go straight to insert.
        VideoFile? existing = null;
        if (!knownNew)
        {
            var existingQuery = syncCaptions
                ? db.VideoFiles.Include(file => file.Captions).Include(file => file.Fingerprints)
                : db.VideoFiles.Include(file => file.Fingerprints);
            existing = await existingQuery.FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);
        }

        // Also consult entities added in this unit of work but not yet saved. Without this, a file
        // enumerated twice in the same batch (or a stale knownNew hint) would insert a second row and
        // violate the unique (ParentFolderId, Basename) index, aborting the whole SaveChanges batch.
        existing ??= db.VideoFiles.Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);

        Video? targetVideo = null;
        if (videoId.HasValue)
        {
            targetVideo = await db.Videos.FirstOrDefaultAsync(s => s.Id == videoId.Value, ct)
                ?? throw new InvalidOperationException($"Video {videoId.Value} was not found for downloaded media import");

            if (string.IsNullOrWhiteSpace(targetVideo.Title))
                targetVideo.Title = Path.GetFileNameWithoutExtension(path);
        }

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;

            if (targetVideo != null)
                existing.VideoId = targetVideo.Id;

            // Re-probe when the bytes changed in place (re-encode/replacement) or when metadata was
            // never captured (e.g. FFprobe was unavailable on the initial scan).
            if (contentChanged || NeedsMetadataProbe(existing))
            {
                if (videoProbeJson != null)
                    ApplyFfprobeMetadata(existing, videoProbeJson);
                else
                    await ProbeVideoAsync(existing, path, ct);
            }

            if (contentChanged)
            {
                await fileIdentity.RefreshChangedFingerprintsAsync(
                    existing, path,
                    phashEnabled: scanOptions?.GeneratePhashes == true,
                    md5Enabled: config.CalculateMd5 || scanOptions?.GenerateMd5 == true,
                    ct);
                InvalidateStaleVideoAssets(existing, scanOptions);
            }

            if (syncCaptions)
            {
                SyncVideoCaptions(existing, path, captionFilesByDir);
            }

            return (existing, false, false);
        }

        // No row at this path. Before creating a fresh entity, check whether this file's content already
        // exists in the library: a MOVE (re-point the now-missing record) or a DUPLICATE (attach as an
        // additional file of the existing video), rather than creating a separate duplicate entity.
        if (targetVideo == null && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await fileIdentity.MatchExistingAsync(db.VideoFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match != null)
            {
                if (isMove)
                {
                    if (syncCaptions)
                        SyncVideoCaptions(match, path, captionFilesByDir);
                    logger.LogTrace("Re-linked moved video file to {NewPath} (previously {OldPath})", path, match.Path);
                    return (match, true, true);
                }

                // Duplicate: identical content already on disk — add this file to the same video entity.
                var duplicateFile = new VideoFile
                {
                    Basename = basename,
                    ParentFolderId = folderId,
                    Size = stat.Size,
                    ModTime = stat.ModTime,
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    VideoId = match.VideoId,
                };
                db.VideoFiles.Add(duplicateFile);
                await EnrichVideoFileAsync(duplicateFile, path, ct, captionFilesByDir, videoProbeJson);
                logger.LogTrace("Attached duplicate video file {NewPath} to existing video {VideoId}", path, match.VideoId);
                return (duplicateFile, true, false);
            }
        }

        // Create video file entry
        var videoFile = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
            VideoId = targetVideo?.Id
        };

        if (targetVideo == null)
        {
            // Intentionally leave Title null on scan. Storing the filename as the title makes it
            // impossible to filter for entities that have no real title; the UI falls back to the
            // file basename for display when Title is null.
            var video = new Video
            {
                Files = [videoFile]
            };

            db.Videos.Add(video);
        }
        else
        {
            db.VideoFiles.Add(videoFile);
        }

        await EnrichVideoFileAsync(videoFile, path, ct, captionFilesByDir, videoProbeJson);

        logger.LogTrace("Added video file for {Path}", path);
        return (videoFile, false, false);
    }

    // Delete a changed video's stale visual assets (cover/preview/sprite) so the generation phase
    // recreates them from the new content — but only for the asset types this scan is (re)generating,
    // so a metadata-only scan never destroys assets it will not rebuild.
    private void InvalidateStaleVideoAssets(VideoFile videoFile, ScanOperationOptions? options)
    {
        if (options == null || videoFile.VideoId is not int vid)
            return;

        if (options.GenerateCovers)
            TryDeleteGeneratedFile(thumbnailService.GetThumbnailPathForVideo(vid));
        if (options.GeneratePreviews)
            TryDeleteGeneratedFile(thumbnailService.GetPreviewPath(vid));
        if (options.GenerateSprites)
        {
            TryDeleteGeneratedFile(thumbnailService.GetSpritePath(vid));
            TryDeleteGeneratedFile(thumbnailService.GetSpriteVttPath(vid));
        }
    }

    private async Task EnrichVideoFileAsync(
        VideoFile videoFile,
        string path,
        CancellationToken ct,
        ConcurrentDictionary<string, IReadOnlyList<string>>? captionFilesByDir = null,
        string? videoProbeJson = null)
    {
        // Probe with FFprobe for metadata
        if (videoProbeJson != null)
            ApplyFfprobeMetadata(videoFile, videoProbeJson);
        else
            await ProbeVideoAsync(videoFile, path, ct);

        // Compute oshash fingerprint
        var oshash = await ScanFileIdentityService.ComputeOshashAsync(path, ct);
        if (oshash != null)
        {
            videoFile.Fingerprints.Add(new FileFingerprint
            {
                Type = "oshash",
                Value = oshash
            });
        }

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                videoFile.Fingerprints.Add(new FileFingerprint
                {
                    Type = "md5",
                    Value = md5,
                });
            }
        }

        SyncVideoCaptions(videoFile, path, captionFilesByDir);
    }

    private static void SyncVideoCaptions(
        VideoFile videoFile,
        string path,
        ConcurrentDictionary<string, IReadOnlyList<string>>? captionFilesByDir = null)
    {
        var sidecars = DiscoverCaptionSidecars(path, captionFilesByDir);
        var expected = sidecars.ToDictionary(item => item.Filename, StringComparer.OrdinalIgnoreCase);

        foreach (var existing in videoFile.Captions.ToList())
        {
            if (!expected.TryGetValue(existing.Filename, out var sidecar))
            {
                videoFile.Captions.Remove(existing);
                continue;
            }

            existing.LanguageCode = sidecar.LanguageCode;
            existing.CaptionType = sidecar.CaptionType;
        }

        var existingFilenames = videoFile.Captions
            .Select(item => item.Filename)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sidecar in sidecars)
        {
            if (existingFilenames.Contains(sidecar.Filename))
                continue;

            videoFile.Captions.Add(new VideoCaption
            {
                LanguageCode = sidecar.LanguageCode,
                CaptionType = sidecar.CaptionType,
                Filename = sidecar.Filename,
            });
        }
    }

    private static List<CaptionSidecar> DiscoverCaptionSidecars(
        string path,
        ConcurrentDictionary<string, IReadOnlyList<string>>? captionFilesByDir = null)
    {
        var videoDir = Path.GetDirectoryName(path);
        if (videoDir == null || !Directory.Exists(videoDir))
            return [];

        // Enumerating the whole directory once per video is O(files-in-folder) per video —
        // i.e. O(n^2) for a folder full of videos, which is what made later scans crawl.
        // Enumerate each directory's caption files (.vtt/.srt) a single time per scan and
        // reuse the small result for every video in that folder.
        var captionFiles = captionFilesByDir != null
            ? captionFilesByDir.GetOrAdd(videoDir, EnumerateCaptionFiles)
            : EnumerateCaptionFiles(videoDir);

        if (captionFiles.Count == 0)
            return [];

        var prefix = Path.Combine(videoDir, Path.GetFileNameWithoutExtension(path));
        return captionFiles
            .Where(captionFile => captionFile.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(captionFile =>
            {
                var captionFilename = Path.GetFileName(captionFile);
                var ext = Path.GetExtension(captionFile).TrimStart('.').ToLowerInvariant();
                var langCode = "00";
                var nameWithoutExt = Path.GetFileNameWithoutExtension(captionFile);
                var parts = nameWithoutExt.Split('.');
                if (parts.Length >= 2)
                {
                    var candidate = parts[^1];
                    if (candidate.Length is 2 or 3)
                        langCode = candidate.ToLowerInvariant();
                }

                return new CaptionSidecar(captionFilename, langCode, ext);
            })
            .OrderBy(item => item.Filename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> EnumerateCaptionFiles(string videoDir)
    {
        try
        {
            return Directory.EnumerateFiles(videoDir)
                .Where(f => f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase)
                    || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return [];
        }
    }


    private void TryDeleteGeneratedFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Failed to delete stale generated asset {Path}", path);
        }
    }

    internal static bool NeedsMetadataProbe(VideoFile videoFile)
    {
        return videoFile.Width <= 0 || videoFile.Height <= 0 || videoFile.Duration <= 0;
    }


    private async Task ProbeVideoAsync(VideoFile videoFile, string path, CancellationToken ct)
    {
        var result = await mediaProbeService.ProbeAsync(path, ct);
        if (result.Status == MediaProbeStatus.Success && result.Json != null)
            ApplyFfprobeMetadata(videoFile, result.Json);
    }

    /// <summary>
    /// Apply ffprobe's -show_format/-show_streams JSON onto a <see cref="VideoFile"/>. Always overwrites
    /// (using local "first stream seen" flags rather than gating on the current field values), so
    /// re-probing an already-populated file after an in-place re-encode updates the stored codec,
    /// resolution, framerate, duration, and bitrate instead of silently keeping the stale values.
    /// </summary>
    internal static void ApplyFfprobeMetadata(VideoFile videoFile, string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Extract format duration
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur) && double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                videoFile.Duration = duration;
            if (format.TryGetProperty("bit_rate", out var br) && long.TryParse(br.GetString(), out var bitrate))
                videoFile.BitRate = bitrate;
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            var sawVideoStream = false;
            var sawAudioStream = false;
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                if (codecType == "video" && !sawVideoStream)
                {
                    sawVideoStream = true;
                    if (stream.TryGetProperty("width", out var w)) videoFile.Width = w.GetInt32();
                    if (stream.TryGetProperty("height", out var h)) videoFile.Height = h.GetInt32();
                    if (stream.TryGetProperty("codec_name", out var cn)) videoFile.VideoCodec = cn.GetString() ?? "";
                    if (stream.TryGetProperty("r_frame_rate", out var rfr))
                    {
                        var frs = rfr.GetString() ?? "";
                        var frParts = frs.Split('/');
                        if (frParts.Length == 2 && double.TryParse(frParts[0], out var num) && double.TryParse(frParts[1], out var den) && den > 0)
                            videoFile.FrameRate = num / den;
                    }
                }
                else if (codecType == "audio" && !sawAudioStream)
                {
                    sawAudioStream = true;
                    if (stream.TryGetProperty("codec_name", out var acn)) videoFile.AudioCodec = acn.GetString() ?? "";
                }
            }
        }
    }


    private sealed record CaptionSidecar(string Filename, string LanguageCode, string CaptionType);
}
