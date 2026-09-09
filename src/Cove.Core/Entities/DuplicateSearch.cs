namespace Cove.Core.Entities;

public enum DuplicateSearchStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

public sealed class DuplicateSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? OwnerKey { get; set; }
    public string? JobId { get; set; }
    public string MatchType { get; set; } = "fingerprint";
    public int Distance { get; set; }
    public double DurationDifference { get; set; } = 10;
    public DuplicateSearchStatus Status { get; set; } = DuplicateSearchStatus.Pending;
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
    public int GroupCount { get; set; }
    public int VideoCount { get; set; }
    public string? DeletionJobId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(7);
    public ICollection<DuplicateSearchGroup> Groups { get; set; } = [];
    public ICollection<DuplicateDeletionKeeperReservation> KeeperReservations { get; set; } = [];
}

/// <summary>
/// Protects the surviving copy/copies while an asynchronous delete-unkept job is pending or running.
/// The restrictive video FK makes every other video deletion path honor the reservation atomically.
/// </summary>
public sealed class DuplicateDeletionKeeperReservation
{
    public Guid SearchId { get; set; }
    public int VideoId { get; set; }
    public DuplicateSearch? Search { get; set; }
    public Video? Video { get; set; }
}

public sealed class DuplicateSearchGroup
{
    public int Id { get; set; }
    public Guid SearchId { get; set; }
    public int Position { get; set; }
    public Guid? LastDecisionOperationId { get; set; }
    public DuplicateSearch? Search { get; set; }
    public ICollection<DuplicateSearchItem> Items { get; set; } = [];
}

public sealed class DuplicateSearchItem
{
    public int GroupId { get; set; }
    public int VideoId { get; set; }
    public bool Keep { get; set; }
    public DuplicateSearchGroup? Group { get; set; }
    public Video? Video { get; set; }
}
