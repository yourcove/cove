using System.Text.Json;

namespace Cove.Core.Entities;

/// <summary>
/// The shot boundaries of one video file: a contiguous, gapless partition of the file's timeline
/// into <see cref="VideoShot"/> rows, usually produced by an AI analysis and possibly edited by hand.
/// </summary>
/// <remarks>
/// A file has at most one set. Timestamps belong to the analysed file, so a set follows its file
/// through merges and file moves, stays with it when another file becomes the video's primary file,
/// and is deleted with it, unless Cove deletes the file in favour of another copy of the same footage
/// on the same timeline, which then receives the set. Write sets through <c>IVideoShotService</c>,
/// which keeps the partition intact; the entities are public so extensions can query them.
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
    public int? FrameCount { get; set; }

    /// <summary>Where the partition ends: the analysed length of the file, in seconds.</summary>
    public double DurationSec { get; set; }

    public int ShotCount { get; set; }

    /// <summary>When the set was last changed by hand; null for an unedited analysis.</summary>
    public DateTime? EditedAt { get; set; }

    /// <summary>Incremented by every change. Edits pass the revision they started from.</summary>
    public int Revision { get; set; } = 1;

    /// <summary>The file's size when the set was written. A rescan can change a file row in place, and a different size marks the set stale.</summary>
    public long FileSize { get; set; }

    public JsonDocument? Payload { get; set; }

    public VideoFile? File { get; set; }
    public ICollection<VideoShot> Shots { get; set; } = [];
}

/// <summary>One shot of a <see cref="VideoShotSet"/>: the half-open span [<see cref="StartSec"/>, <see cref="EndSec"/>).</summary>
public class VideoShot
{
    public long Id { get; set; }
    public int SetId { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }

    /// <summary>First decoded frame of the shot, when the producer reported frames.</summary>
    public int? StartFrame { get; set; }

    /// <summary>One past the shot's last decoded frame, when the producer reported frames.</summary>
    public int? EndFrame { get; set; }

    /// <summary>The producer's label for the shot itself, for example <c>General</c> or <c>Dissolve</c>.</summary>
    public string? ShotType { get; set; }

    /// <summary>The producer's label for how the shot is entered from the previous one; null for the first shot.</summary>
    public string? TransitionIn { get; set; }

    public VideoShotSet? Set { get; set; }
}
