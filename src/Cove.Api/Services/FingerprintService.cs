using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.PixelFormats;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public interface IFingerprintService
{
    Task<string?> ComputeMd5Async(string path, CancellationToken ct = default);
    Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default);
    /// <summary>Perceptual hash and intrinsic size of already-loaded image bytes, for comparing two covers.</summary>
    ImageSignature? ComputeImageSignature(byte[] data);
    Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default);
    Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default);
    Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default);
    string StartGenerateVideoPhashes();
    string StartGenerateImagePhashes();
}

/// <summary>Perceptual hash and intrinsic size of one decoded image.</summary>
/// <param name="Phash">The 64-bit perception hash, as 16 lowercase hex characters.</param>
/// <param name="ByteSize">Size of the encoded bytes the signature was computed from.</param>
public sealed record ImageSignature(string Phash, int Width, int Height, int ByteSize)
{
    public long PixelCount => (long)Width * Height;
}

public class FingerprintService(
    IServiceScopeFactory scopeFactory,
    IJobService jobService,
    CoveConfiguration config,
    FfmpegConcurrencyLimiter ffmpegConcurrency,
    ILogger<FingerprintService> logger) : IFingerprintService
{
    // Sprite grid and frame size, matching Stash's videophash package. These, the 5% offset and the
    // 90% sampling window below define the hash: Cove's pHashes are compared against Stash's and
    // against remote metadata servers, so changing any of them re-hashes the library and breaks
    // those comparisons.
    private const int SpriteFrameSize = 160;
    private const int SpriteColumns = 5;
    private const int SpriteRows = 5;

    public async Task<string?> ComputeMd5Async(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            await using var stream = FileReadRace.TryOpenRead(path, pathWasObserved: true);
            if (stream == null)
                return null;

            var hash = await MD5.HashDataAsync(stream, ct);
            return Convert.ToHexStringLower(hash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A locked, vanished, or permission-denied file must not abort the caller's batch — skip it.
            logger.LogWarning(ex, "Failed computing MD5 for {Path}; skipping", path);
            return null;
        }
    }

    public async Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default)
    {
        return await ComputeSampledBinaryHashAsync(path, ct);
    }

    public async Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        const int maxBytes = 2 * 1024 * 1024;
        var bytes = await ReadFilePrefixAsync(path, maxBytes, ct);
        if (bytes.Length == 0)
            return null;

        var text = Encoding.UTF8.GetString(bytes);
        var weights = new int[64];
        var token = new StringBuilder(64);
        var tokenCount = 0;

        void AddToken()
        {
            if (token.Length < 2)
            {
                token.Clear();
                return;
            }

            var tokenBytes = Encoding.UTF8.GetBytes(token.ToString().ToLowerInvariant());
            var hash = SHA256.HashData(tokenBytes);
            for (var bit = 0; bit < 64; bit++)
            {
                var set = (hash[bit / 8] & (1 << (bit % 8))) != 0;
                weights[bit] += set ? 1 : -1;
            }

            token.Clear();
            tokenCount++;
        }

        foreach (var ch in text)
        {
            ct.ThrowIfCancellationRequested();
            if (char.IsLetterOrDigit(ch))
            {
                if (token.Length < 64)
                    token.Append(ch);
            }
            else
            {
                AddToken();
            }
        }
        AddToken();

        if (tokenCount == 0)
            return await ComputeSampledBinaryHashAsync(path, ct);

        ulong value = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (weights[bit] > 0)
                value |= 1UL << (63 - bit);
        }

        return value.ToString("x16", CultureInfo.InvariantCulture);
    }

    private static async Task<byte[]> ReadFilePrefixAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var stream = FileReadRace.TryOpenRead(path, pathWasObserved: true);
        if (stream == null)
            return [];

        var length = (int)Math.Min(maxBytes, stream.Length);
        var buffer = new byte[length];
        var totalRead = 0;
        while (totalRead < length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(totalRead, length - totalRead), ct);
            if (read == 0)
                break;
            totalRead += read;
        }

        return totalRead == buffer.Length ? buffer : buffer[..totalRead];
    }

    private static async Task<string?> ComputeSampledBinaryHashAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        const int bucketCount = 64;
        const int sampleSize = 4096;
        await using var stream = FileReadRace.TryOpenRead(path, pathWasObserved: true);
        if (stream == null)
            return null;

        if (stream.Length == 0)
            return null;

        var buffer = new byte[Math.Min(sampleSize, (int)Math.Min(stream.Length, sampleSize))];
        var buckets = new double[bucketCount];
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            ct.ThrowIfCancellationRequested();
            var ratio = bucketCount == 1 ? 0d : bucket / (double)(bucketCount - 1);
            var centerOffset = (long)Math.Round((stream.Length - 1) * ratio);
            var offset = Math.Clamp(centerOffset - buffer.Length / 2, 0, Math.Max(0, stream.Length - buffer.Length));
            stream.Seek(offset, SeekOrigin.Begin);
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);
            if (read == 0)
            {
                buckets[bucket] = 0;
                continue;
            }

            double total = 0;
            for (var i = 0; i < read; i++)
                total += buffer[i];
            buckets[bucket] = total / read;
        }

        var median = GoImageHash.MedianQuickSelect(buckets);
        ulong value = 0;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (buckets[bucket] > median)
                value |= 1UL << (63 - bucket);
        }

        return value.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The perceptual hash and the intrinsic size of encoded image bytes.
    /// </summary>
    public ImageSignature? ComputeImageSignature(byte[] data)
    {
        if (data == null || data.Length == 0)
            return null;

        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var result = StashImagePhash.Compute(stream);
            return new ImageSignature(GoImageHash.Format(result.Hash), result.Width, result.Height, data.Length);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute image signature for {ByteCount} bytes", data.Length);
            return null;
        }
    }

    public async Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            // Decoding and resizing are CPU-bound; read the file up front so the decoder can seek.
            var data = await File.ReadAllBytesAsync(path, ct);
            using var stream = new MemoryStream(data, writable: false);
            return GoImageHash.Format(StashImagePhash.Compute(stream).Hash);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to compute image phash for {Path}", path);
            return null;
        }
    }

    public async Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("Skipping pHash for {Path} — file does not exist", path);
            return null;
        }

        // Stash computes the sample times from the duration rounded to hundredths of a second
        // (ffprobe.go), so the same frames are sampled. A duration that rounds to zero has no frames.
        var rounded = Math.Round(duration * 100, MidpointRounding.AwayFromZero) / 100;
        if (!(rounded > 0))
        {
            logger.LogWarning("Skipping pHash for {Path} — duration is {Duration}s (invalid)", path, duration);
            return null;
        }
        duration = rounded;

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath == null)
        {
            logger.LogError("FFmpeg not found in PATH or configured path; cannot compute pHash for {Path}", path);
            return null;
        }

        // These timestamps define the hash. Changing the count, the 5% offset or the 90% window
        // changes every hash this method produces and silently invalidates the stored ones, so
        // they are fixed regardless of how the frames are extracted.
        var chunkCount = SpriteColumns * SpriteRows; // 25
        var offset = 0.05 * duration;
        var stepSize = (0.9 * duration) / chunkCount;
        var timestamps = new double[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            timestamps[i] = offset + i * stepSize;

        logger.LogTrace("pHash extraction for {Path}: ffmpeg={FfmpegPath}, duration={Duration:F1}s", path, ffmpegPath, duration);

        var extracted = await VideoFrameBatchExtractor.ExtractAsync(
            ffmpegPath, path, timestamps, SpriteFrameSize, ffmpegConcurrency, logger, ct, exactFrames: true);

        if (extracted == null)
        {
            logger.LogWarning("Video pHash extraction failed for {Path} - no frames could be read", path);
            return null;
        }

        try
        {
            // Unlike a sprite, a pHash cannot tolerate substituted frames: filling a gap from a
            // neighbour would produce a hash that no other extraction of the same file reproduces.
            // A missing frame means no hash.
            var missing = 0;
            for (var i = 0; i < extracted.Length; i++)
                if (extracted[i] is null) missing++;

            if (missing > 0)
            {
                logger.LogWarning(
                    "Video pHash extraction failed for {Path} - {Missing} of {Total} sample frames could not be decoded",
                    path, missing, extracted.Length);
                return null;
            }

            return BuildSpritePhash(extracted!);
        }
        finally
        {
            DisposeFrames(extracted);
        }
    }

    /// <summary>
    /// Pastes the frames into Stash's 5×5 montage and hashes it with Stash's resize. Each frame is
    /// copied whole, in order and clipped only to the montage, as <c>imaging.Paste</c> does, rather than
    /// blended. Its alpha is dropped: ffmpeg writes a source with an alpha channel as a 32-bit BMP with
    /// a BITMAPINFOHEADER, whose alpha byte Go's BMP decoder ignores.
    /// </summary>
    private static string BuildSpritePhash(Image<Rgba32>[] frames)
    {
        var cellWidth = frames[0].Width;
        var cellHeight = frames[0].Height;
        using var sprite = new Image<Rgba32>(cellWidth * SpriteColumns, cellHeight * SpriteRows);
        var canvas = sprite.Frames.RootFrame;
        for (var index = 0; index < frames.Length; index++)
        {
            var x = cellWidth * (index % SpriteColumns);
            var y = cellHeight * (index / SpriteRows);
            var frame = frames[index].Frames.RootFrame;
            var width = Math.Min(frame.Width, canvas.Width - x);
            for (var row = 0; row < frame.Height && y + row < canvas.Height; row++)
            {
                var source = frame.DangerousGetPixelRowMemory(row).Span[..width];
                var target = canvas.DangerousGetPixelRowMemory(y + row).Span.Slice(x, width);
                for (var column = 0; column < width; column++)
                    target[column] = source[column] with { A = 255 };
            }
        }
        return GoImageHash.Format(StashImagePhash.ComputeNrgba(sprite));
    }

    private static void DisposeFrames(Image<Rgba32>?[] frames)
    {
        foreach (var f in frames) { try { f?.Dispose(); } catch { } }
    }

    /// <summary>
    /// Resolves the max degree of parallelism from config.
    /// -1 means use all processors; 0 or 1 means single-threaded; >1 means that many threads.
    /// </summary>
    private int ResolveMaxParallelism()
    {
        var configured = config.MaxParallelTasks;
        if (configured == -1) return Environment.ProcessorCount;
        if (configured <= 0) return 1;
        return configured;
    }

    public string StartGenerateVideoPhashes()
    {
        return jobService.Enqueue("generate_video_phashes", "Generating video pHashes", async (progress, ct) =>
        {
            logger.LogInformation("Video pHash generation job started");
            List<(int FileId, string Path, double Duration)> workItems;

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                var totalVideos = await db.VideoFiles.CountAsync(ct);

                // Get IDs of files that already have a phash
                var filesWithPhashIds = await db.FileFingerprints
                    .Where(fp => fp.Type == "phash")
                    .Select(fp => fp.FileId)
                    .Distinct()
                    .ToHashSetAsync(ct);

                logger.LogInformation("Video pHash check: {Total} video files total, {HasPhash} already have a pHash",
                    totalVideos, filesWithPhashIds.Count);

                // Only load files that need phash generation
                var pendingVideoFiles = await db.VideoFiles
                    .Where(file => !filesWithPhashIds.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, file.Path, file.Duration })
                    .ToListAsync(ct);

                workItems = pendingVideoFiles.Select(file => (file.Id, FilesystemPaths.ToNativePath(file.Path), file.Duration)).ToList();
            }

            if (workItems.Count == 0)
            {
                progress.Report(1.0, "All videos already have pHashes");
                logger.LogInformation("Video pHash generation: nothing to do — all video files already have a pHash");
                return;
            }

            var parallelism = ResolveMaxParallelism();
            logger.LogInformation("Generating pHashes for {Count} video files (parallelism={Parallelism})",
                workItems.Count, parallelism);

            var completed = 0;
            var failed = 0;
            // Coarse progress milestone (~ every 10%) for diagnostic progress without
            // adding per-file noise to the default Information log.
            var milestoneEvery = Math.Max(1, workItems.Count / 10);

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
            {
                try
                {
                    logger.LogTrace("Computing pHash for file {FileId}: {Path} (duration={Duration:F1}s)",
                        item.FileId, item.Path, item.Duration);

                    var phash = await ComputeVideoPhashAsync(item.Path, item.Duration, token);

                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        logger.LogTrace("Computed pHash for file {FileId}: {Phash}", item.FileId, phash);
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == item.FileId && fp.Type == "phash", token);
                        if (existing == null)
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint { FileId = item.FileId, Type = "phash", Value = phash });
                            await innerDb.SaveChangesAsync(token);
                            logger.LogTrace("Saved pHash for file {FileId}", item.FileId);
                        }
                    }
                    else
                    {
                        Interlocked.Increment(ref failed);
                        logger.LogTrace("No pHash produced for file {FileId}: {Path}", item.FileId, item.Path);
                    }

                    var done = Interlocked.Increment(ref completed);
                    if (done % milestoneEvery == 0 || done == workItems.Count)
                        logger.LogDebug("Video pHash progress: {Done}/{Total} files processed", done, workItems.Count);
                    progress.Report((double)done / workItems.Count, $"({done}/{workItems.Count}) {Path.GetFileName(item.Path)}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Job was cancelled mid-item. Swallow quietly so Parallel.ForEachAsync ends the loop via
                    // its own CancellationToken (raising a single OperationCanceledException) rather than
                    // surfacing this as an unobserved per-worker exception. The inner scope/DbContext above is
                    // disposed by its `using` even on this path.
                    logger.LogTrace("Video pHash computation cancelled for file {FileId}", item.FileId);
                }
            });

            logger.LogInformation("Video pHash generation finished: {Total} files processed, {Failed} without a pHash",
                workItems.Count, failed);
        });
    }

    public string StartGenerateImagePhashes()
    {
        return jobService.Enqueue("generate_image_phashes", "Generating image pHashes", async (progress, ct) =>
        {
            List<(int FileId, string Path)> workItems;

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                // Get IDs of files that already have a phash
                var filesWithPhash = await db.FileFingerprints
                    .Where(fp => fp.Type == "phash")
                    .Select(fp => fp.FileId)
                    .Distinct()
                    .ToHashSetAsync(ct);

                var pendingImageFiles = await db.ImageFiles
                    .Where(file => !filesWithPhash.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, file.Path })
                    .ToListAsync(ct);

                workItems = pendingImageFiles.Select(file => (file.Id, FilesystemPaths.ToNativePath(file.Path))).ToList();
            }

            if (workItems.Count == 0)
                return;

            var parallelism = ResolveMaxParallelism();
            logger.LogInformation("Generating image pHashes for {Count} files (parallelism={Parallelism})", workItems.Count, parallelism);
            var completed = 0;
            var milestoneEvery = Math.Max(1, workItems.Count / 10);

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
            {
                try
                {
                    var phash = await ComputeImagePhashAsync(item.Path, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == item.FileId && fp.Type == "phash", token);
                        if (existing == null)
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint { FileId = item.FileId, Type = "phash", Value = phash });
                            await innerDb.SaveChangesAsync(token);
                        }
                    }

                    var done = Interlocked.Increment(ref completed);
                    if (done % milestoneEvery == 0 || done == workItems.Count)
                        logger.LogDebug("Image pHash progress: {Done}/{Total} files processed", done, workItems.Count);
                    progress.Report((double)done / workItems.Count, $"({done}/{workItems.Count}) {Path.GetFileName(item.Path)}");
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    // Job cancelled mid-item: swallow so the loop ends via its own CancellationToken rather
                    // than as an unobserved per-worker exception. The inner scope/DbContext is disposed by
                    // its `using` even on this path.
                    logger.LogTrace("Image pHash computation cancelled for file {FileId}", item.FileId);
                }
            });

            logger.LogInformation("Finished generating image pHashes for {Count} files", workItems.Count);
        });
    }

    private async Task EnsureVideoPhashAsync(CoveContext db, VideoFile file, CancellationToken ct)
    {
        if (file.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
            return;

        var path = ResolveFilePath(file);
        if (path == null)
            return;

        var oshash = file.Fingerprints.FirstOrDefault(fp => fp.Type == "oshash")?.Value;
        if (!string.IsNullOrWhiteSpace(oshash))
        {
            var reused = await FindExistingPhashAsync(db, file.Id, "oshash", oshash, ct);
            if (!string.IsNullOrWhiteSpace(reused))
            {
                AddFingerprint(file, "phash", reused);
                return;
            }
        }

        var phash = await ComputeVideoPhashAsync(path, file.Duration, ct);
        if (!string.IsNullOrWhiteSpace(phash))
            AddFingerprint(file, "phash", phash);
    }

    private async Task EnsureImagePhashAsync(CoveContext db, ImageFile file, CancellationToken ct)
    {
        if (file.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
            return;

        var path = ResolveFilePath(file);
        if (path == null)
            return;

        var md5 = file.Fingerprints.FirstOrDefault(fp => fp.Type == "md5")?.Value;
        if (string.IsNullOrWhiteSpace(md5))
        {
            md5 = await ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                AddFingerprint(file, "md5", md5);
        }

        if (!string.IsNullOrWhiteSpace(md5))
        {
            var reused = await FindExistingPhashAsync(db, file.Id, "md5", md5, ct);
            if (!string.IsNullOrWhiteSpace(reused))
            {
                AddFingerprint(file, "phash", reused);
                return;
            }
        }

        var phash = await ComputeImagePhashAsync(path, ct);
        if (!string.IsNullOrWhiteSpace(phash))
            AddFingerprint(file, "phash", phash);
    }

    private static async Task<string?> FindExistingPhashAsync(CoveContext db, int fileId, string sourceType, string sourceValue, CancellationToken ct)
    {
        return await db.FileFingerprints
            .Where(fp => fp.Type == sourceType && fp.Value == sourceValue && fp.FileId != fileId)
            .Join(
                db.FileFingerprints.Where(fp => fp.Type == "phash"),
                source => source.FileId,
                phash => phash.FileId,
                (_, phash) => phash.Value)
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);
    }

    private static void AddFingerprint(BaseFileEntity file, string type, string value)
    {
        if (file.Fingerprints.Any(fp => fp.Type == type && string.Equals(fp.Value, value, StringComparison.OrdinalIgnoreCase)))
            return;

        file.Fingerprints.Add(new FileFingerprint
        {
            Type = type,
            Value = value,
            FileId = file.Id,
        });
    }

    private static string? ResolveFilePath(BaseFileEntity file)
    {
        var path = FilesystemPaths.ToNativePath(file.Path);

        return File.Exists(path) ? path : null;
    }

    private string? FindFfmpeg() => FfmpegExecutableLocator.FindFfmpeg(config);
}
