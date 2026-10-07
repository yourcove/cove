using System.Data;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// The unattended half of the primary-file dialog: swaps a video's primary file when the replacement holds
/// the same footage, and removes files. Extensions use it for batches that must finish without a browser
/// waiting. Anything needing alignment or dependency decisions still belongs in VideoAlignmentsController,
/// where the signed-in user can answer for them.
/// </summary>
public sealed class VideoFileMaintenanceService(
    CoveContext db,
    IFingerprintService fingerprintService,
    ISegmentSpanCacheInvalidator segmentSpanCacheInvalidator,
    IEventBus eventBus,
    VideoGeneratedAssetCoordinator generatedAssetCoordinator,
    ILogger<VideoFileMaintenanceService> logger,
    PhysicalFileDeletionRecoverySignal? physicalFileDeletionRecoverySignal = null) : IVideoFileMaintenanceService
{
    // The tolerances the primary-file dialog uses for "this is the same video".
    private const int EquivalentPhashDistance = 8;
    private const double EquivalentDurationTolerance = 1;

    public async Task<VideoFileOperationResult> MakePrimaryWhenSameContentAsync(int videoId, int fileId, CancellationToken ct)
    {
        var video = await db.Videos.AsNoTracking().SingleOrDefaultAsync(item => item.Id == videoId, ct);
        if (video is null)
            return new(false, "The video no longer exists.");

        var target = await db.VideoFiles.AsNoTracking().SingleOrDefaultAsync(file => file.Id == fileId && file.VideoId == videoId, ct);
        if (target is null)
            return new(false, "The replacement file is not attached to this video.");
        if (video.PrimaryFileId == target.Id)
            return new(true, null);

        var source = video.PrimaryFileId.HasValue
            ? await db.VideoFiles.AsNoTracking().SingleOrDefaultAsync(file => file.Id == video.PrimaryFileId, ct)
            : null;

        // Hashing reads the whole file, so it runs before any transaction opens.
        if (!await HoldsTheSameContentAsync(source, target, ct))
        {
            return new(false,
                "The replacement file is not the same footage as the current primary file (different length or appearance), "
                + "so it stays attached to the video without becoming primary.");
        }

        await using var assetLease = await generatedAssetCoordinator.AcquireAsync(videoId, ct);
        var changed = false;
        VideoFileOperationResult result = new(false, "The primary file could not be changed.");
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            changed = false;
            result = new(false, "The primary file could not be changed.");
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : null;

            var tracked = await db.Videos.SingleOrDefaultAsync(item => item.Id == videoId, ct);
            if (tracked is null) { result = new(false, "The video no longer exists."); return; }
            if (tracked.PrimaryFileId != video.PrimaryFileId)
            { result = new(false, "The primary file changed while this change was being prepared."); return; }
            if (!await db.VideoFiles.AnyAsync(file => file.Id == fileId && file.VideoId == videoId, ct))
            { result = new(false, "The replacement file is no longer attached to this video."); return; }

            tracked.PrimaryFileId = fileId;
            tracked.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            changed = true;
            result = new(true, null);
        });

        if (!changed)
            return result;

        // The footage is unchanged, so covers, sprites and previews stay valid and are deliberately kept.
        generatedAssetCoordinator.Advance(videoId);
        try
        {
            segmentSpanCacheInvalidator.InvalidateVideo(videoId);
            eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "video", videoId));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not finish post-commit notifications after changing the primary file for video {VideoId}", videoId);
        }

        return result;
    }

    public async Task<VideoFileOperationResult> DeleteFileAsync(int fileId, bool deleteFromDisk, CancellationToken ct)
    {
        var deleted = false;
        var stagedPaths = 0;
        int? ownerVideoId = null;
        VideoFileOperationResult result = new(false, "The file could not be deleted.");

        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            deleted = false;
            stagedPaths = 0;
            ownerVideoId = null;
            result = new(false, "The file could not be deleted.");
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct)
                : null;

            var file = await db.Set<BaseFileEntity>().Include(item => item.ParentFolder).SingleOrDefaultAsync(item => item.Id == fileId, ct);
            if (file is null) { result = new(false, "The file no longer exists."); return; }
            if (await db.Videos.IgnoreQueryFilters().AnyAsync(item => item.PrimaryFileId == fileId, ct))
            { result = new(false, "This file is the primary file of a video, so it was kept."); return; }

            var physicalPaths = new List<string>();
            if (deleteFromDisk)
            {
                physicalPaths.Add(!string.IsNullOrWhiteSpace(file.Path)
                    ? file.Path
                    : BaseFileEntity.ComputePath(file.ParentFolder?.Path, file.Basename));
            }

            if (file is VideoFile)
                await VideoShotCarryOver.KeepOnReplacementAsync(db, [file.Id], replacementFileId: null, ct);

            db.Set<BaseFileEntity>().Remove(file);
            var deletionContext = new BulkDeletionExecutionContext();
            deletionContext.StagePhysicalFiles(db, physicalPaths);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);

            deleted = true;
            stagedPaths = physicalPaths.Count;
            ownerVideoId = (file as VideoFile)?.VideoId;
            result = new(true, null);
        });

        if (!deleted)
            return result;

        if (stagedPaths > 0)
            physicalFileDeletionRecoverySignal?.Notify();
        if (ownerVideoId is { } videoId)
            eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", videoId));

        return result;
    }

    private async Task<bool> HoldsTheSameContentAsync(VideoFile? source, VideoFile target, CancellationToken ct)
    {
        if (source is null || Math.Abs(source.Duration - target.Duration) > EquivalentDurationTolerance)
            return false;

        var sourceHash = await EnsurePhashAsync(source, ct);
        var targetHash = await EnsurePhashAsync(target, ct);
        return !string.IsNullOrWhiteSpace(sourceHash)
            && !string.IsNullOrWhiteSpace(targetHash)
            && MetadataServerService.ComputePhashHammingDistance(sourceHash, targetHash) <= EquivalentPhashDistance;
    }

    private async Task<string?> EnsurePhashAsync(VideoFile file, CancellationToken ct)
    {
        var existing = await db.FileFingerprints
            .AsNoTracking()
            .FirstOrDefaultAsync(fingerprint => fingerprint.FileId == file.Id && fingerprint.Type == "phash" && fingerprint.Value != "", ct);
        if (existing is not null)
            return existing.Value;

        var path = FilesystemPaths.ToNativePath(file.Path);
        if (!File.Exists(path))
            return null;

        var value = await fingerprintService.ComputeVideoPhashAsync(path, file.Duration, ct);
        if (string.IsNullOrWhiteSpace(value))
            return null;

        db.FileFingerprints.Add(new FileFingerprint { FileId = file.Id, Type = "phash", Value = value });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return value;
    }
}
