using Cove.Core.Entities;

namespace Cove.Api.Services;

/// <summary>Which segments Generate's Segment Covers option makes a frame for.</summary>
internal static class SegmentCoverScope
{
    private const string FaceKind = "face";

    /// <summary>Video segments without a cover chosen by hand: the ones a generated cover can be made for.</summary>
    public static IQueryable<Segment> ThatCanHaveGeneratedCovers(this IQueryable<Segment> segments)
        => segments.Where(segment => segment.HostType == SegmentHostType.Video && segment.ImageBlobId == null);

    /// <summary>
    /// The segments of the kinds a run makes covers for (compared as KindOf does). Without a choice, every kind
    /// except face: face detection makes those in large numbers, mostly a few seconds long, so they keep the
    /// stand-in picture unless they are asked for.
    /// </summary>
    public static IQueryable<Segment> OfKinds(this IQueryable<Segment> segments, IReadOnlyCollection<string>? chosenKinds)
    {
        if (chosenKinds is null)
            return segments.Where(segment => segment.Kind == null || segment.Kind.Trim().ToLower() != FaceKind);
        var kinds = chosenKinds.Select(KindOf).Distinct().ToList();
        var withoutKind = kinds.Contains("");
        return segments.Where(segment => segment.Kind == null ? withoutKind : kinds.Contains(segment.Kind.Trim().ToLower()));
    }

    /// <summary>A segment kind as Generate compares it: trimmed and lowercased, with no kind as "".</summary>
    public static string KindOf(string? kind) => kind?.Trim().ToLowerInvariant() ?? "";
}
