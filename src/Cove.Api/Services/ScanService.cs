using System.IO.Enumeration;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using ExtJobProgress = Cove.Plugins.IJobProgress;

namespace Cove.Api.Services;

public partial class ScanService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    IEventBus eventBus,
    IFingerprintService fingerprintService,
    IThumbnailService thumbnailService,
    TextExtractionService textExtractionService,
    ZipGalleryReader zipGalleryReader,
    ExtensionManager extensionManager,
    ILogger<ScanService> logger) : IScanService
{
    // Striped lock pool: serializes concurrent creation of the same folder path. A fixed pool keeps
    // memory constant — the previous per-path ConcurrentDictionary added a SemaphoreSlim for every
    // unique folder ever scanned and never released them, leaking for the life of the process. Hash
    // collisions only cause occasional extra serialization between unrelated folders, which is benign.
    private static readonly SemaphoreSlim[] FolderCreationLocks =
        Enumerable.Range(0, 256).Select(static _ => new SemaphoreSlim(1, 1)).ToArray();

    private static SemaphoreSlim GetFolderCreationLock(string dirPath)
        => FolderCreationLocks[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(dirPath) % (uint)FolderCreationLocks.Length];
    private static readonly TimeSpan FileModTimeUnchangedTolerance = TimeSpan.FromMilliseconds(1);

    // Number of changed/new files a worker accumulates before flushing them to the database in a
    // single transaction. Each Postgres commit is a network round-trip + fsync, so committing once
    // per file (as the original code did) dominated scan time on large libraries. Batching amortises
    // that cost; a failed batch falls back to per-file saves so one bad row can't poison its neighbours.
    private const int ScanSaveBatchSize = 50;

    // The default Npgsql command timeout (30s) is too tight for a full library scan's index loads and
    // batched saves on large/busy databases; exceeding it cascades into RetryLimitExceeded and aborts
    // the whole scan. Scan runs as a background job, so a generous per-command timeout is appropriate.
    private static readonly TimeSpan ScanCommandTimeout = TimeSpan.FromMinutes(5);

    // Resolving ffprobe walks PATH doing a File.Exists per entry. That is cheap once but was being
    // repeated for every single file (millions of redundant syscalls on a large scan). The resolved
    // path cannot change within a scan, so cache it after the first lookup.
    private readonly object _ffprobeResolveLock = new();
    private string? _cachedFfprobePath;
    private bool _ffprobeResolved;
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

    public async Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded video file not found", path);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (videoFile, _) = await ProcessVideoFileAsync(db, path, videoId, ct);
        await db.SaveChangesAsync(ct);

        var resolvedVideoId = videoFile.VideoId;
        if (!resolvedVideoId.HasValue || resolvedVideoId.Value == 0)
            throw new InvalidOperationException($"Imported video file {path} was not attached to a video");

        eventBus.Publish(new EntityEvent(
            videoId.HasValue ? EventType.VideoUpdated : EventType.VideoCreated,
            "Video",
            resolvedVideoId.Value));

        return resolvedVideoId.Value;
    }

    public async Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded image file not found", path);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (image, _) = await ProcessImageFileAsync(db, path, imageId, ct);
        await db.SaveChangesAsync(ct);

        if (image.Id == 0)
            throw new InvalidOperationException($"Imported image file {path} was not attached to an image");

        eventBus.Publish(new EntityEvent(
            imageId.HasValue ? EventType.ImageUpdated : EventType.ImageCreated,
            "Image",
            image.Id));

        return image.Id;
    }

    public async Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded gallery file not found", path);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var gallery = await ProcessGalleryFileAsync(db, path, galleryId, ct);
        await db.SaveChangesAsync(ct);

        if (gallery.Id == 0)
            throw new InvalidOperationException($"Imported gallery file {path} was not attached to a gallery");

        eventBus.Publish(new EntityEvent(
            galleryId.HasValue ? EventType.GalleryUpdated : EventType.GalleryCreated,
            "Gallery",
            gallery.Id));

        return gallery.Id;
    }

    public async Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded audio file not found", path);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (audio, _) = await ProcessAudioFileAsync(db, path, audioId, ct);
        await db.SaveChangesAsync(ct);

        if (audio.Id == 0)
            throw new InvalidOperationException($"Imported audio file {path} was not attached to an audio item");

        eventBus.Publish(new EntityEvent(
            audioId.HasValue ? EventType.AudioUpdated : EventType.AudioCreated,
            "Audio",
            audio.Id));

        return audio.Id;
    }

    public async Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded text file not found", path);

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (textDocument, _) = await ProcessTextFileAsync(db, path, textDocumentId, ct);
        await db.SaveChangesAsync(ct);

        if (textDocument.Id == 0)
            throw new InvalidOperationException($"Imported text file {path} was not attached to a text document");

        eventBus.Publish(new EntityEvent(
            textDocumentId.HasValue ? EventType.TextUpdated : EventType.TextCreated,
            "Text",
            textDocument.Id));

        return textDocument.Id;
    }

    public string StartScan(ScanOperationOptions? options = null)
    {
        options ??= new ScanOperationOptions();

        return jobService.Enqueue("scan", "Scanning library", async (progress, ct) =>
        {
            var cfg = config;
            var scanTargets = ResolveScanTargets(cfg, options.Paths);

            if (scanTargets.Count == 0)
            {
                logger.LogWarning("No cove paths configured. Nothing to scan.");
                return;
            }

            var videoExts = new HashSet<string>(cfg.VideoExtensions, StringComparer.OrdinalIgnoreCase);
            var imageExts = new HashSet<string>(cfg.ImageExtensions, StringComparer.OrdinalIgnoreCase);
            var galleryExts = new HashSet<string>(cfg.GalleryExtensions, StringComparer.OrdinalIgnoreCase);
            var audioExts = new HashSet<string>(cfg.AudioExtensions, StringComparer.OrdinalIgnoreCase);
            var textExts = new HashSet<string>(cfg.TextExtensions, StringComparer.OrdinalIgnoreCase);
            var allExts = videoExts.Union(imageExts).Union(galleryExts).Union(audioExts).Union(textExts).ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Per-directory cache of caption sidecar files (.vtt/.srt), shared across workers,
            // so each directory is enumerated once per scan instead of once per video.
            var captionFilesByDir = new ConcurrentDictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

            // Written from multiple scan workers, so these must be concurrent collections.
            var processedVideoPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var processedImagePaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var processedAudioPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var processedTextPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            var ignoreRuleCache = new Dictionary<string, List<IgnoreRule>>(StringComparer.OrdinalIgnoreCase);

            var scanStopwatch = Stopwatch.StartNew();

            // Phase 1: Discover files
            progress.Report(0, "Discovering files...");
            var files = new List<DiscoveredFile>();
            var discoveryProgress = new ScanDiscoveryProgress(progress, logger);
            foreach (var scanTarget in scanTargets)
            {
                if (scanTarget.IsFile)
                {
                    if (!File.Exists(scanTarget.Path))
                    {
                        logger.LogWarning("Scan target does not exist: {Path}", scanTarget.Path);
                        continue;
                    }

                    var ext = Path.GetExtension(scanTarget.Path);
                    if (!allExts.Contains(ext))
                    {
                        continue;
                    }
                    if (IsMediaTypeExcludedByScanTarget(
                        ext,
                        scanTarget.ExcludeVideo,
                        scanTarget.ExcludeImage,
                        scanTarget.ExcludeAudio,
                        scanTarget.ExcludeText,
                        videoExts,
                        imageExts,
                        galleryExts,
                        audioExts,
                        textExts))
                        continue;
                    if (IsExcludedByConfiguredPatterns(scanTarget.Path, ext, imageExts, galleryExts, cfg)
                        || IsExcludedByFolderIgnore(scanTarget.Path, Path.GetDirectoryName(scanTarget.Path) ?? scanTarget.Path, ignoreRuleCache))
                    {
                        continue;
                    }

                    if (TryCreateDiscoveredFile(scanTarget.Path, ext, out var discoveredFile))
                    {
                        files.Add(discoveredFile);
                        discoveryProgress.RecordMediaFile(discoveredFile.Path);
                    }
                    continue;
                }

                if (!Directory.Exists(scanTarget.Path))
                {
                    logger.LogWarning("Scan target does not exist: {Path}", scanTarget.Path);
                    continue;
                }

                foreach (var discoveredFile in DiscoverFilesSafely(
                    scanTarget,
                    allExts,
                    videoExts,
                    imageExts,
                    galleryExts,
                    audioExts,
                    textExts,
                    cfg,
                    ignoreRuleCache,
                    discoveryProgress,
                    ct))
                {
                    files.Add(discoveredFile);
                }
            }
            discoveryProgress.Complete();

            logger.LogInformation(
                "Scan phase discovery completed in {ElapsedMs} ms. Discovered {FileCount} media files across {DirectoryCount} directories; skipped {IgnoredPathCount} ignored paths and {UnsupportedFileCount} unsupported files.",
                scanStopwatch.ElapsedMilliseconds,
                files.Count,
                discoveryProgress.DirectoryCount,
                discoveryProgress.IgnoredPathCount,
                discoveryProgress.UnsupportedFileCount);

            // Overlapping scan targets can surface the same physical file more than once.
            // De-duplicate by stored path so we never process a file twice, and so the
            // new-file fast path below can safely skip its existence lookup.
            if (files.Count > 0)
            {
                var beforeDedup = files.Count;
                files = files
                    .GroupBy(file => file.StoredPath, FilesystemPaths.PathComparer)
                    .Select(group => group.First())
                    .ToList();
                if (files.Count != beforeDedup)
                    logger.LogInformation("Scan de-duplicated {DuplicateCount} discovered file path(s).", beforeDedup - files.Count);

                // Process files in stable alphabetical (a-z) path order. This matches stash's
                // directory-sorted walk so progress is predictable, and — because a sorted path list
                // groups every file in a directory contiguously — it keeps the parallel workers
                // reading from the same / adjacent folders at once for far better disk locality.
                files = files
                    .OrderBy(file => file.StoredPath, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (files.Count > 0)
            {
                // Phase 2: Process files
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                // The default 30s Npgsql command timeout is too tight for a full library scan: loading
                // the existing file index and the large batched saves below can exceed it on big/busy
                // libraries, surfacing as RetryLimitExceeded -> TimeoutException and aborting the scan.
                // Scan is a background job, so allow generous time per command. Guarded because the
                // in-memory provider used by tests is non-relational and would throw here.
                if (db.Database.IsRelational())
                    db.Database.SetCommandTimeout(ScanCommandTimeout);
                progress.Report(0.10, $"Loading existing file index for {files.Count:N0} media files...");
                var indexStopwatch = Stopwatch.StartNew();
                var existingFiles = await LoadExistingFileScanIndexAsync(db, files, videoExts, imageExts, galleryExts, audioExts, textExts, progress, logger, ct);
                logger.LogInformation(
                    "Scan phase existing-file index completed in {ElapsedMs} ms. Matched {ExistingCount} of {DiscoveredCount} discovered media files.",
                    indexStopwatch.ElapsedMilliseconds,
                    existingFiles.Count,
                    files.Count);

                // Move/rename detection is only meaningful when the library already has files (a first
                // scan has nothing to move). Gating on existing files also avoids paying for per-new-file
                // identity lookups on the initial import, where none can possibly match.
                var moveDetectionEnabled = config.EnableMoveDetection
                    && await db.Set<BaseFileEntity>().AnyAsync(f => f.ZipFileId == null, ct);
                var moveIndex = new MoveDetectionIndex { Enabled = moveDetectionEnabled };
                if (moveDetectionEnabled)
                    logger.LogInformation("Move/rename detection enabled for this scan.");

                void PublishScanEntityEvent(string entityType, int entityId, bool isUpdate)
                {
                    var eventType = entityType switch
                    {
                        "Video" => isUpdate ? EventType.VideoUpdated : EventType.VideoCreated,
                        "Image" => isUpdate ? EventType.ImageUpdated : EventType.ImageCreated,
                        "Gallery" => isUpdate ? EventType.GalleryUpdated : EventType.GalleryCreated,
                        "Audio" => isUpdate ? EventType.AudioUpdated : EventType.AudioCreated,
                        "Text" => isUpdate ? EventType.TextUpdated : EventType.TextCreated,
                        _ => EventType.ScanCompleted,
                    };

                    eventBus.Publish(new EntityEvent(eventType, entityType, entityId));
                }

                var processedCount = 0;
                var skippedUnchangedCount = 0;
                var changedOrNewCount = 0;
                var newFileCount = 0;
                var metadataProbeCount = 0;
                var sizeChangedCount = 0;
                var modTimeChangedCount = 0;
                var rescanForcedCount = 0;
                var typeMismatchCount = 0;
                var failedCount = 0;
                var processStopwatch = Stopwatch.StartNew();
                var lastProcessProgressAt = DateTime.MinValue;
                var progressLock = new object();

                // Called from multiple workers, so it is fully guarded.
                void ReportProcessingProgress(bool force, string? path = null)
                {
                    lock (progressLock)
                    {
                        var now = DateTime.UtcNow;
                        if (!force && processedCount % 1000 != 0 && (now - lastProcessProgressAt).TotalSeconds < 1)
                            return;

                        lastProcessProgressAt = now;
                        var ratio = files.Count == 0 ? 1d : (double)processedCount / files.Count;
                        var message = $"Checking media files ({processedCount:N0}/{files.Count:N0}; {skippedUnchangedCount:N0} unchanged, {changedOrNewCount:N0} changed/new)";
                        if (!string.IsNullOrWhiteSpace(path))
                            message += $": {Path.GetFileName(path)}";
                        progress.Report(0.15 + (0.70 * ratio), message);
                    }
                }

                // Phase 2a: classify every discovered file against the in-memory index.
                // This is cheap (no I/O, no DB) so it stays single-threaded; it skips
                // unchanged files and collects only the ones that actually need work.
                var filesToProcess = new List<(DiscoveredFile File, bool IsKnownFile, bool ContentChanged)>(files.Count);
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();

                    // True when a known file's bytes changed on disk (or a rescan forces reprocessing):
                    // its metadata, identity hash, and derived assets must be refreshed, not just its
                    // size/modtime. A metadata-only reprobe (metadata was never captured) is handled
                    // separately and does not count as a content change.
                    var contentChanged = false;
                    var isKnownFile = existingFiles.TryGetValue(file.StoredPath, out var existingFile);
                    if (isKnownFile)
                    {
                        var changeReason = GetKnownFileChangeReason(existingFile!, file, options.Rescan);
                        if (changeReason == ScanFileChangeReason.Unchanged)
                        {
                            skippedUnchangedCount++;
                            processedCount++;
                            ReportProcessingProgress(false);
                            continue; // Not modified and metadata present, skip
                        }

                        switch (changeReason)
                        {
                            case ScanFileChangeReason.MetadataProbe:
                                metadataProbeCount++;
                                break;
                            case ScanFileChangeReason.SizeChanged:
                                sizeChangedCount++;
                                contentChanged = true;
                                break;
                            case ScanFileChangeReason.ModTimeChanged:
                                modTimeChangedCount++;
                                contentChanged = true;
                                break;
                            case ScanFileChangeReason.RescanForced:
                                rescanForcedCount++;
                                contentChanged = true;
                                break;
                        }

                        TraceKnownFileClassified(
                            file.StoredPath,
                            existingFile!.Kind,
                            changeReason,
                            existingFile.Size,
                            file.Size);

                        var expectedKind = GetExpectedFileKind(
                            file.Extension,
                            videoExts,
                            imageExts,
                            galleryExts,
                            audioExts,
                            textExts);
                        if (existingFile!.Kind != ExistingFileKind.Unknown
                            && expectedKind != ExistingFileKind.Unknown
                            && existingFile.Kind != expectedKind)
                        {
                            typeMismatchCount++;
                            logger.LogWarning(
                                "Skipping changed scan path because it already exists as {ExistingKind} but extension maps to {ExpectedKind}: {Path}",
                                existingFile.Kind,
                                expectedKind,
                                file.Path);
                            processedCount++;
                            ReportProcessingProgress(false);
                            continue;
                        }
                    }
                    else
                    {
                        newFileCount++;
                        TraceNewFileClassified(file.StoredPath);
                    }

                    changedOrNewCount++;
                    filesToProcess.Add((file, isKnownFile, contentChanged));
                }

                // Resolve every parent folder once, up front, into a shared id map. Workers then look
                // folders up in memory instead of each re-querying (and re-locking) the Folders table,
                // and the batched save path below stays free of incidental folder writes.
                var folderIdsByPath = await ResolveScanFolderIdsAsync(db, filesToProcess, ct);

                // Phase 2b: process the changed/new files across a fixed pool of workers.
                // Concurrency is capped at the configured maximum so the UI and other jobs
                // are not starved (a value of 1 reproduces the original sequential path).
                // Each worker owns its own DbContext because EF contexts are not thread-safe;
                // shared state is updated via thread-safe primitives. Workers commit in batches
                // (ScanSaveBatchSize) to amortise Postgres commit overhead; a failed batch is
                // retried one file at a time so a single bad file can never abort its neighbours.
                var maxParallelism = Math.Max(1, ResolveMaxParallelism());
                if (filesToProcess.Count > 0)
                {
                    // Never spin up more workers (each opens its own DB connection) than there
                    // is work for, but otherwise honour the configured concurrency ceiling.
                    var workerCount = Math.Min(maxParallelism, filesToProcess.Count);
                    progress.Report(0.15, $"Processing {filesToProcess.Count:N0} changed/new file(s) using up to {workerCount} worker(s)...");

                    var workQueue = new ConcurrentQueue<(DiscoveredFile File, bool IsKnownFile, bool ContentChanged)>(filesToProcess);

                    int? ResolveFolderId(DiscoveredFile file)
                    {
                        var dir = NormalizeStoredFolderPath(Path.GetDirectoryName(file.Path) ?? file.Path);
                        return folderIdsByPath.TryGetValue(dir, out var id) ? id : null;
                    }

                    async Task RunScanWorkerAsync()
                    {
                        using var workerScope = scopeFactory.CreateScope();
                        var workerDb = workerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        // Batched commits can exceed the default 30s command timeout on large/busy
                        // libraries; give scan workers a generous timeout to avoid spurious abort.
                        if (workerDb.Database.IsRelational())
                            workerDb.Database.SetCommandTimeout(ScanCommandTimeout);

                        // The current un-committed batch, plus the entity events to publish once it commits.
                        var batchItems = new List<(DiscoveredFile File, bool IsKnownFile, bool ContentChanged)>(ScanSaveBatchSize);
                        var batchEvents = new List<Action>(ScanSaveBatchSize);

                        // Stage one file's entities into the worker context (no save). Appends the event
                        // to fire once persisted. Galleries are excluded here as they commit internally.
                        async Task StageAsync((DiscoveredFile File, bool IsKnownFile, bool ContentChanged) work, List<Action> events)
                        {
                            var file = work.File;
                            var isKnownFile = work.IsKnownFile;
                            var contentChanged = work.ContentChanged;
                            var folderId = ResolveFolderId(file);

                            if (videoExts.Contains(file.Extension))
                            {
                                processedVideoPaths.TryAdd(file.Path, 0);
                                var (videoFile, relinked) = await ProcessVideoFileAsync(workerDb, file.Path, null, ct, file.Stat, null, syncCaptions: true, knownNew: !isKnownFile, captionFilesByDir: captionFilesByDir, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() => { if (videoFile.VideoId.HasValue) PublishScanEntityEvent("Video", videoFile.VideoId.Value, isKnownFile || relinked); });
                            }
                            else if (imageExts.Contains(file.Extension))
                            {
                                processedImagePaths.TryAdd(file.Path, 0);
                                var (image, relinked) = await ProcessImageFileAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() => PublishScanEntityEvent("Image", image.Id, isKnownFile || relinked));
                            }
                            else if (audioExts.Contains(file.Extension))
                            {
                                processedAudioPaths.TryAdd(file.Path, 0);
                                var (audio, relinked) = await ProcessAudioFileAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() => PublishScanEntityEvent("Audio", audio.Id, isKnownFile || relinked));
                            }
                            else if (textExts.Contains(file.Extension))
                            {
                                processedTextPaths.TryAdd(file.Path, 0);
                                var (textDocument, relinked) = await ProcessTextFileAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() => PublishScanEntityEvent("Text", textDocument.Id, isKnownFile || relinked));
                            }
                        }

                        // Process a single item in its own transaction. Used for galleries (which commit
                        // internally) and as the fallback when a batch save fails.
                        async Task ProcessSingleAsync((DiscoveredFile File, bool IsKnownFile, bool ContentChanged) work)
                        {
                            var file = work.File;
                            var isKnownFile = work.IsKnownFile;
                            try
                            {
                                if (galleryExts.Contains(file.Extension))
                                {
                                    var gallery = await ProcessGalleryFileAsync(workerDb, file.Path, null, ct, file.Stat, null, parentFolderId: ResolveFolderId(file));
                                    await workerDb.SaveChangesAsync(ct);
                                    PublishScanEntityEvent("Gallery", gallery.Id, isKnownFile);
                                }
                                else
                                {
                                    var events = new List<Action>(1);
                                    await StageAsync(work, events);
                                    await workerDb.SaveChangesAsync(ct);
                                    foreach (var publish in events)
                                        publish();
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            {
                                Interlocked.Increment(ref failedCount);
                                logger.LogError(ex, "Error processing file: {Path}", file.Path);
                            }
                            finally
                            {
                                workerDb.ChangeTracker.Clear();
                            }
                        }

                        // Commit the staged batch. On failure, discard it and retry each item individually
                        // so one bad row can't fail the whole group.
                        async Task FlushBatchAsync()
                        {
                            if (batchItems.Count == 0)
                                return;

                            try
                            {
                                await workerDb.SaveChangesAsync(ct);
                                TraceScanBatchCommitted(batchItems.Count);
                                workerDb.ChangeTracker.Clear();
                                foreach (var publish in batchEvents)
                                    publish();
                                batchItems.Clear();
                                batchEvents.Clear();
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            {
                                logger.LogWarning(ex, "Batched scan save of {Count} file(s) failed; retrying individually.", batchItems.Count);
                                workerDb.ChangeTracker.Clear();
                                var retryItems = batchItems.ToList();
                                batchItems.Clear();
                                batchEvents.Clear();
                                foreach (var retry in retryItems)
                                    await ProcessSingleAsync(retry);
                            }
                        }

                        while (workQueue.TryDequeue(out var item))
                        {
                            if (ct.IsCancellationRequested)
                                break;

                            var file = item.File;

                            if (galleryExts.Contains(file.Extension))
                            {
                                // Galleries commit internally, so flush any pending batch first to keep
                                // ordering and error isolation intact.
                                await FlushBatchAsync();
                                await ProcessSingleAsync(item);
                            }
                            else
                            {
                                try
                                {
                                    await StageAsync(item, batchEvents);
                                    batchItems.Add(item);
                                    if (batchItems.Count >= ScanSaveBatchSize)
                                        await FlushBatchAsync();
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                                {
                                    // Staging threw mid-item, leaving partial tracked state. Drop the
                                    // whole pending batch, count this file as failed, and re-stage the
                                    // previously-good items one at a time. Rare: the hot path (new files,
                                    // ffprobe errors are swallowed internally) does not throw here.
                                    Interlocked.Increment(ref failedCount);
                                    logger.LogError(ex, "Error processing file: {Path}", file.Path);
                                    workerDb.ChangeTracker.Clear();
                                    var good = batchItems.ToList();
                                    batchItems.Clear();
                                    batchEvents.Clear();
                                    foreach (var recovered in good)
                                        await ProcessSingleAsync(recovered);
                                }
                            }

                            Interlocked.Increment(ref processedCount);
                            ReportProcessingProgress(false, file.Path);
                        }

                        await FlushBatchAsync();
                    }

                    var workers = new Task[workerCount];
                    for (var workerIndex = 0; workerIndex < workerCount; workerIndex++)
                        workers[workerIndex] = RunScanWorkerAsync();
                    await Task.WhenAll(workers);
                    ct.ThrowIfCancellationRequested();
                }

                ReportProcessingProgress(true);

                logger.LogInformation(
                    "Scan phase processing completed in {ElapsedMs} ms. Checked {CheckedCount} files; skipped {SkippedCount} unchanged; processed {ChangedOrNewCount} changed/new; new={NewFileCount}, metadataProbe={MetadataProbeCount}, sizeChanged={SizeChangedCount}, modTimeChanged={ModTimeChangedCount}, rescanForced={RescanForcedCount}, typeMismatch={TypeMismatchCount}, failed={FailedCount}.",
                    processStopwatch.ElapsedMilliseconds,
                    processedCount,
                    skippedUnchangedCount,
                    changedOrNewCount,
                    newFileCount,
                    metadataProbeCount,
                    sizeChangedCount,
                    modTimeChangedCount,
                    rescanForcedCount,
                    typeMismatchCount,
                    failedCount);

                // Phase 3: Create galleries from folders (if enabled)
                if (cfg.CreateGalleriesFromFolders || HasForceGalleryHints(files))
                {
                    progress.Report(0.90, "Creating galleries from folders...");
                    await CreateGalleriesFromFoldersAsync(db, cfg.CreateGalleriesFromFolders, ct);
                }

                await GenerateRequestedAssetsAsync(
                    db,
                    progress,
                    new HashSet<string>(processedVideoPaths.Keys, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(processedImagePaths.Keys, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(processedAudioPaths.Keys, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(processedTextPaths.Keys, StringComparer.OrdinalIgnoreCase),
                    options,
                    thumbnailService,
                    ct);
            }

            // Phase 5: Extension scan participants
            var participants = extensionManager.GetScanParticipants()
                .Select(participant => (
                    Participant: participant,
                    Execution: extensionManager.CaptureExtensionExecution(participant)))
                .ToList();
            if (participants.Count > 0)
            {
                var scanPathInfos = scanTargets
                    .Select(t => new ScanPathInfo(t.Path, t.ExcludeVideo, t.ExcludeImage, t.ExcludeAudio, t.IsFile, t.ExcludeText))
                    .ToList();

                for (var i = 0; i < participants.Count; i++)
                {
                    var participant = participants[i];
                    var participantProgress = new ScopedProgress(progress,
                        rangeStart: 0.95 + (0.05 * i / participants.Count),
                        rangeEnd: 0.95 + (0.05 * (i + 1) / participants.Count));

                    try
                    {
                        logger.LogInformation("Running scan participant: {Name}", participant.Participant.Name);
                        // Overlay-aware scope so a runtime extension participant can resolve its own services.
                        using var participantScope = extensionManager.CreateExtensionScope(participant.Execution);
                        var scanContext = new ScanContext(scanPathInfos, participantProgress, participantScope.ServiceProvider, options.Rescan);
                        await participant.Participant.ScanAsync(scanContext, ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Extension scan participant {Name} failed", participant.Participant.Name);
                    }
                }
            }

            logger.LogInformation("Scan completed. Processed {Count} core files, {ParticipantCount} extension participant(s)", files.Count, participants.Count);
            logger.LogInformation("Scan total elapsed time: {ElapsedMs} ms", scanStopwatch.ElapsedMilliseconds);
            eventBus.Publish(new CoveEvent(EventType.ScanCompleted));
        });
    }

    private bool TryCreateDiscoveredFile(string path, string extension, out DiscoveredFile discoveredFile)
    {
        try
        {
            var normalizedPath = NormalizePath(path);
            var fileInfo = new FileInfo(normalizedPath);
            discoveredFile = new DiscoveredFile(
                normalizedPath,
                NormalizeStoredFilePath(normalizedPath),
                extension,
                new FileStat(fileInfo.Length, NormalizeFileModTime(fileInfo.LastWriteTimeUtc)));
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException)
        {
            logger.LogWarning(ex, "Skipping unreadable scan file: {Path}", path);
            discoveredFile = null!;
            return false;
        }
    }

    private static async Task<Dictionary<string, ExistingFileScanInfo>> LoadExistingFileScanIndexAsync(
        CoveContext db,
        IReadOnlyCollection<DiscoveredFile> files,
        IReadOnlySet<string> videoExts,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts,
        IReadOnlySet<string> audioExts,
        IReadOnlySet<string> textExts,
        Cove.Core.Interfaces.IJobProgress progress,
        ILogger<ScanService> logger,
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

    private static ScanFileChangeReason GetKnownFileChangeReason(ExistingFileScanInfo existingFile, DiscoveredFile file, bool rescan)
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

    private static ExistingFileKind GetExpectedFileKind(
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

    private static async Task AddExistingBaseFilesAsync(
        CoveContext db,
        Dictionary<string, ExistingFileScanInfo> index,
        IReadOnlyCollection<DiscoveredFile> files,
        Cove.Core.Interfaces.IJobProgress progress,
        ILogger<ScanService> logger,
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
        Cove.Core.Interfaces.IJobProgress progress,
        ILogger<ScanService> logger,
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
                file.Width <= 0 || file.Height <= 0 || file.Duration <= 0))
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

    private async Task GenerateRequestedAssetsAsync(
        CoveContext db,
        Cove.Core.Interfaces.IJobProgress progress,
        HashSet<string> processedVideoPaths,
        HashSet<string> processedImagePaths,
        HashSet<string> processedAudioPaths,
        HashSet<string> processedTextPaths,
        ScanOperationOptions options,
        IThumbnailService thumbnailService,
        CancellationToken ct)
    {
        var generateVideoAssets = options.GenerateCovers || options.GeneratePreviews || options.GenerateSprites || options.GeneratePhashes || options.GenerateMd5;
        var generateImageAssets = options.GenerateImagePhashes || options.GenerateImageThumbnails || options.GenerateMd5;
        var generateAudioAssets = options.GenerateAudioPhashes || options.GenerateMd5;
        var generateTextAssets = options.GenerateTextPhashes || options.GenerateMd5;

        if ((!generateVideoAssets && !generateImageAssets && !generateAudioAssets && !generateTextAssets)
            || (processedVideoPaths.Count == 0 && processedImagePaths.Count == 0 && processedAudioPaths.Count == 0 && processedTextPaths.Count == 0))
        {
            return;
        }

        if (generateVideoAssets && processedVideoPaths.Count > 0)
        {
            progress.Report(0.92, "Generating video assets...");

            var videoDirs = processedVideoPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.VideoFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && videoDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var videoFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedVideoPaths.Contains(NormalizePath(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .Where(file => file.VideoId.HasValue && file.VideoId.Value != 0)
                .GroupBy(file => file.VideoId)
                .Select(group => group.First())
                .Where(file =>
                {
                    var videoId = file.VideoId!.Value;
                    return (options.GenerateCovers && !File.Exists(thumbnailService.GetThumbnailPathForVideo(videoId)))
                        || (options.GeneratePreviews && !File.Exists(thumbnailService.GetPreviewPath(videoId)))
                        || (options.GenerateSprites && (!File.Exists(thumbnailService.GetSpritePath(videoId)) || !File.Exists(thumbnailService.GetSpriteVttPath(videoId))))
                        || (options.GeneratePhashes && !file.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                        || (options.GenerateMd5 && !file.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)));
                })
                .ToList();

            var total = Math.Max(videoFiles.Count, 1);
            var completed = 0;
            var failed = 0;

            var maxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(videoFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (videoFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                var videoId = videoFile.VideoId!.Value;

                progress.Report(0.92 + (0.06 * done / total), $"Generating video assets ({done}/{videoFiles.Count})");

                // Isolate each video: a single corrupt/locked file (e.g. an ffmpeg decode failure or an
                // IO error computing MD5) must not abort the whole batch. Parallel.ForEachAsync cancels
                // all workers on the first unhandled exception, which is why one bad file previously
                // poisoned the entire generation run. Swallow per-file errors and keep going; only real
                // cancellation propagates.
                try
                {
                if (options.GenerateCovers)
                {
                    var thumbnailPath = thumbnailService.GetThumbnailPathForVideo(videoId);
                    if (!File.Exists(thumbnailPath))
                        await thumbnailService.GenerateVideoThumbnailAsync(videoId, null, token);
                }
                if (options.GeneratePreviews)
                {
                    var previewPath = thumbnailService.GetPreviewPath(videoId);
                    if (!File.Exists(previewPath))
                        await thumbnailService.GenerateVideoPreviewAsync(videoId, token);
                }
                if (options.GenerateSprites)
                {
                    var spritePath = thumbnailService.GetSpritePath(videoId);
                    var spriteVttPath = thumbnailService.GetSpriteVttPath(videoId);
                    if (!File.Exists(spritePath) || !File.Exists(spriteVttPath))
                        await thumbnailService.GenerateVideoSpriteAsync(videoId, token);
                }
                if (options.GeneratePhashes
                    && videoFile.ParentFolder != null
                    && !videoFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var filePath = Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename);
                    var phash = await fingerprintService.ComputeVideoPhashAsync(filePath, videoFile.Duration, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == videoFile.Id && fp.Type == "phash", token);
                        if (existing != null)
                        {
                            existing.Value = phash;
                        }
                        else
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint
                            {
                                FileId = videoFile.Id,
                                Type = "phash",
                                Value = phash,
                            });
                        }
                        await innerDb.SaveChangesAsync(token);
                    }
                }
                if (options.GenerateMd5
                    && videoFile.ParentFolder != null
                    && !videoFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var filePath = Path.Combine(videoFile.ParentFolder.Path, videoFile.Basename);
                    var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == videoFile.Id && fp.Type == "md5", token);
                        if (existing != null)
                        {
                            existing.Value = md5;
                        }
                        else
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint
                            {
                                FileId = videoFile.Id,
                                Type = "md5",
                                Value = md5,
                            });
                        }
                        await innerDb.SaveChangesAsync(token);
                    }
                }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed generating assets for video {VideoId}; skipping", videoId);
                }
            });

            if (failed > 0)
                logger.LogWarning("Video asset generation completed with {Failed} failed of {Total} videos", failed, videoFiles.Count);
        }

        if (generateImageAssets && processedImagePaths.Count > 0)
        {
            progress.Report(0.98, "Generating image assets...");

            var imageDirs = processedImagePaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.ImageFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && imageDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var imageFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedImagePaths.Contains(NormalizePath(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(imageFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            var imgMaxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = imgMaxParallelism, CancellationToken = ct }, async (imageFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.98 + (0.01 * done / total), $"Generating image assets ({done}/{imageFiles.Count})");

                if (imageFile.ParentFolder == null)
                    return;

                // Isolate each image so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                if (options.GenerateImageThumbnails && imageFile.ImageId.HasValue)
                {
                    await thumbnailService.GenerateImageThumbnailAsync(imageFile.ImageId.Value, overwrite: false, ct: token);
                }

                if (options.GenerateImagePhashes
                    && !imageFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var filePath = Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename);
                    var phash = await fingerprintService.ComputeImagePhashAsync(filePath, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == imageFile.Id && fp.Type == "phash", token);
                        if (existing != null)
                        {
                            existing.Value = phash;
                        }
                        else
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint
                            {
                                FileId = imageFile.Id,
                                Type = "phash",
                                Value = phash,
                            });
                        }
                        await innerDb.SaveChangesAsync(token);
                    }
                }

                if (options.GenerateMd5
                    && !imageFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var filePath = Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename);
                    var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == imageFile.Id && fp.Type == "md5", token);
                        if (existing != null)
                        {
                            existing.Value = md5;
                        }
                        else
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint
                            {
                                FileId = imageFile.Id,
                                Type = "md5",
                                Value = md5,
                            });
                        }
                        await innerDb.SaveChangesAsync(token);
                    }
                }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed generating assets for image {ImageId}; skipping", imageFile.ImageId);
                }
            });

            if (failed > 0)
                logger.LogWarning("Image asset generation completed with {Failed} failed of {Total} images", failed, imageFiles.Count);
        }

        if (generateAudioAssets && processedAudioPaths.Count > 0)
        {
            progress.Report(0.99, "Generating audio fingerprints...");

            var audioDirs = processedAudioPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.AudioFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && audioDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var audioFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedAudioPaths.Contains(NormalizePath(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(audioFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            var maxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(audioFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (audioFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.99, $"Generating audio fingerprints ({done}/{audioFiles.Count})");

                if (audioFile.ParentFolder == null)
                    return;

                // Isolate each audio file so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                var filePath = Path.Combine(audioFile.ParentFolder.Path, audioFile.Basename);
                if (options.GenerateAudioPhashes
                    && !audioFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var phash = await fingerprintService.ComputeAudioPhashAsync(filePath, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == audioFile.Id && fp.Type == "phash", token);
                        if (existing != null) existing.Value = phash;
                        else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = audioFile.Id, Type = "phash", Value = phash });
                        await innerDb.SaveChangesAsync(token);
                    }
                }

                if (options.GenerateMd5
                    && !audioFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == audioFile.Id && fp.Type == "md5", token);
                        if (existing != null) existing.Value = md5;
                        else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = audioFile.Id, Type = "md5", Value = md5 });
                        await innerDb.SaveChangesAsync(token);
                    }
                }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed generating audio fingerprints for file {FileId}; skipping", audioFile.Id);
                }
            });

            if (failed > 0)
                logger.LogWarning("Audio fingerprint generation completed with {Failed} failed of {Total} files", failed, audioFiles.Count);
        }

        if (generateTextAssets && processedTextPaths.Count > 0)
        {
            progress.Report(0.99, "Generating text fingerprints...");

            var textDirs = processedTextPaths
                .Select(path => Path.GetDirectoryName(path))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var candidateFiles = await db.TextFiles
                .Include(f => f.ParentFolder)
                .Include(f => f.Fingerprints)
                .Where(f => f.ParentFolder != null && textDirs.Contains(f.ParentFolder.Path))
                .ToListAsync(ct);

            var textFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedTextPaths.Contains(NormalizePath(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .ToList();

            var total = Math.Max(textFiles.Count, 1);
            var completed = 0;
            var failed = 0;
            var maxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(textFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (textFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                progress.Report(0.99, $"Generating text fingerprints ({done}/{textFiles.Count})");

                if (textFile.ParentFolder == null)
                    return;

                // Isolate each text file so one unreadable/corrupt file can't abort the whole batch.
                try
                {
                var filePath = Path.Combine(textFile.ParentFolder.Path, textFile.Basename);
                if (options.GenerateTextPhashes
                    && !textFile.Fingerprints.Any(fp => fp.Type == "phash" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var phash = await fingerprintService.ComputeTextPhashAsync(filePath, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == textFile.Id && fp.Type == "phash", token);
                        if (existing != null) existing.Value = phash;
                        else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = textFile.Id, Type = "phash", Value = phash });
                        await innerDb.SaveChangesAsync(token);
                    }
                }

                if (options.GenerateMd5
                    && !textFile.Fingerprints.Any(fp => fp.Type == "md5" && !string.IsNullOrWhiteSpace(fp.Value)))
                {
                    var md5 = await fingerprintService.ComputeMd5Async(filePath, token);
                    if (!string.IsNullOrWhiteSpace(md5))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == textFile.Id && fp.Type == "md5", token);
                        if (existing != null) existing.Value = md5;
                        else innerDb.FileFingerprints.Add(new FileFingerprint { FileId = textFile.Id, Type = "md5", Value = md5 });
                        await innerDb.SaveChangesAsync(token);
                    }
                }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    logger.LogWarning(ex, "Failed generating text fingerprints for file {FileId}; skipping", textFile.Id);
                }
            });

            if (failed > 0)
                logger.LogWarning("Text fingerprint generation completed with {Failed} failed of {Total} files", failed, textFiles.Count);
        }
    }

    /// <summary>
    /// Create folder-based galleries for folders that contain images but have no gallery yet.
    /// </summary>
    private async Task CreateGalleriesFromFoldersAsync(CoveContext db, bool createAllEligibleFolders, CancellationToken ct)
    {
        // Find folders that contain image files but don't already have a gallery
        var foldersWithImages = await db.ImageFiles
            .Where(f => f.ParentFolderId != 0 && f.ZipFileId == null) // Only real folders, not zip virtual folders
            .Select(f => f.ParentFolderId)
            .Distinct()
            .ToListAsync(ct);

        if (foldersWithImages.Count == 0) return;

        // Get existing folder-based galleries
        var existingGalleryFolderIds = await db.Galleries
            .Where(g => g.FolderId != null && foldersWithImages.Contains(g.FolderId.Value))
            .Select(g => g.FolderId!.Value)
            .ToListAsync(ct);

        var newFolderIds = foldersWithImages.Except(existingGalleryFolderIds).ToList();
        if (newFolderIds.Count == 0) return;

        // Load the folders
        var folders = await db.Folders
            .Where(f => newFolderIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id, ct);

        var eligibleFolderIds = folders
            .Where(item => ShouldCreateFolderGallery(item.Value.Path, createAllEligibleFolders))
            .Select(item => item.Key)
            .ToHashSet();

        if (eligibleFolderIds.Count == 0) return;

        // Get image IDs per folder
        var imagesByFolder = await db.ImageFiles
            .Where(f => eligibleFolderIds.Contains(f.ParentFolderId) && f.ZipFileId == null && f.ImageId != null)
            .GroupBy(f => f.ParentFolderId)
            .Select(g => new { FolderId = g.Key, ImageIds = g.Select(f => f.ImageId!.Value).ToList() })
            .ToListAsync(ct);

        var createdGalleries = new List<Gallery>();
        foreach (var group in imagesByFolder)
        {
            if (!folders.TryGetValue(group.FolderId, out var folder)) continue;

            // Intentionally leave Title null on scan. Storing the folder name as the title makes it
            // impossible to filter for galleries that have no real title; the UI falls back to the
            // folder name for display when Title is null.
            var gallery = new Gallery
            {
                FolderId = folder.Id,
            };

            foreach (var imageId in group.ImageIds)
            {
                gallery.ImageGalleries.Add(new ImageGallery { ImageId = imageId, Gallery = gallery });
            }

            db.Galleries.Add(gallery);
            createdGalleries.Add(gallery);
            logger.LogTrace("Created folder gallery for: {Path} with {Count} images", folder.Path, group.ImageIds.Count);
        }

        await db.SaveChangesAsync(ct);
        foreach (var gallery in createdGalleries)
            eventBus.Publish(new EntityEvent(EventType.GalleryCreated, "Gallery", gallery.Id));
    }

    /// <summary>
    /// Resolves (and creates, where missing) the parent folder of every file about to be processed,
    /// returning a path → folder-id map shared by all scan workers.
    ///
    /// Previously each worker owned its own folder cache and re-queried the database for the same
    /// folders, so a single folder could be looked up once per worker. Doing the resolution once up
    /// front — and outside the parallel phase — means workers never touch the Folders table and the
    /// per-folder creation locks are never contended during processing, which also keeps the batched
    /// SaveChanges path (below) free of incidental folder writes.
    /// </summary>
    private async Task<ConcurrentDictionary<string, int>> ResolveScanFolderIdsAsync(
        CoveContext db,
        IReadOnlyCollection<(DiscoveredFile File, bool IsKnownFile, bool ContentChanged)> filesToProcess,
        CancellationToken ct)
    {
        // Use the host filesystem's case sensitivity so two folders differing only by case (distinct on
        // Linux, e.g. .../Weibtm and .../weibtm) get separate folder ids instead of being collapsed —
        // which would make their identically-named files collide on the unique (ParentFolderId, Basename) index.
        var folderIdsByPath = new ConcurrentDictionary<string, int>(FilesystemPaths.PathComparer);

        var directories = filesToProcess
            .Select(item => NormalizeStoredFolderPath(Path.GetDirectoryName(item.File.Path) ?? item.File.Path))
            .Distinct(FilesystemPaths.PathComparer)
            .ToList();

        if (directories.Count == 0)
            return folderIdsByPath;

        // Load all already-known folders in bulk.
        foreach (var chunk in directories.Chunk(1000))
        {
            var rows = await db.Folders
                .AsNoTracking()
                .Where(folder => chunk.Contains(folder.Path))
                .Select(folder => new { folder.Path, folder.Id })
                .ToListAsync(ct);

            foreach (var row in rows)
                folderIdsByPath[row.Path] = row.Id;
        }

        // Create any folders that don't exist yet. Shallowest paths first so a child can pick up its
        // parent's id from the map without an extra query.
        var missing = directories
            .Where(dir => !folderIdsByPath.ContainsKey(dir))
            .OrderBy(dir => dir.Count(c => c == '/'))
            .ThenBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // A discovered directory that didn't match an existing folder by exact stored path may still be
        // the SAME physical directory as a folder already in the DB that was stored under a differently
        // normalized path — most commonly a Stash-migrated folder, whose path was stored with only
        // backslash->slash conversion rather than the scanner's full canonicalization (Path.GetFullPath +
        // trailing-slash trim). Reuse it by matching on canonicalized path; otherwise the scan creates a
        // duplicate folder and therefore a duplicate entry for every file under it. Canonical full-path
        // equality means the same directory, so this can never merge genuinely distinct folders.
        if (missing.Count > 0)
        {
            var canonicalFolderIds = new Dictionary<string, int>(FilesystemPaths.PathComparer);
            var candidateFolders = await db.Folders
                .AsNoTracking()
                .Select(folder => new { folder.Id, folder.Path })
                .ToListAsync(ct);
            foreach (var candidate in candidateFolders)
            {
                var canonical = TryCanonicalizeStoredFolderPath(candidate.Path);
                if (canonical != null)
                    canonicalFolderIds.TryAdd(canonical, candidate.Id);
            }

            var reusedByCanonicalPath = 0;
            foreach (var dir in missing)
            {
                if (!folderIdsByPath.ContainsKey(dir) && canonicalFolderIds.TryGetValue(dir, out var existingId))
                {
                    folderIdsByPath[dir] = existingId;
                    reusedByCanonicalPath++;
                }
            }

            if (reusedByCanonicalPath > 0)
                logger.LogInformation(
                    "Scan reused {Count} existing folder(s) matched by canonicalized path (differently-normalized stored paths, e.g. Stash-migrated) to avoid duplicate folders.",
                    reusedByCanonicalPath);

            missing = missing.Where(dir => !folderIdsByPath.ContainsKey(dir)).ToList();
        }

        foreach (var dir in missing)
        {
            if (folderIdsByPath.ContainsKey(dir))
                continue;

            var existing = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == dir, ct);
            if (existing != null)
            {
                folderIdsByPath[dir] = existing.Id;
                continue;
            }

            var folder = new Folder
            {
                Path = dir,
                ModTime = TryGetDirectoryModTime(dir),
            };

            var parentDir = GetParentStoredFolderPath(dir);
            if (!string.IsNullOrEmpty(parentDir) && parentDir != dir)
            {
                if (folderIdsByPath.TryGetValue(parentDir, out var parentId))
                    folder.ParentFolderId = parentId;
                else
                {
                    var parent = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == parentDir, ct);
                    if (parent != null)
                        folder.ParentFolderId = parent.Id;
                }
            }

            db.Folders.Add(folder);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Lost a race (or unique-constraint hit): fall back to the persisted row.
                db.Entry(folder).State = EntityState.Detached;
                var raced = await db.Folders.AsNoTracking().FirstOrDefaultAsync(f => f.Path == dir, ct);
                if (raced == null)
                    throw;
                folderIdsByPath[dir] = raced.Id;
                continue;
            }

            folderIdsByPath[dir] = folder.Id;
            db.Entry(folder).State = EntityState.Detached;
        }

        return folderIdsByPath;
    }

    private static DateTime TryGetDirectoryModTime(string dirPath)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(dirPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            return DateTime.UtcNow;
        }
    }

    private async Task<Folder> EnsureFolderAsync(CoveContext db, string dirPath, CancellationToken ct, Dictionary<string, Folder>? folderCache = null)
    {
        dirPath = NormalizeStoredFolderPath(dirPath);
        if (folderCache != null && folderCache.TryGetValue(dirPath, out var cachedFolder))
            return cachedFolder;

        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
        if (folder != null)
        {
            folderCache?.TryAdd(dirPath, folder);
            return folder;
        }

        var folderLock = GetFolderCreationLock(dirPath);
        await folderLock.WaitAsync(ct);
        try
        {
            folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
            if (folder != null)
            {
                folderCache?.TryAdd(dirPath, folder);
                return folder;
            }

            folder = new Folder
            {
                Path = dirPath,
                ModTime = Directory.GetLastWriteTimeUtc(dirPath)
            };

            // Link parent folder if path has a parent
            var parentDir = GetParentStoredFolderPath(dirPath);
            if (!string.IsNullOrEmpty(parentDir) && parentDir != dirPath)
            {
                var parentFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == parentDir, ct);
                if (parentFolder != null)
                    folder.ParentFolderId = parentFolder.Id;
            }

            db.Folders.Add(folder);
            try
            {
                await db.SaveChangesAsync(ct);
                folderCache?.TryAdd(dirPath, folder);
                return folder;
            }
            catch (DbUpdateException)
            {
                db.Entry(folder).State = EntityState.Detached;
                var existing = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
                if (existing != null)
                {
                    folderCache?.TryAdd(dirPath, existing);
                    return existing;
                }

                throw;
            }
        }
        finally
        {
            folderLock.Release();
        }
    }

    private async Task<(VideoFile File, bool Relinked)> ProcessVideoFileAsync(
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
        MoveDetectionIndex? moveIndex = null)
    {
        var stat = fileStat ?? GetFileStat(path);
        var dirPath = NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await EnsureFolderAsync(db, dirPath, ct, folderCache)).Id;

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
            if (contentChanged || NeedsVideoMetadataProbe(existing))
            {
                await ProbeVideoAsync(existing, path, ct);
            }

            if (contentChanged)
            {
                await RefreshChangedFileFingerprintsAsync(
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

            return (existing, false);
        }

        // No row at this path. Before creating a fresh entity, check whether this file's content already
        // exists in the library: a MOVE (re-point the now-missing record) or a DUPLICATE (attach as an
        // additional file of the existing video), rather than creating a separate duplicate entity.
        if (targetVideo == null && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await TryMatchExistingFileAsync(db.VideoFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match != null)
            {
                if (isMove)
                {
                    if (syncCaptions)
                        SyncVideoCaptions(match, path, captionFilesByDir);
                    logger.LogTrace("Re-linked moved video file to {NewPath} (previously {OldPath})", path, match.Path);
                    return (match, true);
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
                await EnrichVideoFileAsync(duplicateFile, path, ct, captionFilesByDir);
                logger.LogTrace("Attached duplicate video file {NewPath} to existing video {VideoId}", path, match.VideoId);
                return (duplicateFile, true);
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

        await EnrichVideoFileAsync(videoFile, path, ct, captionFilesByDir);

        logger.LogTrace("Added video file for: {Path}", path);
        return (videoFile, false);
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

    /// <summary>
    /// Match a file now appearing at <paramref name="path"/> against an already-known file with identical
    /// content (same oshash) and classify what to do:
    /// <list type="bullet">
    /// <item><c>(row, IsMove: true)</c> — the matched record's old location is gone, so the file MOVED:
    /// the returned row has been re-pointed to the new location (preserving its entity/tags/fingerprints).</item>
    /// <item><c>(row, IsMove: false)</c> — an identical file still exists on disk, so this is a DUPLICATE:
    /// the returned row identifies the entity the caller should attach the new file to (not mutated).</item>
    /// <item><c>(null, false)</c> — no confident single match; the caller should create a fresh entity.</item>
    /// </list>
    /// Byte-identity only (oshash): a move/copy preserves exact bytes, and oshash uniquely identifies any
    /// real media file (>64KB). Perceptual/fuzzy matching is intentionally NOT used — it could group a
    /// different-but-similar file into the wrong entity.
    /// </summary>
    private async Task<(TFile? Match, bool IsMove)> TryMatchExistingFileAsync<TFile>(
        DbSet<TFile> trackedSet,
        string path,
        int folderId,
        string basename,
        FileStat stat,
        MoveDetectionIndex moveIndex,
        CancellationToken ct)
        where TFile : BaseFileEntity
    {
        var oshash = await ComputeOshashAsync(path, ct);
        if (string.IsNullOrEmpty(oshash))
            return (null, false); // file too small to fingerprint (e.g. <64KB) — treat as brand new

        // Candidate rows carry the same oshash. Exclude zip-backed entries (their on-disk identity is the
        // archive, not a movable loose file). ParentFolder is deliberately NOT included: a move re-point
        // sets the FK and nulls the navigation so ComputeFilePaths derives the NEW path from the new id.
        var candidates = (await trackedSet
            .Where(f => f.ZipFileId == null
                && f.Fingerprints.Any(fp => fp.Type == "oshash" && fp.Value == oshash))
            .ToListAsync(ct))
            .Where(candidate => !moveIndex.ClaimedFileIds.ContainsKey(candidate.Id))
            .ToList();

        if (candidates.Count == 0)
            return (null, false);

        // Prefer a MOVE: if exactly one matching record's old path is gone, re-point it so its entity and
        // tags follow the file to its new location. (Done even when a duplicate also exists on disk, so a
        // real move never loses metadata to an unrelated copy.)
        var missing = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && !File.Exists(candidate.Path))
            .ToList();

        if (missing.Count == 1)
        {
            var claim = missing[0];
            if (!moveIndex.ClaimedFileIds.TryAdd(claim.Id, 0))
                return (null, false); // lost the race to another worker

            // Re-point the existing row to the new location. Null the navigation so the path recompute in
            // CoveContext.ComputeFilePaths batch-loads the NEW folder's path (it trusts a non-null nav).
            claim.ParentFolderId = folderId;
            claim.ParentFolder = null;
            claim.Basename = basename;
            claim.Size = stat.Size;
            claim.ModTime = stat.ModTime;
            return (claim, true);
        }

        // Otherwise, if an identical file still exists on disk, this is a duplicate: attach it as another
        // file of that existing entity rather than creating a separate one. Pick deterministically.
        var present = candidates
            .Where(candidate => !string.IsNullOrEmpty(candidate.Path) && File.Exists(candidate.Path))
            .OrderBy(candidate => candidate.Id)
            .FirstOrDefault();

        return present != null ? (present, false) : (null, false);
    }

    private async Task EnrichVideoFileAsync(
        VideoFile videoFile,
        string path,
        CancellationToken ct,
        ConcurrentDictionary<string, IReadOnlyList<string>>? captionFilesByDir = null)
    {
        // Probe with FFprobe for metadata
        await ProbeVideoAsync(videoFile, path, ct);

        // Compute oshash fingerprint
        var oshash = await ComputeOshashAsync(path, ct);
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

    private async Task<(Image Entity, bool Relinked)> ProcessImageFileAsync(
        CoveContext db,
        string path,
        int? imageId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool knownNew = false,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null)
    {
        var stat = fileStat ?? GetFileStat(path);
        var dirPath = NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await EnsureFolderAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = knownNew
            ? null
            : await db.ImageFiles
                .Include(f => f.Image)
                .Include(f => f.Fingerprints)
                .FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);

        // Consult entities added but not yet saved in this batch to avoid violating the unique
        // (ParentFolderId, Basename) index when a file is enumerated twice in one pass.
        existing ??= db.ImageFiles.Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;

            if (contentChanged)
            {
                await RefreshChangedFileFingerprintsAsync(
                    existing, path,
                    phashEnabled: scanOptions?.GenerateImagePhashes == true,
                    md5Enabled: config.CalculateMd5 || scanOptions?.GenerateMd5 == true,
                    ct);
                // Drop the stale thumbnail so the generation phase rebuilds it from the new content.
                if (scanOptions?.GenerateImageThumbnails == true && existing.ImageId is int changedImageId)
                    await thumbnailService.DeleteImageGeneratedFilesAsync(changedImageId, ct);
            }

            return (existing.Image ?? throw new InvalidOperationException($"Image file {path} is not attached to an image"), false);
        }

        // Content already in the library: re-link a moved image, or attach a duplicate to its entity.
        if (!imageId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await TryMatchExistingFileAsync(db.ImageFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.ImageId is int matchedImageId)
            {
                var parentImage = await db.Images.FirstOrDefaultAsync(item => item.Id == matchedImageId, ct);
                if (parentImage != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved image file to {NewPath} (previously {OldPath})", path, match.Path);
                        return (parentImage, true);
                    }

                    var duplicateFile = new ImageFile
                    {
                        Basename = basename,
                        ParentFolderId = folderId,
                        Size = stat.Size,
                        ModTime = stat.ModTime,
                        Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                        ImageId = matchedImageId,
                    };
                    db.ImageFiles.Add(duplicateFile);
                    await EnrichImageFileAsync(duplicateFile, path, ct);
                    logger.LogTrace("Attached duplicate image file {NewPath} to existing image {ImageId}", path, matchedImageId);
                    return (parentImage, true);
                }
            }
        }

        var imageFile = new ImageFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
        };

        Image image;
        if (imageId.HasValue)
        {
            image = await db.Images
                .Include(item => item.Files)
                .FirstOrDefaultAsync(item => item.Id == imageId.Value, ct)
                ?? throw new InvalidOperationException($"Image {imageId.Value} was not found for downloaded media import");

            if (string.IsNullOrWhiteSpace(image.Title))
                image.Title = Path.GetFileNameWithoutExtension(path);

            image.Files.Add(imageFile);
        }
        else
        {
            // Intentionally leave Title null on scan. Storing the filename as the title makes it
            // impossible to filter for entities that have no real title; the UI falls back to the
            // file basename for display when Title is null.
            image = new Image
            {
                Files = [imageFile]
            };

            db.Images.Add(image);
        }

        await EnrichImageFileAsync(imageFile, path, ct);

        logger.LogTrace("Added image for: {Path}", path);
        return (image, false);
    }

    // Compute the always-on identity fingerprint (oshash) plus the optional md5 for a new image file.
    // oshash is what lets a later scan recognise this image if it moves or is renamed.
    private async Task EnrichImageFileAsync(ImageFile imageFile, string path, CancellationToken ct)
    {
        var oshash = await ComputeOshashAsync(path, ct);
        if (oshash != null)
            UpsertFingerprint(imageFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                UpsertFingerprint(imageFile, "md5", md5);
        }
    }

    private async Task<Gallery> ProcessGalleryFileAsync(
        CoveContext db,
        string path,
        int? galleryId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        int? parentFolderId = null)
    {
        var stat = fileStat ?? GetFileStat(path);
        var dirPath = NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await EnsureFolderAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = await db.Set<GalleryFile>()
            .Include(gf => gf.Gallery)
            .ThenInclude(g => g!.ImageGalleries)
            .FirstOrDefaultAsync(f => f.ParentFolderId == folderId && f.Basename == basename, ct);

        // Consult entities added but not yet saved in this batch to avoid violating the unique
        // (ParentFolderId, Basename) index when a file is enumerated twice in one pass.
        existing ??= db.Set<GalleryFile>().Local.FirstOrDefault(f => f.ParentFolderId == folderId && f.Basename == basename);

        // If gallery exists and already has images, skip re-processing
        if (existing?.Gallery?.ImageGalleries.Count > 0)
        {
            logger.LogTrace("Gallery already processed with {Count} images: {Path}",
                existing.Gallery.ImageGalleries.Count, path);
            return existing.Gallery;
        }

        // Create or update the gallery file entry
        GalleryFile galleryFile;
        Gallery gallery;

        if (existing != null)
        {
            // Update existing file metadata
            galleryFile = existing;
            galleryFile.Size = stat.Size;
            galleryFile.ModTime = stat.ModTime;
            gallery = existing.Gallery!;
        }
        else
        {
            galleryFile = new GalleryFile
            {
                Basename = basename,
                ParentFolderId = folderId,
                Size = stat.Size,
                ModTime = stat.ModTime
            };

            if (galleryId.HasValue)
            {
                gallery = await db.Galleries
                    .Include(item => item.Files)
                    .Include(item => item.ImageGalleries)
                    .FirstOrDefaultAsync(item => item.Id == galleryId.Value, ct)
                    ?? throw new InvalidOperationException($"Gallery {galleryId.Value} was not found for downloaded media import");

                if (string.IsNullOrWhiteSpace(gallery.Title))
                    gallery.Title = Path.GetFileNameWithoutExtension(path);

                gallery.Files.Add(galleryFile);
            }
            else
            {
                // Intentionally leave Title null on scan. Storing the filename as the title makes it
                // impossible to filter for galleries that have no real title; the UI falls back to the
                // file basename for display when Title is null.
                gallery = new Gallery
                {
                    Files = [galleryFile]
                };

                db.Galleries.Add(gallery);
            }
        }

        // Save to get the GalleryFile ID (needed for ZipFileId on images)
        await db.SaveChangesAsync(ct);

        // Now extract images from the zip file
        try
        {
            // Get all images from the zip, sorted by path
            var imageEntries = await zipGalleryReader.GetImageEntriesAsync(path, ct);

            if (imageEntries.Count == 0)
            {
                logger.LogWarning("No images found in gallery zip: {Path}", path);
                return gallery;
            }

            // Wonky zips can contain multiple entries with identical internal paths. Every
            // image in a gallery shares one virtual folder, so duplicate names collide on the
            // (ParentFolderId, Basename) unique constraint and fail the entire gallery insert.
            // Keep the first occurrence of each name (case-sensitive, matching Postgres text).
            var distinctEntries = imageEntries
                .GroupBy(entry => entry.FullName, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            if (distinctEntries.Count != imageEntries.Count)
                logger.LogWarning(
                    "Gallery zip contained {DuplicateCount} duplicate entry name(s); keeping one of each: {Path}",
                    imageEntries.Count - distinctEntries.Count,
                    path);

            logger.LogTrace("Found {Count} images in gallery: {Path}", distinctEntries.Count, path);

            // Create a virtual folder for this zip's contents
            // This ensures images from different zips don't conflict on the unique constraint (ParentFolderId + Basename)
            var virtualFolderPath = $"{path}#virtual";
            var virtualFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == virtualFolderPath, ct);
            if (virtualFolder == null)
            {
                virtualFolder = new Folder { Path = virtualFolderPath };
                db.Folders.Add(virtualFolder);
                await db.SaveChangesAsync(ct);
            }

            // Create Image entities for each image in the zip
            foreach (var entry in distinctEntries)
            {
                // Create ImageFile record representing the image within the zip
                // Use FullName to preserve the internal zip path structure and avoid duplicate basenames
                var imageFile = new ImageFile
                {
                    Basename = entry.FullName,  // Use full internal path to avoid collisions
                    ParentFolderId = virtualFolder.Id,  // Use virtual folder specific to this zip
                    ZipFileId = galleryFile.Id,  // Link to parent zip file
                    Size = entry.Length,
                    ModTime = NormalizeFileModTime(entry.LastWriteTime.UtcDateTime),
                    Format = Path.GetExtension(entry.Name).TrimStart('.').ToLowerInvariant(),
                    // TODO: Extract dimensions using image processing library
                    Width = 0,
                    Height = 0
                };

                // Create Image entity
                var image = new Image
                {
                    Title = Path.GetFileNameWithoutExtension(entry.Name),
                    Files = [imageFile]
                };

                db.Images.Add(image);

                // Link image to gallery via junction table
                // Note: We'll add this after the image is saved and has an ID
                gallery.ImageGalleries.Add(new ImageGallery
                {
                    Image = image,
                    Gallery = gallery
                });
            }

            // Save all images and their gallery associations
            await db.SaveChangesAsync(ct);

            logger.LogTrace("Added gallery with {Count} images: {Path}", distinctEntries.Count, path);
        }
        catch (FileNotFoundException)
        {
            logger.LogError("Zip file not found (may have been moved/deleted): {Path}", path);
        }
        catch (InvalidDataException ex)
        {
            logger.LogError("Invalid or corrupt zip file: {Path} - {Error}", path, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error processing gallery zip file: {Path}", path);

            // Discard any image rows that failed to persist so the caller's next SaveChanges
            // doesn't retry them and surface the same error a second time. The gallery row
            // itself was already committed above, so it survives (as an empty gallery).
            db.ChangeTracker.Clear();
        }

        return gallery;
    }

    private async Task<(Audio Entity, bool Relinked)> ProcessAudioFileAsync(
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
        MoveDetectionIndex? moveIndex = null)
    {
        var stat = fileStat ?? GetFileStat(path);
        var dirPath = NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await EnsureFolderAsync(db, dirPath, ct, folderCache)).Id;

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
            await EnrichAudioFileAsync(existingAudio, existing, path, ct);
            // A re-encode invalidates the stored phash; blank it so the generation phase recomputes it.
            if (contentChanged && scanOptions?.GenerateAudioPhashes == true)
                BlankFingerprint(existing, "phash");
            RefreshAudioSummary(existingAudio);
            return (existingAudio, false);
        }

        // Content already in the library: re-link a moved audio file, or attach a duplicate to its entity.
        if (!audioId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await TryMatchExistingFileAsync(db.AudioFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.AudioId is int matchedAudioId)
            {
                var parentAudio = await db.Audios.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == matchedAudioId, ct);
                if (parentAudio != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved audio file to {NewPath} (previously {OldPath})", path, match.Path);
                        RefreshAudioSummary(parentAudio);
                        return (parentAudio, true);
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
                    await EnrichAudioFileAsync(parentAudio, duplicateFile, path, ct);
                    RefreshAudioSummary(parentAudio);
                    logger.LogTrace("Attached duplicate audio file {NewPath} to existing audio {AudioId}", path, matchedAudioId);
                    return (parentAudio, true);
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

        await EnrichAudioFileAsync(audio, audioFile, path, ct);
        RefreshAudioSummary(audio);

        logger.LogTrace("Added audio for: {Path}", path);
        return (audio, false);
    }

    private async Task<(TextDocument Entity, bool Relinked)> ProcessTextFileAsync(
        CoveContext db,
        string path,
        int? textDocumentId,
        CancellationToken ct,
        FileStat? fileStat = null,
        Dictionary<string, Folder>? folderCache = null,
        bool knownNew = false,
        int? parentFolderId = null,
        bool contentChanged = false,
        ScanOperationOptions? scanOptions = null,
        MoveDetectionIndex? moveIndex = null)
    {
        var stat = fileStat ?? GetFileStat(path);
        var dirPath = NormalizeStoredFolderPath(Path.GetDirectoryName(path) ?? path);
        var folderId = parentFolderId ?? (await EnsureFolderAsync(db, dirPath, ct, folderCache)).Id;

        var basename = Path.GetFileName(path);
        var existing = knownNew
            ? null
            : await db.TextFiles
                .Include(file => file.Fingerprints)
                .Include(file => file.TextDocument)
                .ThenInclude(text => text!.Files)
                .FirstOrDefaultAsync(file => file.ParentFolderId == folderId && file.Basename == basename, ct);

        if (existing != null)
        {
            existing.Size = stat.Size;
            existing.ModTime = stat.ModTime;
            existing.Path = BaseFileEntity.ComputePath(dirPath, basename);

            var existingDocument = existing.TextDocument ?? throw new InvalidOperationException($"Text file {path} is not attached to a text document");
            await EnrichTextFileAsync(existingDocument, existing, path, ct);
            // A content change invalidates the stored phash; blank it so the generation phase recomputes it.
            if (contentChanged && scanOptions?.GenerateTextPhashes == true)
                BlankFingerprint(existing, "phash");
            RefreshTextSummary(existingDocument);
            return (existingDocument, false);
        }

        // Content already in the library: re-link a moved text file, or attach a duplicate to its entity.
        if (!textDocumentId.HasValue && moveIndex is { Enabled: true })
        {
            var (match, isMove) = await TryMatchExistingFileAsync(db.TextFiles, path, folderId, basename, stat, moveIndex, ct);
            if (match?.TextDocumentId is int matchedTextId)
            {
                var parentDocument = await db.TextDocuments.Include(item => item.Files).FirstOrDefaultAsync(item => item.Id == matchedTextId, ct);
                if (parentDocument != null)
                {
                    if (isMove)
                    {
                        logger.LogTrace("Re-linked moved text file to {NewPath} (previously {OldPath})", path, match.Path);
                        RefreshTextSummary(parentDocument);
                        return (parentDocument, true);
                    }

                    var duplicateFile = new TextFile
                    {
                        Basename = basename,
                        ParentFolderId = folderId,
                        Path = BaseFileEntity.ComputePath(dirPath, basename),
                        Size = stat.Size,
                        ModTime = stat.ModTime,
                        Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    };
                    parentDocument.Files.Add(duplicateFile);
                    await EnrichTextFileAsync(parentDocument, duplicateFile, path, ct);
                    RefreshTextSummary(parentDocument);
                    logger.LogTrace("Attached duplicate text file {NewPath} to existing text document {TextId}", path, matchedTextId);
                    return (parentDocument, true);
                }
            }
        }

        var textFile = new TextFile
        {
            Basename = basename,
            ParentFolderId = folderId,
            Path = BaseFileEntity.ComputePath(dirPath, basename),
            Size = stat.Size,
            ModTime = stat.ModTime,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
        };

        TextDocument textDocument;
        if (textDocumentId.HasValue)
        {
            textDocument = await db.TextDocuments
                .Include(item => item.Files)
                .FirstOrDefaultAsync(item => item.Id == textDocumentId.Value, ct)
                ?? throw new InvalidOperationException($"Text document {textDocumentId.Value} was not found for downloaded media import");

            textDocument.Files.Add(textFile);
        }
        else
        {
            textDocument = new TextDocument
            {
                Title = Path.GetFileNameWithoutExtension(path),
                Files = [textFile],
            };

            db.TextDocuments.Add(textDocument);
        }

        await EnrichTextFileAsync(textDocument, textFile, path, ct);
        RefreshTextSummary(textDocument);

        logger.LogTrace("Added text document for: {Path}", path);
        return (textDocument, false);
    }

    private async Task EnrichAudioFileAsync(Audio audio, AudioFile audioFile, string path, CancellationToken ct)
    {
        var metadata = await ProbeAudioAsync(audioFile, path, ct);
        var fallbackTitle = Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrWhiteSpace(audio.Title) || string.Equals(audio.Title, fallbackTitle, StringComparison.OrdinalIgnoreCase))
            audio.Title = metadata.Title ?? fallbackTitle;

        // Always-on identity fingerprint so a later scan can recognise this file if it moves/renames.
        var oshash = await ComputeOshashAsync(path, ct);
        if (oshash != null)
            UpsertFingerprint(audioFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                UpsertFingerprint(audioFile, "md5", md5);
            }
        }
    }

    private async Task EnrichTextFileAsync(TextDocument textDocument, TextFile textFile, string path, CancellationToken ct)
    {
        try
        {
            var metadata = await textExtractionService.ExtractMetadataAsync(path, ct);
            var fallbackTitle = Path.GetFileNameWithoutExtension(path);
            textFile.PageCount = metadata.PageCount;
            textFile.WordCount = metadata.WordCount;
            textFile.ExcerptText = metadata.ExcerptText;

            if (string.IsNullOrWhiteSpace(textDocument.Title) || string.Equals(textDocument.Title, fallbackTitle, StringComparison.OrdinalIgnoreCase))
                textDocument.Title = metadata.Title ?? fallbackTitle;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to extract text metadata for {Path}", path);
        }

        // Always-on identity fingerprint so a later scan can recognise this file if it moves/renames.
        var oshash = await ComputeOshashAsync(path, ct);
        if (oshash != null)
            UpsertFingerprint(textFile, "oshash", oshash);

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                UpsertFingerprint(textFile, "md5", md5);
            }
        }
    }

    private async Task<AudioProbeMetadata> ProbeAudioAsync(AudioFile audioFile, string path, CancellationToken ct)
    {
        var ffprobePath = FindFfprobe();
        if (ffprobePath == null)
        {
            logger.LogDebug("FFprobe not found, skipping audio metadata probe for {Path}", path);
            return new AudioProbeMetadata(null);
        }

        audioFile.HasVideoTrack = false;
        audioFile.AudioCodec = string.Empty;
        audioFile.SampleRate = null;
        audioFile.Channels = null;

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };

            process.Start();
            var json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrEmpty(json))
                return new AudioProbeMetadata(null);

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
                if (format.TryGetProperty("tags", out var tags))
                {
                    if (tags.TryGetProperty("title", out var titleProp))
                        title = titleProp.GetString();
                }
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
                        if (stream.TryGetProperty("sample_rate", out var sampleRateProp))
                        {
                            if (int.TryParse(sampleRateProp.GetString(), out var sampleRate))
                                audioFile.SampleRate = sampleRate;
                        }
                        if (stream.TryGetProperty("channels", out var channelsProp) && channelsProp.TryGetInt32(out var channels))
                            audioFile.Channels = channels;
                        if (audioFile.BitRate == 0 && stream.TryGetProperty("bit_rate", out var streamBitrateProp))
                        {
                            if (long.TryParse(streamBitrateProp.GetString(), out var streamBitrate))
                                audioFile.BitRate = streamBitrate;
                        }
                    }
                    else if (codecType == "video")
                    {
                        // Audio container album art is a "video" stream flagged attached_pic; don't treat it as a real video track.
                        var isAttachedPic = stream.TryGetProperty("disposition", out var disposition)
                            && disposition.TryGetProperty("attached_pic", out var attachedPic)
                            && attachedPic.TryGetInt32(out var attachedPicFlag)
                            && attachedPicFlag == 1;
                        // Some encoders embed cover art as an image-codec video stream without the attached_pic
                        // disposition. Treat single-image codecs (mjpeg/png/etc.) as album art, not a real video track.
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
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FFprobe failed for audio {Path}", path);
            return new AudioProbeMetadata(null);
        }
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
        audio.FileSearchText = BuildFileSearchText(paths);
        audio.HasVideoFiles = files.Any(file => file.HasVideoTrack);
    }

    private static void RefreshTextSummary(TextDocument textDocument)
    {
        var files = textDocument.Files.ToList();
        textDocument.FileCount = files.Count;
        if (files.Count == 0)
        {
            textDocument.MaxWordCount = null;
            textDocument.MaxPageCount = null;
            textDocument.MaxFileSize = 0;
            textDocument.MaxFileModTime = null;
            textDocument.MinPath = null;
            textDocument.MaxPath = null;
            textDocument.FileSearchText = null;
            return;
        }

        var paths = files
            .Select(file => string.IsNullOrWhiteSpace(file.Path) ? BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename) : file.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        textDocument.MaxWordCount = files.Max(file => file.WordCount);
        textDocument.MaxPageCount = files.Max(file => file.PageCount);
        textDocument.MaxFileSize = files.Max(file => file.Size);
        textDocument.MaxFileModTime = files.Max(file => (DateTime?)file.ModTime);
        textDocument.MinPath = paths.FirstOrDefault();
        textDocument.MaxPath = paths.LastOrDefault();
        textDocument.FileSearchText = BuildFileSearchText(paths);
    }

    private static string? BuildFileSearchText(IEnumerable<string> paths)
    {
        var values = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Replace('\\', '/').Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return values.Length == 0 ? null : string.Join('\n', values);
    }

    private static void UpsertFingerprint(BaseFileEntity file, string type, string value)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint => string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        file.Fingerprints.Add(new FileFingerprint
        {
            Type = type,
            Value = value,
        });
    }

    // Blank (rather than delete) a stale fingerprint's value. The asset-generation phase and the
    // duplicate/move matchers all treat an empty value as "absent", so blanking a stale phash/md5 on
    // an in-place content change makes the generation phase recompute it — and never leaves a wrong
    // hash behind that could mis-identify the file. Requires the file's Fingerprints to be loaded.
    private static void BlankFingerprint(BaseFileEntity file, string type)
    {
        var existing = file.Fingerprints.FirstOrDefault(fingerprint => string.Equals(fingerprint.Type, type, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            existing.Value = string.Empty;
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

    /// <summary>
    /// Refresh the identity/derived state of an already-known file whose bytes changed in place
    /// (in-place re-encode or replacement). The oshash identity always refreshes so move-detection
    /// and duplicate matching stay correct; heavier fingerprints and visual assets refresh only when
    /// this scan is configured to (re)generate them, honouring the per-scan generate options.
    /// </summary>
    private async Task RefreshChangedFileFingerprintsAsync(
        BaseFileEntity file,
        string path,
        bool phashEnabled,
        bool md5Enabled,
        CancellationToken ct)
    {
        var oshash = await ComputeOshashAsync(path, ct);
        if (oshash != null)
            UpsertFingerprint(file, "oshash", oshash);

        if (md5Enabled)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
                UpsertFingerprint(file, "md5", md5);
            else
                BlankFingerprint(file, "md5");
        }
        else
        {
            // md5 is now wrong and this scan is not recomputing it; drop the stale value.
            BlankFingerprint(file, "md5");
        }

        // phash needs the frame/audio pipeline, which runs in the generation phase; blank the stale
        // value so that phase recomputes it (only when phash generation is enabled for this type).
        if (phashEnabled)
            BlankFingerprint(file, "phash");
    }

    /// <summary>
    /// Compute OpenSubtitles hash (oshash) for a video file.
    /// Standard oshash algorithm.
    /// </summary>
    private static async Task<string?> ComputeOshashAsync(string path, CancellationToken ct)
    {
        const int chunkSize = 65536; // 64KB
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, chunkSize, useAsync: true);
            var fileSize = stream.Length;
            if (fileSize < chunkSize) return null;

            ulong hash = (ulong)fileSize;
            var buf = new byte[chunkSize];

            // Hash first 64KB
            await stream.ReadExactlyAsync(buf, ct);
            for (int i = 0; i < chunkSize; i += 8)
                hash += BitConverter.ToUInt64(buf, i);

            // Hash last 64KB
            stream.Seek(-chunkSize, SeekOrigin.End);
            await stream.ReadExactlyAsync(buf, ct);
            for (int i = 0; i < chunkSize; i += 8)
                hash += BitConverter.ToUInt64(buf, i);

            return hash.ToString("x16");
        }
        catch
        {
            return null;
        }
    }

    internal static bool NeedsVideoMetadataProbe(VideoFile videoFile)
    {
        return videoFile.Width <= 0 || videoFile.Height <= 0 || videoFile.Duration <= 0;
    }

    internal static bool IsMediaTypeExcludedByScanTarget(
        string extension,
        bool excludeVideo,
        bool excludeImage,
        bool excludeAudio,
        bool excludeText,
        IReadOnlySet<string> videoExts,
        IReadOnlySet<string> imageExts,
        IReadOnlySet<string> galleryExts,
        IReadOnlySet<string> audioExts,
        IReadOnlySet<string> textExts)
    {
        return (excludeVideo && videoExts.Contains(extension))
            || (excludeImage && (imageExts.Contains(extension) || galleryExts.Contains(extension)))
            || (excludeAudio && audioExts.Contains(extension))
            || (excludeText && textExts.Contains(extension));
    }

    private async Task ProbeVideoAsync(VideoFile videoFile, string path, CancellationToken ct)
    {
        var ffprobePath = FindFfprobe();
        if (ffprobePath == null)
        {
            logger.LogDebug("FFprobe not found, skipping metadata probe for {Path}", path);
            return;
        }

        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ffprobePath,
                    Arguments = $"-v quiet -print_format json -show_format -show_streams \"{path}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var json = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0 || string.IsNullOrEmpty(json)) return;

            ApplyFfprobeMetadata(videoFile, json);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FFprobe failed for {Path}", path);
        }
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

    private string? FindFfprobe()
    {
        if (_ffprobeResolved)
            return _cachedFfprobePath;

        lock (_ffprobeResolveLock)
        {
            if (_ffprobeResolved)
                return _cachedFfprobePath;

            _cachedFfprobePath = ResolveFfprobePath();
            _ffprobeResolved = true;
            return _cachedFfprobePath;
        }
    }

    private string? ResolveFfprobePath()
    {
        // Check configured FFmpeg path directory for ffprobe
        if (!string.IsNullOrEmpty(config.FfmpegPath))
        {
            var dir = Path.GetDirectoryName(config.FfmpegPath);
            if (dir != null)
            {
                var probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
                if (File.Exists(probe)) return probe;
            }
        }

        // Search PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var probe = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");
            if (File.Exists(probe)) return probe;
        }

        return null;
    }

    private static bool IsExcluded(string path, List<string> patterns)
    {
        foreach (var pattern in patterns)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool IsExcludedByConfiguredPatterns(
        string path,
        string extension,
        HashSet<string> imageExts,
        HashSet<string> galleryExts,
        CoveConfiguration cfg)
    {
        return IsExcluded(path, cfg.ExcludePatterns)
            || (imageExts.Contains(extension) && IsExcluded(path, cfg.ExcludeImagePatterns))
            || (galleryExts.Contains(extension) && IsExcluded(path, cfg.ExcludeGalleryPatterns));
    }

    private IEnumerable<DiscoveredFile> DiscoverFilesSafely(
        ScanTarget scanTarget,
        HashSet<string> allExts,
        HashSet<string> videoExts,
        HashSet<string> imageExts,
        HashSet<string> galleryExts,
        HashSet<string> audioExts,
        HashSet<string> textExts,
        CoveConfiguration cfg,
        Dictionary<string, List<IgnoreRule>> ruleCache,
        ScanDiscoveryProgress discoveryProgress,
        CancellationToken ct)
    {
        var pending = new Stack<DirectoryScanFrame>();
        pending.Push(CreateDirectoryScanFrame(scanTarget.Path, []));

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var frame = pending.Pop();
            var directory = frame.Path;
            discoveryProgress.RecordDirectory(directory);

            List<FileSystemInfo> entries;
            try
            {
                entries = new DirectoryInfo(directory)
                    .EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false })
                    .ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
            {
                discoveryProgress.RecordUnreadablePath(directory);
                logger.LogWarning(ex, "Skipping unreadable scan directory: {Path}", directory);
                continue;
            }

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                var path = entry.FullName;
                FileAttributes attributes;
                try
                {
                    attributes = entry.Attributes;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
                {
                    discoveryProgress.RecordUnreadablePath(path);
                    logger.LogWarning(ex, "Skipping unreadable scan path: {Path}", path);
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (isDirectory)
                {
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        discoveryProgress.RecordIgnoredPath(path);
                        continue;
                    }

                    if (IsExcludedByActiveIgnoreRules(path, frame.IgnoreRuleSets, isDirectory: true))
                    {
                        discoveryProgress.RecordIgnoredPath(path);
                        continue;
                    }

                    pending.Push(CreateDirectoryScanFrame(path, frame.IgnoreRuleSets));
                    continue;
                }

                var ext = Path.GetExtension(path);
                if (!allExts.Contains(ext))
                {
                    discoveryProgress.RecordUnsupportedFile();
                    continue;
                }

                if (IsMediaTypeExcludedByScanTarget(
                    ext,
                    scanTarget.ExcludeVideo,
                    scanTarget.ExcludeImage,
                    scanTarget.ExcludeAudio,
                    scanTarget.ExcludeText,
                    videoExts,
                    imageExts,
                    galleryExts,
                    audioExts,
                    textExts))
                {
                    discoveryProgress.RecordIgnoredPath(path);
                    continue;
                }

                if (IsExcludedByConfiguredPatterns(path, ext, imageExts, galleryExts, cfg)
                    || IsExcludedByActiveIgnoreRules(path, frame.IgnoreRuleSets))
                {
                    discoveryProgress.RecordIgnoredPath(path);
                    continue;
                }

                if (entry is not FileInfo fileInfo)
                {
                    discoveryProgress.RecordUnsupportedFile();
                    continue;
                }

                DiscoveredFile discoveredFile;
                try
                {
                    var normalizedPath = NormalizePath(path);
                    discoveredFile = new DiscoveredFile(
                        normalizedPath,
                        NormalizeStoredFilePath(normalizedPath),
                        ext,
                        new FileStat(fileInfo.Length, NormalizeFileModTime(fileInfo.LastWriteTimeUtc)));
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileNotFoundException or DirectoryNotFoundException)
                {
                    discoveryProgress.RecordUnreadablePath(path);
                    logger.LogWarning(ex, "Skipping unreadable scan file: {Path}", path);
                    continue;
                }

                discoveryProgress.RecordMediaFile(discoveredFile.Path);
                yield return discoveredFile;
            }
        }

        DirectoryScanFrame CreateDirectoryScanFrame(string directory, IReadOnlyList<ActiveIgnoreRuleSet> inheritedRuleSets)
        {
            var rules = GetIgnoreRules(NormalizePath(directory), ruleCache);
            if (rules.Count == 0)
                return new DirectoryScanFrame(directory, inheritedRuleSets);

            var ruleSets = new List<ActiveIgnoreRuleSet>(inheritedRuleSets.Count + 1);
            ruleSets.AddRange(inheritedRuleSets);
            ruleSets.Add(new ActiveIgnoreRuleSet(NormalizePath(directory), rules));
            return new DirectoryScanFrame(directory, ruleSets);
        }
    }

    private static bool IsExcludedByActiveIgnoreRules(string path, IReadOnlyList<ActiveIgnoreRuleSet> ruleSets, bool isDirectory = false)
    {
        if (ruleSets.Count == 0)
            return false;

        var fullPath = NormalizePath(path);
        var fileName = Path.GetFileName(fullPath);
        var ignored = false;

        foreach (var ruleSet in ruleSets)
        {
            var relativePath = Path.GetRelativePath(ruleSet.Directory, fullPath).Replace('\\', '/');
            if (isDirectory && !relativePath.EndsWith('/'))
                relativePath += "/";
            foreach (var rule in ruleSet.Rules)
            {
                if (IgnoreRuleMatches(rule.Pattern, relativePath, fileName))
                    ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static bool IsExcludedByFolderIgnore(string path, string rootPath, Dictionary<string, List<IgnoreRule>> ruleCache)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
            return false;

        var fullPath = NormalizePath(path);
        var root = NormalizePath(rootPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            return false;

        var ancestors = new Stack<string>();
        for (var current = NormalizePath(directory); !string.IsNullOrWhiteSpace(current) && IsPathWithin(current, root); current = Path.GetDirectoryName(current))
            ancestors.Push(current);

        bool ignored = false;
        while (ancestors.Count > 0)
        {
            var ruleDirectory = ancestors.Pop();
            foreach (var rule in GetIgnoreRules(ruleDirectory, ruleCache))
            {
                var relativePath = Path.GetRelativePath(ruleDirectory, fullPath).Replace('\\', '/');
                if (IgnoreRuleMatches(rule.Pattern, relativePath, Path.GetFileName(fullPath)))
                    ignored = !rule.Negated;
            }
        }

        return ignored;
    }

    private static List<IgnoreRule> GetIgnoreRules(string directory, Dictionary<string, List<IgnoreRule>> ruleCache)
    {
        if (ruleCache.TryGetValue(directory, out var cached))
            return cached;

        var rules = new List<IgnoreRule>();
        foreach (var fileName in FolderIgnoreFileNames)
        {
            var ignoreFile = Path.Combine(directory, fileName);
            if (!File.Exists(ignoreFile))
                continue;

            foreach (var line in File.ReadLines(ignoreFile))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    continue;

                var negated = trimmed.StartsWith('!');
                var pattern = (negated ? trimmed[1..] : trimmed).Trim().Replace('\\', '/');
                if (pattern.Length > 0)
                    rules.Add(new IgnoreRule(pattern, negated));
            }
        }

        ruleCache[directory] = rules;
        return rules;
    }

    private static bool IgnoreRuleMatches(string pattern, string relativePath, string fileName)
    {
        var normalizedPattern = pattern.TrimStart('/');
        var directoryPattern = normalizedPattern.EndsWith('/');
        if (directoryPattern)
            normalizedPattern = normalizedPattern.TrimEnd('/');

        if (normalizedPattern.Length == 0)
            return false;

        if (directoryPattern)
        {
            return relativePath.StartsWith(normalizedPattern + '/', StringComparison.OrdinalIgnoreCase)
                || relativePath.Contains('/' + normalizedPattern + '/', StringComparison.OrdinalIgnoreCase);
        }

        if (normalizedPattern.Contains('/'))
            return FileSystemName.MatchesSimpleExpression(normalizedPattern, relativePath, ignoreCase: true);

        return FileSystemName.MatchesSimpleExpression(normalizedPattern, fileName, ignoreCase: true)
            || relativePath.Split('/').Any(segment => FileSystemName.MatchesSimpleExpression(normalizedPattern, segment, ignoreCase: true));
    }

    private static bool HasForceGalleryHints(IEnumerable<DiscoveredFile> files)
    {
        return files
            .Select(file => Path.GetDirectoryName(file.Path))
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Any(directory => File.Exists(Path.Combine(directory!, ".forcegallery")));
    }

    private static bool ShouldCreateFolderGallery(string folderPath, bool createAllEligibleFolders)
    {
        if (File.Exists(Path.Combine(folderPath, ".nogallery")))
            return false;

        return createAllEligibleFolders || File.Exists(Path.Combine(folderPath, ".forcegallery"));
    }

    private static List<ScanTarget> ResolveScanTargets(CoveConfiguration cfg, List<string>? selectedPaths)
    {
        if (selectedPaths == null)
        {
            return cfg.CovePaths
                .Select(path => new ScanTarget(NormalizePath(path.Path), path.ExcludeVideo, path.ExcludeImage, path.ExcludeAudio, path.ExcludeText, false))
                .ToList();
        }

        var targets = new List<ScanTarget>();
        foreach (var selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matchingConfig = cfg.CovePaths
                .Select(path => new { Config = path, NormalizedPath = NormalizePath(path.Path) })
                .Where(x => IsPathWithin(selectedPath, x.NormalizedPath))
                .OrderByDescending(x => x.NormalizedPath.Length)
                .Select(x => x.Config)
                .FirstOrDefault();

            if (matchingConfig == null)
            {
                // Selective scan is restricted to configured library roots: ignore any requested path
                // that isn't at or below one of them, so a scan can never wander outside the library.
                continue;
            }

            var excludeVideo = matchingConfig.ExcludeVideo;
            var excludeImage = matchingConfig.ExcludeImage;
            var excludeAudio = matchingConfig.ExcludeAudio;
            var excludeText = matchingConfig.ExcludeText;
            var isFile = File.Exists(selectedPath);

            if (!isFile && !Directory.Exists(selectedPath))
            {
                continue;
            }

            targets.Add(new ScanTarget(selectedPath, excludeVideo, excludeImage, excludeAudio, excludeText, isFile));
        }

        return targets;
    }

    private static bool IsPathWithin(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static FileStat GetFileStat(string path)
    {
        var fileInfo = new FileInfo(path);
        return new FileStat(fileInfo.Length, NormalizeFileModTime(fileInfo.LastWriteTimeUtc));
    }

    private static DateTime NormalizeFileModTime(DateTime modTime)
    {
        var utc = modTime.Kind == DateTimeKind.Utc ? modTime : modTime.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private static string NormalizeStoredFolderPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        var normalized = !string.IsNullOrEmpty(root) && string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalized.Replace('\\', '/');
    }

    private static string NormalizeStoredFilePath(string path)
    {
        var dirPath = Path.GetDirectoryName(path) ?? string.Empty;
        var basename = Path.GetFileName(path);
        return BaseFileEntity.ComputePath(NormalizeStoredFolderPath(dirPath), basename);
    }

    /// <summary>
    /// Canonicalizes a stored folder path to the scanner's canonical form for comparison. Returns null
    /// when the stored path can't be canonicalized (e.g. malformed, or a path from a different host that
    /// Path.GetFullPath would reject) — such folders simply won't match a discovered directory, which is
    /// safe: they aren't being scanned on this host.
    /// </summary>
    private static string? TryCanonicalizeStoredFolderPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        try
        {
            return NormalizeStoredFolderPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetParentStoredFolderPath(string storedPath)
    {
        var nativePath = storedPath.Replace('/', Path.DirectorySeparatorChar);
        var parentPath = Path.GetDirectoryName(nativePath);
        return string.IsNullOrWhiteSpace(parentPath) ? null : NormalizeStoredFolderPath(parentPath);
    }

    private static readonly string[] FolderIgnoreFileNames = [".coveignore", ".stashignore"];

    [LoggerMessage(
        EventId = 2101,
        Level = LogLevel.Trace,
        Message = "Classified known {MediaType} file {Path} as {Reason}; oldBytes={OldBytes}, newBytes={NewBytes}")]
    private partial void TraceKnownFileClassified(
        string path,
        ExistingFileKind mediaType,
        ScanFileChangeReason reason,
        long oldBytes,
        long newBytes);

    [LoggerMessage(
        EventId = 2102,
        Level = LogLevel.Trace,
        Message = "Classified file {Path} as new")]
    private partial void TraceNewFileClassified(string path);

    [LoggerMessage(
        EventId = 2103,
        Level = LogLevel.Trace,
        Message = "Committed scan batch containing {FileCount} files")]
    private partial void TraceScanBatchCommitted(int fileCount);

    private record CaptionSidecar(string Filename, string LanguageCode, string CaptionType);

    // Shared, worker-safe state for move/rename detection. Enabled is decided once per scan (only when
    // the library already has files to move to/from). ClaimedFileIds prevents two workers from both
    // re-pointing the same now-missing record when a file's bytes appear at more than one new location.
    private sealed class MoveDetectionIndex
    {
        public required bool Enabled { get; init; }
        public ConcurrentDictionary<int, byte> ClaimedFileIds { get; } = new();
    }

    private enum ExistingFileKind { Unknown, Video, Image, Gallery, Audio, Text }
    private sealed record ExistingFileScanInfo(string StoredPath, int Id, ExistingFileKind Kind, long Size, DateTime ModTime, bool NeedsMetadataProbe);
    private sealed record AudioProbeMetadata(string? Title);
    private enum ScanFileChangeReason { Unchanged, RescanForced, MetadataProbe, SizeChanged, ModTimeChanged }
    private sealed record ActiveIgnoreRuleSet(string Directory, IReadOnlyList<IgnoreRule> Rules);
    private sealed record DirectoryScanFrame(string Path, IReadOnlyList<ActiveIgnoreRuleSet> IgnoreRuleSets);
    private sealed class ScanDiscoveryProgress(Cove.Core.Interfaces.IJobProgress progress, ILogger<ScanService> logger)
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private DateTime _lastUiReport = DateTime.MinValue;
        private DateTime _lastLogReport = DateTime.MinValue;

        public int DirectoryCount { get; private set; }
        public int MediaFileCount { get; private set; }
        public int UnsupportedFileCount { get; private set; }
        public int IgnoredPathCount { get; private set; }
        public int UnreadablePathCount { get; private set; }

        public void RecordDirectory(string path)
        {
            DirectoryCount++;
            ReportIfDue(path);
        }

        public void RecordMediaFile(string path)
        {
            MediaFileCount++;
            ReportIfDue(path);
        }

        public void RecordUnsupportedFile()
        {
            UnsupportedFileCount++;
        }

        public void RecordIgnoredPath(string path)
        {
            IgnoredPathCount++;
            ReportIfDue(path);
        }

        public void RecordUnreadablePath(string path)
        {
            UnreadablePathCount++;
            ReportIfDue(path);
        }

        public void Complete()
        {
            progress.Report(0.10, $"Discovered {MediaFileCount:N0} media files in {DirectoryCount:N0} folders.");
        }

        private void ReportIfDue(string? path)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastUiReport).TotalSeconds >= 1)
            {
                _lastUiReport = now;
                progress.Report(0.05, BuildMessage(path));
            }

            if ((now - _lastLogReport).TotalSeconds >= 10)
            {
                _lastLogReport = now;
                logger.LogDebug(
                    "Scan discovery progress after {ElapsedMs} ms: {MediaFileCount} media files, {DirectoryCount} directories, {IgnoredPathCount} ignored, {UnsupportedFileCount} unsupported, {UnreadablePathCount} unreadable. Current path: {Path}",
                    _elapsed.ElapsedMilliseconds,
                    MediaFileCount,
                    DirectoryCount,
                    IgnoredPathCount,
                    UnsupportedFileCount,
                    UnreadablePathCount,
                    path);
            }
        }

        private string BuildMessage(string? path)
        {
            var message = $"Discovering files: {MediaFileCount:N0} media files, {DirectoryCount:N0} folders";
            if (IgnoredPathCount > 0)
                message += $", {IgnoredPathCount:N0} ignored";
            if (!string.IsNullOrWhiteSpace(path))
                message += $": {Path.GetFileName(path)}";
            return message;
        }
    }

    private record DiscoveredFile(string Path, string StoredPath, string Extension, FileStat Stat)
    {
        public long Size => Stat.Size;
        public DateTime ModTime => Stat.ModTime;
    }

    private readonly record struct FileStat(long Size, DateTime ModTime);
    private record IgnoreRule(string Pattern, bool Negated);
    private record ScanTarget(string Path, bool ExcludeVideo, bool ExcludeImage, bool ExcludeAudio, bool ExcludeText, bool IsFile);

    /// <summary>
    /// Wraps a progress reporter to map 0-100% into a sub-range of the parent progress.
    /// Used to give extension scan participants their own slice of the overall progress bar.
    /// </summary>
    private sealed class ScopedProgress(Cove.Core.Interfaces.IJobProgress parent, double rangeStart, double rangeEnd) : ExtJobProgress
    {
        public void Report(double percent, string? message = null)
        {
            var mapped = rangeStart + (percent / 100.0) * (rangeEnd - rangeStart);
            parent.Report(mapped * 100, message);
        }
    }
}
