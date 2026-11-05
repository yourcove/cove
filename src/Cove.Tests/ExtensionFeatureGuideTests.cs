using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;

namespace Cove.Tests;

public class ExtensionFeatureGuideTests
{
    private const string ExtensionId = "docs.guides";

    private static string Guide(string id, string title = "Guide title") => $$"""
    {
        "$schema": "./schema.json",
        "schemaVersion": 1,
        "id": "{{id}}",
        "title": "{{title}}",
        "description": "What the guide is for.",
        "order": 20,
        "contexts": ["settings-tab:extensions/installed"],
        "sections": [
            {
                "id": "start",
                "title": "Start here",
                "blocks": [
                    { "type": "paragraph", "text": "Read this first." },
                    { "type": "image", "src": "../images/overview.png", "alt": "Overview" }
                ]
            }
        ]
    }
    """;

    private static Task<UIManifest> LoadManifestAsync(
        string featureGuidesJson,
        IReadOnlyDictionary<string, string> files,
        string extraManifestJson = "") =>
        LoadPackagesAsync([new Package(ExtensionId, featureGuidesJson, files, extraManifestJson)]);

    private sealed record Package(
        string Id,
        string FeatureGuidesJson,
        IReadOnlyDictionary<string, string> Files,
        string ExtraManifestJson = "",
        IReadOnlyDictionary<string, byte[]>? BinaryFiles = null);

    private static async Task<UIManifest> LoadPackagesAsync(
        IReadOnlyList<Package> packages,
        Func<ExtensionManager, Task>? beforeAggregating = null)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-feature-guides-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");
        Directory.CreateDirectory(dataDir);

        try
        {
            foreach (var package in packages)
            {
                var packageDir = Path.Combine(extensionsDir, package.Id);
                Directory.CreateDirectory(packageDir);
                await File.WriteAllTextAsync(Path.Combine(packageDir, "extension.json"), $$"""
                {
                    "id": "{{package.Id}}",
                    "name": "Docs Guides",
                    "version": "1.0.0",
                    "kind": "bundle",
                    {{package.ExtraManifestJson}}
                    "featureGuides": {{package.FeatureGuidesJson}}
                }
                """, TestContext.Current.CancellationToken);

                foreach (var (relativePath, content) in package.Files)
                    await File.WriteAllBytesAsync(PackageFile(packageDir, relativePath), System.Text.Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
                foreach (var (relativePath, content) in package.BinaryFiles ?? new Dictionary<string, byte[]>())
                    await File.WriteAllBytesAsync(PackageFile(packageDir, relativePath), content, TestContext.Current.CancellationToken);
            }

            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = dataDir,
                CoveVersion = "1.0.0",
            });
            manager.DiscoverExtensions(extensionsDir);
            if (beforeAggregating is not null)
                await beforeAggregating(manager);
            return manager.GetAggregatedManifest();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static string PackageFile(string packageDir, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(packageDir, relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        return fullPath;
    }

    [Fact]
    public async Task AggregatedManifest_IncludesManifestDeclaredFeatureGuides()
    {
        var manifest = await LoadManifestAsync(
            """["docs/guides/overview.json", "./docs/guides/settings.json"]""",
            new Dictionary<string, string>
            {
                ["docs/guides/overview.json"] = Guide("docs-overview", "Overview"),
                ["docs/guides/settings.json"] = Guide("docs-settings", "Settings"),
            });

        Assert.Equal(["docs-overview", "docs-settings"], manifest.FeatureGuides.Select(guide => guide.Guide.GetProperty("id").GetString()));
        var overview = manifest.FeatureGuides[0];
        Assert.Equal(ExtensionId, overview.ExtensionId);
        Assert.Equal("docs/guides/overview.json", overview.Path);
        Assert.Equal("docs/guides/settings.json", manifest.FeatureGuides[1].Path);
        Assert.Equal("Overview", overview.Guide.GetProperty("title").GetString());
        Assert.Equal(
            "../images/overview.png",
            overview.Guide.GetProperty("sections")[0].GetProperty("blocks")[1].GetProperty("src").GetString());

        // The guide survives the API's JSON serialization unchanged.
        var serialized = JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(serialized);
        var guide = document.RootElement.GetProperty("featureGuides")[0];
        Assert.Equal(ExtensionId, guide.GetProperty("extensionId").GetString());
        Assert.Equal("docs-overview", guide.GetProperty("guide").GetProperty("id").GetString());
        Assert.Equal("./schema.json", guide.GetProperty("guide").GetProperty("$schema").GetString());
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("docs/../../outside.json")]
    [InlineData("/etc/hostname")]
    [InlineData("docs/missing.json")]
    [InlineData("docs/guide.txt")]
    [InlineData("")]
    [InlineData("docs/a\u0000.json")]
    [InlineData("docs/a\u0001b.json")]
    public async Task AggregatedManifest_SkipsFeatureGuidePathsOutsideThePackage(string path)
    {
        var manifest = await LoadManifestAsync(
            JsonSerializer.Serialize(new[] { path, "docs/valid.json" }),
            new Dictionary<string, string>
            {
                ["../outside.json"] = Guide("outside-guide"),
                ["docs/guide.txt"] = Guide("text-guide"),
                ["docs/valid.json"] = Guide("valid-guide"),
            });

        var guide = Assert.Single(manifest.FeatureGuides);
        Assert.Equal("valid-guide", guide.Guide.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{ "schemaVersion": 2, "id": "future-guide", "title": "T", "description": "D", "order": 1, "sections": [{ "id": "s", "title": "S", "blocks": [] }] }""")]
    [InlineData("""{ "schemaVersion": 1, "id": "Not Kebab", "title": "T", "description": "D", "order": 1, "sections": [{ "id": "s", "title": "S", "blocks": [] }] }""")]
    [InlineData("""{ "schemaVersion": 1, "id": "no-title", "title": "", "description": "D", "order": 1, "sections": [{ "id": "s", "title": "S", "blocks": [] }] }""")]
    [InlineData("""{ "schemaVersion": 1, "id": "no-sections", "title": "T", "description": "D", "order": 1, "sections": [] }""")]
    [InlineData("""{ "schemaVersion": 1, "id": "no-order", "title": "T", "description": "D", "sections": [{ "id": "s", "title": "S", "blocks": [] }] }""")]
    public async Task AggregatedManifest_SkipsInvalidFeatureGuides(string invalidGuide)
    {
        var manifest = await LoadManifestAsync(
            """["docs/invalid.json", "docs/valid.json"]""",
            new Dictionary<string, string>
            {
                ["docs/invalid.json"] = invalidGuide,
                ["docs/valid.json"] = Guide("valid-guide"),
            });

        var guide = Assert.Single(manifest.FeatureGuides);
        Assert.Equal("valid-guide", guide.Guide.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_KeepsAGuideWhoseOtherSectionsThisCoveCannotRead()
    {
        var guide = Guide("mixed-guide").Replace(
            "\"sections\": [",
            "\"sections\": [ { \"id\": \"Not A Slug\", \"title\": \"\", \"blocks\": \"later\" },",
            StringComparison.Ordinal);

        var manifest = await LoadManifestAsync(
            """["docs/mixed.json"]""",
            new Dictionary<string, string> { ["docs/mixed.json"] = guide });

        Assert.Equal("mixed-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_ListsEachGuidePathOnceAndCapsGuidesPerExtension()
    {
        var count = ExtensionFeatureGuideLoader.MaxGuidesPerExtension + 3;
        var files = Enumerable.Range(0, count).ToDictionary(index => $"docs/guide-{index}.json", index => Guide($"guide-{index}"));
        var declared = new[] { "docs/guide-0.json", "./docs/guide-0.json" }.Concat(files.Keys.Skip(1));

        var manifest = await LoadManifestAsync(JsonSerializer.Serialize(declared), files);

        // The repeated path is loaded once but still takes one of the listed slots.
        Assert.Equal(
            Enumerable.Range(0, ExtensionFeatureGuideLoader.MaxGuidesPerExtension - 1).Select(index => $"guide-{index}"),
            manifest.FeatureGuides.Select(guide => guide.Guide.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task AggregatedManifest_ReadsAFeatureGuideAgainAfterItChanges()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-feature-guides-{Guid.NewGuid():N}");
        var packageDir = Path.Combine(root, "extensions", ExtensionId);
        var guidePath = Path.Combine(packageDir, "docs", "guide.json");
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.GetDirectoryName(guidePath)!);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(packageDir, "extension.json"), $$"""
            { "id": "{{ExtensionId}}", "name": "Docs Guides", "version": "1.0.0", "kind": "bundle", "featureGuides": ["docs/guide.json"] }
            """, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(guidePath, Guide("changing-guide", "Before"), TestContext.Current.CancellationToken);

            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = Path.Combine(root, "data"),
                CoveVersion = "1.0.0",
            });
            manager.DiscoverExtensions(Path.Combine(root, "extensions"));
            Assert.Equal("Before", Assert.Single(manager.GetAggregatedManifest().FeatureGuides).Guide.GetProperty("title").GetString());

            await File.WriteAllTextAsync(guidePath, Guide("changing-guide", "After the update"), TestContext.Current.CancellationToken);
            Assert.Equal("After the update", Assert.Single(manager.GetAggregatedManifest().FeatureGuides).Guide.GetProperty("title").GetString());

            File.Delete(guidePath);
            Assert.Empty(manager.GetAggregatedManifest().FeatureGuides);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AggregatedManifest_SkipsOversizedFeatureGuides()
    {
        var oversized = Guide("large-guide").Replace("Read this first.", new string('x', 1024 * 1024), StringComparison.Ordinal);

        var manifest = await LoadManifestAsync(
            """["docs/large.json", "docs/valid.json"]""",
            new Dictionary<string, string>
            {
                ["docs/large.json"] = oversized,
                ["docs/valid.json"] = Guide("valid-guide"),
            });

        var guide = Assert.Single(manifest.FeatureGuides);
        Assert.Equal("valid-guide", guide.Guide.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_KeepsTheFirstFeatureGuideOrTopicForEachId()
    {
        var manifest = await LoadManifestAsync(
            """["docs/first.json", "docs/second.json", "docs/topic.json"]""",
            new Dictionary<string, string>
            {
                ["docs/first.json"] = Guide("shared-guide", "First"),
                ["docs/second.json"] = Guide("shared-guide", "Second"),
                ["docs/topic.json"] = Guide("slide-topic", "Collides with a slide topic"),
            },
            """
            "tutorialTopics": [
                { "id": "slide-topic", "title": "Slide topic", "slides": [{ "id": "one", "title": "One" }] }
            ],
            """);

        var guide = Assert.Single(manifest.FeatureGuides);
        Assert.Equal("First", guide.Guide.GetProperty("title").GetString());
        Assert.Single(manifest.TutorialTopics, topic => topic.Id == "slide-topic");
    }

    public static TheoryData<string> SharedBlockCases()
    {
        var data = new TheoryData<string>();
        foreach (var blockCase in ReadBlockCases().RootElement.GetProperty("cases").EnumerateArray())
            data.Add(blockCase.GetProperty("name").GetString()!);
        return data;
    }

    // The UI normalizer is tested against the same file, so a guide the host lists is one the User Guide shows.
    [Theory]
    [MemberData(nameof(SharedBlockCases))]
    public void OutlineCheck_AcceptsExactlyTheBlocksTheUserGuideRenders(string name)
    {
        using var cases = ReadBlockCases();
        var blockCase = cases.RootElement.GetProperty("cases").EnumerateArray().Single(candidate => candidate.GetProperty("name").GetString() == name);
        var guide = $$"""
        {
            "schemaVersion": 1, "id": "block-case", "title": "Block case", "description": "One block.", "order": 1,
            "sections": [{ "id": "only", "title": "Only section", "blocks": [{{blockCase.GetProperty("block").GetRawText()}}] }]
        }
        """;
        using var document = JsonDocument.Parse(guide);

        var problem = ExtensionFeatureGuideLoader.DescribeOutlineProblem(document.RootElement, cases.RootElement.GetProperty("guidePath").GetString()!);

        Assert.Equal(blockCase.GetProperty("renderable").GetBoolean(), problem is null);
    }

    private static JsonDocument ReadBlockCases()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ui", "src", "test", "fixtures", "extension-guide-blocks.json")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.FullName, "ui", "src", "test", "fixtures", "extension-guide-blocks.json")));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""[{ "type": "video", "src": "intro.mp4" }]""")]
    [InlineData("""[{ "type": "paragraph", "text": "" }]""")]
    public async Task AggregatedManifest_SkipsAGuideWithNothingTheUserGuideCanShow(string blocks)
    {
        var guide = System.Text.RegularExpressions.Regex.Replace(Guide("empty-guide"), "\"blocks\": \\[[^\\]]*\\]", $"\"blocks\": {blocks}");
        Assert.Contains(blocks, guide, StringComparison.Ordinal);

        var manifest = await LoadManifestAsync(
            """["docs/empty.json"]""",
            new Dictionary<string, string> { ["docs/empty.json"] = guide });

        Assert.Empty(manifest.FeatureGuides);
    }

    [Theory]
    [InlineData("docs.a", "docs.b")]
    [InlineData("docs.b", "docs.a")]
    public async Task AggregatedManifest_DoesNotLetAGuideItCannotShowTakeAnotherExtensionsTopicId(string brokenId, string workingId)
    {
        var empty = Guide("shared-guide", "Nothing to show").Replace("\"text\": \"Read this first.\"", "\"text\": \"\"", StringComparison.Ordinal)
            .Replace("\"src\": \"../images/overview.png\"", "\"src\": \"../../../outside.png\"", StringComparison.Ordinal);

        var manifest = await LoadPackagesAsync(
        [
            new Package(brokenId, """["docs/guide.json"]""", new Dictionary<string, string> { ["docs/guide.json"] = empty }),
            new Package(workingId, """["docs/guide.json"]""", new Dictionary<string, string> { ["docs/guide.json"] = Guide("shared-guide", "Working") }),
        ]);

        var guide = Assert.Single(manifest.FeatureGuides);
        Assert.Equal(workingId, guide.ExtensionId);
        Assert.Equal("Working", guide.Guide.GetProperty("title").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_ReadsAFeatureGuideSavedWithAByteOrderMark()
    {
        var manifest = await LoadPackagesAsync(
        [
            new Package(ExtensionId, """["docs/bom.json"]""", new Dictionary<string, string>(), BinaryFiles: new Dictionary<string, byte[]>
            {
                ["docs/bom.json"] = [0xEF, 0xBB, 0xBF, .. System.Text.Encoding.UTF8.GetBytes(Guide("bom-guide"))],
            }),
        ]);

        Assert.Equal("bom-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    // A guide saved as Windows-1252 writes a curly apostrophe as the byte 0x92, which is not UTF-8.
    [Fact]
    public async Task AggregatedManifest_SkipsAFeatureGuideThatIsNotUtf8()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(Guide("windows-guide")).ToList();
        var marker = System.Text.Encoding.UTF8.GetBytes("Read this first.");
        var at = Enumerable.Range(0, bytes.Count - marker.Length).First(index => bytes.Skip(index).Take(marker.Length).SequenceEqual(marker));
        bytes.Insert(at + 4, 0x92);

        var manifest = await LoadPackagesAsync(
        [
            new Package(ExtensionId, """["docs/windows.json", "docs/valid.json"]""", new Dictionary<string, string> { ["docs/valid.json"] = Guide("valid-guide") }, BinaryFiles: new Dictionary<string, byte[]>
            {
                ["docs/windows.json"] = [.. bytes],
            }),
        ]);

        Assert.Equal("valid-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(", \"slides\": []")]
    [InlineData("")]
    public async Task AggregatedManifest_LetsAFeatureGuideUseTheIdOfASlideTopicWithNoSlides(string slides)
    {
        var manifest = await LoadManifestAsync(
            """["docs/guide.json"]""",
            new Dictionary<string, string> { ["docs/guide.json"] = Guide("empty-topic") },
            $$"""
            "tutorialTopics": [{ "id": "empty-topic", "title": "Empty topic"{{slides}} }],
            """);

        Assert.Equal("empty-topic", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
        // The topic itself stays in the manifest as before, so manifest-only packages still list it; the
        // User Guide does not show it.
        var topic = Assert.Single(manifest.TutorialTopics, topic => topic.Id == "empty-topic");
        Assert.Equal(ExtensionId, topic.ExtensionId);
    }

    [Fact]
    public async Task AggregatedManifest_KeepsAFeatureGuideFromTakingTheIdOfASlideTopicWithSlides()
    {
        var manifest = await LoadManifestAsync(
            """["docs/guide.json"]""",
            new Dictionary<string, string> { ["docs/guide.json"] = Guide("slide-topic") },
            """
            "tutorialTopics": [{ "id": "slide-topic", "title": "Slide topic", "slides": [{ "id": "one", "title": "One" }] }],
            """);

        Assert.Empty(manifest.FeatureGuides);
        Assert.Single(manifest.TutorialTopics, topic => topic.Id == "slide-topic");
    }

    [Fact]
    public async Task AggregatedManifest_CountsRepeatedPathsTowardTheGuideLimit()
    {
        var declared = Enumerable.Repeat("docs/first.json", ExtensionFeatureGuideLoader.MaxGuidesPerExtension).Append("docs/late.json");

        var manifest = await LoadManifestAsync(
            JsonSerializer.Serialize(declared),
            new Dictionary<string, string> { ["docs/first.json"] = Guide("first-guide"), ["docs/late.json"] = Guide("late-guide") });

        Assert.Equal("first-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("\"com.example.overview\"")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task AggregatedManifest_KeepsAGuideWhoseParentIsNotAKebabCaseTopicId(string parentTopicId)
    {
        var guide = Guide("nested-guide").Replace("\"order\": 20,", $"\"order\": 20, \"parentTopicId\": {parentTopicId},", StringComparison.Ordinal);

        var manifest = await LoadManifestAsync(
            """["docs/nested.json"]""",
            new Dictionary<string, string> { ["docs/nested.json"] = guide });

        Assert.Equal("nested-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_AcceptsWholeNumbersWrittenWithADecimalPoint()
    {
        var guide = Guide("decimal-guide").Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1.0,", StringComparison.Ordinal)
            .Replace("\"order\": 20,", "\"order\": 20.0,", StringComparison.Ordinal);

        var manifest = await LoadManifestAsync(
            """["docs/decimal.json"]""",
            new Dictionary<string, string> { ["docs/decimal.json"] = guide });

        Assert.Equal("decimal-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    [Fact]
    public async Task AggregatedManifest_CountsMissingAndInvalidPathsTowardTheGuideLimit()
    {
        var declared = Enumerable.Range(0, ExtensionFeatureGuideLoader.MaxGuidesPerExtension).Select(index => index % 2 == 0 ? $"docs/missing-{index}.json" : $"../outside-{index}.json")
            .Append("docs/guide.json");

        var manifest = await LoadManifestAsync(
            JsonSerializer.Serialize(declared),
            new Dictionary<string, string> { ["docs/guide.json"] = Guide("late-guide") });

        Assert.Empty(manifest.FeatureGuides);
    }

    [Fact]
    public async Task AggregatedManifest_LeavesOutTheGuidesOfADisabledExtension()
    {
        var manifest = await LoadPackagesAsync(
            [
                new Package("docs.disabled", """["docs/guide.json"]""", new Dictionary<string, string> { ["docs/guide.json"] = Guide("disabled-guide") }),
                new Package("docs.enabled", """["docs/guide.json"]""", new Dictionary<string, string> { ["docs/guide.json"] = Guide("enabled-guide") }),
            ],
            manager => manager.DisableExtensionAsync("docs.disabled", TestContext.Current.CancellationToken));

        Assert.Equal("enabled-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
    }

    // A JSON escape can encode half of a UTF-16 surrogate pair. JsonDocument parses it, but the string
    // cannot be read or written back out, so accepting such a guide failed the manifest response for
    // every user (a code line the outline check never reads) or dropped every guide of the extension
    // and logged on every request (block text the check reads).
    [Theory]
    [InlineData("""{ "type": "code", "lines": ["\ud800"] }""")]
    [InlineData("""{ "type": "paragraph", "text": "\ud800" }""")]
    [InlineData("""{ "type": "paragraph", "text": "Fine.", "caption": "\udfff" }""")]
    public async Task AggregatedManifest_SkipsAGuideWithAnUnpairedSurrogateAndStillServesTheManifest(string block)
    {
        var broken = System.Text.RegularExpressions.Regex.Replace(Guide("broken-guide"), "\"blocks\": \\[", $"\"blocks\": [{block},");

        var manifest = await LoadManifestAsync(
            """["docs/broken.json", "docs/working.json"]""",
            new Dictionary<string, string> { ["docs/broken.json"] = broken, ["docs/working.json"] = Guide("working-guide") });

        Assert.Equal("working-guide", Assert.Single(manifest.FeatureGuides).Guide.GetProperty("id").GetString());
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public static TheoryData<int> SharedSectionIdCases()
    {
        using var cases = ReadBlockCases();
        var data = new TheoryData<int>();
        for (var index = 0; index < cases.RootElement.GetProperty("sectionIdCases").GetArrayLength(); index++)
            data.Add(index);
        return data;
    }

    [Theory]
    [MemberData(nameof(SharedSectionIdCases))]
    public void OutlineCheck_AcceptsExactlyTheSectionIdsTheUserGuideRenders(int index)
    {
        using var cases = ReadBlockCases();
        var sectionCase = cases.RootElement.GetProperty("sectionIdCases")[index];
        var guide = $$"""
        {
            "schemaVersion": 1, "id": "section-case", "title": "Section case", "description": "One section.", "order": 1,
            "sections": [{ "id": {{sectionCase.GetProperty("id").GetRawText()}}, "title": "Only section", "blocks": [{ "type": "paragraph", "text": "Body." }] }]
        }
        """;
        using var document = JsonDocument.Parse(guide);

        var problem = ExtensionFeatureGuideLoader.DescribeOutlineProblem(document.RootElement, "docs/guides/guide.json");

        Assert.Equal(sectionCase.GetProperty("valid").GetBoolean(), problem is null);
    }
}
