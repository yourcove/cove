namespace Cove.Core.Entities;

public sealed class TagDeletionBlockedException(
    IReadOnlyList<TagDeletionExtensionOwner> extensions,
    bool hasUninspectableReferences = false,
    bool hasUnknownExtensionOwners = false,
    Exception? innerException = null)
    : InvalidOperationException(
        CreateMessage(extensions, hasUninspectableReferences, hasUnknownExtensionOwners),
        innerException)
{
    public IReadOnlyList<TagDeletionExtensionOwner> Extensions { get; } = extensions;
    public bool HasUninspectableReferences { get; } = hasUninspectableReferences;
    public bool HasUnknownExtensionOwners { get; } = hasUnknownExtensionOwners;

    private static string CreateMessage(
        IReadOnlyList<TagDeletionExtensionOwner> extensions,
        bool hasUninspectableReferences,
        bool hasUnknownExtensionOwners)
    {
        var owners = extensions
            .Select(extension => $"• {extension.Name}")
            .Concat(hasUnknownExtensionOwners ? ["• An unidentified extension"] : [])
            .ToArray();
        var ownerList = owners.Length == 0 ? string.Empty : $"\n{string.Join('\n', owners)}";
        if (hasUninspectableReferences)
        {
            return owners.Length == 0
                ? "Cove can’t verify whether extension-owned data still references this tag. Restore access to the owning extension’s data or ask an administrator for help, then try again."
                : $"Cove can’t verify the tag references owned by:{ownerList}\nRestore access to the listed extension data or ask an administrator for help, then try again.";
        }

        return owners.Length == 0
            ? "This tag can’t be deleted because extension-owned data still references it. Remove or retag the related records in the owning extension, then try again."
            : $"This tag can’t be deleted because it is still referenced by:{ownerList}\nRemove or retag the related records in the listed extensions, then try again.";
    }
}

public sealed record TagDeletionExtensionOwner(string Id, string Name);
