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
    DateTime ExpiresAt);

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
    long ReclaimableBytes);

public sealed record DuplicateKeeperDecisionRequest(IReadOnlyList<int> KeepVideoIds);

public sealed record DuplicateAutoSelectRequest(
    IReadOnlyList<DuplicateKeeperRule>? Rules,
    IReadOnlyList<int>? GroupIds = null,
    bool OverwriteManual = false);

public sealed record DuplicateAutoSelectResult(int UpdatedGroupCount, int ChangedGroupCount);

public sealed record DuplicateResolveRequest(
    IReadOnlyList<int>? GroupIds,
    string Action = "remove",
    bool DeleteFiles = false,
    bool DeleteGenerated = true);

public sealed record DuplicateResolveResult(int QueuedGroupCount, string? JobId);
