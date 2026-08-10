using Cove.Core.Entities;

namespace Cove.Api.Services;

internal static class GeneratePathFilter
{
    public static List<string> Normalize(IEnumerable<string>? paths)
        => paths?
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizePath)
            .Where(path => path.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            ?? [];

    public static bool Contains(string candidatePath, IReadOnlyList<string> filterPaths)
    {
        if (filterPaths.Count == 0)
            return true;

        var candidate = NormalizePath(candidatePath);
        return filterPaths.Any(filterPath =>
        {
            var path = NormalizePath(filterPath);
            return candidate.Equals(path, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase);
        });
    }

    public static string Resolve(BaseFileEntity file)
        => file.ParentFolder is not null
            ? Path.Combine(file.ParentFolder.Path, file.Basename)
            : file.Basename;

    private static string NormalizePath(string path)
        => path.Trim().Replace('\\', '/').TrimEnd('/');
}
