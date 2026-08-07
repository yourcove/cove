using System.Diagnostics;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Services;

/// <summary>
/// Loads the persisted file index and classifies discovered files before scan processing begins.
/// </summary>
internal static class ScanExistingFileIndex
{
    private static readonly TimeSpan FileModTimeUnchangedTolerance = TimeSpan.FromMilliseconds(1);

    public static async Task<Dictionary<string, ExistingFileScanInfo>> LoadAsync(
        CoveContext db,
        IReadOnlyCollection<DiscoveredFile> files,
        IReadOnlySet<string> videoExts,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts,
        IReadOnlySet<string> audioExts,
        IReadOnlySet<string> textExts,
        IJobProgress progress,
        ILogger logger,
        CancellationToken ct)
    {
        var index = new Dictionary<string, ExistingFileScanInfo>(StringComparer.OrdinalIgnoreCase);
        await AddExistingBaseFilesAsync(db, index, files, progress, logger, ct);
        await AddExistingFilesForExtensionsAsync(db, index, files, videoExts, "videos", AddExistingVideoFilesAsync, progress, logger, ct);
        await AddExistingFilesForExtensionsAsync(db, index, files, imageExts, "images", AddExistingImageFilesAsync, progress, logger, ct);
        await AddExistingFilesForExtensionsAsync(db, index, files, galleryExts, "galleries", AddExistingGalleryFilesAsync, progress, logger, ct);
        await AddExistingFilesForExtensionsAsync(db, index, files, audioExts, "audio", AddExistingAudioFilesAsync, progress, logger, ct);
        await AddExistingFilesForExtensionsAsync(db, index, files, textExts, "texts", AddExistingTextFilesAsync, progress, logger, ct);

        return index;
    }

    public static ScanFileChangeReason GetChangeReason(ExistingFileScanInfo existingFile, DiscoveredFile file, bool rescan)
    {
        if (rescan)
            return ScanFileChangeReason.RescanForced;

        if (existingFile.NeedsMetadataProbe)
            return ScanFileChangeReason.MetadataProbe;

        if (existingFile.Size != file.Size)
            return ScanFileChangeReason.SizeChanged;

        if (existingFile.ModTime >= file.ModTime
            || file.ModTime - existingFile.ModTime <= FileModTimeUnchangedTolerance)
        {
            return ScanFileChangeReason.Unchanged;
        }

        return ScanFileChangeReason.ModTimeChanged;
    }

    public static ExistingFileKind GetExpectedKind(
        string extension,
        IReadOnlySet<string> videoExts,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts,
        IReadOnlySet<string> audioExts,
        IReadOnlySet<string> textExts)
    {
        if (videoExts.Contains(extension)) return ExistingFileKind.Video;
        if (imageExts.Contains(extension)) return ExistingFileKind.Image;
        if (galleryExts.Contains(extension)) return ExistingFileKind.Gallery;
        if (audioExts.Contains(extension)) return ExistingFileKind.Audio;
        if (textExts.Contains(extension)) return ExistingFileKind.Text;
        return ExistingFileKind.Unknown;
    }

    private static ExistingFileKind ToExistingFileKind(string? fileType) => fileType switch
    {
        "Video" => ExistingFileKind.Video,
        "Image" => ExistingFileKind.Image,
        "Gallery" => ExistingFileKind.Gallery,
        "Audio" => ExistingFileKind.Audio,
        "Text" => ExistingFileKind.Text,
        _ => ExistingFileKind.Unknown,
    };

    public static ScanMediaKind ToMediaKind(ExistingFileKind kind) => kind switch
    {
        ExistingFileKind.Video => ScanMediaKind.Video,
        ExistingFileKind.Image => ScanMediaKind.Image,
        ExistingFileKind.Gallery => ScanMediaKind.Gallery,
        ExistingFileKind.Audio => ScanMediaKind.Audio,
        ExistingFileKind.Text => ScanMediaKind.Text,
        _ => throw new InvalidOperationException("Unsupported scan media type"),
    };

    public static bool NeedsRequestedVideoAsset(
        ExistingFileScanInfo existingFile,
        ScanOperationOptions options,
        IThumbnailService thumbnailService)
    {
        if (existingFile.Kind != ExistingFileKind.Video || !existingFile.MediaEntityId.HasValue)
            return false;

        var videoId = existingFile.MediaEntityId.Value;
        return (options.GenerateCovers && !File.Exists(thumbnailService.GetThumbnailPathForVideo(videoId)))
            || (options.GeneratePreviews && !File.Exists(thumbnailService.GetPreviewPath(videoId)))
            || (options.GenerateSprites
                && (!File.Exists(thumbnailService.GetSpritePath(videoId))
                    || !File.Exists(thumbnailService.GetSpriteVttPath(videoId))));
    }

    private static async Task AddExistingBaseFilesAsync(
        CoveContext db,
        Dictionary<string, ExistingFileScanInfo> index,
        IReadOnlyCollection<DiscoveredFile> files,
        IJobProgress progress,
        ILogger logger,
        CancellationToken ct)
    {
        var storedPaths = files
            .Select(file => file.StoredPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (storedPaths.Length == 0)
            return;

        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("Scan existing-file index: loading {Count} base file paths", storedPaths.Length);

        var chunkIndex = 0;
        foreach (var chunk in storedPaths.Chunk(1000))
        {
            chunkIndex++;
            if (chunkIndex == 1 || chunkIndex % 25 == 0)
            {
                progress.Report(0.10, $"Loading existing file index ({Math.Min(chunkIndex * 1000, storedPaths.Length):N0}/{storedPaths.Length:N0})");
            }

            var rows = await db.Set<BaseFileEntity>()
                .AsNoTracking()
                .Where(file => chunk.Contains(file.Path))
                .Select(file => new
                {
                    file.Path,
                    file.Id,
                    FileType = EF.Property<string>(file, "FileType"),
                    file.Size,
                    file.ModTime,
                })
                .ToListAsync(ct);

            foreach (var row in rows)
            {
                index[row.Path] = new ExistingFileScanInfo(
                    row.Path,
                    row.Id,
                    ToExistingFileKind(row.FileType),
                    row.Size,
                    row.ModTime,
                    false);
            }
        }

        logger.LogDebug(
            "Scan existing-file index: loaded base file paths in {ElapsedMs} ms using {ChunkCount} chunks",
            stopwatch.ElapsedMilliseconds,
            chunkIndex);
    }

    private static async Task AddExistingFilesForExtensionsAsync(
        CoveContext db,
        Dictionary<string, ExistingFileScanInfo> index,
        IReadOnlyCollection<DiscoveredFile> files,
        IReadOnlySet<string> extensions,
        string mediaType,
        Func<CoveContext, Dictionary<string, ExistingFileScanInfo>, string[], CancellationToken, Task> addExistingFiles,
        IJobProgress progress,
        ILogger logger,
        CancellationToken ct)
    {
        var storedPaths = files
            .Where(file => extensions.Contains(file.Extension))
            .Select(file => file.StoredPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (storedPaths.Length == 0)
            return;

        var stopwatch = Stopwatch.StartNew();
        logger.LogDebug("Scan existing-file index: loading {Count} {MediaType} paths", storedPaths.Length, mediaType);

        var chunkIndex = 0;
        foreach (var chunk in storedPaths.Chunk(1000))
        {
            chunkIndex++;
            if (chunkIndex == 1 || chunkIndex % 25 == 0)
            {
                progress.Report(0.10, $"Loading existing {mediaType} index ({Math.Min(chunkIndex * 1000, storedPaths.Length):N0}/{storedPaths.Length:N0})");
            }

            await addExistingFiles(db, index, chunk, ct);
        }

        logger.LogDebug(
            "Scan existing-file index: loaded {MediaType} paths in {ElapsedMs} ms using {ChunkCount} chunks",
            mediaType,
            stopwatch.ElapsedMilliseconds,
            chunkIndex);
    }

    private static async Task AddExistingVideoFilesAsync(CoveContext db, Dictionary<string, ExistingFileScanInfo> index, string[] storedPaths, CancellationToken ct)
    {
        var rows = await db.VideoFiles
            .AsNoTracking()
            .Where(file => storedPaths.Contains(file.Path))
            .Select(file => new ExistingFileScanInfo(
                file.Path,
                file.Id,
                ExistingFileKind.Video,
                file.Size,
                file.ModTime,
                file.Width <= 0 || file.Height <= 0 || file.Duration <= 0,
                file.VideoId))
            .ToListAsync(ct);

        foreach (var row in rows)
            index[row.StoredPath] = row;
    }

    private static async Task AddExistingImageFilesAsync(CoveContext db, Dictionary<string, ExistingFileScanInfo> index, string[] storedPaths, CancellationToken ct)
    {
        var rows = await db.ImageFiles
            .AsNoTracking()
            .Where(file => storedPaths.Contains(file.Path))
            .Select(file => new ExistingFileScanInfo(file.Path, file.Id, ExistingFileKind.Image, file.Size, file.ModTime, false))
            .ToListAsync(ct);

        foreach (var row in rows)
            index[row.StoredPath] = row;
    }

    private static async Task AddExistingGalleryFilesAsync(CoveContext db, Dictionary<string, ExistingFileScanInfo> index, string[] storedPaths, CancellationToken ct)
    {
        var rows = await db.GalleryFiles
            .AsNoTracking()
            .Where(file => storedPaths.Contains(file.Path))
            .Select(file => new ExistingFileScanInfo(file.Path, file.Id, ExistingFileKind.Gallery, file.Size, file.ModTime, false))
            .ToListAsync(ct);

        foreach (var row in rows)
            index[row.StoredPath] = row;
    }

    private static async Task AddExistingAudioFilesAsync(CoveContext db, Dictionary<string, ExistingFileScanInfo> index, string[] storedPaths, CancellationToken ct)
    {
        var rows = await db.AudioFiles
            .AsNoTracking()
            .Where(file => storedPaths.Contains(file.Path))
            .Select(file => new ExistingFileScanInfo(
                file.Path,
                file.Id,
                ExistingFileKind.Audio,
                file.Size,
                file.ModTime,
                file.Duration == 0 && file.AudioCodec == string.Empty))
            .ToListAsync(ct);

        foreach (var row in rows)
            index[row.StoredPath] = row;
    }

    private static async Task AddExistingTextFilesAsync(CoveContext db, Dictionary<string, ExistingFileScanInfo> index, string[] storedPaths, CancellationToken ct)
    {
        var rows = await db.TextFiles
            .AsNoTracking()
            .Where(file => storedPaths.Contains(file.Path))
            .Select(file => new ExistingFileScanInfo(
                file.Path,
                file.Id,
                ExistingFileKind.Text,
                file.Size,
                file.ModTime,
                !file.WordCount.HasValue && (file.ExcerptText == null || file.ExcerptText == string.Empty)))
            .ToListAsync(ct);

        foreach (var row in rows)
            index[row.StoredPath] = row;
    }

    /// <summary>
    /// Create folder-based galleries for folders that contain images but have no gallery yet.
    /// </summary>
}

internal enum ExistingFileKind { Unknown, Video, Image, Gallery, Audio, Text }

internal sealed record ExistingFileScanInfo(
    string StoredPath,
    int Id,
    ExistingFileKind Kind,
    long Size,
    DateTime ModTime,
    bool NeedsMetadataProbe,
    int? MediaEntityId = null);

internal enum ScanFileChangeReason { Unchanged, RescanForced, MetadataProbe, SizeChanged, ModTimeChanged }
