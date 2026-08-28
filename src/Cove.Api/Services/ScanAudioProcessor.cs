using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

internal sealed class ScanAudioProcessor(
    CoveConfiguration config,
    IFingerprintService fingerprintService,
    IMediaProbeService mediaProbeService,
    ScanFolderResolver folderResolver,
    ScanFileIdentityService fileIdentity,
    ILogger logger)
{
    internal async Task<(Audio Entity, bool Relinked, bool Moved)> ProcessAsync(
        CoveContext db,
        string path,
        int? audioId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool knownNew = false,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null,
        string? mediaProbeJson = null)
    {
        var stat = fileStat ?? ScanPath.GetFileStat(path);
        var dirPath = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await folderResolver.EnsureAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = knownNew
            ? null
            : await db.AudioFiles
                .Include(file => file.Fingerprints)
                .Include(file => file.Audio)
                .ThenInclude(audio => audio!.Files)
                .FirstOrDefaultAsync(file => file.ParentFolderId == folderId && file.Basename == basename, ct);

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;
            existing.Path = BaseFileEntity.ComputePath(dirPath, basename);

            var existingAudio = existing.Audio ?? throw new InvalidOperationException($"Audio file {path} is not attached to an audio entity");
            await EnrichAudioFileAsync(existingAudio, existing, path, ct, mediaProbeJson, moveIndex);
            // A re-encode invalidates the stored phash; blank it so the generation phase recomputes it.
            if (contentChanged && scanOptions?.GenerateAudioPhashes == true)
                ScanFileIdentityService.BlankFingerprint(existing, "phash");
            RefreshAudioSummary(existingAudio);
            return (existingAudio, false, false);
        }

        // Content already in the library: re-link a moved audio file, or attach a duplicate to its entity.
        if (!audioId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await fileIdentity.MatchExistingAsync(db.AudioFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.AudioId is int matchedAudioId)
            {
                var parentAudio = await db.Audios.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == matchedAudioId, ct);
                if (parentAudio != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved audio file to {NewPath} (previously {OldPath})", path, match.Path);
                        RefreshAudioSummary(parentAudio);
                        return (parentAudio, true, true);
                    }

                    var duplicateFile = new AudioFile
                    {
                        Basename = basename,
                        ParentFolderId = folderId,
                        Path = BaseFileEntity.ComputePath(dirPath, basename),
                        Size = stat.Size,
                        ModTime = stat.ModTime,
                        Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    };
                    parentAudio.Files.Add(duplicateFile);
                    await EnrichAudioFileAsync(parentAudio, duplicateFile, path, ct, mediaProbeJson, moveIndex);
                    RefreshAudioSummary(parentAudio);
                    logger.LogTrace("Attached duplicate audio file {NewPath} to existing audio {AudioId}", path, matchedAudioId);
                    return (parentAudio, true, false);
                }
            }
        }

        var audioFile = new AudioFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Path = BaseFileEntity.ComputePath(dirPath, basename),
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
        };

        Audio audio;
        if (audioId.HasValue)
        {
            audio = await db.Audios
                .Include(item => item.Files)
                .FirstOrDefaultAsync(item => item.Id == audioId.Value, ct)
                ?? throw new InvalidOperationException($"Audio {audioId.Value} was not found for downloaded media import");

            audio.Files.Add(audioFile);
        }
        else
        {
            audio = new Audio
            {
                Title = Path.GetFileNameWithoutExtension(path),
                Files = [audioFile],
            };

            db.Audios.Add(audio);
        }

        await EnrichAudioFileAsync(audio, audioFile, path, ct, mediaProbeJson, moveIndex);
        RefreshAudioSummary(audio);

        logger.LogTrace("Added audio for {Path}", path);
        return (audio, false, false);
    }


    private async Task EnrichAudioFileAsync(
        Audio audio,
        AudioFile audioFile,
        string path,
        CancellationToken ct,
        string? mediaProbeJson = null,
        MoveDetectionIndex? moveIndex = null)
    {
        var metadata = mediaProbeJson == null
            ? await ProbeAudioAsync(audioFile, path, ct)
            : ApplyAudioProbeMetadata(audioFile, mediaProbeJson);
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrWhiteSpace(audio.Title) || string.Equals(audio.Title, fallbackTitle, StringComparison.OrdinalIgnoreCase))
            audio.Title = metadata.Title ?? fallbackTitle;

        // Always-on identity fingerprint so a later scan can recognise this file if it moves/renames.
        var oshash = await ScanFileIdentityService.ComputeOshashAsync(path, moveIndex, ct);
        if (oshash != null)
            ScanFileIdentityService.UpsertFingerprint(audioFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                ScanFileIdentityService.UpsertFingerprint(audioFile, "md5", md5);
            }
        }
    }


    private async Task<AudioProbeMetadata> ProbeAudioAsync(AudioFile audioFile, string path, CancellationToken ct)
    {
        try
        {
            var result = await mediaProbeService.ProbeAsync(path, ct);
            if (result.Status != MediaProbeStatus.Success || string.IsNullOrWhiteSpace(result.Json))
            {
                logger.LogDebug("FFprobe did not return audio metadata for {Path}: {Reason}", path, result.Reason);
                return new AudioProbeMetadata(null);
            }

            return ApplyAudioProbeMetadata(audioFile, result.Json);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogDebug(ex, "FFprobe failed for audio {Path}", path);
            return new AudioProbeMetadata(null);
        }
    }

    private static AudioProbeMetadata ApplyAudioProbeMetadata(AudioFile audioFile, string json)
    {
        audioFile.HasVideoTrack = false;
        audioFile.AudioCodec = string.Empty;
        audioFile.SampleRate = null;
        audioFile.Channels = null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? title = null;
        if (root.TryGetProperty("format", out var format))
        {
            if (format.TryGetProperty("duration", out var dur))
            {
                if (double.TryParse(dur.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration))
                    audioFile.Duration = duration;
            }
            if (format.TryGetProperty("bit_rate", out var br))
            {
                if (long.TryParse(br.GetString(), out var bitrate))
                    audioFile.BitRate = bitrate;
            }
            if (format.TryGetProperty("tags", out var tags)
                && tags.TryGetProperty("title", out var titleProp))
                title = titleProp.GetString();
        }

        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var codecType = stream.TryGetProperty("codec_type", out var typeProp) ? typeProp.GetString() : null;
                if (codecType == "audio" && string.IsNullOrWhiteSpace(audioFile.AudioCodec))
                {
                    if (stream.TryGetProperty("codec_name", out var codecName))
                        audioFile.AudioCodec = codecName.GetString() ?? string.Empty;
                    if (stream.TryGetProperty("sample_rate", out var sampleRateProp)
                        && int.TryParse(sampleRateProp.GetString(), out var sampleRate))
                        audioFile.SampleRate = sampleRate;
                    if (stream.TryGetProperty("channels", out var channelsProp) && channelsProp.TryGetInt32(out var channels))
                        audioFile.Channels = channels;
                    if (audioFile.BitRate == 0
                        && stream.TryGetProperty("bit_rate", out var streamBitrateProp)
                        && long.TryParse(streamBitrateProp.GetString(), out var streamBitrate))
                        audioFile.BitRate = streamBitrate;
                }
                else if (codecType == "video")
                {
                    // Audio container album art is a "video" stream flagged attached_pic; don't treat it as a real video track.
                    var isAttachedPic = stream.TryGetProperty("disposition", out var disposition)
                        && disposition.TryGetProperty("attached_pic", out var attachedPic)
                        && attachedPic.TryGetInt32(out var attachedPicFlag)
                        && attachedPicFlag == 1;
                    var streamCodec = stream.TryGetProperty("codec_name", out var videoCodecName)
                        ? videoCodecName.GetString()
                        : null;
                    var isImageCodec = streamCodec is "mjpeg" or "png" or "bmp" or "gif" or "webp" or "tiff" or "jpeg";
                    if (!isAttachedPic && !isImageCodec)
                        audioFile.HasVideoTrack = true;
                }
            }
        }

        return new AudioProbeMetadata(title);
    }

    private static void RefreshAudioSummary(Audio audio)
    {
        var files = audio.Files.ToList();
        audio.FileCount = files.Count;
        if (files.Count == 0)
        {
            audio.MaxDuration = 0;
            audio.MaxBitRate = 0;
            audio.MaxFileSize = 0;
            audio.MaxFileModTime = null;
            audio.MinPath = null;
            audio.MaxPath = null;
            audio.FileSearchText = null;
            audio.HasVideoFiles = false;
            return;
        }

        var paths = files
            .Select(file => string.IsNullOrWhiteSpace(file.Path) ? BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename) : file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        audio.MaxDuration = files.Max(file => file.Duration);
        audio.MaxBitRate = files.Max(file => file.BitRate);
        audio.MaxFileSize = files.Max(file => file.Size);
        audio.MaxFileModTime = files.Max(file => (DateTime?)file.ModTime);
        audio.MinPath = paths.FirstOrDefault();
        audio.MaxPath = paths.LastOrDefault();
        audio.FileSearchText = ScanMediaSummary.BuildFileSearchText(paths);
        audio.HasVideoFiles = files.Any(file => file.HasVideoTrack);
    }


    private sealed record AudioProbeMetadata(string? Title);
}
