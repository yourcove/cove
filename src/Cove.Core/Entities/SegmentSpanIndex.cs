namespace Cove.Core.Entities;

/// <summary>
/// One resolved span of one video under one display profile, stored so the Segments list can filter,
/// sort, count and page spans in the database instead of resolving every video on each request. The
/// rows are derived data: the span index service rewrites a video's rows whenever its segments, the
/// profile's rules or a tag those segments use change.
/// </summary>
public class StoredSegmentSpan
{
    public long Id { get; set; }
    public int ProfileId { get; set; }
    public int VideoId { get; set; }

    /// <summary>Position among the video's spans in resolver order (start, end, span key).</summary>
    public int Ordinal { get; set; }

    public string SpanKey { get; set; } = string.Empty;
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public double DurationSec { get; set; }

    public string? SourceKey { get; set; }
    public string? Kind { get; set; }
    public int? TagId { get; set; }
    public string? Label { get; set; }
    public string? ColorHint { get; set; }
    public int? Lane { get; set; }
    public bool CollapsedToInstant { get; set; }

    /// <summary>The contributing segments, ascending by id.</summary>
    public int[] SegmentIds { get; set; } = [];
    public int SegmentCount { get; set; }

    // Facts about the contributing segments that the list filters and sorts on. A filter that needs
    // more than these (an exact confidence, a date inside a range) narrows by them first and then
    // reads the few remaining spans' segments.
    public long[] RefIds { get; set; } = [];

    /// <summary>
    /// <see cref="RefIds"/> as the integer ids faces and performers have, so a face or performer can be
    /// looked up by primary key from a span (a reference outside the integer range names neither).
    /// </summary>
    public int[] EntityRefIds { get; set; } = [];
    public string[] Titles { get; set; } = [];
    public string[] SourceRunIds { get; set; } = [];
    public string[] ColorHints { get; set; } = [];
    public bool HasImage { get; set; }
    public bool HasPayload { get; set; }
    public float? MinConfidence { get; set; }
    public float? MaxConfidence { get; set; }
    public DateTime MinSegmentCreatedAt { get; set; }
    public DateTime MaxSegmentCreatedAt { get; set; }
    public DateTime MinSegmentUpdatedAt { get; set; }
    public DateTime MaxSegmentUpdatedAt { get; set; }

    /// <summary>A hash of the span's identity; mixed with the request's seed for the random sort.</summary>
    public int RandomKey { get; set; }
}

/// <summary>
/// A counter per video that a database trigger increments whenever the video's segments change, and
/// whenever a tag those segments use changes in a way that affects spans. Writers that bypass the
/// application (extensions, bulk SQL) are covered because the trigger lives in the database.
/// </summary>
public class SegmentSpanVideoVersion
{
    public int VideoId { get; set; }
    public long Version { get; set; }

    /// <summary>When the counter last moved; the background materializer waits for a video to settle.</summary>
    public DateTime ChangedAt { get; set; }
}

/// <summary>
/// What a video's stored spans were built from. The spans are current when the profile version and
/// the video version both match the live values.
/// </summary>
public class SegmentSpanVideoState
{
    public int ProfileId { get; set; }
    public int VideoId { get; set; }
    public int ProfileVersion { get; set; }
    public long ResolvedVersion { get; set; }
}

/// <summary>A display profile whose spans are kept stored, registered the first time it is listed.</summary>
public class SegmentSpanIndexedProfile
{
    public int ProfileId { get; set; }
}
