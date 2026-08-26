using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class ImageDeletionService(
    CoveContext db,
    CustomFieldService customFields,
    IThumbnailService thumbnailService,
    ILogger<ImageDeletionService>? logger = null,
    EntityHostDependencyService? hostDependencyService = null,
    IBlobService? blobService = null,
    ISegmentSpanCacheInvalidator? segmentSpanCacheInvalidator = null,
    IBlobReferenceCounter? blobReferenceCounter = null,
    PhysicalFileDeletionRecoverySignal? physicalFileDeletionRecoverySignal = null)
{
    private readonly EntityHostDependencyService _hostDependencies = hostDependencyService ?? new EntityHostDependencyService(db);

    public Task<bool> DeleteAsync(int id, bool deleteFile, bool deleteGenerated, CancellationToken ct = default)
        => DeleteAsync(id, deleteFile, deleteGenerated, null, ct);

    public async Task<bool> DeleteAsync(
        int id,
        bool deleteFile,
        bool deleteGenerated,
        BulkDeletionExecutionContext? executionContext,
        CancellationToken ct = default)
    {
        // Deletion only needs the file rows. Loading tags, performers, galleries, URLs, studios, and
        // parent folders for every image made large jobs spend most of their time materializing data
        // that the database's cascade rules delete without our involvement.
        var image = await db.Images
            .Include(item => item.Files)
            .FirstOrDefaultAsync(item => item.Id == id, ct);
        if (image == null)
            return false;

        var physicalPaths = deleteFile
            ? image.Files.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(FilesystemPaths.PathComparer).ToArray()
            : [];
        if (image.Files.Count > 0)
            db.ImageFiles.RemoveRange(image.Files);
        var hostCleanup = await _hostDependencies.StageDeleteAsync(AffinityHostType.Image, id, ct);
        await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Image, id, ct);
        db.Images.Remove(image);
        var physicalContext = executionContext ?? new BulkDeletionExecutionContext();
        physicalContext.StagePhysicalFiles(db, physicalPaths);
        await db.SaveChangesAsync(ct);

        foreach (var videoId in hostCleanup.SegmentVideoIds)
            segmentSpanCacheInvalidator?.InvalidateVideo(videoId);

        if (blobService is not null)
        {
            foreach (var blobId in hostCleanup.BlobIds)
            {
                try
                {
                    if (blobReferenceCounter is not null
                        && await blobReferenceCounter.CountReferencesAsync(blobId, maximum: 1, ct) == 0)
                        await thumbnailService.DeleteBlobGeneratedFilesAsync(blobId, ct);
                    await blobService.DeleteBlobIfUnreferencedAsync(blobId, ct);
                }
                catch (Exception ex)
                {
                    logger?.LogWarning(ex, "Image {ImageId} was deleted, but dependent blob {BlobId} could not be removed.", id, blobId);
                }
            }
        }

        if (executionContext is not null)
        {
            foreach (var path in physicalPaths)
                executionContext.TrackPhysicalFile(path);
        }
        else
        {
            // The metadata transaction and durable outbox are authoritative. Do not leave this HTTP
            // request waiting behind a potentially hours-long scan's producer lease.
            if (physicalPaths.Length > 0)
                physicalFileDeletionRecoverySignal?.Notify();
        }

        if (deleteGenerated)
        {
            try
            {
                await thumbnailService.DeleteImageGeneratedFilesAsync(image.Id, ct);
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "Image {ImageId} was deleted, but its generated files could not be fully removed.", image.Id);
            }
        }
        return true;
    }

}
