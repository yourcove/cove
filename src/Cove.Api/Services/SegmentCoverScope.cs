using Cove.Core.Entities;

namespace Cove.Api.Services;

/// <summary>Which segments Generate's Segment Covers option makes a frame for.</summary>
internal static class SegmentCoverScope
{
    private const string FaceKind = "face";

    /// <summary>
    /// Video segments without a cover chosen by hand, except face segments. Face detection makes those in
    /// large numbers, mostly a few seconds long, and they are not worth a decode each, so they keep the
    /// stand-in picture.
    /// </summary>
    public static IQueryable<Segment> ThatGetGeneratedCovers(this IQueryable<Segment> segments)
        => segments.Where(segment => segment.HostType == SegmentHostType.Video
            && segment.ImageBlobId == null
            && (segment.Kind == null || segment.Kind.ToLower() != FaceKind));
}
