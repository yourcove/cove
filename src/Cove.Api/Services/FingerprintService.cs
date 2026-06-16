using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public interface IFingerprintService
{
    Task<string?> ComputeMd5Async(string path, CancellationToken ct = default);
    Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default);
    Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default);
    Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default);
    Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default);
    string StartGenerateVideoPhashes();
    string StartGenerateImagePhashes();
}

public class FingerprintService(
    IServiceScopeFactory scopeFactory,
    IJobService jobService,
    CoveConfiguration config,
    ILogger<FingerprintService> logger) : IFingerprintService
{
    // Matches goimagehash PerceptionHash: 64×64 resize, 8×8 DCT low-frequency block
    private const int DctImageSize = 64;
    private const int DctLowFreqSize = 8;

    // Sprite generation constants matching Go's videophash package
    private const int SpriteFrameSize = 160;
    private const int SpriteColumns = 5;
    private const int SpriteRows = 5;

    public async Task<string?> ComputeMd5Async(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await MD5.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
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
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
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

        var median = MedianQuickSelect(buckets);
        ulong value = 0;
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (buckets[bucket] > median)
                value |= 1UL << (63 - bucket);
        }

        return value.ToString("x16", CultureInfo.InvariantCulture);
    }

    public async Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var image = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(path, ct);
            return ComputePerceptionHash(image);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to compute image phash for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Computes a perceptual hash matching Go's goimagehash.PerceptionHash:
    /// 1. Resize to 64×64 using bilinear interpolation
    /// 2. Convert to grayscale using ITU-R BT.601 luminance (0.299R + 0.587G + 0.114B)
    /// 3. Apply 2D DCT (Lee 1984 algorithm, no normalization)
    /// 4. Extract top-left 8×8 block (64 coefficients)
    /// 5. Compute median threshold
    /// 6. Set bits MSB-first where coefficient > median
    /// </summary>
    private static string ComputePerceptionHash(Image<Rgba32> image)
    {
        // Step 1: Resize to 64×64 (Go uses nfnt/resize Bilinear; closest match in ImageSharp is Triangle/Bilinear)
        image.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new SixLabors.ImageSharp.Size(DctImageSize, DctImageSize),
            Sampler = KnownResamplers.Triangle, // Bilinear/Triangle resampler
            Mode = ResizeMode.Stretch,
        }));

        // Step 2: Convert to grayscale using Go's luminance formula
        // Go: lum = 0.299*(r/257) + 0.587*(g/257) + 0.114*(b/256)
        var pixels = new double[DctImageSize * DctImageSize];
        for (var y = 0; y < DctImageSize; y++)
        {
            for (var x = 0; x < DctImageSize; x++)
            {
                var px = image[x, y];
                // Go divides 16-bit RGBA values (0-65535) by 257 for R,G and 256 for B.
                // ImageSharp Rgba32 gives 8-bit values (0-255), which is R*257/257 = R.
                // So we can use the 8-bit values directly since they represent the same ratio.
                pixels[y * DctImageSize + x] = 0.299 * px.R + 0.587 * px.G + 0.114 * px.B;
            }
        }

        // Step 3: Apply 2D DCT (Lee 1984, matching goimagehash DCT2DFast64)
        Dct2DInPlace64(pixels);

        // Step 4: Extract top-left 8×8 block
        var flattened = new double[DctLowFreqSize * DctLowFreqSize];
        for (var i = 0; i < DctLowFreqSize; i++)
        {
            for (var j = 0; j < DctLowFreqSize; j++)
            {
                flattened[DctLowFreqSize * i + j] = pixels[i * DctImageSize + j];
            }
        }

        // Step 5: Compute median
        var median = MedianQuickSelect(flattened);

        // Step 6: Set bits MSB-first (matching Go's leftShiftSet(64 - idx - 1))
        ulong hash = 0;
        for (var idx = 0; idx < flattened.Length; idx++)
        {
            if (flattened[idx] > median)
                hash |= 1UL << (63 - idx);
        }

        // Format as hex without leading zeros (Go uses fmt.Sprintf("%x", hash))
        return hash.ToString("x", CultureInfo.InvariantCulture);
    }

    public async Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default)
    {
        if (!File.Exists(path))
        {
            logger.LogWarning("Skipping pHash for {Path} — file does not exist", path);
            return null;
        }

        if (duration <= 0)
        {
            logger.LogWarning("Skipping pHash for {Path} — duration is {Duration}s (invalid)", path, duration);
            return null;
        }

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath == null)
        {
            logger.LogError("FFmpeg not found in PATH or configured path; cannot compute pHash for {Path}", path);
            return null;
        }

        // In-process extraction is opt-in via the "managed" frame-extraction mode; otherwise use
        // the crash-isolated ffmpeg CLI path below.
        var useInProcess = string.Equals(config.FrameExtractionMode, "managed", StringComparison.OrdinalIgnoreCase);
        if (useInProcess)
            FfmpegInProcess.EnsureInitialized(ffmpegPath, config.EnableFfmpegHwAccel);
        logger.LogDebug("pHash FFmpeg setup: path={FfmpegPath}, managed={Managed}, inProcessAvailable={IsAvailable}, duration={Duration:F1}s, target={Path}",
            ffmpegPath, useInProcess, FfmpegInProcess.IsAvailable, duration, path);

        var chunkCount = SpriteColumns * SpriteRows; // 25
        var offset = 0.05 * duration;
        var stepSize = (0.9 * duration) / chunkCount;
        var timestamps = new double[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            timestamps[i] = offset + i * stepSize;

        if (useInProcess && FfmpegInProcess.IsAvailable)
        {
            // Fast path: in-process frame extraction (seeks directly, no process spawning).
            logger.LogDebug("Attempting in-process pHash extraction for {Path}", path);
            try
            {
                var frames = FfmpegInProcess.ExtractFrames(path, timestamps, SpriteFrameSize, threadCount: 1, ct);
                if (frames != null)
                {
                    logger.LogDebug("In-process pHash extraction succeeded for {Path}", path);
                    try
                    {
                        return BuildSpritePhash(frames);
                    }
                    finally
                    {
                        foreach (var f in frames) f?.Dispose();
                    }
                }

                logger.LogWarning("In-process pHash frame extraction returned null for {Path}, falling back to process spawn", path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "In-process FFmpeg failed for {Path}, falling back to process spawn", path);
            }
        }
        else
        {
            logger.LogDebug("Using ffmpeg CLI for pHash extraction (managed={Managed}, available={Available}) for {Path}",
                useInProcess, FfmpegInProcess.IsAvailable, path);
        }

        var spritePhash = await TryComputeVideoPhashViaSpriteAsync(ffmpegPath, path, duration, ct);
        if (!string.IsNullOrWhiteSpace(spritePhash))
            return spritePhash;

        logger.LogDebug("Single-process sprite extraction failed for {Path}; falling back to per-frame process extraction", path);

        // Final fallback path: spawn ffmpeg once per timestamp and extract a single frame each time.
        return await ComputeVideoPhashViaProcessAsync(ffmpegPath, path, timestamps, ct);
    }

    private string? BuildSpritePhash(Image<Rgba32>[] frames)
    {
        var frameWidth = frames[0].Width;
        var frameHeight = frames[0].Height;
        using var sprite = new Image<Rgba32>(frameWidth * SpriteColumns, frameHeight * SpriteRows);
        for (var index = 0; index < frames.Length; index++)
        {
            var x = frameWidth * (index % SpriteColumns);
            var y = frameHeight * (int)Math.Floor((double)index / SpriteRows);
            sprite.Mutate(ctx => ctx.DrawImage(frames[index], new SixLabors.ImageSharp.Point(x, y), 1f));
        }
        return ComputePerceptionHash(sprite);
    }

    /// <summary>
    /// Single-process fallback: builds a tiled sprite with one ffmpeg invocation and hashes
    /// that image directly. This is much faster than spawning ffmpeg once per timestamp and
    /// serves as the primary cross-platform fallback when AutoGen is unavailable.
    /// </summary>
    private async Task<string?> TryComputeVideoPhashViaSpriteAsync(
        string ffmpegPath,
        string videoPath,
        double duration,
        CancellationToken ct)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cove_phash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var spritePath = Path.Combine(tmpDir, "sprite.jpg");
        try
        {
            var offset = Math.Max(duration * 0.05d, 0d);
            var sampleWindow = Math.Max(duration * 0.9d, 0.001d);
            var step = sampleWindow / (SpriteColumns * SpriteRows);
            var offsetText = offset.ToString("0.########", CultureInfo.InvariantCulture);
            var sampleWindowText = sampleWindow.ToString("0.########", CultureInfo.InvariantCulture);
            var stepText = step.ToString("0.########", CultureInfo.InvariantCulture);
            var decodeArgs = GetFfmpegDecodeArgs();
            var filter = $"select='if(isnan(prev_selected_t),1,gte(t-prev_selected_t,{stepText}))',scale={SpriteFrameSize}:-2,tile={SpriteColumns}x{SpriteRows}:margin=0:padding=0";
            var args = $"{decodeArgs} -v error -fflags +discardcorrupt -err_detect ignore_err -y -ss {offsetText} -t {sampleWindowText} -i \"{videoPath}\" -vf \"{filter}\" -frames:v 1 -q:v 3 -f image2 \"{spritePath}\"";
            var timeout = TimeSpan.FromSeconds(Math.Clamp(duration / 2d, 45d, 300d));

            logger.LogDebug("Attempting single-process sprite extraction for {Path}", videoPath);
            if (!await TryRunFfmpegAsync(ffmpegPath, args, timeout, ct) || !File.Exists(spritePath))
                return null;

            using var sprite = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(spritePath, ct);
            return ComputePerceptionHash(sprite);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Single-process sprite extraction failed for {Path}", videoPath);
            return null;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Process-based fallback: spawns ffmpeg (the CLI binary) once per timestamp to extract
    /// a single scaled frame, then composes the sprite and computes the phash.
    /// Slower than in-process but works on any platform regardless of shared library availability.
    /// </summary>
    private async Task<string?> ComputeVideoPhashViaProcessAsync(
        string ffmpegPath, string videoPath, double[] timestamps, CancellationToken ct)
    {
        var frames = await FfmpegProcessFrameExtractor.ExtractFramesAsync(
            ffmpegPath,
            videoPath,
            timestamps,
            SpriteFrameSize,
            logger,
            ct);

        if (frames == null)
            return null;

        try
        {
            return BuildSpritePhash(frames);
        }
        finally
        {
            DisposeFrames(frames);
        }
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
                    .Include(file => file.ParentFolder)
                    .Where(file => !filesWithPhashIds.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, Path = file.ParentFolder != null ? file.ParentFolder.Path + System.IO.Path.DirectorySeparatorChar + file.Basename : file.Basename, file.Duration })
                    .ToListAsync(ct);

                workItems = pendingVideoFiles.Select(file => (file.Id, file.Path, file.Duration)).ToList();
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
            // Coarse progress milestone (~ every 10%) so the default Info log shows job progress.
            var milestoneEvery = Math.Max(1, workItems.Count / 10);

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
            {
                logger.LogDebug("Computing pHash for file {FileId}: {Path} (duration={Duration:F1}s)",
                    item.FileId, item.Path, item.Duration);

                var phash = await ComputeVideoPhashAsync(item.Path, item.Duration, token);

                if (!string.IsNullOrWhiteSpace(phash))
                {
                    logger.LogDebug("Computed pHash for file {FileId}: {Phash}", item.FileId, phash);
                    using var innerScope = scopeFactory.CreateScope();
                    var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                    var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == item.FileId && fp.Type == "phash", token);
                    if (existing == null)
                    {
                        innerDb.FileFingerprints.Add(new FileFingerprint { FileId = item.FileId, Type = "phash", Value = phash });
                        await innerDb.SaveChangesAsync(token);
                        logger.LogDebug("Saved pHash for file {FileId}", item.FileId);
                    }
                }
                else
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning("No pHash produced for file {FileId}: {Path}", item.FileId, item.Path);
                }

                var done = Interlocked.Increment(ref completed);
                if (done % milestoneEvery == 0 || done == workItems.Count)
                    logger.LogInformation("Video pHash progress: {Done}/{Total} files processed", done, workItems.Count);
                progress.Report((double)done / workItems.Count, $"({done}/{workItems.Count}) {Path.GetFileName(item.Path)}");
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
                    .Include(file => file.ParentFolder)
                    .Where(file => !filesWithPhash.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, Path = file.ParentFolder != null ? file.ParentFolder.Path + System.IO.Path.DirectorySeparatorChar + file.Basename : file.Basename })
                    .ToListAsync(ct);

                workItems = pendingImageFiles.Select(file => (file.Id, file.Path)).ToList();
            }

            if (workItems.Count == 0)
                return;

            var parallelism = ResolveMaxParallelism();
            logger.LogInformation("Generating image pHashes for {Count} files (parallelism={Parallelism})", workItems.Count, parallelism);
            var completed = 0;
            var milestoneEvery = Math.Max(1, workItems.Count / 10);

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, async (item, token) =>
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
                    logger.LogInformation("Image pHash progress: {Done}/{Total} files processed", done, workItems.Count);
                progress.Report((double)done / workItems.Count, $"({done}/{workItems.Count}) {Path.GetFileName(item.Path)}");
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
        var path = file.ParentFolder != null
            ? Path.Combine(file.ParentFolder.Path, file.Basename)
            : file.Basename;

        return File.Exists(path) ? path : null;
    }

    private string? FindFfmpeg()
    {
        if (!string.IsNullOrWhiteSpace(config.FfmpegPath) && File.Exists(config.FfmpegPath))
            return config.FfmpegPath;

        var pathDirectories = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? [];
        foreach (var directory in pathDirectories)
        {
            var ffmpegPath = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(ffmpegPath))
                return ffmpegPath;
        }

        return null;
    }

    private string GetFfmpegDecodeArgs()
    {
        // These extraction pipelines use software filters (select/scale/tile/image encode),
        // so implicit hwaccel adds costly hwdownload/format bridging and can be slower than CPU.
        if (!string.IsNullOrWhiteSpace(config.LiveTranscodeInputArgs))
            return config.LiveTranscodeInputArgs;

        if (!string.IsNullOrWhiteSpace(config.TranscodeInputArgs))
            return config.TranscodeInputArgs;

        return string.Empty;
    }

    private async Task<bool> TryRunFfmpegAsync(string ffmpegPath, string args, TimeSpan timeout, CancellationToken ct)
    {
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            }
        };

        process.Start();
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            logger.LogWarning("pHash FFmpeg timed out: {Args}", args[..Math.Min(200, args.Length)]);
            return false;
        }

        if (process.ExitCode == 0)
            return true;

        var stderr = await stderrTask;
        logger.LogWarning("pHash FFmpeg failed (exit {Code}): {Error}", process.ExitCode, stderr[..Math.Min(500, stderr.Length)]);
        return false;
    }

    /// <summary>
    /// DCT-II using Lee 1984 recursive algorithm, matching goimagehash's DCT1DFast64.
    /// Operates in-place on the input span of length 64.
    /// </summary>
    private static void Dct1DInPlace64(Span<double> input)
    {
        ForwardTransform(input, stackalloc double[64], 64);
    }

    private static void ForwardTransform(Span<double> input, Span<double> temp, int len)
    {
        if (len == 1) return;

        var halfLen = len / 2;
        for (var i = 0; i < halfLen; i++)
        {
            double x = input[i], y = input[len - 1 - i];
            temp[i] = x + y;
            temp[i + halfLen] = (x - y) / (Math.Cos((i + 0.5) * Math.PI / len) * 2);
        }

        ForwardTransform(temp, input, halfLen);
        ForwardTransform(temp.Slice(halfLen), input, halfLen);

        for (var i = 0; i < halfLen - 1; i++)
        {
            input[i * 2] = temp[i];
            input[i * 2 + 1] = temp[i + halfLen] + temp[i + halfLen + 1];
        }
        input[len - 2] = temp[halfLen - 1];
        input[len - 1] = temp[len - 1];
    }

    /// <summary>
    /// 2D DCT matching goimagehash's DCT2DFast64. Operates in-place on a flat 4096-element array.
    /// </summary>
    private static void Dct2DInPlace64(double[] pixels)
    {
        // Apply DCT to each row
        for (var i = 0; i < DctImageSize; i++)
        {
            Dct1DInPlace64(pixels.AsSpan(i * DctImageSize, DctImageSize));
        }

        // Apply DCT to each column
        Span<double> column = stackalloc double[DctImageSize];
        for (var i = 0; i < DctImageSize; i++)
        {
            for (var j = 0; j < DctImageSize; j++)
                column[j] = pixels[i + j * DctImageSize];

            Dct1DInPlace64(column);

            for (var j = 0; j < DctImageSize; j++)
                pixels[i + j * DctImageSize] = column[j];
        }
    }

    /// <summary>
    /// Median matching Go's MedianOfPixelsFast64: quickselect to position len/2,
    /// then average seq[k-1] and seq[k] when len is even.
    /// </summary>
    private static double MedianQuickSelect(double[] input)
    {
        var tmp = new double[input.Length];
        Array.Copy(input, tmp, input.Length);
        var pos = tmp.Length / 2;
        QuickSelect(tmp, 0, tmp.Length - 1, pos);

        // Go averages two middle elements for even-length arrays
        if (tmp.Length % 2 == 0)
            return tmp[pos - 1] / 2 + tmp[pos] / 2;
        return tmp[pos];
    }

    private static void QuickSelect(double[] seq, int low, int hi, int k)
    {
        if (low == hi) return;

        while (low < hi)
        {
            var pivot = low / 2 + hi / 2;
            var pivotValue = seq[pivot];
            var storeIdx = low;
            (seq[pivot], seq[hi]) = (seq[hi], seq[pivot]);

            for (var i = low; i < hi; i++)
            {
                if (seq[i] < pivotValue)
                {
                    (seq[storeIdx], seq[i]) = (seq[i], seq[storeIdx]);
                    storeIdx++;
                }
            }
            (seq[hi], seq[storeIdx]) = (seq[storeIdx], seq[hi]);

            if (k <= storeIdx)
                hi = storeIdx;
            else
                low = storeIdx + 1;
        }
    }
}
