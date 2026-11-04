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

    private static async Task<UIManifest> LoadManifestAsync(
        string featureGuidesJson,
        IReadOnlyDictionary<string, string> files,
        string extraManifestJson = "")
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-feature-guides-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");
        var packageDir = Path.Combine(extensionsDir, ExtensionId);
        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(packageDir);

        try
        {
            await File.WriteAllTextAsync(Path.Combine(packageDir, "extension.json"), $$"""
            {
                "id": "{{ExtensionId}}",
                "name": "Docs Guides",
                "version": "1.0.0",
                "kind": "bundle",
                {{extraManifestJson}}
                "featureGuides": {{featureGuidesJson}}
            }
            """, TestContext.Current.CancellationToken);

            foreach (var (relativePath, content) in files)
            {
                var fullPath = Path.GetFullPath(Path.Combine(packageDir, relativePath));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                await File.WriteAllTextAsync(fullPath, content, TestContext.Current.CancellationToken);
            }

            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = dataDir,
                CoveVersion = "1.0.0",
            });
            manager.DiscoverExtensions(extensionsDir);
            return manager.GetAggregatedManifest();
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
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

        Assert.Equal(ExtensionFeatureGuideLoader.MaxGuidesPerExtension, manifest.FeatureGuides.Count);
        Assert.Equal(
            Enumerable.Range(0, ExtensionFeatureGuideLoader.MaxGuidesPerExtension).Select(index => $"guide-{index}"),
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
}
