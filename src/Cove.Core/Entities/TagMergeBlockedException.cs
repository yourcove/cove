namespace Cove.Core.Entities;

public sealed class TagMergeBlockedException(
    int referenceCount,
    int affectedTagCount,
    bool hasUninspectableReferences = false) : InvalidOperationException(
        hasUninspectableReferences
            ? $"The merge was not applied because non-core tag references on {affectedTagCount} source tag(s) could not all be inspected safely. Use the owning extension or a database administrator to resolve the restricted table references."
            : $"The merge was not applied because {referenceCount} extension-owned tag reference(s) on {affectedTagCount} source tag(s) cannot be transferred safely.")
{
    public int ReferenceCount { get; } = referenceCount;
    public int AffectedTagCount { get; } = affectedTagCount;
    public bool HasUninspectableReferences { get; } = hasUninspectableReferences;
}
