using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class ExtensionBundleSupportTests
{
    [Fact]
    public void Manifest_builder_owner_stamps_permission_gated_pages_and_tabs()
    {
        var manifest = new UIManifestBuilder("catalog.extension")
            .AddPage(new UIPageDefinition(
                "missing-scenes", "Missing Scenes",
                ComponentName: "MissingScenesPage",
                ExtensionId: "spoofed.extension")
            {
                RequiredPermissions = ["extensions.configure", "videos.read"],
                RequiredPermissionMode = PermissionMode.All,
            })
            .AddTab(new UITabContribution(
                "missing-scenes", "Missing Scenes", "performer",
                "spoofed.extension", "MissingScenesTab")
            {
                RequiredPermissions = ["extensions.configure", "videos.read", "performers.read"],
                RequiredPermissionMode = PermissionMode.All,
            })
            .Build();

        var page = Assert.Single(manifest.Pages);
        Assert.Equal("catalog.extension", page.ExtensionId);
        Assert.Equal(["extensions.configure", "videos.read"], Assert.IsType<string[]>(page.RequiredPermissions));
        Assert.Equal(PermissionMode.All, page.RequiredPermissionMode);
        var tab = Assert.Single(manifest.Tabs);
        Assert.Equal("catalog.extension", tab.ExtensionId);
        Assert.Equal(["extensions.configure", "videos.read", "performers.read"], Assert.IsType<string[]>(tab.RequiredPermissions));
        Assert.Equal(PermissionMode.All, tab.RequiredPermissionMode);
    }

    [Fact]
    public void Permission_mode_serializes_with_the_browser_contract_value()
    {
        var page = new UIPageDefinition("catalog", "Catalog")
        {
            RequiredPermissions = ["catalog.read", "catalog.configure"],
            RequiredPermissionMode = PermissionMode.Any,
        };
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));

        var json = JsonSerializer.SerializeToElement(page, options);

        Assert.Equal("any", json.GetProperty("requiredPermissionMode").GetString());
    }

    [Fact]
    public void CoveExtensionBase_CanContributeSettingsTabs()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        var extension = new SettingsTabContributionExtension();
        Assert.IsAssignableFrom<IUIExtension>(extension);

        manager.Register(extension, "local");

        var manifest = manager.GetAggregatedManifest();
        var settingsTab = Assert.Single(manifest.SettingsTabs);
        Assert.Equal("extensions/example", settingsTab.Key);
        Assert.Equal(SettingsTabContributionExtension.ExtensionId, settingsTab.ExtensionId);

        var settingsPanel = Assert.Single(manifest.SettingsPanels);
        Assert.Equal("extensions/example", settingsPanel.TargetTab);
    }

    [Fact]
    public void AddSettingsTab_DefaultsToPanelsLayout()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new SettingsTabContributionExtension(), "local");

        var tab = Assert.Single(manager.GetAggregatedManifest().SettingsTabs);
        Assert.Equal(SettingsTabLayout.Panels, tab.Layout);
    }

    [Fact]
    public void AddSettingsTab_WithPageLayout_SourcesContentFromItsPanels()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new SettingsPageContributionExtension(), "local");

        var manifest = manager.GetAggregatedManifest();
        var tab = Assert.Single(manifest.SettingsTabs);
        Assert.Equal("extensions/example-page", tab.Key);
        Assert.Equal(SettingsTabLayout.Page, tab.Layout);
        // A page is just uncarded panels: the tab's content comes from the panels targeting it,
        // exactly like the panels layout — only the host's chrome differs.
        var panel = Assert.Single(manifest.SettingsPanels);
        Assert.Equal("extensions/example-page", panel.TargetTab);
        Assert.Equal("ExamplePage", panel.ComponentName);
    }

    [Fact]
    public void AddSettingsTab_KeepsOriginalOverloadSignatureForBinaryCompatibility()
    {
        // The page-layout capability must be additive at the binary level: appending a parameter to
        // this overload would compile fine but throw MissingMethodException in any extension already
        // built against the prior signature (the layout lives on a NEW overload instead). Only a
        // reflection check guards this — the compiler never will. If this fails, do not "fix" it by
        // changing the assertion; restore the original signature and add capability via an overload.
        var original = typeof(UIManifestBuilder).GetMethod(
            nameof(UIManifestBuilder.AddSettingsTab),
            [typeof(string), typeof(string), typeof(int), typeof(string), typeof(string), typeof(string), typeof(string[]), typeof(string[])]);
        Assert.NotNull(original);

        // The UISettingsTab primary constructor must likewise stay at its original arity — Layout is
        // an init property, not a constructor parameter, for the same binary-compatibility reason.
        Assert.DoesNotContain(
            typeof(UISettingsTab).GetConstructors(),
            ctor => ctor.GetParameters().Any(p => p.ParameterType == typeof(SettingsTabLayout)));
    }

    [Fact]
    public void AggregatedManifest_IncludesTabManualContexts()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new TabContributionExtension(), "local");

        var manifest = manager.GetAggregatedManifest();
        var tab = Assert.Single(manifest.Tabs);
        Assert.Equal("related", tab.Key);
        var manualContexts = Assert.IsType<string[]>(tab.ManualContexts);
        Assert.Equal(["panel:related-media", "feature:example.detail"], manualContexts);
    }

    [Fact]
    public void AggregatedManifest_IncludesListFiltersAndSorts()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new ListContributionExtension(), "local");

        var manifest = manager.GetAggregatedManifest();
        var listFilter = Assert.Single(manifest.ListFilters);
        Assert.Equal("video-quality-filter", listFilter.Id);
        Assert.Equal("videos", listFilter.EntityType);
        Assert.Equal("quality_score", listFilter.CustomFieldKey);
        Assert.Equal("number", listFilter.CustomFieldType);
        Assert.Equal(ListContributionExtension.ExtensionId, listFilter.ExtensionId);

        var listSort = Assert.Single(manifest.ListSorts);
        Assert.Equal("video-quality-sort", listSort.Id);
        Assert.Equal("videos", listSort.EntityType);
        Assert.Equal("quality_score", listSort.CustomFieldKey);
        Assert.Equal("number", listSort.CustomFieldType);
        Assert.Equal(ListContributionExtension.ExtensionId, listSort.ExtensionId);
    }


    [Fact]
    public void GetExtensions_UsesManifestCategoriesForLoadedExtensions()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new RuntimeCategoryFallbackExtension(), "local");

        var manifest = new ExtensionManifestFile
        {
            Id = RuntimeCategoryFallbackExtension.ExtensionId,
            Name = "Runtime Category Fallback",
            Version = "1.0.0",
            Categories = ["scraper", "metadata"],
        };

        var install = manager.GetInstallation(RuntimeCategoryFallbackExtension.ExtensionId);
        Assert.NotNull(install);
        install!.ManifestJson = JsonSerializer.Serialize(manifest);
        install.Categories = null;

        var controller = CreateController(manager);

        var allResult = controller.GetExtensions();
        var allOk = Assert.IsType<OkObjectResult>(allResult.Result);
        var extension = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ExtensionInfo>>(allOk.Value));
        Assert.Contains("scraper", extension.Categories);

        var filteredResult = controller.GetExtensions("scraper");
        var filteredOk = Assert.IsType<OkObjectResult>(filteredResult.Result);
        var filteredExtension = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ExtensionInfo>>(filteredOk.Value));
        Assert.Equal(RuntimeCategoryFallbackExtension.ExtensionId, filteredExtension.Id);

        var unmatchedResult = controller.GetExtensions("theme");
        var unmatchedOk = Assert.IsType<OkObjectResult>(unmatchedResult.Result);
        Assert.Empty(Assert.IsAssignableFrom<IEnumerable<ExtensionInfo>>(unmatchedOk.Value));
    }

    [Fact]
    public async Task ManifestOnlyBundles_AreDiscoverableListableAndUninstallable()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-bundle-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");
        var bundleDir = Path.Combine(extensionsDir, "docs.full");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(bundleDir);

        var manifest = new ExtensionManifestFile
        {
            Id = "docs.full",
            Name = "Docs Full",
            Version = "1.2.3",
            Kind = "bundle",
            Description = "Installs the full docs stack.",
            Dependencies = new Dictionary<string, string>
            {
                ["docs.core"] = ">=1.0.0",
                ["docs.search"] = ">=1.0.0",
            },
            Categories = ["docs"],
        };

        await File.WriteAllTextAsync(
            Path.Combine(bundleDir, "extension.json"),
            JsonSerializer.Serialize(manifest));

        try
        {
            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = dataDir,
                CoveVersion = "1.0.0",
            });

            manager.DiscoverExtensions(extensionsDir);

            Assert.True(manager.IsManifestOnlyExtension("docs.full"));
            var install = Assert.IsType<ExtensionInstallation>(manager.GetInstallation("docs.full"));
            Assert.Equal("1.2.3", install.Version);

            var controller = CreateController(manager, new ServiceCollection().BuildServiceProvider());

            var listResult = controller.GetExtensions();
            var ok = Assert.IsType<OkObjectResult>(listResult.Result);
            var items = Assert.IsAssignableFrom<IEnumerable<ExtensionInfo>>(ok.Value);
            var bundle = Assert.Single(items);
            Assert.Equal("docs.full", bundle.Id);
            Assert.Equal("bundle", bundle.Kind);
            Assert.Equal(2, bundle.Dependencies.Count);

            var uninstallResult = await controller.RegistryUninstall(new RegistryUninstallRequest { ExtensionId = "docs.full" });
            Assert.IsType<OkObjectResult>(uninstallResult);
            Assert.Null(manager.GetInstallation("docs.full"));
            Assert.False(Directory.Exists(bundleDir));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

        [Fact]
        public async Task AggregatedManifest_IncludesManifestDeclaredTutorialTopics()
        {
                var root = Path.Combine(Path.GetTempPath(), $"cove-manual-topics-{Guid.NewGuid():N}");
                var dataDir = Path.Combine(root, "data");
                var extensionsDir = Path.Combine(root, "extensions");
                var bundleDir = Path.Combine(extensionsDir, "docs.bundle");

                Directory.CreateDirectory(dataDir);
                Directory.CreateDirectory(bundleDir);

                var manifestJson = """
                {
                    "id": "docs.bundle",
                    "name": "Docs Bundle",
                    "version": "1.0.0",
                    "kind": "bundle",
                    "tutorialTopics": [
                        {
                            "id": "docs.root",
                            "title": "Docs Root",
                            "description": "Top-level manual topic.",
                            "contexts": ["settings-tab:extensions/docs", "pane:docs.example"],
                            "order": 10,
                            "slides": [
                                {
                                    "id": "overview",
                                    "title": "Overview",
                                    "caption": "Read this first.",
                                    "bodyMarkdown": "Use **manual pages** for extension docs instead of external metadata fields.",
                                    "points": ["Open the manual"],
                                    "imageSrc": "docs/overview.png",
                                    "links": [{ "label": "External guide", "url": "https://example.com/docs" }]
                                }
                            ]
                        },
                        {
                            "id": "docs.child",
                            "title": "Docs Child",
                            "parentTopicId": "docs.root",
                            "order": 11,
                            "slides": [
                                {
                                    "id": "child",
                                    "title": "Child topic",
                                    "caption": "Nested manual page.",
                                    "points": ["Stay inside Cove"]
                                }
                            ]
                        }
                    ]
                }
                """;

                await File.WriteAllTextAsync(Path.Combine(bundleDir, "extension.json"), manifestJson);

                try
                {
                        var manager = new ExtensionManager(new ExtensionContext
                        {
                                Configuration = new ConfigurationBuilder().Build(),
                                DataDirectory = dataDir,
                                CoveVersion = "1.0.0",
                        });

                        manager.DiscoverExtensions(extensionsDir);

                        var manifest = manager.GetAggregatedManifest();
                        var rootTopic = Assert.Single(manifest.TutorialTopics, topic => topic.Id == "docs.root");
                        var childTopic = Assert.Single(manifest.TutorialTopics, topic => topic.Id == "docs.child");

                        Assert.Equal("docs.bundle", rootTopic.ExtensionId);
                        Assert.Equal("docs.root", childTopic.ParentTopicId);
                        Assert.Contains("settings-tab:extensions/docs", rootTopic.Contexts!);
                        Assert.Contains("pane:docs.example", rootTopic.Contexts!);
                        Assert.Equal("Use **manual pages** for extension docs instead of external metadata fields.", rootTopic.Slides![0].BodyMarkdown);
                        Assert.Equal("docs/overview.png", rootTopic.Slides![0].ImageSrc);
                        var link = Assert.Single(rootTopic.Slides![0].Links!);
                        Assert.Equal("External guide", link.Label);
                        Assert.Equal("https://example.com/docs", link.Url);
                }
                finally
                {
                        if (Directory.Exists(root))
                        {
                                Directory.Delete(root, recursive: true);
                        }
                }
        }

    [Fact]
    public async Task DisableExtensionAsync_DisablesEnabledTransitiveDependents()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new TestExtension("base", "Base"), "local");
        manager.Register(new TestExtension("middle", "Middle", new Dictionary<string, string> { ["base"] = ">=1.0.0" }), "local");
        manager.Register(new TestExtension("leaf", "Leaf", new Dictionary<string, string> { ["middle"] = ">=1.0.0" }), "local");

        var disabled = await manager.DisableExtensionAsync("base");

        Assert.Equal(["base", "leaf", "middle"], disabled.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.False(manager.IsEnabled("base"));
        Assert.False(manager.IsEnabled("middle"));
        Assert.False(manager.IsEnabled("leaf"));
    }

    [Fact]
    public async Task EnableExtensionAsync_EnablesDisabledTransitiveDependenciesFirst()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new TestExtension("base", "Base"), "local");
        manager.Register(new TestExtension("middle", "Middle", new Dictionary<string, string> { ["base"] = ">=1.0.0" }), "local");
        manager.Register(new TestExtension("leaf", "Leaf", new Dictionary<string, string> { ["middle"] = ">=1.0.0" }), "local");

        await manager.DisableExtensionAsync("base");
        var enabled = await manager.EnableExtensionAsync("leaf");

        Assert.Equal(["base", "middle", "leaf"], enabled.ToArray());
        Assert.True(manager.IsEnabled("base"));
        Assert.True(manager.IsEnabled("middle"));
        Assert.True(manager.IsEnabled("leaf"));
    }

    [Fact]
    public async Task RegistryUninstall_RequiresConfirmationBeforeRemovingDependents()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-dependent-uninstall-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");
        var baseDir = Path.Combine(extensionsDir, "base.pack");
        var dependentDir = Path.Combine(extensionsDir, "dependent.bundle");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(baseDir);
        Directory.CreateDirectory(dependentDir);

        await File.WriteAllTextAsync(Path.Combine(baseDir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = "base.pack",
            Name = "Base Pack",
            Version = "1.0.0",
            Kind = "scraper-pack",
        }));

        await File.WriteAllTextAsync(Path.Combine(dependentDir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = "dependent.bundle",
            Name = "Dependent Bundle",
            Version = "1.0.0",
            Kind = "bundle",
            Dependencies = new Dictionary<string, string> { ["base.pack"] = ">=1.0.0" },
        }));

        try
        {
            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = dataDir,
                CoveVersion = "1.0.0",
            });

            manager.DiscoverExtensions(extensionsDir);
            var controller = CreateController(manager, new ServiceCollection().BuildServiceProvider());

            var previewResult = await controller.RegistryUninstall(new RegistryUninstallRequest { ExtensionId = "base.pack" });
            var previewOk = Assert.IsType<OkObjectResult>(previewResult);
            var preview = JsonSerializer.SerializeToElement(previewOk.Value);
            Assert.True(preview.GetProperty("requiresDependents").GetBoolean());
            Assert.Equal("dependent.bundle", preview.GetProperty("dependents")[0].GetProperty("Id").GetString());
            Assert.True(Directory.Exists(baseDir));
            Assert.True(Directory.Exists(dependentDir));

            var uninstallResult = await controller.RegistryUninstall(new RegistryUninstallRequest
            {
                ExtensionId = "base.pack",
                UninstallDependents = true,
            });
            Assert.IsType<OkObjectResult>(uninstallResult);
            Assert.False(Directory.Exists(baseDir));
            Assert.False(Directory.Exists(dependentDir));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class RuntimeCategoryFallbackExtension : IExtension
    {
        public const string ExtensionId = "com.example.runtime-category-fallback";

        public string Id => ExtensionId;
        public string Name => "Runtime Category Fallback";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }

    private sealed class SettingsTabContributionExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.settings-tab";

        public override string Id => ExtensionId;
        public override string Name => "Settings Tab Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest()
            => ManifestBuilder()
                .AddSettingsTab(
                    "extensions/example",
                    "Example",
                    description: "Example settings tab from a normal extension.")
                .AddSettingsSection("extensions/example", "Example Settings", "ExampleSettingsPanel")
                .Build();
    }

    private sealed class SettingsPageContributionExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.settings-page";

        public override string Id => ExtensionId;
        public override string Name => "Settings Page Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest()
            => ManifestBuilder()
                .AddSettingsTab(
                    "extensions/example-page",
                    "Example Page",
                    description: "A full-page settings tab owned by the extension.",
                    layout: SettingsTabLayout.Page)
                .AddSettingsSection("extensions/example-page", "Example Page", "ExamplePage")
                .Build();
    }

    private sealed class ListContributionExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.list-contribution";

        public override string Id => ExtensionId;
        public override string Name => "List Contribution Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest()
            => ManifestBuilder()
                .AddCustomFieldListFilter("videos", "video-quality-filter", "Quality Score", "quality_score", "number", order: 10)
                .AddCustomFieldListSort("videos", "video-quality-sort", "Quality Score", "quality_score", "number", order: 10)
                .Build();
    }

    private sealed class TabContributionExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.tab-contribution";

        public override string Id => ExtensionId;
        public override string Name => "Tab Contribution Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest()
            => ManifestBuilder()
                .AddTab(
                    "video",
                    "related",
                    "Related",
                    "RelatedTab",
                    manualContexts: ["panel:related-media", "feature:example.detail"])
                .Build();
    }

    private sealed class TestExtension(
        string id,
        string name,
        IReadOnlyDictionary<string, string>? dependencies = null) : IExtension
    {
        public string Id => id;
        public string Name => name;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public IReadOnlyDictionary<string, string> Dependencies { get; } = dependencies ?? new Dictionary<string, string>();

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }

    private static ExtensionsController CreateController(ExtensionManager manager, IServiceProvider? requestServices = null)
    {
        var controller = new ExtensionsController(
            manager,
            new ScraperService(new CoveConfiguration(), NullLogger<ScraperService>.Instance, new TestHttpClientFactory(), manager));

        if (requestServices != null)
        {
            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    RequestServices = requestServices,
                },
            };
        }

        return controller;
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
