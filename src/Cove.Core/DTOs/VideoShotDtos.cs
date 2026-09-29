using System.Text.Json;

namespace Cove.Core.DTOs;

/// <summary>How <see cref="Interfaces.IVideoShotService.WriteSetAsync"/> treats a file that already has shots.</summary>
public enum VideoShotWriteMode
{
    /// <summary>Leave any existing set alone, edited or not, and report it.</summary>
    SkipIfExists = 0,

    /// <summary>Replace the existing set as a whole, hand edits included.</summary>
    Replace = 1,
}

public enum VideoShotWriteOutcome
{
    Written = 1,
    Replaced = 2,
    SkippedExisting = 3,
    Conflict = 4,
    Invalid = 5,
    FileNotFound = 6,
}

public enum VideoShotEditStatus
{
    Updated = 1,
    Deleted = 2,
    NotFound = 3,
    Invalid = 4,
    Conflict = 5,
}

/// <summary>
/// Identifies one state of a file's set. <see cref="Revision"/> alone is not enough: a set that is
/// deleted and written again starts at revision 1.
/// </summary>
public sealed record VideoShotSetVersion
{
    public required int SetId { get; init; }
    public required int Revision { get; init; }
}

public sealed record VideoShotDto
{
    public long Id { get; init; }
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public int? StartFrame { get; init; }
    public int? EndFrame { get; init; }
    public string? ShotType { get; init; }
    public string? TransitionIn { get; init; }
}

public sealed record VideoShotSetDto
{
    public int Id { get; init; }
    public int FileId { get; init; }
    public int? VideoId { get; init; }
    public bool IsPrimaryFile { get; init; }
    public string SourceKey { get; init; } = string.Empty;
    public string? SourceRunId { get; init; }
    public string? Model { get; init; }
    public string? ModelVersion { get; init; }
    public string? Mode { get; init; }
    public string? DecodeBackend { get; init; }
    public double? Fps { get; init; }
    public int? FrameCount { get; init; }
    public double DurationSec { get; init; }
    public int ShotCount { get; init; }
    public int Revision { get; init; }
    public DateTime? EditedAt { get; init; }

    /// <summary>True when the file's size changed since the set was written, so it may describe older content.</summary>
    public bool IsStale { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>The producer's own data, as written; null on summaries.</summary>
    public JsonElement? Payload { get; init; }

    /// <summary>The shots in order; null on summaries.</summary>
    public IReadOnlyList<VideoShotDto>? Shots { get; init; }
}

/// <summary>One shot to write. Times are seconds; frames, when given, are the half-open decoded-frame range.</summary>
public sealed record VideoShotInput
{
    public double StartSec { get; init; }
    public double EndSec { get; init; }
    public int? StartFrame { get; init; }
    public int? EndFrame { get; init; }
    public string? ShotType { get; init; }
    public string? TransitionIn { get; init; }
}

/// <summary>A whole set to write for one file, usually an analysis result.</summary>
public sealed record VideoShotSetWrite
{
    public required int FileId { get; init; }
    public VideoShotWriteMode WriteMode { get; init; } = VideoShotWriteMode.SkipIfExists;

    /// <summary>
    /// With <see cref="VideoShotWriteMode.Replace"/>, replace only this exact set; any other state
    /// (including no set) is a <see cref="VideoShotWriteOutcome.Conflict"/>. Not allowed with
    /// <see cref="VideoShotWriteMode.SkipIfExists"/>.
    /// </summary>
    public VideoShotSetVersion? Expected { get; init; }

    public required string SourceKey { get; init; }
    public string? SourceRunId { get; init; }
    public string? Model { get; init; }
    public string? ModelVersion { get; init; }
    public string? Mode { get; init; }
    public string? DecodeBackend { get; init; }
    public double? Fps { get; init; }
    public int? FrameCount { get; init; }
    public required double DurationSec { get; init; }
    public required IReadOnlyList<VideoShotInput> Shots { get; init; }

    /// <summary>Set when importing a set that was already edited by hand elsewhere; null for an analysis.</summary>
    public DateTime? EditedAt { get; init; }

    public JsonElement? Payload { get; init; }
}

public sealed record VideoShotWriteResult
{
    public VideoShotWriteOutcome Outcome { get; init; }
    public string? Reason { get; init; }

    /// <summary>A summary of the set after the write, or of the existing set when it was skipped or conflicted.</summary>
    public VideoShotSetDto? Set { get; init; }

    /// <summary>True when <see cref="VideoShotWriteMode.Replace"/> overwrote a set that had been edited by hand.</summary>
    public bool ReplacedEditedSet { get; init; }
}

/// <summary>
/// Splits the shot containing <see cref="AtSec"/>. Without <see cref="Expected"/> the file must have no
/// set yet: one shot covering the whole file is created first, as a set drawn by hand.
/// </summary>
public sealed record VideoShotSplitRequest
{
    public required int FileId { get; init; }
    public required double AtSec { get; init; }
    public VideoShotSetVersion? Expected { get; init; }

    /// <summary>Source key for a set created by this request; defaults to <c>user</c>.</summary>
    public string? SourceKey { get; init; }
}

/// <summary>Removes the cut at <see cref="CutSec"/>, joining the shots on either side of it.</summary>
public sealed record VideoShotMergeRequest
{
    public required int FileId { get; init; }
    public required double CutSec { get; init; }
    public required VideoShotSetVersion Expected { get; init; }
}

/// <summary>Moves the cut at <see cref="FromSec"/> to <see cref="ToSec"/>, between its neighbouring cuts.</summary>
public sealed record VideoShotMoveCutRequest
{
    public required int FileId { get; init; }
    public required double FromSec { get; init; }
    public required double ToSec { get; init; }
    public required VideoShotSetVersion Expected { get; init; }
}

/// <summary>
/// Replaces every shot of a set, for example to undo or redo an edit. Shots whose start is unchanged
/// keep their ids. Without <see cref="Expected"/> the file must have no set yet, and one is created as
/// a set drawn by hand, ending where the last shot ends.
/// </summary>
public sealed record VideoShotReplaceRequest
{
    public required int FileId { get; init; }
    public required IReadOnlyList<VideoShotInput> Shots { get; init; }
    public VideoShotSetVersion? Expected { get; init; }

    /// <summary>Source key for a set created by this request; defaults to <c>user</c>.</summary>
    public string? SourceKey { get; init; }
}

/// <summary>Deletes a file's set. Without <see cref="Expected"/> whatever set the file has is deleted.</summary>
public sealed record VideoShotDeleteRequest
{
    public required int FileId { get; init; }
    public VideoShotSetVersion? Expected { get; init; }
}

public sealed record VideoShotEditResult
{
    public VideoShotEditStatus Status { get; init; }
    public string? Error { get; init; }

    /// <summary>The whole set after the edit. On a conflict, the current set, so an editor can reload without another call.</summary>
    public VideoShotSetDto? Set { get; init; }
}
