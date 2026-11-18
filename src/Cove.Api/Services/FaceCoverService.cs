using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>Where one of a face's images was taken from: the video or image, the moment, and the face segment there.</summary>
public sealed record FaceImageSourceDto(
    int DetectionId,
    string HostType,
    int HostId,
    string HostTitle,
    double? ObservedAtSec,
    int? SegmentId);

/// <summary>
/// The face's stored cover. <paramref name="DetectionId"/> is null when the cover was uploaded or its writer did
/// not record a source; <paramref name="SourceOnFace"/> is false when the recorded detection has since left the face.
/// </summary>
public sealed record FaceCoverStateDto(bool HasStoredCover, int? DetectionId, bool SourceOnFace);

public sealed record FaceImageSourcesDto(FaceCoverStateDto Cover, IReadOnlyList<FaceImageSourceDto> Sources);

public sealed record FaceCoverFromDetectionDto(int DetectionId);

public enum FaceCoverChangeOutcome
{
    Changed,
    FaceNotFound,
    DetectionNotOnFace,
    SourceUnavailable,
}

/// <summary>
/// Keeps a face's stored cover traceable to the detection it shows, so a cover can be chosen from the face's own
/// detections and does not go on showing someone whose detections have been moved to another face.
/// </summary>
public sealed class FaceCoverService(
    CoveContext db,
    IBlobService blobService,
    IThumbnailService thumbnailService,
    ILogger<FaceCoverService> logger,
    ICurrentPrincipalAccessor? principalAccessor = null)
{
    // Larger than the default crop so the cover stays sharp wherever a larger face image is requested.
    private const int CoverMaxDimension = 1024;

    /// <summary>
    /// The cover's state and the sources of the requested detections, limited to detections that belong to the
    /// face. The cover's own detection is always included when it is known and still on the face.
    /// </summary>
    public async Task<FaceImageSourcesDto?> DescribeAsync(int faceId, IReadOnlyCollection<int> detectionIds, CancellationToken cancellationToken)
    {
        var face = await db.Faces
            .AsNoTracking()
            .Where(item => item.Id == faceId)
            .Select(item => new { item.CoverBlobId, item.CoverDetectionId })
            .FirstOrDefaultAsync(cancellationToken);
        if (face is null)
            return null;

        var faceRefIds = await FaceMergedIds.LoadWithMergedAsync(db, faceId, cancellationToken);
        var hasStoredCover = !string.IsNullOrEmpty(face.CoverBlobId);
        var coverDetectionId = hasStoredCover ? face.CoverDetectionId : null;
        var requestedIds = detectionIds.ToHashSet();
        if (coverDetectionId is { } coverId)
            requestedIds.Add(coverId);

        var detections = requestedIds.Count == 0
            ? []
            : await FaceDetections(db.VisibleDetections().AsNoTracking(), faceRefIds)
                .Where(detection => requestedIds.Contains(detection.Id))
                .Select(detection => new SourceDetection(detection.Id, detection.HostType, detection.HostId, detection.ObservedAtSec))
                .ToListAsync(cancellationToken);

        var videoIds = detections.Where(item => item.HostType == DetectionHostType.Video).Select(item => item.HostId).Distinct().ToArray();
        var imageIds = detections.Where(item => item.HostType == DetectionHostType.Image).Select(item => item.HostId).Distinct().ToArray();
        var videoTitles = await LoadVideoTitlesAsync(videoIds, cancellationToken);
        var imageTitles = await LoadImageTitlesAsync(imageIds, cancellationToken);
        var segments = await LoadFaceSegmentsAsync(faceRefIds, videoIds, cancellationToken);

        var sources = detections
            .OrderBy(detection => detection.Id)
            .Select(detection => detection.HostType == DetectionHostType.Video
                ? new FaceImageSourceDto(
                    detection.Id,
                    "video",
                    detection.HostId,
                    videoTitles.GetValueOrDefault(detection.HostId) ?? $"Video {detection.HostId}",
                    detection.ObservedAtSec,
                    FindSegmentId(segments, detection.HostId, detection.ObservedAtSec))
                : new FaceImageSourceDto(
                    detection.Id,
                    "image",
                    detection.HostId,
                    imageTitles.GetValueOrDefault(detection.HostId) ?? $"Image {detection.HostId}",
                    null,
                    null))
            .ToList();

        // Whether the source is still on the face does not depend on whether this caller may see its video.
        var sourceOnFace = coverDetectionId is { } id
            && (detections.Any(detection => detection.Id == id)
                || await FaceDetections(db.Detections.AsNoTracking(), faceRefIds).AnyAsync(detection => detection.Id == id, cancellationToken));

        return new FaceImageSourcesDto(new FaceCoverStateDto(hasStoredCover, coverDetectionId, sourceOnFace), sources);
    }

    /// <summary>Replaces the face's cover with a crop of one of its own detections and records that detection as the source.</summary>
    public async Task<FaceCoverChangeOutcome> SetFromDetectionAsync(int faceId, int detectionId, CancellationToken cancellationToken)
    {
        if (!await db.Faces.AnyAsync(item => item.Id == faceId, cancellationToken))
            return FaceCoverChangeOutcome.FaceNotFound;

        var faceRefIds = await FaceMergedIds.LoadWithMergedAsync(db, faceId, cancellationToken);
        var detection = await FaceDetections(db.VisibleDetections().AsNoTracking(), faceRefIds)
            .FirstOrDefaultAsync(item => item.Id == detectionId, cancellationToken);
        if (detection is null)
            return FaceCoverChangeOutcome.DetectionNotOnFace;

        // Only the detection's own frame: the image is kept, so a stand-in from another moment would stay wrong.
        var source = detection.HostType == DetectionHostType.Image
            ? (await thumbnailService.GetImageStreamAsync(detection.HostId, cancellationToken))?.stream
            : await DetectionFrame.OpenAsync(db, thumbnailService, detection, cancellationToken);
        if (source is null)
            return FaceCoverChangeOutcome.SourceUnavailable;

        string blobId;
        await using (source)
        await using (var crop = await DetectionCropRenderer.RenderAsync(detection, source, CoverMaxDimension, cancellationToken: cancellationToken))
        {
            if (crop is null)
                return FaceCoverChangeOutcome.SourceUnavailable;
            blobId = await blobService.StoreBlobAsync(crop, "image/jpeg", cancellationToken);
        }

        // Loaded only now, after the frame work that can take seconds, so a cover written meanwhile is replaced
        // knowingly and its blob cleaned up.
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == faceId, cancellationToken);
        if (face is null)
        {
            await CleanupReplacedBlobAsync(blobId, null);
            return FaceCoverChangeOutcome.FaceNotFound;
        }

        var previousBlobId = face.CoverBlobId;
        face.CoverBlobId = blobId;
        face.CoverDetectionId = detectionId;
        // Choosing the same detection again leaves the id unchanged; mark it so the save does not treat the
        // new cover as one whose source nobody stated.
        db.Entry(face).Property(item => item.CoverDetectionId).IsModified = true;
        await db.SaveChangesAsync(cancellationToken);
        await CleanupReplacedBlobAsync(previousBlobId, blobId);
        return FaceCoverChangeOutcome.Changed;
    }

    /// <summary>
    /// Removes the stored cover when the detection it was cropped from no longer belongs to the face, so the face
    /// falls back to an image of what it still contains. Covers with no recorded source are left alone.
    /// </summary>
    public async Task<bool> ReleaseIfSourceLeftAsync(int faceId, CancellationToken cancellationToken)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == faceId && item.CoverDetectionId != null, cancellationToken);
        if (face is null || string.IsNullOrEmpty(face.CoverBlobId))
            return false;

        var coverDetectionId = face.CoverDetectionId!.Value;
        var faceRefIds = await FaceMergedIds.LoadWithMergedAsync(db, faceId, cancellationToken);
        if (await FaceDetections(db.Detections.AsNoTracking(), faceRefIds).AnyAsync(detection => detection.Id == coverDetectionId, cancellationToken))
            return false;

        var previousBlobId = face.CoverBlobId;
        face.CoverBlobId = null;
        face.CoverDetectionId = null;
        await db.SaveChangesAsync(cancellationToken);
        await CleanupReplacedBlobAsync(previousBlobId, null);
        return true;
    }

    private static IQueryable<Detection> FaceDetections(IQueryable<Detection> detections, long[] faceRefIds)
    {
        return detections.Where(detection =>
            detection.RefId != null && faceRefIds.Contains(detection.RefId.Value) && detection.RefKind != null && detection.RefKind.ToLower() == "face");
    }

    // Untitled media falls back to its file basename, as the media cards do.
    private async Task<Dictionary<int, string?>> LoadVideoTitlesAsync(int[] videoIds, CancellationToken cancellationToken)
        => videoIds.Length == 0
            ? []
            : (await db.Videos
                    .AsNoTracking()
                    .Where(video => videoIds.Contains(video.Id))
                    .Select(video => new
                    {
                        video.Id,
                        video.Title,
                        Basename = video.PrimaryFile != null
                            ? video.PrimaryFile.Basename
                            : video.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                    })
                    .ToListAsync(cancellationToken))
                .ToDictionary(video => video.Id, video => Clean(video.Title) ?? Clean(video.Basename));

    private async Task<Dictionary<int, string?>> LoadImageTitlesAsync(int[] imageIds, CancellationToken cancellationToken)
        => imageIds.Length == 0
            ? []
            : (await db.Images
                    .AsNoTracking()
                    .Where(image => imageIds.Contains(image.Id))
                    .Select(image => new
                    {
                        image.Id,
                        image.Title,
                        Basename = image.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                    })
                    .ToListAsync(cancellationToken))
                .ToDictionary(image => image.Id, image => Clean(image.Title) ?? Clean(image.Basename));

    private async Task<List<FaceSegment>> LoadFaceSegmentsAsync(long[] faceRefIds, int[] videoIds, CancellationToken cancellationToken)
    {
        // A segment id is only useful to someone who may open segments.
        if (videoIds.Length == 0 || principalAccessor?.Current is { } principal && !principal.Has(Permissions.SegmentsRead))
            return [];

        return await db.Segments
            .AsNoTracking()
            .Where(segment =>
                segment.HostType == SegmentHostType.Video
                && videoIds.Contains(segment.HostId)
                && segment.RefId != null
                && faceRefIds.Contains(segment.RefId.Value)
                && segment.Kind != null
                && segment.Kind.ToLower() == "face")
            .Select(segment => new FaceSegment(segment.Id, segment.HostId, segment.StartSec, segment.EndSec))
            .ToListAsync(cancellationToken);
    }

    // The face's segment that contains the moment; the shortest one when several overlap it.
    private static int? FindSegmentId(IReadOnlyList<FaceSegment> segments, int videoId, double? observedAtSec)
    {
        if (observedAtSec is not { } seconds)
            return null;

        return segments
            .Where(segment => segment.HostId == videoId && segment.StartSec <= seconds && seconds <= (segment.EndSec ?? segment.StartSec))
            .OrderBy(segment => (segment.EndSec ?? segment.StartSec) - segment.StartSec)
            .ThenBy(segment => segment.Id)
            .Select(segment => (int?)segment.Id)
            .FirstOrDefault();
    }

    private async Task CleanupReplacedBlobAsync(string? previousBlobId, string? currentBlobId)
    {
        if (string.IsNullOrWhiteSpace(previousBlobId) || string.Equals(previousBlobId, currentBlobId, StringComparison.Ordinal))
            return;

        try
        {
            await blobService.DeleteBlobIfUnreferencedAsync(previousBlobId, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Failed to clean up replaced face cover blob {BlobId}; the persisted cover state remains valid", previousBlobId);
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private readonly record struct SourceDetection(int Id, DetectionHostType HostType, int HostId, double? ObservedAtSec);

    private readonly record struct FaceSegment(int Id, int HostId, double StartSec, double? EndSec);
}
