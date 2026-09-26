using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using IVideoFileMaintenanceService = Cove.Plugins.IVideoFileMaintenanceService;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>What a conversion to one codec would run on this machine, for the conversion dialog.</summary>
public sealed record VideoConversionEncoderInfo(string Codec, string? Encoder, bool Hardware);

public sealed record VideoConversionJobStart(string JobId, int ItemCount);

/// <summary>
/// Converts videos' primary files to another codec and/or container. Each converted file is written next
/// to the original and attached to the same video as an extra file. When the job is asked to replace the
/// originals, the new file must also pass a full decode check and the primary-file swap's same-footage
/// check before it becomes primary and the original is deleted, so generated assets, markers and clips
/// carry over without regenerating anything. Any file that fails a check is left alone, and the unit says why.
/// </summary>
public sealed class VideoConversionJobService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    IMediaProbeService mediaProbe,
    HardwareEncodeSessionGate hwEncodeSessionGate,
    FfmpegConcurrencyLimiter ffmpegConcurrency,
    PhysicalFileAccessCoordinator physicalFileCoordinator,
    CoveConfiguration config,
    ILogger<VideoConversionJobService> logger)
{
    // ffmpeg writes a -progress block about twice a second; this long without one means it is hung.
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(5);

    // Share of a unit's progress bar for each phase: measuring quality on samples, the encode itself,
    // and the decode check that precedes replacing an original.
    private const double SearchShare = 0.15;
    private const double EncodeShare = 0.65;
    private const double VerifyShare = 0.15;

    // Whether each ffmpeg build can score quality samples, keyed by path; asked once per build.
    private readonly Dictionary<string, bool> _qualityMeasurement = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<(string Fingerprint, VideoConversionCodec Codec), string?> _encoders = [];
    private readonly object _encoderLock = new();

    public VideoConversionJobStart Start(CovePrincipal? principal, IReadOnlyList<int> videoIds, VideoConversionSettings settings)
    {
        var ids = videoIds.Where(id => id > 0).Distinct().ToArray();
        var target = settings.Codec == VideoConversionCodec.Copy
            ? ContainerLabelFor(settings)
            : $"{FfmpegHwAccel.CodecLabel(settings.Codec)} {ContainerLabelFor(settings)}";
        var description = $"Converting {ids.Length} video{(ids.Length == 1 ? "" : "s")} to {target}"
            + (settings.ReplaceOriginal ? " (replacing originals)" : string.Empty);

        var jobId = jobService.EnqueueFor(
            JobOwner.FromPrincipal(principal),
            "convert-videos",
            description,
            (progress, ct) => RunAsync(ids, settings, progress, ct));
        return new VideoConversionJobStart(jobId, ids.Length);
    }

    /// <summary>The encoder each codec would use under the current settings. Probes run once per ffmpeg/setting combination.</summary>
    public IReadOnlyList<VideoConversionEncoderInfo> DescribeEncoders()
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg(config);
        return [.. new[] { VideoConversionCodec.H264, VideoConversionCodec.Hevc, VideoConversionCodec.Av1 }
            .Select(codec =>
            {
                var encoder = ffmpeg is null ? null : ResolveEncoder(ffmpeg, codec);
                return new VideoConversionEncoderInfo(
                    VideoConversionPlanner.CodecName(codec),
                    encoder,
                    encoder is not null && !FfmpegHwAccel.IsSoftwareEncoder(encoder));
            })];
    }

    private string? ResolveEncoder(string ffmpegPath, VideoConversionCodec codec, bool preferHardware = true)
    {
        var key = ($"{ffmpegPath}|{config.HardwareAcceleration}|{preferHardware}", codec);
        lock (_encoderLock)
        {
            if (_encoders.TryGetValue(key, out var cached))
                return cached;
        }

        // Probing runs test encodes, so it happens outside the lock; a concurrent duplicate probe is harmless.
        var encoder = FfmpegHwAccel.SelectConversionEncoder(ffmpegPath, codec, config.HardwareAcceleration, preferHardware, logger);
        lock (_encoderLock)
            _encoders[key] = encoder;
        return encoder;
    }

    /// <summary>
    /// Bytes reclaimed across a run. The common use of this feature is re-encoding the largest files in a
    /// library to a denser codec, where the number worth reporting is the total freed, not each file's
    /// before-and-after. Only counted when the original is actually replaced; adding a converted file
    /// alongside its original consumes space rather than freeing it.
    /// </summary>
    private sealed class ReclaimedSpace
    {
        private long _bytes;
        private int _videos;
        private int _leftAlone;

        public void Add(long sourceSize, long outputSize)
        {
            Interlocked.Add(ref _bytes, sourceSize - outputSize);
            Interlocked.Increment(ref _videos);
        }

        /// <summary>A file left alone because keeping its quality would not have saved enough space.</summary>
        public void AddLeftAlone() => Interlocked.Increment(ref _leftAlone);

        public long Bytes => Interlocked.Read(ref _bytes);
        public int Videos => Volatile.Read(ref _videos);
        public int LeftAlone => Volatile.Read(ref _leftAlone);
    }

    private async Task RunAsync(int[] ids, VideoConversionSettings settings, IJobProgress progress, CancellationToken ct)
    {
        var ffmpeg = FfmpegExecutableLocator.FindFfmpeg(config)
            ?? throw new InvalidOperationException("FFmpeg was not found. Set its path in Settings before converting videos.");

        string? encoder = null;
        if (settings.Codec != VideoConversionCodec.Copy)
        {
            progress.Report(0, $"Choosing the {FfmpegHwAccel.CodecLabel(settings.Codec)} encoder...");
            encoder = ResolveEncoder(ffmpeg, settings.Codec, VideoConversionPlanner.PrefersHardware(settings.Effort))
                ?? throw new InvalidOperationException(
                    $"This ffmpeg build cannot encode {FfmpegHwAccel.CodecLabel(settings.Codec)}: "
                    + $"{FfmpegHwAccel.SoftwareEncoderFor(settings.Codec)} is not built in and no hardware encoder for it passed a test encode.");

            // Every re-encode measures quality on samples first. Without the means to measure, there is
            // no honest way to choose a setting, so refuse rather than guess one.
            bool measurable;
            lock (_qualityMeasurement)
            {
                if (!_qualityMeasurement.TryGetValue(ffmpeg, out measurable))
                    _qualityMeasurement[ffmpeg] = measurable = FfmpegHwAccel.HasQualityMeasurement(ffmpeg);
            }
            if (!measurable)
            {
                throw new InvalidOperationException(
                    "This ffmpeg build cannot measure video quality, which conversion does for each video before "
                    + "encoding it: it needs libvmaf with its built-in vmaf_v0.6.1 model and the psnr_hvs feature. "
                    + "Use an ffmpeg build that includes them, such as BtbN's GPL builds.");
            }
        }

        List<(int Id, string Label)> work;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var found = await db.Videos.AsNoTracking()
                .Where(video => ids.Contains(video.Id))
                .Select(video => new { video.Id, video.Title, Basename = video.PrimaryFile != null ? video.PrimaryFile.Basename : null })
                .ToDictionaryAsync(video => video.Id, ct);
            work = [.. ids.Select(id => (id, found.TryGetValue(id, out var video)
                ? (!string.IsNullOrWhiteSpace(video.Title) ? video.Title : video.Basename ?? $"Video {id}")
                : $"Video {id}"))];
        }

        progress.DeclareUnitCount(work.Count);

        // Hardware encoders run within the shared GPU session limit; a software encode already uses
        // every core, so running two at once only makes both slower.
        var parallelism = encoder is not null && !FfmpegHwAccel.IsSoftwareEncoder(encoder)
            ? Math.Min(2, hwEncodeSessionGate.Capacity)
            : 1;

        var reclaimed = new ReclaimedSpace();
        var result = await jobService.RunBatchAsync(
            work,
            parallelism,
            (item, unit, token) => ConvertAsync(ffmpeg, encoder, item.Id, settings, reclaimed, unit, token),
            progress,
            unitIdFactory: (item, _) => item.Id.ToString(CultureInfo.InvariantCulture),
            labelFactory: item => item.Label,
            ct: ct);

        // The bare counts are not enough on their own: a run that encodes for minutes and then throws
        // the result away still ends "completed", and reads as success unless the reason is said here.
        var summary = result.Summary;
        if (reclaimed.Videos > 0 && reclaimed.Bytes > 0)
            summary += $" Freed {FormatSize(reclaimed.Bytes)} across {reclaimed.Videos} replaced file(s).";
        else if (reclaimed.Videos > 0 && reclaimed.Bytes < 0)
            summary += $" Used {FormatSize(-reclaimed.Bytes)} more across {reclaimed.Videos} replaced file(s).";

        if (reclaimed.LeftAlone > 0)
        {
            summary += reclaimed.LeftAlone == 1
                ? " 1 video was left alone because keeping its quality would not have saved enough space."
                : $" {reclaimed.LeftAlone} videos were left alone because keeping their quality would not have saved enough space.";
        }

        logger.LogInformation(
            "Conversion finished: {Summary} (encoder {Encoder})",
            summary, encoder ?? "stream copy");

        progress.SetSummary(summary);
    }


    private async Task ConvertAsync(
        string ffmpeg,
        string? encoder,
        int videoId,
        VideoConversionSettings settings,
        ReclaimedSpace reclaimed,
        IJobUnit unit,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        var video = await db.Videos.AsNoTracking()
            .Include(item => item.PrimaryFile!).ThenInclude(file => file.ParentFolder)
            .SingleOrDefaultAsync(item => item.Id == videoId, ct);
        if (video is null)
        {
            unit.Complete(JobUnitOutcome.Skipped, "The video no longer exists.");
            return;
        }

        var original = video.PrimaryFile;
        if (original is null)
        {
            unit.Complete(JobUnitOutcome.Skipped, "The video has no primary file.");
            return;
        }
        if (original.ZipFileId is not null)
        {
            unit.Complete(JobUnitOutcome.Skipped, "The primary file is inside an archive.");
            return;
        }

        var sourcePath = FilesystemPaths.ToNativePath(!string.IsNullOrWhiteSpace(original.Path)
            ? original.Path
            : BaseFileEntity.ComputePath(original.ParentFolder?.Path, original.Basename));
        if (!File.Exists(sourcePath))
        {
            unit.Complete(JobUnitOutcome.Failed, $"The primary file is missing on disk: {sourcePath}");
            return;
        }

        unit.Report(0, "Reading the original file...");
        var source = await ProbeAsync(sourcePath, "original", ct);
        var sourceCodec = source.Video?.CodecName ?? string.Empty;
        if (VideoConversionPlanner.SkipReason(sourcePath, settings) is { } skip)
        {
            unit.Complete(JobUnitOutcome.Skipped, skip);
            return;
        }

        // Names already used in the folder, including rows whose file is missing from disk: importing onto
        // one of those would adopt that row instead of creating a new file.
        var usedNames = (await db.Set<BaseFileEntity>().AsNoTracking()
                .Where(file => file.ParentFolderId == original.ParentFolderId)
                .Select(file => file.Basename)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var outputPath = VideoConversionPlanner.ChooseOutputPath(sourcePath, settings, candidate =>
            File.Exists(candidate)
            || File.Exists(candidate + VideoConversionPlanner.PartialSuffix)
            || usedNames.Contains(Path.GetFileName(candidate)));
        var partialPath = outputPath + VideoConversionPlanner.PartialSuffix;

        var sourceSize = new FileInfo(sourcePath).Length;
        EnsureFreeSpace(outputPath, sourceSize);

        // A file already in the target codec is not left alone for that reason: whether re-encoding it
        // pays is measured like any other. When it does not, the stream can still be copied into the
        // requested container rather than the request being ignored.
        var sameCodec = settings.Codec == VideoConversionCodec.Copy || VideoConversionPlanner.CopiesVideo(sourceCodec, settings.Codec);
        var changesContainer = !VideoConversionPlanner.IsInContainer(sourcePath, settings.Container);
        var notes = new List<string>();
        var published = false;
        try
        {
            var current = settings.Codec == VideoConversionCodec.Copy ? null : encoder;
            ConversionAttempt attempt;
            while (true)
            {
                try
                {
                    attempt = await SearchAndEncodeAsync(
                        ffmpeg, current, source, sourcePath, partialPath, sourceSize, settings, video.IsVr,
                        sameCodec && changesContainer, notes, unit, ct);
                    break;
                }
                catch (HardwareEncodeFailedException failure) when (current is not null && !FfmpegHwAccel.IsSoftwareEncoder(current))
                {
                    // Same policy as preview generation: a hardware encoder that passed its probe can still
                    // fail on a real file (a 10-bit format the GPU lacks, an exhausted session). The software
                    // encoder is measured afresh rather than handed the hardware one's setting, because the
                    // two scales do not correspond.
                    var software = FfmpegHwAccel.SoftwareEncoderFor(settings.Codec);
                    logger.LogWarning("Hardware conversion with {Encoder} failed for {Path}; retrying with {Software}: {Error}",
                        current, sourcePath, software, failure.Message);
                    notes.Add($"{current} failed ({failure.Message}), so it was measured and encoded with {software} instead.");
                    DeletePartial(partialPath);
                    current = software;
                }
            }

            if (attempt.SkipReason is { } leftAlone)
            {
                reclaimed.AddLeftAlone();
                unit.Complete(JobUnitOutcome.Skipped, Describe(leftAlone, notes));
                return;
            }

            var plan = attempt.Plan!;
            notes.AddRange(plan.Notes);

            var output = await ProbeAsync(partialPath, "converted", ct);
            if (VideoConversionPlanner.VerifyOutput(source, output, plan) is { } problem)
            {
                unit.Complete(JobUnitOutcome.Failed, $"{problem} The original was kept.");
                return;
            }

            var outputSize = new FileInfo(partialPath).Length;
            if (attempt.Choice is { } made && outputSize >= sourceSize && !settings.ConvertEvenIfLarger)
            {
                // The size was predicted from samples, and a video whose samples did not represent it can
                // land wide of that. Replacing a file with a larger one is never what was asked for.
                logger.LogWarning(
                    "Discarded the conversion of {Path}: {Output} is no smaller than the original {Source}; its samples predicted {Predicted}.",
                    sourcePath, FormatSize(outputSize), FormatSize(sourceSize), FormatSize(made.PredictedBytes));
                unit.Complete(JobUnitOutcome.Skipped, Describe(
                    $"The encoded file came out at {FormatSize(outputSize)}, not smaller than the original's {FormatSize(sourceSize)}, "
                    + $"although its samples predicted about {FormatSize(made.PredictedBytes)}. It was discarded and the original kept.", notes));
                return;
            }

            if (settings.ReplaceOriginal)
            {
                unit.Report(SearchShare + EncodeShare, "Checking the converted file decodes cleanly...");
                await VerifyDecodesAsync(ffmpeg, partialPath, output.Duration, unit, ct);
            }

            int newFileId;
            unit.Report(SearchShare + EncodeShare + VerifyShare, "Adding the converted file to the video...");
            using (await physicalFileCoordinator.AcquireReadAsync(ct))
            {
                if (File.Exists(outputPath))
                    throw new VideoConversionException($"{outputPath} appeared while converting, so the converted file was not moved into place.");

                File.Move(partialPath, outputPath);
                published = true;
                var scanService = scope.ServiceProvider.GetRequiredService<IScanService>() as ScanService
                    ?? throw new InvalidOperationException("Library conversion needs Cove's scan service to import the converted file.");
                newFileId = await scanService.ImportConvertedVideoFileWithinProducerLeaseAsync(outputPath, videoId, ct);
            }

            var outcome = $"Converted to {Path.GetFileName(outputPath)} ({FormatSize(sourceSize)} → {FormatSize(outputSize)})";
            if (!settings.ReplaceOriginal)
            {
                unit.Complete(JobUnitOutcome.Succeeded, Describe($"{outcome}, added as an extra file.", notes));
                return;
            }

            // Replacing is only safe while the file that was converted is still the one the video plays.
            var currentPrimary = await db.Videos.AsNoTracking()
                .Where(item => item.Id == videoId)
                .Select(item => item.PrimaryFileId)
                .SingleOrDefaultAsync(ct);
            if (currentPrimary != original.Id)
            {
                unit.Complete(JobUnitOutcome.Failed,
                    Describe($"{outcome}, but the video's primary file changed during the conversion, so nothing was replaced. The converted file is attached as an extra file.", notes));
                return;
            }

            // The swap re-checks that both files show the same footage (length and perceptual hash) before
            // changing anything, which is what keeps the video's generated assets valid.
            unit.Report(0.97, "Making the converted file primary...");
            var maintenance = scope.ServiceProvider.GetRequiredService<IVideoFileMaintenanceService>();
            var swap = await maintenance.MakePrimaryWhenSameContentAsync(videoId, newFileId, ct);
            if (!swap.Applied)
            {
                unit.Complete(JobUnitOutcome.Failed,
                    Describe($"{outcome}, but it was not made primary: {swap.Reason} The original was kept, and the converted file is attached as an extra file.", notes));
                return;
            }

            var removal = await maintenance.DeleteFileAsync(original.Id, deleteFromDisk: true, ct);
            if (!removal.Applied)
            {
                unit.Complete(JobUnitOutcome.Failed,
                    Describe($"{outcome} and made primary, but the original could not be deleted: {removal.Reason}", notes));
                return;
            }

            // Counted only here: this is the one path where the original is actually gone from disk.
            reclaimed.Add(sourceSize, outputSize);
            unit.Complete(JobUnitOutcome.Succeeded, Describe($"{outcome}; it replaced the original, which was deleted.", notes));
        }
        catch (VideoConversionException ex)
        {
            unit.Complete(JobUnitOutcome.Failed, ex.Message);
        }
        finally
        {
            if (!published)
                DeletePartial(partialPath);
        }
    }

    /// <summary>
    /// The frame rate the output will actually have. A requested rate only ever lowers: interpolating a
    /// 30fps source up to 60 invents frames, costing size and gaining nothing. A selection converted
    /// together can hold a mix of
    /// source rates, so this is resolved per video rather than once for the batch.
    /// </summary>
    private static double? EffectiveFrameRate(ProbedMedia source, VideoConversionSettings settings)
    {
        if (settings.OutputFrameRate is not { } requested || requested <= 0)
            return null;

        var sourceRate = source.Video?.FrameRate ?? 0;
        if (sourceRate <= 0)
            return requested;

        // Within a frame of the source is the source; re-timing for that gains nothing.
        return requested < sourceRate - 0.01 ? requested : null;
    }

    /// <summary>What one pass of measure-then-encode produced: a finished encode, or why the video was left alone.</summary>
    private sealed record ConversionAttempt(VideoConversionPlan? Plan, QualityChoice? Choice, string? SkipReason);

    /// <summary>The setting the quality search settled on, and what its samples measured and predicted.</summary>
    private sealed record QualityChoice(string Encoder, double Level, double Score, double Target, long PredictedBytes, int MaxKbps, int Rounds)
    {
        public string Describe() => string.Create(CultureInfo.InvariantCulture,
            $"Measured on samples: {Encoder} at quality level {Level:0.##} scored {Score:0.0} PSNR-HVS against a target of {Target:0.0}, "
            + $"settled in {Rounds} round{(Rounds == 1 ? "" : "s")}, predicting about {FormatSize(PredictedBytes)}.");
    }

    /// <summary>A hardware encoder failed where software may not. Handled by retrying the whole pass in software.</summary>
    private sealed class HardwareEncodeFailedException(string message) : Exception(message);

    /// <summary>
    /// Measures the quality setting this video needs with <paramref name="encoder"/>, then encodes the whole
    /// video at it. A null encoder copies the video stream (a remux). Throws
    /// <see cref="HardwareEncodeFailedException"/> when a hardware encoder fails, so the caller can retry.
    /// </summary>
    private async Task<ConversionAttempt> SearchAndEncodeAsync(
        string ffmpeg,
        string? encoder,
        ProbedMedia source,
        string sourcePath,
        string partialPath,
        long sourceSize,
        VideoConversionSettings settings,
        bool isVr,
        bool canRemuxInstead,
        List<string> notes,
        IJobUnit unit,
        CancellationToken ct)
    {
        QualityChoice? choice = null;
        var videoEncoder = encoder;
        if (encoder is not null)
        {
            var (found, reason) = await SearchQualityAsync(ffmpeg, encoder, source, sourcePath, sourceSize, settings, isVr, unit, ct);
            if (found is not null)
            {
                choice = found;
                notes.Insert(0, found.Describe());
            }
            else if (canRemuxInstead)
            {
                // Same codec, different container: re-encoding would not pay, but the container change was
                // still asked for and needs no re-encode at all.
                notes.Add($"{reason} The video was copied into {ContainerLabelFor(settings)} unchanged instead.");
                videoEncoder = null;
            }
            else
            {
                return new ConversionAttempt(null, null, reason);
            }
        }

        var plan = await EncodeAsync(ffmpeg, videoEncoder, source, sourcePath, partialPath, settings, choice, unit, ct);
        return new ConversionAttempt(plan, choice, null);
    }

    /// <summary>
    /// Finds the highest quality level (smallest file) at which this video's samples still reach the
    /// target score, and whether the saving is worth the encode. Returns the choice, or null and a
    /// user-facing reason to leave the video alone. See <see cref="VideoQualitySearch"/>.
    /// </summary>
    private async Task<(QualityChoice? Choice, string Reason)> SearchQualityAsync(
        string ffmpeg,
        string encoder,
        ProbedMedia source,
        string sourcePath,
        long sourceSize,
        VideoConversionSettings settings,
        bool isVr,
        IJobUnit unit,
        CancellationToken ct)
    {
        var video = source.Video ?? throw new VideoConversionException("The file has no video stream to convert.");
        var windows = VideoQualitySearch.SampleWindows(source.Duration);
        if (windows.Count == 0)
            throw new VideoConversionException("The file's length could not be read, so its quality could not be measured.");

        var outputRate = EffectiveFrameRate(source, settings);
        var scoreRate = outputRate ?? (video.FrameRate > 0 ? video.FrameRate : 30);
        var target = VideoQualitySearch.Target(settings.Effort);
        var search = new VideoQualitySearchState(FfmpegHwAccel.ConversionQualityKnob(encoder), target);
        var tenBit = VideoConversionPlanner.IsTenBit(video);
        var maxKbps = MaxRateFor(source);
        var audioKbps = source.Audio.Sum(stream => stream.BitRateKbps);

        // Any level that fails the target needs a lower (larger) level to pass, so once a failing level
        // already predicts a file too big to be worth it, no passing one can be. That ends the search for
        // an already-lean file after a single round.
        var worthItBelow = settings.ConvertEvenIfLarger
            ? long.MaxValue
            : settings.ConvertMarginalSavings
                ? sourceSize
                : (long)(sourceSize * (1 - VideoConversionPlanner.MarginalSavingThreshold));

        var work = Directory.CreateTempSubdirectory("cove-convert-");
        try
        {
            unit.Report(0.01, "Measuring quality: taking samples...");
            var clips = new List<string>(windows.Count);
            for (var i = 0; i < windows.Count; i++)
            {
                var clip = Path.Combine(work.FullName, $"clip{i}.mkv");
                var args = VideoConversionPlanner.SampleClipArguments(sourcePath, video.Index, windows[i].Start, windows[i].Length, clip);
                var result = await RunGatedAsync(ffmpeg, args, encoder: null, _ => { }, ct);
                if (result.ExitCode != 0)
                    throw new VideoConversionException($"A sample of the file could not be taken: {LastLine(result.StandardError)} The original was kept.");
                clips.Add(clip);
            }

            while (search.Next() is { } level)
            {
                var round = search.Rounds.Count + 1;
                unit.Report(SearchShare * round / (VideoQualitySearch.MaxRounds + 1),
                    string.Create(CultureInfo.InvariantCulture, $"Measuring quality: round {round}, {encoder} at level {level:0.##}..."));

                var (score, bytesPerSecond) = await MeasureRoundAsync(
                    ffmpeg, encoder, level, clips, work.FullName, settings, tenBit, maxKbps, outputRate, scoreRate, video, isVr, ct);
                search.Record(level, score, bytesPerSecond);

                var predicted = VideoQualitySearch.PredictBytes(bytesPerSecond, source.Duration, audioKbps);
                logger.LogInformation(
                    "Quality search for {Path}: {Encoder} level {Level} scored {Score:0.00} PSNR-HVS (target {Target}), predicting {Predicted}",
                    sourcePath, encoder, level, score, target, FormatSize(predicted));

                if (score < target && predicted >= worthItBelow)
                {
                    return (null, $"Keeping this quality would take more than {FormatSize(predicted)} against the original's "
                                  + $"{FormatSize(sourceSize)}, so it was left alone.");
                }
            }

            if (search.Best is not { } best)
            {
                return (null, search.Unreachable
                    ? $"Even {encoder}'s highest quality setting did not reach the target on this video's samples, so it was left alone."
                    : $"The quality search did not settle within {VideoQualitySearch.MaxRounds} rounds, so the video was left alone.");
            }

            var predictedBytes = VideoQualitySearch.PredictBytes(best.BytesPerSecond, source.Duration, audioKbps);
            if (WorthItReason(sourceSize, predictedBytes, settings) is { } notWorth)
                return (null, notWorth);

            return (new QualityChoice(encoder, best.Level, best.Score, target, predictedBytes, maxKbps, search.Rounds.Count), string.Empty);
        }
        finally
        {
            try
            {
                work.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not remove the quality samples in {Path}", work.FullName);
            }
        }
    }

    /// <summary>
    /// One search round: every sample encoded at <paramref name="level"/> and scored. Each sample is
    /// scored on the CPU while the next one encodes, so the round costs little more than its encodes.
    /// Returns the mean score across samples and the encoded bytes per second of video.
    /// </summary>
    private async Task<(double Score, double BytesPerSecond)> MeasureRoundAsync(
        string ffmpeg,
        string encoder,
        double level,
        IReadOnlyList<string> clips,
        string workDir,
        VideoConversionSettings settings,
        bool tenBit,
        int maxKbps,
        double? outputRate,
        double scoreRate,
        ProbedStream video,
        bool isVr,
        CancellationToken ct)
    {
        var scores = new List<Task<double?>>(clips.Count);
        long bytes = 0;
        double seconds = 0;
        var tag = level.ToString("0.##", CultureInfo.InvariantCulture);
        try
        {
            for (var i = 0; i < clips.Count; i++)
            {
                var encoded = Path.Combine(workDir, $"sample{i}-{tag}.mkv");
                var result = await RunGatedAsync(
                    ffmpeg,
                    VideoConversionPlanner.SampleEncodeArguments(
                        clips[i], encoded, encoder, level, settings.Effort, tenBit, maxKbps, outputRate, config.FfmpegInputArgs),
                    encoder, _ => { }, ct);
                if (result.ExitCode != 0)
                {
                    if (!FfmpegHwAccel.IsSoftwareEncoder(encoder))
                        throw new HardwareEncodeFailedException(LastLine(result.StandardError));
                    throw new VideoConversionException(
                        $"A quality sample could not be encoded: {LastLine(result.StandardError)} The original was kept.");
                }

                bytes += new FileInfo(encoded).Length;
                seconds += (await ProbeAsync(encoded, "quality sample", ct)).Duration;

                var log = Path.Combine(workDir, $"score{i}-{tag}.json");
                scores.Add(ScoreSampleAsync(
                    ffmpeg,
                    VideoConversionPlanner.SampleScoreArguments(
                        encoded, clips[i], video.Width, video.Height, scoreRate, outputRate is not null, isVr, log),
                    log, ct));
            }
        }
        finally
        {
            // Never leave a scoring process running behind an exception.
            await Task.WhenAll(scores.Select(task => task.ContinueWith(_ => { }, TaskScheduler.Default)));
        }

        var values = await Task.WhenAll(scores);
        return (VideoQualitySearch.RoundScore(values), seconds > 0 ? bytes / seconds : 0);
    }

    private async Task<double?> ScoreSampleAsync(string ffmpeg, IReadOnlyList<string> arguments, string logPath, CancellationToken ct)
    {
        FfmpegProcessResult result;
        // Scoring decodes two inputs, the sample and its reference.
        await using (await ffmpegConcurrency.AcquireAsync(2, ct))
            result = await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg, arguments, _ => { }, StallTimeout, ct);

        if (result.ExitCode != 0)
            throw new VideoConversionException($"A quality sample could not be measured: {LastLine(result.StandardError)} The original was kept.");
        return VideoQualitySearch.ParsePsnrHvs(await File.ReadAllTextAsync(logPath, ct));
    }

    /// <summary>
    /// The peak rate an encode may reach. NVENC needs one in constant-quality mode (see
    /// <see cref="FfmpegHwAccel.ConversionQualityArgs"/>); twice the source's own rate leaves quality free
    /// to rise wherever the search needs it, since an output needing more than that would not be a
    /// conversion worth making anyway.
    /// </summary>
    private static int MaxRateFor(ProbedMedia source)
        => source.VideoBitRateKbps > 0
            ? (int)Math.Clamp(source.VideoBitRateKbps * 2, 8_000, 800_000)
            : 400_000;

    /// <summary>Why a conversion predicted to come out at <paramref name="predictedBytes"/> is not worth running, or null.</summary>
    private static string? WorthItReason(long sourceSize, long predictedBytes, VideoConversionSettings settings)
    {
        if (settings.ConvertEvenIfLarger || sourceSize <= 0 || predictedBytes <= 0)
            return null;

        if (predictedBytes >= sourceSize)
        {
            return $"Keeping this quality would take about {FormatSize(predictedBytes)}, no smaller than the original's "
                 + $"{FormatSize(sourceSize)}, so it was left alone.";
        }

        var saving = VideoConversionPlanner.ProjectedSaving(sourceSize, predictedBytes);
        if (saving < VideoConversionPlanner.MarginalSavingThreshold && !settings.ConvertMarginalSavings)
        {
            return $"Keeping this quality would only save about {saving:P0} ({FormatSize(sourceSize)} to about "
                 + $"{FormatSize(predictedBytes)}), so it was left alone. Allow small savings to convert it anyway.";
        }

        return null;
    }

    private async Task<VideoConversionPlan> EncodeAsync(
        string ffmpeg,
        string? encoder,
        ProbedMedia source,
        string sourcePath,
        string partialPath,
        VideoConversionSettings settings,
        QualityChoice? choice,
        IJobUnit unit,
        CancellationToken ct)
    {
        var plan = VideoConversionPlanner.Build(
            source, sourcePath, partialPath, settings, encoder, config.FfmpegInputArgs,
            choice?.Level ?? 0, choice?.MaxKbps ?? 0, EffectiveFrameRate(source, settings));
        var action = plan.CopiesVideo ? "Remuxing" : $"Encoding with {encoder}";

        var result = await RunTrackedAsync(ffmpeg, plan.Arguments, source.Duration, $"{action}...", SearchShare, EncodeShare, unit, ct, encoder);
        if (result.ExitCode == 0)
            return plan;

        if (encoder is not null && !FfmpegHwAccel.IsSoftwareEncoder(encoder))
            throw new HardwareEncodeFailedException(LastLine(result.StandardError));

        throw new VideoConversionException(result.TimedOut
            ? "ffmpeg stopped responding, so the conversion was stopped. The original was kept."
            : $"ffmpeg could not convert the file: {LastLine(result.StandardError)} The original was kept.");
    }

    private async Task VerifyDecodesAsync(string ffmpeg, string path, double duration, IJobUnit unit, CancellationToken ct)
    {
        var result = await RunTrackedAsync(
            ffmpeg,
            VideoConversionPlanner.DecodeCheckArguments(path, config.FfmpegInputArgs),
            duration,
            "Checking the converted file decodes cleanly...",
            SearchShare + EncodeShare,
            VerifyShare,
            unit,
            ct,
            encoder: null);

        // With -v error, anything ffmpeg prints is a decode error.
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StandardError))
        {
            throw new VideoConversionException(result.TimedOut
                ? "The decode check stopped responding, so the converted file was discarded and the original kept."
                : $"The converted file did not decode cleanly ({LastLine(result.StandardError)}), so it was discarded and the original kept.");
        }
    }

    private async Task<FfmpegProcessResult> RunTrackedAsync(
        string ffmpeg,
        IReadOnlyList<string> arguments,
        double duration,
        string message,
        double start,
        double share,
        IJobUnit unit,
        CancellationToken ct,
        string? encoder)
    {
        unit.Report(start, message);
        var lastReported = -1d;
        void OnProgress(string line)
        {
            if (duration <= 0 || !VideoConversionPlanner.TryParseProgressSeconds(line, out var seconds))
                return;
            var fraction = Math.Clamp(seconds / duration, 0, 1);
            // Throttle to whole percents so a long encode does not flood the job feed.
            if (fraction - lastReported < 0.01)
                return;
            lastReported = fraction;
            unit.Report(start + share * fraction, $"{message} {fraction:P0}");
        }

        return await RunGatedAsync(ffmpeg, arguments, encoder, OnProgress, ct);
    }

    /// <summary>
    /// Runs one ffmpeg that reads a single input, within the limits it shares with the rest of Cove.
    /// It reserves one decode slot: conversion caps its own parallelism, but it can run alongside a
    /// generate job that does not know about it, and the configured limit is meant to bound everything
    /// Cove decodes at once. A hardware encode also holds one of the GPU's encode sessions.
    /// </summary>
    private async Task<FfmpegProcessResult> RunGatedAsync(
        string ffmpeg, IReadOnlyList<string> arguments, string? encoder, Action<string> onProgress, CancellationToken ct)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Running ffmpeg {Arguments}", FfmpegProcessRunner.Describe(arguments, int.MaxValue));

        if (encoder is null || FfmpegHwAccel.IsSoftwareEncoder(encoder))
        {
            await using var softwareSlot = await ffmpegConcurrency.AcquireAsync(1, ct);
            return await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg, arguments, onProgress, StallTimeout, ct);
        }

        using (await hwEncodeSessionGate.AcquireAsync(ct))
        {
            await using var hardwareSlot = await ffmpegConcurrency.AcquireAsync(1, ct);
            return await FfmpegProcessRunner.RunWithProgressAsync(ffmpeg, arguments, onProgress, StallTimeout, ct);
        }
    }

    private async Task<ProbedMedia> ProbeAsync(string path, string which, CancellationToken ct)
    {
        var probe = await mediaProbe.ProbeAsync(path, ct);
        if (probe.Status != MediaProbeStatus.Success || probe.Json is null)
            throw new VideoConversionException($"ffprobe could not read the {which} file: {probe.Reason ?? probe.Status.ToString()}");
        return ProbedMedia.Parse(probe.Json);
    }

    private static void EnsureFreeSpace(string outputPath, long sourceSize)
    {
        DriveInfo drive;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(outputPath));
            if (string.IsNullOrEmpty(root))
                return;
            drive = new DriveInfo(root);
            if (!drive.IsReady)
                return;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network shares and some mounts have no drive to ask; ffmpeg reports a full disk itself.
            return;
        }

        // A conversion can come out larger than the original, so require room for a full copy.
        if (drive.AvailableFreeSpace < sourceSize)
        {
            throw new VideoConversionException(
                $"Not enough free space on {drive.Name}: the conversion needs up to {FormatSize(sourceSize)} and {FormatSize(drive.AvailableFreeSpace)} is free.");
        }
    }

    private void DeletePartial(string partialPath)
    {
        try
        {
            if (File.Exists(partialPath))
                File.Delete(partialPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete the partial conversion output {Path}", partialPath);
        }
    }

    private static string Describe(string outcome, List<string> notes)
        => notes.Count == 0 ? outcome : $"{outcome} {string.Join(" ", notes)}";

    private static string LastLine(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrEmpty(line) ? "no error output" : line;
    }

    private static string ContainerLabelFor(VideoConversionSettings settings) => VideoConversionPlanner.ContainerLabel(settings.Container);

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }
}
