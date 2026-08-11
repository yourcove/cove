namespace Cove.Core.DTOs;

public sealed record EntityExternalReferenceDto(
    int EntityId,
    string ReferenceKey,
    string SchemaName,
    string TableName,
    string ColumnName,
    string DeleteBehavior,
    int? RowCount,
    string? AccessLimitation = null);

public sealed record EntityExternalReferenceResolutionDto(
    int EntityId,
    string ReferenceKey,
    string Action);
