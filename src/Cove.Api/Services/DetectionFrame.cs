using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// The frame a video detection was observed in. A detection's box only lines up with its own frame: a
/// sprite tile, or a frame from another moment in the same second, puts the box over something else.
/// So the frame is made from the video at the detection's exact time when it does not exist yet.
/// </summary>
/// <remarks>A frame that cannot be made, for whatever reason, is reported as null so callers can fall back.</remarks>
public static class DetectionFrame
{
    /// <summary>The detection's own frame, or null when it is not a video detection or the frame cannot be made.</summary>
    public static async Task<Stream?> OpenAsync(CoveContext db, IThumbnailService thumbnailService, Detection detection, CancellationToken ct)
    {
        if (detection.HostType != DetectionHostType.Video || detection.ObservedAtSec is not { } seconds)
            return null;

        // Detections on a clip use the source video's timeline, so the frame is the source's.
        var sourceVideoId = await db.Videos.AsNoTracking()
            .Where(video => video.Id == detection.HostId)
            .Select(video => (int?)(video.ParentVideoId ?? video.Id))
            .FirstOrDefaultAsync(ct);
        if (sourceVideoId is null)
            return null;

        var framePath = thumbnailService.GetTimestampedThumbnailPath(sourceVideoId.Value, seconds);
        if (!File.Exists(framePath))
        {
            try
            {
                await thumbnailService.GenerateVideoThumbnailAsync(sourceVideoId.Value, seconds, ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }
        }

        return FileReadRace.TryOpenRead(framePath, bufferSize: 8192);
    }
}
