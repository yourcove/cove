using System.Text.Json;

namespace Cove.Core.Entities;

/// <summary>
/// The shot boundaries of one video file: a contiguous, gapless partition of the file's timeline into
/// <see cref="ShotCount"/> shots, usually produced by an AI analysis and possibly edited by hand.
/// </summary>
/// <remarks>
/// <para>
/// The shots are stored in the set's own row as the cuts between them, so each boundary is stored once
/// and the partition can have no gaps or overlaps. Shot <c>i</c> (from 0) is the half-open span from
/// cut <c>i - 1</c>, or 0 for the first shot, to cut <c>i</c>, or <see cref="DurationSec"/> for the last.
/// When <see cref="CutFrames"/> is not null, the shot's frames run the same way from frame 0 to
/// <see cref="FrameCount"/>. A set of one shot has no cuts and no transitions.
/// </para>
/// <para>
/// A file has at most one set. Timestamps belong to the analysed file, so a set follows its file
/// through merges and file moves, stays with it when another file becomes the video's primary file,
/// and is deleted with it, unless Cove deletes the file in favour of another copy of the same footage
/// on the same timeline, which then receives the set. Write sets through <c>IVideoShotService</c>,
/// which keeps the partition intact; the entity is public so extensions can query it.
/// </para>
/// </remarks>
public class VideoShotSet : BaseEntity
{
    public int FileId { get; set; }

    /// <summary>Who produced the set: an extension's <c>ext:</c> key, or <c>user</c> for a set drawn by hand.</summary>
    public string SourceKey { get; set; } = string.Empty;

    /// <summary>The <see cref="AiRun.RunKey"/> of the analysis that produced the set, when there was one.</summary>
    public string? SourceRunId { get; set; }

    public string? Model { get; set; }
    public string? ModelVersion { get; set; }
    public string? Mode { get; set; }
    public string? DecodeBackend { get; set; }
    public double? Fps { get; set; }

    /// <summary>The number of decoded frames; where the frames of the last shot end when the set has frames.</summary>
    public int? FrameCount { get; set; }

    /// <summary>Where the partition ends: the analysed length of the file, in seconds.</summary>
    public double DurationSec { get; set; }

    public int ShotCount { get; set; }

    /// <summary>The <see cref="ShotCount"/> − 1 cuts between shots, in seconds, strictly increasing between 0 and <see cref="DurationSec"/>.</summary>
    public double[] CutTimes { get; set; } = [];

    /// <summary>
    /// The first decoded frame after each cut, strictly increasing between 0 and <see cref="FrameCount"/>;
    /// null when the set has no frames, which then holds for every shot.
    /// </summary>
    public int[]? CutFrames { get; set; }

    /// <summary>The producer's label for each of the <see cref="ShotCount"/> shots, for example <c>General</c>; null where there is none.</summary>
    public string?[] ShotTypes { get; set; } = [];

    /// <summary>
    /// The producer's label for how the shot after each cut is entered from the one before it, for
    /// example <c>Hard_Cut</c>; null where there is none. The first shot has no transition.
    /// </summary>
    public string?[] Transitions { get; set; } = [];

    /// <summary>When the set was last changed by hand; null for an unedited analysis.</summary>
    public DateTime? EditedAt { get; set; }

    /// <summary>Incremented by every change. Edits pass the revision they started from.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>The file's size when the set was written. A rescan can change a file row in place, and a different size marks the set stale.</summary>
    public long FileSize { get; set; }

    public JsonDocument? Payload { get; set; }

    public VideoFile? File { get; set; }
}
