namespace Cove.Core.DTOs;

public sealed record TagNameConflictSummaryDto(
    int UnresolvedGroupCount,
    DateTime ScannedAtUtc);

public sealed record TagNameConflictScanDto(
    int UnresolvedGroupCount,
    DateTime ScannedAtUtc,
    string Revision,
    IReadOnlyList<TagNameConflictGroupDto> Groups);

public sealed record TagNameConflictGroupDto(
    string Key,
    string Revision,
    string NormalizedName,
    IReadOnlyList<string> Kinds,
    bool RequiresMerge,
    bool HasCrossTagClaims,
    int RecommendedSurvivorTagId,
    IReadOnlyList<int> RecommendedMergeTagIds,
    IReadOnlyList<int> RecommendedRemoveAliasIds,
    IReadOnlyList<TagNameClaimDto> Claims,
    IReadOnlyList<TagNameImpactDto> Impacts);

public sealed record TagNameClaimDto(
    int TagId,
    string TagName,
    string ClaimType,
    int? AliasId,
    string OriginalValue,
    string? NormalizedValue,
    string RecommendedAction,
    bool IsRecommendedSurvivingClaim);

public sealed record TagNameImpactDto(
    int TagId,
    string TagName,
    int TaggedEntityCount,
    int SegmentCount,
    int ParentRelationshipCount,
    int ChildRelationshipCount,
    int RatingCount,
    int OtherMetadataCount,
    int ExtensionMetadataCount,
    IReadOnlyList<TagExternalReferenceDto> ExternalReferences,
    long ReferenceCount);

public sealed record TagExternalReferenceDto(
    int TagId,
    string ReferenceKey,
    string SchemaName,
    string TableName,
    string ColumnName,
    string DeleteBehavior,
    int? RowCount,
    string? AccessLimitation = null);

public sealed record TagNameClaimResolutionDto(
    int TagId,
    int? AliasId,
    string Action,
    string? NewValue = null);

public sealed record TagExternalReferenceResolutionDto(
    int TagId,
    string ReferenceKey,
    string Action);

public sealed record ResolveTagNameConflictDto(
    string GroupKey,
    string? ExpectedRevision = null,
    int? SurvivorTagId = null,
    IReadOnlyList<TagNameClaimResolutionDto>? Resolutions = null,
    IReadOnlyList<TagExternalReferenceResolutionDto>? ExternalReferenceResolutions = null);

public sealed record ResolveTagNameConflictBatchDto(
    string ExpectedRevision,
    IReadOnlyList<ResolveTagNameConflictDto> Groups);
