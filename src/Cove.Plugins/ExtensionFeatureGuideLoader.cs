using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Reads the shared feature-guide JSON files an extension manifest lists under <c>featureGuides</c>.
/// Only files inside the extension package that the User Guide can list and show are returned; the
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
        // Every listed path counts toward the limit, not only the guides that load: the manifest is built
        // on every request, so a package listing many missing or invalid files must not cost a disk check
        // for each of them every time.
        var listed = 0;
        foreach (var declaredPath in declaredPaths)
        {
            var relativePath = ResolvePackagePath(packageDirectory, declaredPath, out var fullPath);
            if (listed++ == MaxGuidesPerExtension)
            {
                Report(logger, extensionId, extensionVersion, relativePath ?? declaredPath, $"is listed after the first {MaxGuidesPerExtension} feature guides of its extension");
                break;
            }
            if (relativePath is not null && !seenPaths.Add(relativePath))
                continue;
            if (relativePath is null)
            {
                Report(logger, extensionId, extensionVersion, declaredPath, "is not a .json file inside its package");
                continue;
            }

            var read = Read(fullPath!, relativePath, extensionVersion);
            if (read.Guide is not { } guide)
            {
                Report(logger, extensionId, extensionVersion, relativePath, read.Problem ?? "could not be read");
                continue;
            }

            guides.Add(new UIFeatureGuide { ExtensionId = extensionId, Path = relativePath, Guide = guide });
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

    private GuideRead Read(string fullPath, string relativePath, string? extensionVersion)
    {
        try
        {
            var file = new FileInfo(fullPath);
            if (!file.Exists)
            {
                _cache.TryRemove(fullPath, out _);
                return new GuideRead(null, "is missing");
            }

            var stamp = new GuideStamp(extensionVersion, file.LastWriteTimeUtc, file.Length);
            if (_cache.TryGetValue(fullPath, out var cached) && cached.Stamp == stamp)
                return cached.Result;

            var result = Parse(fullPath, relativePath);
            _cache[fullPath] = new CachedGuide(stamp, result);
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not cached: a transient sharing violation must not hide the guide until the file changes.
            return new GuideRead(null, "could not be read");
        }
    }

    private static GuideRead Parse(string fullPath, string relativePath)
    {
        // Read at most one byte past the limit, so a file that grows after it was measured is still refused.
        var buffer = new byte[MaxGuideBytes + 1];
        int length;
        using (var stream = File.OpenRead(fullPath))
        {
            length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }
        if (length > MaxGuideBytes)
            return new GuideRead(null, $"is larger than {MaxGuideBytes / 1024} KiB");

        // Parsing bytes does not skip a byte order mark the way reading text does, and editors such as
        // Visual Studio save "UTF-8 with signature" by default.
        var json = buffer.AsMemory(0, length);
        if (json.Span.StartsWith(Utf8ByteOrderMark))
            json = json[Utf8ByteOrderMark.Length..];

        // JsonDocument accepts bytes that are not UTF-8, such as a file saved as Windows-1252, but reading
        // such a string later throws, and writing it replaces the bytes with U+FFFD.
        if (!Utf8.IsValid(json.Span))
            return new GuideRead(null, "is not valid UTF-8");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return new GuideRead(null, "is not valid JSON or is nested more than 32 levels deep");
        }

        using (document)
        {
            // A JSON escape can encode half of a UTF-16 surrogate pair, such as "\ud800". JsonDocument parses
            // it, but reading that string throws, and so does writing it into the manifest response, which
            // would fail the manifest for every user. Writing the guide out once here finds any such string,
            // wherever it is, before the guide is accepted.
            try
            {
                JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or ArgumentException)
            {
                return new GuideRead(null, "contains text that is not valid Unicode");
            }

            var problem = DescribeOutlineProblem(document.RootElement, relativePath);
            return problem is null ? new GuideRead(document.RootElement.Clone(), null) : new GuideRead(null, problem);
        }
    }

    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Checks that the User Guide can list, order, and show a guide, and returns why not when it cannot.
    /// Sections and blocks this Cove cannot render are allowed, so a guide written for a newer Cove still
    /// shows what this Cove understands, but at least one section must have a block it can render. A
    /// guide without one would be listed in the manifest, claim its topic ID, and then show nothing.
    /// </summary>
    /// <remarks>
    /// The block rules must match <c>normalizeExtensionFeatureGuide</c> in
    /// <c>ui/src/components/featureGuideContent.ts</c>, which renders what this check accepts.
    /// <c>ui/src/test/fixtures/extension-guide-blocks.json</c> holds the cases both test suites check.
    /// </remarks>
    internal static string? DescribeOutlineProblem(JsonElement guide, string guidePath)
    {
        if (guide.ValueKind != JsonValueKind.Object)
            return "is not a JSON object";
        if (!TryGetWholeNumber(guide, "schemaVersion", out var version) || version != 1)
            return "is not a schemaVersion 1 feature guide";
        if (!IsSlug(guide, "id"))
            return "has no kebab-case id";
        if (!IsText(guide, "title"))
            return "has no title";
        if (!IsText(guide, "description"))
            return "has no description";
        if (!TryGetWholeNumber(guide, "order", out var order) || order < 0)
            return "has no non-negative whole-number order";
        if (!guide.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            return "has no sections";

        var guideDirectory = guidePath.Split('/')[..^1];
        return sections.EnumerateArray().Any(section => IsRenderableSection(section, guideDirectory))
            ? null
            : "has no section with a kebab-case id, a title, and a block this Cove can show";
    }

    private static bool IsRenderableSection(JsonElement section, string[] guideDirectory) =>
        section.ValueKind == JsonValueKind.Object
        && IsSlug(section, "id")
        && IsText(section, "title")
        && section.TryGetProperty("blocks", out var blocks)
        && blocks.ValueKind == JsonValueKind.Array
        && blocks.EnumerateArray().Any(block => IsRenderableBlock(block, guideDirectory, allowRecipes: true));

    private static bool IsRenderableBlock(JsonElement block, string[] guideDirectory, bool allowRecipes)
    {
        if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return false;

        switch (type.GetString())
        {
            case "paragraph":
            case "note":
                return IsText(block, "text");
            case "steps":
            case "list":
                return CountTexts(block, "items") > 0;
            case "heading":
                return IsText(block, "text") && TryGetWholeNumber(block, "level", out var level) && level is 3 or 4;
            case "links":
                return Items(block).Any(link =>
                    IsText(link, "label")
                    && IsText(link, "href")
                    && WebsiteHrefPattern().IsMatch(link.GetProperty("href").GetString()!));
            case "image":
                return IsText(block, "alt")
                    && IsText(block, "src")
                    && IsPackageImage(block.GetProperty("src").GetString()!, guideDirectory);
            case "table":
                var columns = CountTexts(block, "columns");
                return columns > 0
                    && block.TryGetProperty("rows", out var rows)
                    && rows.ValueKind == JsonValueKind.Array
                    && rows.EnumerateArray().Any(row =>
                        row.ValueKind == JsonValueKind.Array
                        && row.GetArrayLength() == columns
                        && row.EnumerateArray().All(cell => cell.ValueKind == JsonValueKind.String));
            case "code":
                return block.TryGetProperty("lines", out var lines)
                    && lines.ValueKind == JsonValueKind.Array
                    && lines.GetArrayLength() > 0
                    && lines.EnumerateArray().All(line => line.ValueKind == JsonValueKind.String);
            case "recipes":
                return allowRecipes && Items(block).Any(recipe =>
                    IsSlug(recipe, "id")
                    && IsText(recipe, "title")
                    && recipe.TryGetProperty("blocks", out var recipeBlocks)
                    && recipeBlocks.ValueKind == JsonValueKind.Array
                    && recipeBlocks.EnumerateArray().Any(recipeBlock => IsRenderableBlock(recipeBlock, guideDirectory, allowRecipes: false)));
            default:
                return false;
        }
    }

    /// <summary>Matches <c>resolveExtensionGuideImage</c>: https and inline images, or a file inside the package.</summary>
    private static bool IsPackageImage(string source, string[] guideDirectory)
    {
        if (StartsWithAsciiIgnoreCase(source, "https://") || StartsWithAsciiIgnoreCase(source, "data:image/"))
            return true;
        if (UriSchemePattern().IsMatch(source) || source.StartsWith('/') || source.StartsWith('\\'))
            return false;

        var depth = guideDirectory.Length;
        foreach (var segment in source.Split('/', '\\'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (depth == 0)
                    return false;
                depth--;
            }
            else
            {
                depth++;
            }
        }
        return depth > 0;
    }

    private static IEnumerable<JsonElement> Items(JsonElement block) =>
        block.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.Object)
            : [];

    private static int CountTexts(JsonElement element, string property) =>
        element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Count(value => value.ValueKind == JsonValueKind.String && !IsBlank(value.GetString()!))
            : 0;

    /// <summary>JSON Schema and the User Guide both treat <c>1.0</c> as the integer 1.</summary>
    private static bool TryGetWholeNumber(JsonElement element, string property, out double value)
    {
        value = 0;
        return element.TryGetProperty(property, out var number)
            && number.ValueKind == JsonValueKind.Number
            && number.TryGetDouble(out value)
            && double.IsFinite(value)
            && value == Math.Floor(value);
    }

    private static bool IsText(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && !IsBlank(value.GetString()!);

    /// <summary>
    /// Matches JavaScript's <c>trim()</c>, which the User Guide uses: it strips U+FEFF, which .NET does not count
    /// as white space, and keeps U+0085, which .NET does.
    /// </summary>
    private static bool IsBlank(string value) =>
        value.All(character => character == '\uFEFF' || (character != '\u0085' && char.IsWhiteSpace(character)));

    /// <summary>Matches a JavaScript <c>/i</c> regular expression, which folds only ASCII letters here.</summary>
    private static bool StartsWithAsciiIgnoreCase(string value, string asciiPrefix)
    {
        if (value.Length < asciiPrefix.Length)
            return false;
        for (var index = 0; index < asciiPrefix.Length; index++)
        {
            if (char.ToLowerInvariant(asciiPrefix[index]) != (char.IsAsciiLetterUpper(value[index]) ? (char)(value[index] + 32) : value[index]))
                return false;
        }
        return true;
    }

    private static bool IsSlug(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
        && SlugPattern().IsMatch(value.GetString()!);

    // \z, not $: in .NET, $ also matches before a final newline, which would accept "start\n".
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*\\z")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^(?:/docs/|https?://)")]
    private static partial Regex WebsiteHrefPattern();

    // ASCII letters only, without IgnoreCase: .NET case folding maps the Kelvin sign to k, JavaScript's does not.
    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex UriSchemePattern();

    private readonly record struct GuideStamp(string? ExtensionVersion, DateTime LastWriteTimeUtc, long Length);

    private readonly record struct GuideRead(JsonElement? Guide, string? Problem);

    private sealed record CachedGuide(GuideStamp Stamp, GuideRead Result);
}
