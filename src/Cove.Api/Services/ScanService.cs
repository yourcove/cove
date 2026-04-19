using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public class ScanService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    IEventBus eventBus,
    IFingerprintService fingerprintService,
    IThumbnailService thumbnailService,
    ZipGalleryReader zipGalleryReader,
    ILogger<ScanService> logger) : IScanService
{
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
            var allExts = videoExts.Union(imageExts).Union(galleryExts).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var processedVideoPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var processedImagePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Phase 1: Discover files
            progress.Report(0, "Discovering files...");
            var files = new List<DiscoveredFile>();
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
                    if (scanTarget.ExcludeVideo && videoExts.Contains(ext))
                    {
                        continue;
                    }
                    if (scanTarget.ExcludeImage && imageExts.Contains(ext))
                    {
                        continue;
                    }
                    if (IsExcluded(scanTarget.Path, cfg.ExcludePatterns))
                    {
                        continue;
                    }

                    files.Add(new DiscoveredFile(NormalizePath(scanTarget.Path), ext));
                    continue;
                }

                if (!Directory.Exists(scanTarget.Path))
                {
                    logger.LogWarning("Scan target does not exist: {Path}", scanTarget.Path);
                    continue;
                }

                var dirFiles = Directory.EnumerateFiles(scanTarget.Path, "*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var ext = Path.GetExtension(f);
                        if (!allExts.Contains(ext)) return false;
                        if (scanTarget.ExcludeVideo && videoExts.Contains(ext)) return false;
                        if (scanTarget.ExcludeImage && imageExts.Contains(ext)) return false;
                        return !IsExcluded(f, cfg.ExcludePatterns);
                    })
                    .Select(f => new DiscoveredFile(NormalizePath(f), Path.GetExtension(f)));

                files.AddRange(dirFiles);
            }

            logger.LogInformation("Discovered {Count} files to scan", files.Count);
            if (files.Count == 0) return;

            // Phase 2: Process files
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

            var processedCount = 0;
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                processedCount++;
                progress.Report(0.85 * (double)processedCount / files.Count, Path.GetFileName(file.Path));

                try
                {
                    // Check if file already exists in DB by path
                    var existingFolder = await db.Folders
                        .FirstOrDefaultAsync(f => f.Path == Path.GetDirectoryName(file.Path), ct);

                    if (existingFolder != null)
                    {
                        var basename = Path.GetFileName(file.Path);
                        var existingFile = await db.Set<BaseFileEntity>()
                            .FirstOrDefaultAsync(f => f.ParentFolderId == existingFolder.Id && f.Basename == basename, ct);

                        if (existingFile != null)
                        {
                            // Check if file has been modified — but always re-process videos with missing metadata
                            var fileInfo = new FileInfo(file.Path);
                            var needsMetadata = existingFile is VideoFile vf && vf.Width == 0 && vf.Height == 0 && vf.Duration == 0;
                            if (!options.Rescan && !needsMetadata && existingFile.ModTime >= fileInfo.LastWriteTimeUtc && existingFile.Size == fileInfo.Length)
                                continue; // Not modified and metadata present, skip
                        }
                    }

                    // Process the file
                    if (videoExts.Contains(file.Extension))
                    {
                        processedVideoPaths.Add(file.Path);
                        await ProcessVideoFileAsync(db, file.Path, ct);
                    }
                    else if (imageExts.Contains(file.Extension))
                    {
                        processedImagePaths.Add(file.Path);
                        await ProcessImageFileAsync(db, file.Path, ct);
                    }
                    else if (galleryExts.Contains(file.Extension))
                        await ProcessGalleryFileAsync(db, file.Path, ct);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error processing file: {Path}", file.Path);
                }
            }

            await db.SaveChangesAsync(ct);

            // Phase 3: Create galleries from folders (if enabled)
            if (cfg.CreateGalleriesFromFolders)
            {
                progress.Report(0.90, "Creating galleries from folders...");
                await CreateGalleriesFromFoldersAsync(db, ct);
            }

            await GenerateRequestedAssetsAsync(db, progress, processedVideoPaths, processedImagePaths, options, thumbnailService, ct);

            logger.LogInformation("Scan completed. Processed {Count} files", processedCount);
            eventBus.Publish(new CoveEvent(EventType.ScanCompleted));
        });
    }

    private async Task GenerateRequestedAssetsAsync(
        CoveContext db,
        IJobProgress progress,
        HashSet<string> processedVideoPaths,
        HashSet<string> processedImagePaths,
        ScanOperationOptions options,
        IThumbnailService thumbnailService,
        CancellationToken ct)
    {
        var generateSceneAssets = options.GenerateCovers || options.GeneratePreviews || options.GenerateSprites || options.GeneratePhashes;
        var generateImageAssets = options.GenerateImagePhashes;

        if ((!generateSceneAssets && !generateImageAssets) || (processedVideoPaths.Count == 0 && processedImagePaths.Count == 0))
        {
            return;
        }

        if (generateSceneAssets && processedVideoPaths.Count > 0)
        {
            progress.Report(0.92, "Generating scene assets...");

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

            var sceneFiles = candidateFiles
                .Where(file => file.ParentFolder != null && processedVideoPaths.Contains(NormalizePath(Path.Combine(file.ParentFolder.Path, file.Basename))))
                .Where(file => file.SceneId.HasValue && file.SceneId.Value != 0)
                .GroupBy(file => file.SceneId)
                .Select(group => group.First())
                .ToList();

            var total = Math.Max(sceneFiles.Count, 1);
            var completed = 0;

            var maxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(sceneFiles, new ParallelOptions { MaxDegreeOfParallelism = maxParallelism, CancellationToken = ct }, async (sceneFile, token) =>
            {
                var done = Interlocked.Increment(ref completed);
                var sceneId = sceneFile.SceneId!.Value;

                progress.Report(0.92 + (0.06 * done / total), $"Generating scene assets ({done}/{sceneFiles.Count})");

                if (options.GenerateCovers)
                {
                    await thumbnailService.GenerateSceneThumbnailAsync(sceneId, null, token);
                }
                if (options.GeneratePreviews)
                {
                    await thumbnailService.GenerateScenePreviewAsync(sceneId, token);
                }
                if (options.GenerateSprites)
                {
                    await thumbnailService.GenerateSceneSpriteAsync(sceneId, token);
                }
                if (options.GeneratePhashes && sceneFile.ParentFolder != null)
                {
                    var filePath = Path.Combine(sceneFile.ParentFolder.Path, sceneFile.Basename);
                    var phash = await fingerprintService.ComputeVideoPhashAsync(filePath, sceneFile.Duration, token);
                    if (!string.IsNullOrWhiteSpace(phash))
                    {
                        using var innerScope = scopeFactory.CreateScope();
                        var innerDb = innerScope.ServiceProvider.GetRequiredService<CoveContext>();
                        var existing = await innerDb.FileFingerprints.FirstOrDefaultAsync(fp => fp.FileId == sceneFile.Id && fp.Type == "phash", token);
                        if (existing != null)
                        {
                            existing.Value = phash;
                        }
                        else
                        {
                            innerDb.FileFingerprints.Add(new FileFingerprint
                            {
                                FileId = sceneFile.Id,
                                Type = "phash",
                                Value = phash,
                            });
                        }
                        await innerDb.SaveChangesAsync(token);
                    }
                }
            });
        }

        if (generateImageAssets && processedImagePaths.Count > 0)
        {
            progress.Report(0.98, "Generating image perceptual hashes...");

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

            var imgMaxParallelism = ResolveMaxParallelism();
            await Parallel.ForEachAsync(imageFiles, new ParallelOptions { MaxDegreeOfParallelism = imgMaxParallelism, CancellationToken = ct }, async (imageFile, token) =>
            {
                if (imageFile.ParentFolder == null)
                    return;

                var filePath = Path.Combine(imageFile.ParentFolder.Path, imageFile.Basename);
                var phash = await fingerprintService.ComputeImagePhashAsync(filePath, token);
                if (string.IsNullOrWhiteSpace(phash))
                    return;

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
            });
        }
    }

    /// <summary>
    /// Create folder-based galleries for folders that contain images but have no gallery yet.
    /// </summary>
    private async Task CreateGalleriesFromFoldersAsync(CoveContext db, CancellationToken ct)
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

        // Get image IDs per folder
        var imagesByFolder = await db.ImageFiles
            .Where(f => newFolderIds.Contains(f.ParentFolderId) && f.ZipFileId == null && f.ImageId != null)
            .GroupBy(f => f.ParentFolderId)
            .Select(g => new { FolderId = g.Key, ImageIds = g.Select(f => f.ImageId!.Value).ToList() })
            .ToListAsync(ct);

        foreach (var group in imagesByFolder)
        {
            if (!folders.TryGetValue(group.FolderId, out var folder)) continue;

            var gallery = new Gallery
            {
                Title = Path.GetFileName(folder.Path) ?? folder.Path,
                FolderId = folder.Id,
            };

            foreach (var imageId in group.ImageIds)
            {
                gallery.ImageGalleries.Add(new ImageGallery { ImageId = imageId, Gallery = gallery });
            }

            db.Galleries.Add(gallery);
            logger.LogDebug("Created folder gallery for: {Path} with {Count} images", folder.Path, group.ImageIds.Count);
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task<Folder> EnsureFolderAsync(CoveContext db, string dirPath, CancellationToken ct)
    {
        var folder = await db.Folders.FirstOrDefaultAsync(f => f.Path == dirPath, ct);
        if (folder != null) return folder;

        folder = new Folder
        {
            Path = dirPath,
            ModTime = Directory.GetLastWriteTimeUtc(dirPath)
        };

        // Link parent folder if path has a parent
        var parentDir = Path.GetDirectoryName(dirPath);
        if (!string.IsNullOrEmpty(parentDir) && parentDir != dirPath)
        {
            var parentFolder = await db.Folders.FirstOrDefaultAsync(f => f.Path == parentDir, ct);
            if (parentFolder != null)
                folder.ParentFolderId = parentFolder.Id;
        }

        db.Folders.Add(folder);
        await db.SaveChangesAsync(ct);
        return folder;
    }

    private async Task ProcessVideoFileAsync(CoveContext db, string path, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        var dirPath = Path.GetDirectoryName(path) ?? path;
        var folder = await EnsureFolderAsync(db, dirPath, ct);

        var basename = Path.GetFileName(path);
        var existing = await db.VideoFiles
            .FirstOrDefaultAsync(f => f.ParentFolderId == folder.Id && f.Basename == basename, ct);

        if (existing != null)
        {
            existing.Size = fileInfo.Length;
            existing.ModTime = fileInfo.LastWriteTimeUtc;

            // Re-probe if metadata is missing (e.g., FFprobe wasn't available during initial scan)
            if (existing.Width == 0 && existing.Height == 0 && existing.Duration == 0)
            {
                await ProbeVideoAsync(existing, path, ct);
            }
            return;
        }

        // Create video file entry
        var videoFile = new VideoFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Size = fileInfo.Length,
            ModTime = fileInfo.LastWriteTimeUtc,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
        };

        // Probe with FFprobe for metadata
        await ProbeVideoAsync(videoFile, path, ct);

        // Create scene for the video file
        var scene = new Scene
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Files = [videoFile]
        };

        db.Scenes.Add(scene);

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

        // Detect sidecar caption files (.vtt, .srt) adjacent to the video file
        var videoDir = Path.GetDirectoryName(path);
        var videoBaseName = Path.GetFileNameWithoutExtension(path);
        if (videoDir != null)
        {
            foreach (var captionFile in Directory.EnumerateFiles(videoDir)
                .Where(f => f.StartsWith(Path.Combine(videoDir, videoBaseName), StringComparison.OrdinalIgnoreCase)
                    && (f.EndsWith(".vtt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".srt", StringComparison.OrdinalIgnoreCase))))
            {
                var captionFilename = Path.GetFileName(captionFile);
                var ext = Path.GetExtension(captionFile).TrimStart('.').ToLowerInvariant();
                // Try to extract language code from filename pattern: video.en.vtt or video.en.srt
                var langCode = "00";
                var nameWithoutExt = Path.GetFileNameWithoutExtension(captionFile);
                var parts = nameWithoutExt.Split('.');
                if (parts.Length >= 2)
                {
                    var candidate = parts[^1];
                    if (candidate.Length is 2 or 3) langCode = candidate.ToLowerInvariant();
                }
                videoFile.Captions.Add(new VideoCaption
                {
                    LanguageCode = langCode,
                    CaptionType = ext,
                    Filename = captionFilename
                });
            }
        }

        logger.LogDebug("Added scene for: {Path}", path);
        eventBus.Publish(new EntityEvent(EventType.SceneCreated, "Scene", 0, scene));
    }

    private async Task ProcessImageFileAsync(CoveContext db, string path, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        var dirPath = Path.GetDirectoryName(path) ?? path;
        var folder = await EnsureFolderAsync(db, dirPath, ct);

        var basename = Path.GetFileName(path);
        var existing = await db.ImageFiles
            .FirstOrDefaultAsync(f => f.ParentFolderId == folder.Id && f.Basename == basename, ct);

        if (existing != null)
        {
            existing.Size = fileInfo.Length;
            existing.ModTime = fileInfo.LastWriteTimeUtc;
            return;
        }

        var imageFile = new ImageFile
        {
            Basename = basename,
            ParentFolderId = folder.Id,
            Size = fileInfo.Length,
            ModTime = fileInfo.LastWriteTimeUtc,
            Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant()
        };

        var image = new Image
        {
            Title = Path.GetFileNameWithoutExtension(path),
            Files = [imageFile]
        };

        if (config.CalculateMd5)
        {
            var md5 = await fingerprintService.ComputeMd5Async(path, ct);
            if (!string.IsNullOrWhiteSpace(md5))
            {
                imageFile.Fingerprints.Add(new FileFingerprint
                {
                    Type = "md5",
                    Value = md5,
                });
            }
        }

        db.Images.Add(image);
        logger.LogDebug("Added image for: {Path}", path);
    }

    private async Task ProcessGalleryFileAsync(CoveContext db, string path, CancellationToken ct)
    {
        var fileInfo = new FileInfo(path);
        var dirPath = Path.GetDirectoryName(path) ?? path;
        var folder = await EnsureFolderAsync(db, dirPath, ct);

        var basename = Path.GetFileName(path);
        var existing = await db.Set<GalleryFile>()
            .Include(gf => gf.Gallery)
            .ThenInclude(g => g!.ImageGalleries)
            .FirstOrDefaultAsync(f => f.ParentFolderId == folder.Id && f.Basename == basename, ct);

        // If gallery exists and already has images, skip re-processing
        if (existing?.Gallery?.ImageGalleries.Count > 0)
        {
            logger.LogDebug("Gallery already processed with {Count} images: {Path}",
                existing.Gallery.ImageGalleries.Count, path);
            return;
        }

        // Create or update the gallery file entry
        GalleryFile galleryFile;
        Gallery gallery;

        if (existing != null)
        {
            // Update existing file metadata
            galleryFile = existing;
            galleryFile.Size = fileInfo.Length;
            galleryFile.ModTime = fileInfo.LastWriteTimeUtc;
            gallery = existing.Gallery!;
        }
        else
        {
            // Create new gallery file and gallery
            galleryFile = new GalleryFile
            {
                Basename = basename,
                ParentFolderId = folder.Id,
                Size = fileInfo.Length,
                ModTime = fileInfo.LastWriteTimeUtc
            };

            gallery = new Gallery
            {
                Title = Path.GetFileNameWithoutExtension(path),
                Files = [galleryFile]
            };

            db.Galleries.Add(gallery);
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
                return;
            }

            logger.LogDebug("Found {Count} images in gallery: {Path}", imageEntries.Count, path);

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
            foreach (var entry in imageEntries)
            {
                // Create ImageFile record representing the image within the zip
                // Use FullName to preserve the internal zip path structure and avoid duplicate basenames
                var imageFile = new ImageFile
                {
                    Basename = entry.FullName,  // Use full internal path to avoid collisions
                    ParentFolderId = virtualFolder.Id,  // Use virtual folder specific to this zip
                    ZipFileId = galleryFile.Id,  // Link to parent zip file
                    Size = entry.Length,
                    ModTime = entry.LastWriteTime.UtcDateTime,
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

            logger.LogDebug("Added gallery with {Count} images: {Path}", imageEntries.Count, path);
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
        }
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

            // Extract stream info
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var codecType = stream.TryGetProperty("codec_type", out var ct2) ? ct2.GetString() : null;
                    if (codecType == "video" && videoFile.Width == 0)
                    {
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
                    else if (codecType == "audio" && string.IsNullOrEmpty(videoFile.AudioCodec))
                    {
                        if (stream.TryGetProperty("codec_name", out var acn)) videoFile.AudioCodec = acn.GetString() ?? "";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "FFprobe failed for {Path}", path);
        }
    }

    private string? FindFfprobe()
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

    private static List<ScanTarget> ResolveScanTargets(CoveConfiguration cfg, List<string>? selectedPaths)
    {
        if (selectedPaths == null)
        {
            return cfg.CovePaths
                .Select(path => new ScanTarget(NormalizePath(path.Path), path.ExcludeVideo, path.ExcludeImage, false))
                .ToList();
        }

        var targets = new List<ScanTarget>();
        foreach (var selectedPath in selectedPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var matchingConfig = cfg.CovePaths
                .Select(path => new { Config = path, NormalizedPath = NormalizePath(path.Path) })
                .Where(x => IsPathWithin(selectedPath, x.NormalizedPath) || IsPathWithin(x.NormalizedPath, selectedPath))
                .OrderByDescending(x => x.NormalizedPath.Length)
                .Select(x => x.Config)
                .FirstOrDefault();

            var excludeVideo = matchingConfig?.ExcludeVideo ?? false;
            var excludeImage = matchingConfig?.ExcludeImage ?? false;
            var isFile = File.Exists(selectedPath);

            if (!isFile && !Directory.Exists(selectedPath))
            {
                continue;
            }

            targets.Add(new ScanTarget(selectedPath, excludeVideo, excludeImage, isFile));
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

    private record DiscoveredFile(string Path, string Extension);
    private record ScanTarget(string Path, bool ExcludeVideo, bool ExcludeImage, bool IsFile);
}
