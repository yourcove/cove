using Cove.Core.Entities;

namespace Cove.Data.Services;

public static class SegmentAccessQueries
{
    /// <summary>
    /// Segments whose host row still exists. Pass <paramref name="hostType"/> whenever the caller
    /// already knows which host kind it wants: the three-branch OR below forces the planner to
    /// evaluate every branch against the whole segments table, which measurably slows aggregates on
    /// large libraries, whereas a single branch lets it use the segment indexes plus one semi-join.
    /// Results are identical either way — a segment matches at most one branch, since each branch
    /// tests a different <see cref="Segment.HostType"/> value.
    /// </summary>
    public static IQueryable<Segment> VisibleSegments(this CoveContext db, SegmentHostType? hostType = null)
        => hostType switch
        {
            SegmentHostType.Video => db.Segments.Where(segment =>
                segment.HostType == SegmentHostType.Video && db.Videos.Any(video => video.Id == segment.HostId)),
            SegmentHostType.Audio => db.Segments.Where(segment =>
                segment.HostType == SegmentHostType.Audio && db.Audios.Any(audio => audio.Id == segment.HostId)),
            SegmentHostType.Image => db.Segments.Where(segment =>
                segment.HostType == SegmentHostType.Image && db.Images.Any(image => image.Id == segment.HostId)),
            _ => db.Segments.Where(segment =>
                segment.HostType == SegmentHostType.Video && db.Videos.Any(video => video.Id == segment.HostId)
                || segment.HostType == SegmentHostType.Audio && db.Audios.Any(audio => audio.Id == segment.HostId)
                || segment.HostType == SegmentHostType.Image && db.Images.Any(image => image.Id == segment.HostId)),
        };

    public static IQueryable<Detection> VisibleDetections(this CoveContext db)
        => db.Detections.Where(detection =>
            detection.HostType == DetectionHostType.Video && db.Videos.Any(video => video.Id == detection.HostId)
            || detection.HostType == DetectionHostType.Image && db.Images.Any(image => image.Id == detection.HostId));
}
