namespace Cove.Core.DTOs;

public sealed record EntityNameConflictSummaryDto(
    int PerformerUnresolvedGroupCount,
    int StudioUnresolvedGroupCount,
    DateTime ScannedAtUtc)
{
    public int UnresolvedGroupCount => PerformerUnresolvedGroupCount + StudioUnresolvedGroupCount;
}

public sealed record EntityNameConflictScanDto(
    string EntityType,
    int UnresolvedGroupCount,
    DateTime ScannedAtUtc,
    string Revision,
    IReadOnlyList<EntityNameConflictGroupDto> Groups);

public sealed record EntityNameConflictGroupDto(
    string EntityType,
    string Key,
    string Revision,
    string NormalizedName,
    string? NormalizedDisambiguation,
    int RecommendedSurvivorEntityId,
    IReadOnlyList<int> RecommendedMergeEntityIds,
    IReadOnlyList<EntityNameConflictCandidateDto> Candidates,
    IReadOnlyList<EntityNameImpactDto> Impacts);

public sealed record EntityNameConflictCandidateDto(
    int EntityId,
    string Name,
    string? Disambiguation,
    string NormalizedName,
    string? NormalizedDisambiguation,
    string RecommendedAction,
    bool IsRecommendedSurvivor);

public sealed record EntityNameImpactDto(
    int EntityId,
    string Name,
    string? Disambiguation,
    int LinkedEntityCount,
    int GroupCount,
    int HierarchyCount,
    int FaceCount,
    int RatingCount,
    int OtherMetadataCount,
    int ExtensionMetadataCount,
    IReadOnlyList<EntityExternalReferenceDto> ExternalReferences,
    long ReferenceCount);

public sealed record EntityExternalReferenceDto(
    int EntityId,
    string ReferenceKey,
    string SchemaName,
    string TableName,
    string ColumnName,
    string DeleteBehavior,
    int? RowCount,
    string? AccessLimitation = null);

public sealed record EntityNameConflictResolutionDto(
    int EntityId,
    string Action,
    string? NewName = null,
    string? NewDisambiguation = null);

public sealed record EntityExternalReferenceResolutionDto(
    int EntityId,
    string ReferenceKey,
    string Action);

public sealed record ResolveEntityNameConflictDto(
    string EntityType,
    string GroupKey,
    string ExpectedRevision,
    int? SurvivorEntityId = null,
    IReadOnlyList<EntityNameConflictResolutionDto>? Resolutions = null,
    IReadOnlyList<EntityExternalReferenceResolutionDto>? ExternalReferenceResolutions = null);

public sealed record ResolveAllEntityNameConflictsDto(
    string EntityType,
    string ExpectedRevision);
