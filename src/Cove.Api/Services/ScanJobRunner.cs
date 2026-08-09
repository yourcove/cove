using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using ExtJobProgress = Cove.Plugins.IJobProgress;

namespace Cove.Api.Services;

internal sealed class ScanJobRunner(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    IEventBus eventBus,
    ExtensionManager extensionManager,
    IScanFileValidator fileValidator,
    ScanDiscoveryService discoveryService,
    ScanAssetGenerationService assetGenerationService,
    ScanFolderResolver folderResolver,
    ScanVideoProcessor videoProcessor,
    ScanImageProcessor imageProcessor,
    ScanGalleryProcessor galleryProcessor,
    ScanAudioProcessor audioProcessor,
    ScanTextProcessor textProcessor,
    ILogger logger)
{
    private const int ScanSaveBatchSize = 50;
    private static readonly TimeSpan ScanCommandTimeout = TimeSpan.FromMinutes(5);

    private int ResolveMaxParallelism()
    {
        var configured = config.MaxParallelTasks;
        if (configured == -1) return Environment.ProcessorCount;
        if (configured <= 0) return 1;
        return configured;
    }

    internal string Start(ScanOperationOptions? options = null)
    {
        options ??= new ScanOperationOptions();

        return jobService.Enqueue("scan", "Scanning library", async (progress, ct) =>
        {
            var cfg = config;
            var scanStopwatch = Stopwatch.StartNew();
            var discovery = await discoveryService.DiscoverAsync(options, progress, ct);
            var scanTargets = discovery.Targets;

            if (scanTargets.Count == 0)
                return;

            var files = discovery.Files;
            var videoExts = discovery.Extensions.Video;
            var imageExts = discovery.Extensions.Image;
            var galleryExts = discovery.Extensions.Gallery;
            var audioExts = discovery.Extensions.Audio;
            var textExts = discovery.Extensions.Text;
            var directoryScanContext = discovery.DirectoryScanContext;
            // Per-directory cache of caption sidecar files (.vtt/.srt), shared across workers,
            // so each directory is enumerated once per scan instead of once per video.
            var captionFilesByDir = new ConcurrentDictionary<string, IReadOnlyList<string>>(FilesystemPaths.PathComparer);

            // Written from multiple scan workers, so these must be concurrent collections.
            var assetVideoPaths = new ConcurrentDictionary<string, byte>(FilesystemPaths.PathComparer);
            var assetImagePaths = new ConcurrentDictionary<string, byte>(FilesystemPaths.PathComparer);
            var assetAudioPaths = new ConcurrentDictionary<string, byte>(FilesystemPaths.PathComparer);
            var assetTextPaths = new ConcurrentDictionary<string, byte>(FilesystemPaths.PathComparer);

            void AddAssetCandidate(ExistingFileKind kind, string path)
            {
                switch (kind)
                {
                    case ExistingFileKind.Video:
                        assetVideoPaths.TryAdd(path, 0);
                        break;
                    case ExistingFileKind.Image:
                        assetImagePaths.TryAdd(path, 0);
                        break;
                    case ExistingFileKind.Audio:
                        assetAudioPaths.TryAdd(path, 0);
                        break;
                    case ExistingFileKind.Text:
                        assetTextPaths.TryAdd(path, 0);
                        break;
                }
            }

            var importedCount = 0;
            var updatedCount = 0;
            var invalidMediaSkippedCount = 0;
            var deferredFileCount = 0;
            var failedCount = 0;
            var assetGenerationFailureCount = 0;

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
                var existingFiles = await ScanExistingFileIndex.LoadAsync(db, files, videoExts, imageExts, galleryExts, audioExts, textExts, progress, logger, ct);
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
                        var changeReason = ScanExistingFileIndex.GetChangeReason(existingFile!, file, options.Rescan);
                        if (changeReason == ScanFileChangeReason.Unchanged)
                        {
                            if (options.IncludeUnchangedFilesInAssetGeneration)
                                AddAssetCandidate(existingFile!.Kind, file.Path);
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

                        var expectedKind = ScanExistingFileIndex.GetExpectedKind(
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
                            directoryScanContext.MarkRequiresConfirmation(file.Path);
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

                    directoryScanContext.MarkRequiresConfirmation(file.Path);
                    changedOrNewCount++;
                    filesToProcess.Add((file, isKnownFile, contentChanged));
                }

                // Resolve every parent folder once, up front, into a shared id map. Workers then look
                // folders up in memory instead of each re-querying (and re-locking) the Folders table,
                // and the batched save path below stays free of incidental folder writes.
                var folderIdsByPath = await folderResolver.ResolveAsync(
                    db,
                    filesToProcess.Select(item => item.File).ToList(),
                    ct);

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
                        var dir = ScanPath.NormalizeStoredFolderPath(Path.GetDirectoryName(file.Path) ?? file.Path);
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
                        async Task<bool> StageAsync((DiscoveredFile File, bool IsKnownFile, bool ContentChanged) work, List<Action> events)
                        {
                            var file = work.File;
                            var isKnownFile = work.IsKnownFile;
                            var contentChanged = work.ContentChanged;
                            var folderId = ResolveFolderId(file);
                            var kind = ScanExistingFileIndex.GetExpectedKind(file.Extension, videoExts, imageExts, galleryExts, audioExts, textExts);
                            var validation = await fileValidator.ValidateAsync(
                                file.Path,
                                file.Size,
                                file.ObservedModTime,
                                ScanExistingFileIndex.ToMediaKind(kind),
                                ct);
                            if (!RecordValidationOutcome(file, validation))
                                return false;

                            if (videoExts.Contains(file.Extension))
                            {
                                if (!isKnownFile || contentChanged)
                                    assetVideoPaths.TryAdd(file.Path, 0);
                                var (videoFile, relinked, moved) = await videoProcessor.ProcessAsync(workerDb, file.Path, null, ct, file.Stat, null, syncCaptions: true, knownNew: !isKnownFile, captionFilesByDir: captionFilesByDir, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex, videoProbeJson: validation.ProbeJson);
                                events.Add(() =>
                                {
                                    RecordPersistedFile(isKnownFile || moved);
                                    if (videoFile.VideoId.HasValue)
                                        PublishScanEntityEvent("Video", videoFile.VideoId.Value, isKnownFile || relinked);
                                });
                            }
                            else if (imageExts.Contains(file.Extension))
                            {
                                if (!isKnownFile || contentChanged)
                                    assetImagePaths.TryAdd(file.Path, 0);
                                var (image, relinked, moved) = await imageProcessor.ProcessAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() =>
                                {
                                    RecordPersistedFile(isKnownFile || moved);
                                    PublishScanEntityEvent("Image", image.Id, isKnownFile || relinked);
                                });
                            }
                            else if (audioExts.Contains(file.Extension))
                            {
                                if (!isKnownFile || contentChanged)
                                    assetAudioPaths.TryAdd(file.Path, 0);
                                var (audio, relinked, moved) = await audioProcessor.ProcessAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex, mediaProbeJson: validation.ProbeJson);
                                events.Add(() =>
                                {
                                    RecordPersistedFile(isKnownFile || moved);
                                    PublishScanEntityEvent("Audio", audio.Id, isKnownFile || relinked);
                                });
                            }
                            else if (textExts.Contains(file.Extension))
                            {
                                if (!isKnownFile || contentChanged)
                                    assetTextPaths.TryAdd(file.Path, 0);
                                var (textDocument, relinked, moved) = await textProcessor.ProcessAsync(workerDb, file.Path, null, ct, file.Stat, null, knownNew: !isKnownFile, parentFolderId: folderId, contentChanged: contentChanged, scanOptions: options, moveIndex: moveIndex);
                                events.Add(() =>
                                {
                                    RecordPersistedFile(isKnownFile || moved);
                                    PublishScanEntityEvent("Text", textDocument.Id, isKnownFile || relinked);
                                });
                            }

                            return true;
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
                                    var validation = await fileValidator.ValidateAsync(
                                        file.Path,
                                        file.Size,
                                        file.ObservedModTime,
                                        ScanMediaKind.Gallery,
                                        ct);
                                    if (!RecordValidationOutcome(file, validation))
                                        return;

                                    var gallery = await galleryProcessor.ProcessAsync(workerDb, file.Path, null, ct, file.Stat, null, parentFolderId: ResolveFolderId(file), prevalidatedEntries: validation.GalleryEntries);
                                    await workerDb.SaveChangesAsync(ct);
                                    RecordPersistedFile(isKnownFile);
                                    PublishScanEntityEvent("Gallery", gallery.Id, isKnownFile);
                                }
                                else
                                {
                                    var events = new List<Action>(1);
                                    var staged = await StageAsync(work, events);
                                    if (!staged)
                                        return;
                                    await workerDb.SaveChangesAsync(ct);
                                    foreach (var publish in events)
                                        publish();
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                            {
                                directoryScanContext.MarkRequiresConfirmation(file.Path);
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
                                    var staged = await StageAsync(item, batchEvents);
                                    if (staged)
                                    {
                                        batchItems.Add(item);
                                        if (batchItems.Count >= ScanSaveBatchSize)
                                            await FlushBatchAsync();
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                                {
                                    // Staging threw mid-item, leaving partial tracked state. Drop the
                                    // whole pending batch, count this file as failed, and re-stage the
                                    // previously-good items one at a time. Rare: the hot path (new files,
                                    // ffprobe errors are swallowed internally) does not throw here.
                                    directoryScanContext.MarkRequiresConfirmation(file.Path);
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
                    "Scan phase processing completed in {ElapsedMs} ms. Checked {CheckedCount} files; skipped {SkippedCount} unchanged; processed {ChangedOrNewCount} changed/new; imported={ImportedCount}, updated={UpdatedCount}, invalidMediaSkipped={InvalidMediaSkippedCount}, deferred={DeferredFileCount}, new={NewFileCount}, metadataProbe={MetadataProbeCount}, sizeChanged={SizeChangedCount}, modTimeChanged={ModTimeChangedCount}, rescanForced={RescanForcedCount}, typeMismatch={TypeMismatchCount}, failed={FailedCount}.",
                    processStopwatch.ElapsedMilliseconds,
                    processedCount,
                    skippedUnchangedCount,
                    changedOrNewCount,
                    importedCount,
                    updatedCount,
                    invalidMediaSkippedCount,
                    deferredFileCount,
                    newFileCount,
                    metadataProbeCount,
                    sizeChangedCount,
                    modTimeChangedCount,
                    rescanForcedCount,
                    typeMismatchCount,
                    failedCount);

                // Phase 3: Create galleries from folders (if enabled)
                if (cfg.CreateGalleriesFromFolders || discovery.HasForceGalleryHints)
                {
                    progress.Report(0.90, "Creating galleries from folders...");
                    await CreateGalleriesFromFoldersAsync(db, cfg.CreateGalleriesFromFolders, ct);
                }

                var assetSummary = await assetGenerationService.GenerateRequestedAssetsAsync(
                    db,
                    progress,
                    new HashSet<string>(assetVideoPaths.Keys, FilesystemPaths.PathComparer),
                    new HashSet<string>(assetImagePaths.Keys, FilesystemPaths.PathComparer),
                    new HashSet<string>(assetAudioPaths.Keys, FilesystemPaths.PathComparer),
                    new HashSet<string>(assetTextPaths.Keys, FilesystemPaths.PathComparer),
                    options,
                    ResolveMaxParallelism(),
                    ct);
                assetGenerationFailureCount += assetSummary.FailedItems;
            }

            if (directoryScanContext.Enabled)
                await discoveryService.PersistDirectoryScanStatesAsync(directoryScanContext, ct);

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

            var completionSummary = $"Scan complete: {importedCount:N0} imported, {updatedCount:N0} updated, {FormatCount(invalidMediaSkippedCount, "invalid media file", "invalid media files")} skipped, {FormatCount(deferredFileCount, "unsettled file", "unsettled files")} deferred, {FormatCount(failedCount, "file failure", "file failures")}, {FormatCount(assetGenerationFailureCount, "asset generation failure", "asset generation failures")}.";
            progress.Report(1, completionSummary);
            logger.LogInformation("Scan completed. {Summary} Processed {Count} core files, {ParticipantCount} extension participant(s)", completionSummary, files.Count, participants.Count);
            logger.LogInformation("Scan total elapsed time: {ElapsedMs} ms", scanStopwatch.ElapsedMilliseconds);
            eventBus.Publish(new CoveEvent(EventType.ScanCompleted));

            void RecordPersistedFile(bool isKnownFile)
            {
                if (isKnownFile)
                    Interlocked.Increment(ref updatedCount);
                else
                    Interlocked.Increment(ref importedCount);
            }

            bool RecordValidationOutcome(DiscoveredFile file, ScanFileValidationResult validation)
            {
                switch (validation.Status)
                {
                    case ScanFileValidationStatus.Ready:
                        return true;
                    case ScanFileValidationStatus.Deferred:
                        directoryScanContext.MarkRequiresConfirmation(file.Path);
                        Interlocked.Increment(ref deferredFileCount);
                        logger.LogInformation("Deferring unsettled scan file; it will be retried on the next scan. Path={Path}, Reason={Reason}", file.Path, validation.Reason);
                        progress.Report(0.85, $"Deferred unsettled file {Path.GetFileName(file.Path)}: {validation.Reason}");
                        return false;
                    case ScanFileValidationStatus.Invalid:
                        directoryScanContext.MarkRequiresConfirmation(file.Path);
                        Interlocked.Increment(ref invalidMediaSkippedCount);
                        logger.LogWarning("Skipping invalid media file; it will be retried on the next scan. Path={Path}, Reason={Reason}", file.Path, validation.Reason);
                        progress.Report(0.85, $"Skipped invalid media file {Path.GetFileName(file.Path)}: {validation.Reason}");
                        return false;
                    default:
                        directoryScanContext.MarkRequiresConfirmation(file.Path);
                        Interlocked.Increment(ref failedCount);
                        logger.LogError("Unable to validate scan file. Path={Path}, Reason={Reason}", file.Path, validation.Reason);
                        progress.Report(0.85, $"Failed to validate {Path.GetFileName(file.Path)}: {validation.Reason}");
                        return false;
                }
            }
        });
    }

    private static string FormatCount(int count, string singular, string plural)
        => $"{count:N0} {(count == 1 ? singular : plural)}";

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
            TraceFolderGalleryCreated(folder.Path, group.ImageIds.Count);
        }

        await db.SaveChangesAsync(ct);
        foreach (var gallery in createdGalleries)
            eventBus.Publish(new EntityEvent(EventType.GalleryCreated, "Gallery", gallery.Id));
    }

    private static bool ShouldCreateFolderGallery(string folderPath, bool createAllEligibleFolders)
    {
        if (File.Exists(Path.Combine(folderPath, ".nogallery")))
            return false;

        return createAllEligibleFolders || File.Exists(Path.Combine(folderPath, ".forcegallery"));
    }

    private void TraceKnownFileClassified(
        string path,
        ExistingFileKind mediaType,
        ScanFileChangeReason reason,
        long oldBytes,
        long newBytes)
        => logger.LogTrace(
            "Classified known {MediaType} file {Path} as {Reason}; oldBytes={OldBytes}, newBytes={NewBytes}",
            mediaType,
            path,
            reason,
            oldBytes,
            newBytes);

    private void TraceNewFileClassified(string path)
        => logger.LogTrace("Classified file {Path} as new", path);

    private void TraceScanBatchCommitted(int fileCount)
        => logger.LogTrace("Committed scan batch containing {FileCount} files", fileCount);

    private void TraceFolderGalleryCreated(string path, int imageCount)
        => logger.LogTrace("Created folder gallery for {Path} with {ImageCount} images", path, imageCount);

    private sealed class ScopedProgress(Cove.Core.Interfaces.IJobProgress parent, double rangeStart, double rangeEnd) : ExtJobProgress
    {
        public void Report(double percent, string? message = null)
        {
            var mapped = rangeStart + (percent / 100.0) * (rangeEnd - rangeStart);
            parent.Report(mapped * 100, message);
        }
    }
}
