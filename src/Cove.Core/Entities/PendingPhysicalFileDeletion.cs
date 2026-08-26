namespace Cove.Core.Entities;

/// <summary>
/// Durable outbox entry written in the same commit that detaches a file row. The physical deletion
/// worker removes the entry after the path is deleted, absent, or protected by another file row.
/// </summary>
public sealed class PendingPhysicalFileDeletion
{
    public long Id { get; set; }
    public Guid BatchId { get; set; }
    public string Path { get; set; } = string.Empty;
    /// <summary>Whether the filesystem snapshot was captured successfully when metadata detached.</summary>
    public bool IdentityCaptured { get; set; }
    /// <summary>Whether a file existed at <see cref="Path"/> when metadata detached.</summary>
    public bool ExpectedExists { get; set; }
    public long? ExpectedLength { get; set; }
    public long? ExpectedLastWriteTimeUtcTicks { get; set; }
    public long? ExpectedCreationTimeUtcTicks { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
