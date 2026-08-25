namespace Cove.Core.Entities;

/// <summary>
/// Proves that one video deletion transaction committed when the database acknowledgement is
/// ambiguous. The worker removes the marker after completing its in-process post-commit work.
/// </summary>
public sealed class VideoDeletionCommitMarker
{
    public Guid BatchId { get; set; }
    public int VideoId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
