using System.Globalization;
using System.Security.Cryptography;
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
    string StartGenerateScenePhashes();
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
            return null;

        var ffmpegPath = FindFfmpeg();
        if (ffmpegPath == null)
        {
            logger.LogWarning("FFmpeg not found. Cannot compute video phash for {Path}", path);
            return null;
        }

        // Initialize FFmpeg.AutoGen bindings (idempotent) and check if in-process is usable.
        FfmpegInProcess.EnsureInitialized(ffmpegPath);

        var chunkCount = SpriteColumns * SpriteRows; // 25
        var offset = 0.05 * duration;
        var stepSize = (0.9 * duration) / chunkCount;
        var timestamps = new double[chunkCount];
        for (var i = 0; i < chunkCount; i++)
            timestamps[i] = offset + i * stepSize;

        if (FfmpegInProcess.IsAvailable)
        {
            // Fast path: in-process frame extraction (seeks directly, no process spawning).
            try
            {
                var frames = FfmpegInProcess.ExtractFrames(path, timestamps, SpriteFrameSize, threadCount: 1, ct);
                if (frames != null)
                {
                    try
                    {
                        return BuildSpritePhash(frames);
                    }
                    finally
                    {
                        foreach (var f in frames) f?.Dispose();
                    }
                }

                logger.LogWarning("In-process frame extraction returned null for {Path}, falling back to process", path);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "In-process FFmpeg failed for {Path}, falling back to process spawn", path);
            }
        }

        // Fallback path: spawn ffmpeg once per timestamp and extract a single frame each time.
        // Works on any platform where FFmpeg is a statically-linked standalone binary (no
        // separate shared .so/.dylib files for AutoGen's dynamic loader) — same as ThumbnailService.
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
    /// Process-based fallback: spawns ffmpeg (the CLI binary) once per timestamp to extract
    /// a single scaled frame, then composes the sprite and computes the phash.
    /// Slower than in-process but works on any platform regardless of shared library availability.
    /// </summary>
    private async Task<string?> ComputeVideoPhashViaProcessAsync(
        string ffmpegPath, string videoPath, double[] timestamps, CancellationToken ct)
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"cove_phash_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            var frames = new Image<Rgba32>[timestamps.Length];
            for (var i = 0; i < timestamps.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var framePath = Path.Combine(tmpDir, $"frame_{i:D3}.jpg");
                var ts = timestamps[i];

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    // -ss before -i is fast (keyframe seek), -vframes 1 grabs one frame,
                    // scale=W:-2 keeps aspect ratio with width=SpriteFrameSize.
                    Arguments = $"-v error -ss {ts:F3} -i \"{videoPath}\" -vframes 1 -vf \"scale={SpriteFrameSize}:-2\" -q:v 3 -y \"{framePath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using var proc = System.Diagnostics.Process.Start(psi)!;
                var stderrTask = proc.StandardError.ReadToEndAsync(ct);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    await proc.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    logger.LogWarning("FFmpeg timed out extracting frame {Index} from {Path}", i, videoPath);
                    DisposeFrames(frames);
                    return null;
                }

                if (proc.ExitCode != 0 || !File.Exists(framePath))
                {
                    var err = await stderrTask;
                    logger.LogWarning("FFmpeg failed extracting frame {Index} from {Path}: {Error}", i, videoPath, err);
                    DisposeFrames(frames);
                    return null;
                }

                frames[i] = await SixLabors.ImageSharp.Image.LoadAsync<Rgba32>(framePath, ct);
            }

            try
            {
                return BuildSpritePhash(frames);
            }
            finally
            {
                DisposeFrames(frames);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Process-based phash extraction failed for {Path}", videoPath);
            return null;
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
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

    public string StartGenerateScenePhashes()
    {
        return jobService.Enqueue("generate_scene_phashes", "Generating scene pHashes", async (progress, ct) =>
        {
            List<(int FileId, string Path, double Duration)> workItems;

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

                // Get IDs of files that already have a phash
                var filesWithPhash = await db.FileFingerprints
                    .Where(fp => fp.Type == "phash")
                    .Select(fp => fp.FileId)
                    .Distinct()
                    .ToHashSetAsync(ct);

                // Only load files that need phash generation
                workItems = await db.VideoFiles
                    .Include(file => file.ParentFolder)
                    .Where(file => !filesWithPhash.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, Path = file.ParentFolder != null ? file.ParentFolder.Path + System.IO.Path.DirectorySeparatorChar + file.Basename : file.Basename, file.Duration })
                    .ToListAsync(ct)
                    .ContinueWith(t => t.Result.Select(f => (f.Id, f.Path, f.Duration)).ToList(), ct);
            }

            if (workItems.Count == 0)
            {
                progress.Report(1.0, "All scenes already have pHashes");
                return;
            }

            logger.LogInformation("Generating pHashes for {Count} video files with parallelism={Parallelism}", workItems.Count, ResolveMaxParallelism());
            var completed = 0;

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = ResolveMaxParallelism(), CancellationToken = ct }, async (item, token) =>
            {
                var phash = await ComputeVideoPhashAsync(item.Path, item.Duration, token);
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
                progress.Report((double)done / workItems.Count, $"({done}/{workItems.Count}) {Path.GetFileName(item.Path)}");
            });

            logger.LogInformation("Finished generating pHashes for {Count} video files", workItems.Count);
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

                workItems = await db.ImageFiles
                    .Include(file => file.ParentFolder)
                    .Where(file => !filesWithPhash.Contains(file.Id))
                    .OrderBy(file => file.Id)
                    .Select(file => new { file.Id, Path = file.ParentFolder != null ? file.ParentFolder.Path + System.IO.Path.DirectorySeparatorChar + file.Basename : file.Basename })
                    .ToListAsync(ct)
                    .ContinueWith(t => t.Result.Select(f => (f.Id, f.Path)).ToList(), ct);
            }

            if (workItems.Count == 0)
                return;

            logger.LogInformation("Generating image pHashes for {Count} files with parallelism={Parallelism}", workItems.Count, ResolveMaxParallelism());
            var completed = 0;

            await Parallel.ForEachAsync(workItems, new ParallelOptions { MaxDegreeOfParallelism = ResolveMaxParallelism(), CancellationToken = ct }, async (item, token) =>
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