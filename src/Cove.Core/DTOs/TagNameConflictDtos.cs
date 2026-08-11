namespace Cove.Core.DTOs;

public sealed record TagExternalReferenceDto(
    int TagId,
    string ReferenceKey,
    string SchemaName,
    string TableName,
    string ColumnName,
    string DeleteBehavior,
    int? RowCount,
    string? AccessLimitation = null);

public sealed record TagExternalReferenceResolutionDto(
    int TagId,
    string ReferenceKey,
    string Action);
