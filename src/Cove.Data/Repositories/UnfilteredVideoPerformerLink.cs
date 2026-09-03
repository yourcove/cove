namespace Cove.Data;

/// <summary>
/// Read-only projection of the video-performer edge. Callers must constrain both endpoints with
/// authorization-aware entity queries before using it.
/// </summary>
internal sealed class UnfilteredVideoPerformerLink
{
    public int VideoId { get; set; }
    public int PerformerId { get; set; }
}
