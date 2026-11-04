using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Reads the shared feature-guide JSON files an extension manifest lists under <c>featureGuides</c>.
/// Only files inside the extension package with a recognizable guide outline are returned; the
/// in-app User Guide renders the individual sections and blocks and drops any it does not recognize.
/// </summary>
/// <remarks>
/// The aggregated manifest is built for every manifest request, so a problem with one guide must never
/// throw, and parsed guides are cached until the file or the extension version changes.
/// </remarks>
internal sealed partial class ExtensionFeatureGuideLoader
{
    /// <summary>Cove's own guides are well under 20 KiB, so this leaves ample room without letting one file bloat every manifest response.</summary>
    internal const int MaxGuideBytes = 256 * 1024;

    /// <summary>Every guide is inlined into each manifest response, so one extension cannot add an unbounded number.</summary>
    internal const int MaxGuidesPerExtension = 32;

    private readonly ConcurrentDictionary<string, CachedGuide> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reportedProblems = new(StringComparer.Ordinal);

    public IReadOnlyList<UIFeatureGuide> Load(
        string extensionId,
        string? extensionVersion,
        string packageDirectory,
        IEnumerable<string> declaredPaths,
        ILogger? logger)
    {
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var guides = new List<UIFeatureGuide>();
        foreach (var declaredPath in declaredPaths)
        {
            var relativePath = ResolvePackagePath(packageDirectory, declaredPath, out var fullPath);
            if (relativePath is null)
            {
                Report(logger, extensionId, extensionVersion, declaredPath, "is not a .json file inside its package");
                continue;
            }
            if (!seenPaths.Add(relativePath))
                continue;
            if (guides.Count == MaxGuidesPerExtension)
            {
                Report(logger, extensionId, extensionVersion, relativePath, $"exceeds the limit of {MaxGuidesPerExtension} feature guides per extension");
                break;
            }

            var guide = Read(fullPath!, extensionVersion);
            if (guide is null)
            {
                Report(logger, extensionId, extensionVersion, relativePath, "is missing, larger than 256 KiB, or not a schemaVersion 1 feature guide");
                continue;
            }

            guides.Add(new UIFeatureGuide { ExtensionId = extensionId, Path = relativePath, Guide = guide.Value });
        }
        return guides;
    }

    /// <summary>Logs a guide problem once per extension version, not on every manifest request.</summary>
    internal void Report(ILogger? logger, string extensionId, string? extensionVersion, string? path, string problem)
    {
        if (logger is null || !_reportedProblems.TryAdd($"{extensionId}\n{extensionVersion}\n{path}\n{problem}", 0))
            return;
        // Quoted and escaped: a declared path can contain the control characters it is rejected for.
        logger.LogWarning("Extension {ExtensionId} feature guide {Path} {Problem}; it is not shown in the User Guide", extensionId, JsonSerializer.Serialize(path), problem);
    }

    internal static string? ResolvePackagePath(string packageDirectory, string? declaredPath, out string? fullPath)
    {
        fullPath = null;
        var value = declaredPath?.Trim();
        if (string.IsNullOrEmpty(value)
            || value.Any(char.IsControl)
            || Path.IsPathRooted(value)
            || value.StartsWith('/')
            || value.StartsWith('\\')
            || !value.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var root = Path.GetFullPath(packageDirectory);
            var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, value));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootWithSeparator, comparison))
                return null;

            fullPath = candidate;
            return Path.GetRelativePath(root, candidate).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private JsonElement? Read(string fullPath, string? extensionVersion)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                _cache.TryRemove(fullPath, out _);
                return null;
            }

            var stamp = new GuideStamp(extensionVersion, file.LastWriteTimeUtc, file.Length);
            if (_cache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
                return cached.Guide;

            var guide = Parse(fullPath);
            _cache[fullPath] = new CachedGuide(stamp, guide);
            return guide;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cached: a transient sharing violation must not hide the guide until the file changes.
            return null;
        }
    }

    private static JsonElement? Parse(string fullPath)
    {
        // Read at most one byte past the limit, so a file that grows after it was measured is still refused.
        var buffer = new byte[MaxGuideBytes + 1];
        int length;
        using (var stream = File.OpenRead(fullPath))
        {
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        if (length > MaxGuideBytes)
            return null;

        try
        {
            using var document = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 32 });
            return HasGuideOutline(document.RootElement) ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Checks the fields the User Guide needs to list, order, and link a topic. Individual sections and
    /// blocks are left to the User Guide, which drops the ones it cannot render, so a guide written for a
    /// newer Cove still shows what this Cove understands.
    /// </summary>
    internal static bool HasGuideOutline(JsonElement guide) =>
        guide.ValueKind == JsonValueKind.Object
        && guide.TryGetProperty("schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 1
        && IsSlug(guide, "id")
        && IsNonEmptyString(guide, "title")
        && IsNonEmptyString(guide, "description")
        && guide.TryGetProperty("order", out var order) && order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var orderValue) && orderValue >= 0
        && guide.TryGetProperty("sections", out var sections) && sections.ValueKind == JsonValueKind.Array
        && sections.EnumerateArray().Any(section =>
            section.ValueKind == JsonValueKind.Object
            && IsSlug(section, "id")
            && IsNonEmptyString(section, "title")
            && section.TryGetProperty("blocks", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
        && (!guide.TryGetProperty("parentTopicId", out _) || IsSlug(guide, "parentTopicId"));

    private static bool IsNonEmptyString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static bool IsSlug(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && SlugPattern().IsMatch(value.GetString()!);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    private readonly record struct GuideStamp(string? ExtensionVersion, DateTime LastWriteTimeUtc, long Length);

    private sealed record CachedGuide(GuideStamp Stamp, JsonElement? Guide);
}
