namespace Cove.Core.Entities;

public sealed class EntityMergeBlockedException(
    string entityType,
    int referenceCount,
    int affectedEntityCount,
    bool hasUninspectableReferences = false)
    : InvalidOperationException(hasUninspectableReferences
        ? $"One or more non-core tables that may reference a source {entityType} cannot be inspected safely. Review the extension-owned locations before merging."
        : $"The merge is blocked because non-core tables still reference {affectedEntityCount} source {entityType}{(affectedEntityCount == 1 ? string.Empty : "s")}. Review and repair those locations first.")
{
    public string EntityType { get; } = entityType;
    public int ReferenceCount { get; } = referenceCount;
    public int AffectedEntityCount { get; } = affectedEntityCount;
    public bool HasUninspectableReferences { get; } = hasUninspectableReferences;
}
