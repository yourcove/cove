namespace Cove.Core.Common;

/// <summary>
/// Case sensitivity for comparing/deduplicating filesystem paths. Windows and macOS use
/// case-insensitive filesystems; Linux is case-sensitive. Using the wrong comparison collapses
/// genuinely distinct folders on Linux (e.g. <c>/data/Videos/Weibtm</c> vs <c>/data/Videos/weibtm</c>),
/// which then causes files with the same basename to collide on the unique
/// (ParentFolderId, Basename) index.
/// </summary>
public static class FilesystemPaths
{
    public static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static readonly StringComparer PathComparer = StringComparer.FromComparison(PathComparison);
}
