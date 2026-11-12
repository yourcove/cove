namespace Cove.Api.Services;

public sealed record DuplicateSearchStartRequest(
    string MatchType = "fingerprint",
    int Distance = 4,
    double? DurationDiff = 5,
    IReadOnlyList<string>? IncludePaths = null,
    IReadOnlyList<string>? ExcludePaths = null,
    double MinimumDuration = 0,
    IReadOnlyList<DuplicateKeeperRule>? KeeperRules = null);

public sealed record DuplicateSearchStarted(Guid SearchId, string JobId, int CandidateCount);

public sealed record DuplicateSearchSummary(
    Guid Id,
    string? JobId,
    string MatchType,
    int Distance,
    double DurationDiff,
    IReadOnlyList<string> IncludePaths,
    IReadOnlyList<string> ExcludePaths,
    double MinimumDuration,
    IReadOnlyList<DuplicateKeeperRule> KeeperRules,
    string Status,
    string? Error,
    int CandidateCount,
    int GroupCount,
    int VideoCount,
    DuplicateGroupCounts Counts,
    long ReclaimableBytes,
    int RemovableVideoCount,
    long RemovedBytes,
    int RemovedVideoCount,
    string? ResolutionJobId,
    DateTime CreatedAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    DateTime ExpiresAt)
{
    /// <summary>The part of <see cref="RemovedVideoCount"/> that was merged into a kept video with its files attached.</summary>
    public int AttachedVideoCount { get; init; }
}

public sealed record DuplicateGroupCounts(int Unresolved, int Queued, int Resolved, int Ignored, int Failed);

public sealed record DuplicateSearchListItem(
    Guid Id,
    string MatchType,
    int Distance,
    string Status,
    int GroupCount,
    int VideoCount,
    int UnresolvedCount,
    bool Scoped,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public sealed record DuplicateGroupPage(
    IReadOnlyList<DuplicateGroupView> Items,
    int TotalCount,
    int Page,
    int PerPage);

public sealed record DuplicateGroupView(
    int Id,
    int Position,
    string Status,
    IReadOnlyList<Cove.Core.DTOs.VideoDto> Videos,
    IReadOnlyList<int> KeepVideoIds,
    string? DecisionSource,
    string? DecisionRule,
    string? ResolutionAction,
    bool DeleteFiles,
    string? Error,
    DateTime? ResolvedAt,
    int RemovedVideoCount,
    long RemovedBytes,
    long ReclaimableBytes,
    // A group from a "files" search shows its single video in Videos and lists the files under review here.
    IReadOnlyList<int> FileIds,
    IReadOnlyList<int> KeepFileIds)
{
    /// <summary>Non-keepers that another group of the same search keeps; resolving this group never removes them.</summary>
    public IReadOnlyList<int> KeptElsewhereVideoIds { get; init; } = [];

    /// <summary>"attach" or "remove" once the group has been queued with the merge action; otherwise null.</summary>
    public string? FileHandling { get; init; }
}

/// <summary>Keepers for a video group, or <paramref name="KeepFileIds"/> for a group from a "files" search.</summary>
public sealed record DuplicateKeeperDecisionRequest(IReadOnlyList<int> KeepVideoIds, IReadOnlyList<int>? KeepFileIds = null);

public sealed record DuplicateAutoSelectRequest(
    IReadOnlyList<DuplicateKeeperRule>? Rules,
    IReadOnlyList<int>? GroupIds = null,
    bool OverwriteManual = false);

public sealed record DuplicateAutoSelectResult(int UpdatedGroupCount, int ChangedGroupCount);

public sealed record DuplicateResolveRequest(
    IReadOnlyList<int>? GroupIds,
    string Action = "remove",
    bool DeleteFiles = false,
    bool DeleteGenerated = true)
{
    /// <summary>
    /// Field-level choices from the merge review. Honoured only when exactly one group is resolved with the
    /// merge action; the "source" side reads across its copies. Absent choices follow the default policy. Not a
    /// positional parameter, so already-compiled callers keep binding to the original constructor.
    /// </summary>
    public Cove.Core.DTOs.VideoMergeMetadataDto? Metadata { get; init; }

    /// <summary>
    /// What happens to the merged copies' files: "attach" moves them onto the kept video, so the group becomes
    /// one video with several files and nothing is removed; "remove" removes the copies with their files.
    /// The names match <see cref="Cove.Core.DTOs.VideoMergeFileHandlingDto"/>, but an absent value means
    /// "remove" here, which is what this endpoint did before the choice existed. "attach" requires the merge
    /// action and cannot be combined with <see cref="DeleteFiles"/>.
    /// </summary>
    public string? FileHandling { get; init; }
}

public sealed record DuplicateResolveResult(int QueuedGroupCount, string? JobId);
