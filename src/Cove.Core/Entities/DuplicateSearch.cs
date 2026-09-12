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

/// <summary>
/// Lifecycle of one duplicate group's review. Keeper choices can change only while a group is
/// <see cref="Unresolved"/> or <see cref="Failed"/>; the resolution worker owns it from
/// <see cref="Queued"/> until it settles again.
/// </summary>
public enum DuplicateGroupStatus
{
    Unresolved,
    Queued,
    Processing,
    Resolved,
    Ignored,
    Failed,
}

public sealed class DuplicateSearch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? OwnerKey { get; set; }
    public string? JobId { get; set; }
    public string MatchType { get; set; } = "fingerprint";
    public int Distance { get; set; }
    public double DurationDifference { get; set; } = 10;
    /// <summary>Library folders a candidate's file must be at or below. Empty means the whole library.</summary>
    public string[] IncludePaths { get; set; } = [];
    /// <summary>Library folders whose files are never considered.</summary>
    public string[] ExcludePaths { get; set; } = [];
    public double MinimumDuration { get; set; }
    /// <summary>Ordered keeper rules (JSON) used for the initial selection and shown with the results.</summary>
    public string? KeeperRulesJson { get; set; }
    public DuplicateSearchStatus Status { get; set; } = DuplicateSearchStatus.Pending;
    public string? Error { get; set; }
    public int CandidateCount { get; set; }
    public int GroupCount { get; set; }
    public int VideoCount { get; set; }
    /// <summary>
    /// Durable claim held by the search's resolution worker while queued groups are processed.
    /// </summary>
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
    public DuplicateGroupStatus Status { get; set; } = DuplicateGroupStatus.Unresolved;
    /// <summary>"auto" when the keeper rules chose the keepers, "manual" after a person changed them.</summary>
    public string? DecisionSource { get; set; }
    /// <summary>The keeper rule that separated the keeper from the other members, when chosen automatically.</summary>
    public string? DecisionRule { get; set; }
    /// <summary>"remove" or "merge" once the group has been queued for resolution.</summary>
    public string? ResolutionAction { get; set; }
    public bool DeleteFiles { get; set; }
    public bool DeleteGenerated { get; set; }
    public string? Error { get; set; }
    public DateTime? QueuedAt { get; set; }
    public DateTime? ResolvedAt { get; set; }
    public int RemovedVideoCount { get; set; }
    public long RemovedBytes { get; set; }
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

/// <summary>
/// Two videos a person confirmed are not duplicates. Stored once per unordered pair
/// (<see cref="LowVideoId"/> &lt; <see cref="HighVideoId"/>) and honored by every later search.
/// </summary>
public sealed class DuplicateIgnoredPair
{
    public int LowVideoId { get; set; }
    public int HighVideoId { get; set; }
    /// <summary>Number of independent group decisions that currently preserve this pair.</summary>
    public int DecisionCount { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Video? LowVideo { get; set; }
    public Video? HighVideo { get; set; }
}
