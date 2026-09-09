using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Entities.Galleries.Zip;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;

namespace Cove.Api.Services;

public class ScanService : IScanService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEventBus _eventBus;
    private readonly ScanVideoProcessor _videoProcessor;
    private readonly ScanImageProcessor _imageProcessor;
    private readonly ScanGalleryProcessor _galleryProcessor;
    private readonly ScanAudioProcessor _audioProcessor;
    private readonly ScanTextProcessor _textProcessor;
    private readonly ScanJobRunner _scanJobRunner;
    private readonly IScanFileValidator _fileValidator;

    public ScanService(
        IJobService jobService,
        IServiceScopeFactory scopeFactory,
        CoveConfiguration config,
        IEventBus eventBus,
        IFingerprintService fingerprintService,
        IThumbnailService thumbnailService,
        TextExtractionService textExtractionService,
        ZipGalleryReader zipGalleryReader,
        ExtensionManager extensionManager,
        IMediaProbeService mediaProbeService,
        IScanFileValidator fileValidator,
        ILogger<ScanService> logger,
        PhysicalFileAccessCoordinator? physicalFileCoordinator = null)
    {
        _scopeFactory = scopeFactory;
        _eventBus = eventBus;
        _fileValidator = fileValidator;
        _physicalFileCoordinator = physicalFileCoordinator ?? PhysicalFileAccessCoordinator.Shared;

        var folderResolver = new ScanFolderResolver(logger);
        var fileIdentity = new ScanFileIdentityService(fingerprintService);
        var discoveryService = new ScanDiscoveryService(scopeFactory, config, logger);
        var assetGenerationService = new ScanAssetGenerationService(scopeFactory, fingerprintService, thumbnailService, logger);

        _videoProcessor = new ScanVideoProcessor(config, fingerprintService, mediaProbeService, folderResolver, fileIdentity, logger);
        _imageProcessor = new ScanImageProcessor(config, fingerprintService, thumbnailService, folderResolver, fileIdentity, logger);
        _galleryProcessor = new ScanGalleryProcessor(zipGalleryReader, folderResolver, logger);
        _audioProcessor = new ScanAudioProcessor(config, fingerprintService, mediaProbeService, folderResolver, fileIdentity, logger);
        _textProcessor = new ScanTextProcessor(config, fingerprintService, textExtractionService, folderResolver, fileIdentity, logger);
        _scanJobRunner = new ScanJobRunner(
            jobService,
            scopeFactory,
            config,
            eventBus,
            extensionManager,
            fileValidator,
            discoveryService,
            assetGenerationService,
            folderResolver,
            _videoProcessor,
            _imageProcessor,
            _galleryProcessor,
            _audioProcessor,
            _textProcessor,
            logger,
            _physicalFileCoordinator);
    }

    private readonly PhysicalFileAccessCoordinator _physicalFileCoordinator;

    public async Task<int> ImportDownloadedVideoAsync(string path, int? videoId, CancellationToken ct = default)
    {
        using var pathLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        return await ImportDownloadedVideoWithinProducerLeaseAsync(path, videoId, ct);
    }

    internal async Task<int> ImportDownloadedVideoWithinProducerLeaseAsync(string path, int? videoId, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded video file not found", path);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (videoFile, _, _) = await _videoProcessor.ProcessAsync(db, path, videoId, ct);
        await db.SaveChangesAsync(ct);

        var resolvedVideoId = videoFile.VideoId;
        if (!resolvedVideoId.HasValue || resolvedVideoId.Value == 0)
            throw new InvalidOperationException($"Imported video file {path} was not attached to a video");

        _eventBus.Publish(new EntityEvent(
            videoId.HasValue ? EventType.VideoUpdated : EventType.VideoCreated,
            "Video",
            resolvedVideoId.Value));

        return resolvedVideoId.Value;
    }

    public async Task<int> ImportDownloadedImageAsync(string path, int? imageId, CancellationToken ct = default)
    {
        using var pathLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        return await ImportDownloadedImageWithinProducerLeaseAsync(path, imageId, ct);
    }

    internal async Task<int> ImportDownloadedImageWithinProducerLeaseAsync(string path, int? imageId, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded image file not found", path);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var validation = await _fileValidator.ValidateImageContentAsync(path, ct);
        if (validation.Status != ScanFileValidationStatus.Ready)
            throw new InvalidDataException(validation.Reason ?? $"Unable to validate image file {path}");
        var (image, _, _) = await _imageProcessor.ProcessAsync(
            db,
            path,
            imageId,
            ct,
            validatedWidth: validation.Width,
            validatedHeight: validation.Height);
        await db.SaveChangesAsync(ct);

        if (image.Id == 0)
            throw new InvalidOperationException($"Imported image file {path} was not attached to an image");

        _eventBus.Publish(new EntityEvent(
            imageId.HasValue ? EventType.ImageUpdated : EventType.ImageCreated,
            "Image",
            image.Id));

        return image.Id;
    }

    public async Task<int> ImportDownloadedGalleryAsync(string path, int? galleryId, CancellationToken ct = default)
    {
        using var pathLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        return await ImportDownloadedGalleryWithinProducerLeaseAsync(path, galleryId, ct);
    }

    internal async Task<int> ImportDownloadedGalleryWithinProducerLeaseAsync(string path, int? galleryId, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded gallery file not found", path);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var gallery = await _galleryProcessor.ProcessAsync(db, path, galleryId, ct);
        await db.SaveChangesAsync(ct);

        if (gallery.Id == 0)
            throw new InvalidOperationException($"Imported gallery file {path} was not attached to a gallery");

        _eventBus.Publish(new EntityEvent(
            galleryId.HasValue ? EventType.GalleryUpdated : EventType.GalleryCreated,
            "Gallery",
            gallery.Id));

        return gallery.Id;
    }

    public async Task<int> ImportDownloadedAudioAsync(string path, int? audioId, CancellationToken ct = default)
    {
        using var pathLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        return await ImportDownloadedAudioWithinProducerLeaseAsync(path, audioId, ct);
    }

    internal async Task<int> ImportDownloadedAudioWithinProducerLeaseAsync(string path, int? audioId, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded audio file not found", path);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (audio, _, _) = await _audioProcessor.ProcessAsync(db, path, audioId, ct);
        await db.SaveChangesAsync(ct);

        if (audio.Id == 0)
            throw new InvalidOperationException($"Imported audio file {path} was not attached to an audio item");

        _eventBus.Publish(new EntityEvent(
            audioId.HasValue ? EventType.AudioUpdated : EventType.AudioCreated,
            "Audio",
            audio.Id));

        return audio.Id;
    }

    public async Task<int> ImportDownloadedTextAsync(string path, int? textDocumentId, CancellationToken ct = default)
    {
        using var pathLease = await _physicalFileCoordinator.AcquireReadAsync(ct);
        return await ImportDownloadedTextWithinProducerLeaseAsync(path, textDocumentId, ct);
    }

    internal async Task<int> ImportDownloadedTextWithinProducerLeaseAsync(string path, int? textDocumentId, CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Downloaded text file not found", path);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var (textDocument, _, _) = await _textProcessor.ProcessAsync(db, path, textDocumentId, ct);
        await db.SaveChangesAsync(ct);

        if (textDocument.Id == 0)
            throw new InvalidOperationException($"Imported text file {path} was not attached to a text document");

        _eventBus.Publish(new EntityEvent(
            textDocumentId.HasValue ? EventType.TextUpdated : EventType.TextCreated,
            "Text",
            textDocument.Id));

        return textDocument.Id;
    }

    public string StartScan(ScanOperationOptions? options = null)
        => _scanJobRunner.Start(options);

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
        => ScanDiscoveryService.IsMediaTypeExcludedByScanTarget(
            extension,
            excludeVideo,
            excludeImage,
            excludeAudio,
            excludeText,
            videoExts,
            imageExts,
            galleryExts,
            audioExts,
            textExts);

    internal static bool NeedsVideoMetadataProbe(VideoFile videoFile)
        => ScanVideoProcessor.NeedsMetadataProbe(videoFile);

    internal static void ApplyFfprobeMetadata(VideoFile videoFile, string json)
        => ScanVideoProcessor.ApplyFfprobeMetadata(videoFile, json);

}
