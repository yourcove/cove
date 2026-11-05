using System.Globalization;

namespace Cove.Api.Http;

/// <summary>
/// Cache revisions of media route families, sent as the <c>r</c> query parameter. Bump a family when
/// browsers must stop reusing what they cached from it, such as when its bytes or response headers change
/// in a way old copies must not survive: the new value gives every URL in the family a new cache key.
/// Revisions only ever go up: an earlier value, 0 included, names URLs browsers may still have cached.
/// A revision of 0 adds no parameter; it is only for a family that has never been bumped.
/// <para>
/// The UI builds most media URLs itself from ui/src/api/mediaCacheRevisions.json, and these values must
/// match it; MediaCacheRevisionsTests.ServerRevisions_MatchTheUisContract fails when they drift apart.
/// </para>
/// </summary>
internal static class MediaCacheRevisions
{
    internal const string QueryKey = "r";

    /// <summary><c>/api/stream/image/{id}</c> and its thumbnail. 1: sandboxing headers.</summary>
    internal const int LibraryImages = 1;

    /// <summary>Entity image routes: videos, performers, studios, tags, groups, galleries, faces, audios, texts. 1: sandboxing headers.</summary>
    internal const int EntityImages = 1;

    /// <summary>
    /// <c>/api/segments/{id}/image</c>. 3: the frame chosen for segments without a cover changed (sent as
    /// <c>frame</c>). 4: sandboxing headers.
    /// </summary>
    internal const int SegmentCovers = 4;

    /// <summary><c>/api/stream/video/{id}/screenshot</c>. 1: sandboxing headers for custom covers.</summary>
    internal const int VideoScreenshots = 1;

    /// <summary>
    /// <c>/api/stream/detection/{id}/crop</c>. 2: cut from each detection's exact frame (sent as <c>frame</c>,
    /// so moving it to <c>r</c> already refreshes cached crops once).
    /// </summary>
    internal const int DetectionCrops = 2;

    /// <summary>Every family by its key in the UI's contract file.</summary>
    internal static IReadOnlyDictionary<string, int> All { get; } = new Dictionary<string, int>
    {
        ["libraryImages"] = LibraryImages,
        ["entityImages"] = EntityImages,
        ["segmentCovers"] = SegmentCovers,
        ["videoScreenshots"] = VideoScreenshots,
        ["detectionCrops"] = DetectionCrops,
    };

    internal static void Append(List<KeyValuePair<string, string?>> query, int revision)
    {
        if (revision > 0)
            query.Add(new(QueryKey, revision.ToString(CultureInfo.InvariantCulture)));
    }
}
