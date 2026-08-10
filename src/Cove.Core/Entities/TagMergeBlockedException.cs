namespace Cove.Core.Entities;

public sealed class TagMergeBlockedException(int referenceCount, int affectedTagCount) : InvalidOperationException(
    $"The merge was not applied because {referenceCount} extension-owned tag reference(s) on {affectedTagCount} source tag(s) cannot be transferred safely.")
{
    public int ReferenceCount { get; } = referenceCount;
    public int AffectedTagCount { get; } = affectedTagCount;
}
