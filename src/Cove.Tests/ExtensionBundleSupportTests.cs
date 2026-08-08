using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Plugins;
using Cove.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class ExtensionBundleSupportTests
{
    [Fact]
    public async Task Extension_migrations_execute_literal_braces_without_composite_formatting()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(options => options.UseSqlite(connection));
        services.AddScoped<DbContext>(provider => provider.GetRequiredService<CoveContext>());
        await using var provider = services.BuildServiceProvider();

        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new LiteralBraceMigrationExtension());

        Assert.True(await manager.InitializeExtensionAsync(
            LiteralBraceMigrationExtension.ExtensionId,
            provider));

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'brace_migration'";
        var schema = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Contains("DEFAULT '{}'", schema);
    }

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
    public void Enabled_runtime_extension_metadata_is_available_before_provider_build()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        manager.CaptureHostServices(serviceCollection);
        using var services = serviceCollection.BuildServiceProvider();
        manager.PrepareRuntimeServices(services);

        var extension = new ComponentOverrideExtension("runtime.pending", "PendingComponent");
        manager.Register(extension, "local");
        var overlayIdsField = typeof(ExtensionManager).GetField(
            "_overlayExtensionIds",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var overlayIds = Assert.IsType<HashSet<string>>(overlayIdsField?.GetValue(manager));
        overlayIds.Add(extension.Id);

        Assert.True(manager.IsEnabled(extension.Id));
        Assert.Same(extension, manager.GetExtension(extension.Id));
        var componentOverride = Assert.Single(manager.GetAggregatedManifest().ComponentOverrides);
        Assert.Equal("PendingComponent", componentOverride.ComponentName);

        var controller = CreateController(manager, services);
        var result = controller.GetExtensions();
        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var info = Assert.Single(Assert.IsAssignableFrom<IEnumerable<ExtensionInfo>>(ok.Value));
        Assert.Equal(extension.Id, info.Id);
    }

    [Fact]
    public async Task Extension_registry_properties_return_stable_snapshots()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new ComponentOverrideExtension("a.extension", "AlphaComponent"), "local");

        var extensions = manager.Extensions;
        var installations = manager.Installations;

        manager.Register(new ComponentOverrideExtension("b.extension", "BetaComponent"), "local");
        await manager.SetInstallationSourceAsync("a.extension", "registry");

        Assert.Single(extensions);
        Assert.DoesNotContain(extensions, extension => extension.Id == "b.extension");
        Assert.Single(installations);
        Assert.DoesNotContain("b.extension", installations.Keys);
        Assert.Equal("local", installations["a.extension"].Source);
    }

    [Fact]
    public async Task Extension_registry_supports_concurrent_registration_and_snapshot_reads()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new ComponentOverrideExtension("seed.extension", "SeedComponent"), "local");

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writers = Enumerable.Range(0, 100).Select(index => Task.Run(async () =>
        {
            await start.Task;
            manager.Register(
                new ComponentOverrideExtension($"extension.{index}", $"Component{index}"),
                "local");
        }));
        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(async () =>
        {
            await start.Task;
            for (var iteration = 0; iteration < 100; iteration++)
            {
                foreach (var extension in manager.Extensions)
                    Assert.Same(extension, manager.GetExtension(extension.Id));
                _ = manager.GetInitializationOrder();
                _ = manager.ValidateDependencies();
                _ = manager.Installations.Values.Select(installation => installation.ExtensionId).ToArray();
            }
        }));

        start.SetResult();
        await Task.WhenAll(writers.Concat(readers));

        Assert.Equal(101, manager.Extensions.Count);
        Assert.Equal(101, manager.Installations.Count);
    }

    [Fact]
    public async Task Extension_registry_supports_concurrent_unload_and_snapshot_reads()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extensionIds = Enumerable.Range(0, 100)
            .Select(index => $"extension.{index}")
            .ToArray();
        foreach (var extensionId in extensionIds)
            manager.Register(new ComponentOverrideExtension(extensionId, $"Component{extensionId}"), "local");

        using var services = new ServiceCollection().BuildServiceProvider();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var unloaders = extensionIds.Select(extensionId => Task.Run(async () =>
        {
            await start.Task;
            Assert.True(await manager.UnloadExtensionAsync(extensionId, services));
        }));
        var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(async () =>
        {
            await start.Task;
            for (var iteration = 0; iteration < 100; iteration++)
            {
                _ = manager.Extensions.Select(extension => extension.Id).ToArray();
                _ = manager.GetInitializationOrder();
                _ = manager.ValidateDependencies();
                _ = manager.Installations.Values.Select(installation => installation.ExtensionId).ToArray();
            }
        }));

        start.SetResult();
        await Task.WhenAll(unloaders.Concat(readers));

        Assert.Empty(manager.Extensions);
        Assert.Empty(manager.Installations);
    }

    [Fact]
    public async Task Same_id_registration_is_rejected_during_in_flight_unload()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var existing = new BlockingUninstallExtension();
        manager.Register(existing, "local");
        using var services = new ServiceCollection().BuildServiceProvider();

        var unload = manager.UnloadExtensionAsync(existing.Id, services);
        await existing.UninstallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var replacement = new ComponentOverrideExtension(existing.Id, "ReplacementComponent");
        var error = Assert.Throws<InvalidOperationException>(() => manager.Register(replacement, "local"));
        Assert.Contains("currently being unloaded", error.Message);

        existing.ReleaseUninstall.TrySetResult();
        Assert.True(await unload.WaitAsync(TimeSpan.FromSeconds(5)));
        manager.Register(replacement, "local");

        Assert.Same(replacement, manager.GetExtension(existing.Id));
        Assert.True(manager.Installations.ContainsKey(existing.Id));
    }

    [Fact]
    public async Task Concurrent_dependent_registration_inherits_prepublished_disabled_state()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        const string dependencyId = "base.extension";
        manager.Register(new ComponentOverrideExtension(dependencyId, "BaseComponent"), "local");
        var existingDependent = new BlockingShutdownExtension("existing.dependent", dependencyId);
        manager.Register(existingDependent, "local");

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        using var services = serviceCollection.BuildServiceProvider();
        await manager.InitializeAllAsync(services);

        var disable = manager.DisableExtensionAsync(dependencyId);
        await existingDependent.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var newDependent = new DependentExtension("new.dependent", dependencyId);
        var registrationAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registerDependent = Task.Run(() =>
        {
            registrationAttempted.TrySetResult();
            manager.Register(newDependent, "local");
        });
        await registrationAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await registerDependent.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(disable.IsCompleted);
        Assert.False(manager.IsEnabled(newDependent.Id));
        existingDependent.ReleaseShutdown.TrySetResult();
        await disable.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(manager.IsEnabled(dependencyId));
        Assert.False(manager.IsEnabled(newDependent.Id));
    }

    [Fact]
    public async Task Pre_canceled_disable_does_not_publish_partial_state()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        const string dependencyId = "base.extension";
        var dependent = new DependentExtension("dependent.extension", dependencyId);
        manager.Register(new ComponentOverrideExtension(dependencyId, "BaseComponent"), "local");
        manager.Register(dependent, "local");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DisableExtensionAsync(dependencyId, cancellation.Token));

        Assert.True(manager.IsEnabled(dependencyId));
        Assert.True(manager.IsEnabled(dependent.Id));
    }

    [Fact]
    public async Task Startup_replacement_propagates_new_disabled_dependency_state()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        const string dependencyId = "base.extension";
        const string replacementId = "replacement.extension";
        manager.Register(new ComponentOverrideExtension(dependencyId, "BaseComponent"), "local");
        manager.Register(new ComponentOverrideExtension(replacementId, "OriginalComponent"), "local");
        var dependent = new DependentExtension("dependent.extension", replacementId);
        manager.Register(dependent, "local");

        await manager.DisableExtensionAsync(dependencyId);
        Assert.True(manager.IsEnabled(replacementId));
        Assert.True(manager.IsEnabled(dependent.Id));

        var replacement = new DependentExtension(replacementId, dependencyId);
        manager.Register(replacement, "local");

        Assert.Same(replacement, manager.GetExtension(replacementId));
        Assert.False(manager.IsEnabled(replacementId));
        Assert.False(manager.IsEnabled(dependent.Id));
    }

    [Fact]
    public async Task Runtime_registration_rejects_replacement_without_unload()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        const string extensionId = "replacement.extension";
        var existing = new ComponentOverrideExtension(extensionId, "OriginalComponent");
        manager.Register(existing, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        await manager.InitializeAllAsync(services);

        var error = Assert.Throws<InvalidOperationException>(() =>
            manager.Register(new ComponentOverrideExtension(extensionId, "ReplacementComponent"), "local"));

        Assert.Contains("Unload it before registering a replacement", error.Message);
        Assert.Same(existing, manager.GetExtension(extensionId));
    }

    [Fact]
    public async Task Unload_shuts_down_and_persists_disabled_dependents_before_removing_dependency()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        const string dependencyId = "base.extension";
        manager.Register(new ComponentOverrideExtension(dependencyId, "BaseComponent"), "local");
        var dependent = new BlockingShutdownExtension("dependent.extension", dependencyId);
        manager.Register(dependent, "local");

        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging();
        using var services = serviceCollection.BuildServiceProvider();
        await manager.InitializeAllAsync(services);

        var unload = manager.UnloadExtensionAsync(dependencyId, services);
        await dependent.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrentUnload = manager.UnloadExtensionAsync(dependencyId, services);
        var enableWhileUnloading = await manager.EnableExtensionAsync(dependent.Id);

        Assert.False(unload.IsCompleted);
        Assert.False(concurrentUnload.IsCompleted);
        Assert.Empty(enableWhileUnloading);
        Assert.False(manager.IsEnabled(dependent.Id));
        Assert.NotNull(manager.GetExtension(dependencyId));

        dependent.ReleaseShutdown.TrySetResult();
        Assert.True(await unload.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await concurrentUnload.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Null(manager.GetExtension(dependencyId));
        Assert.False(manager.IsEnabled(dependent.Id));
        Assert.False(await manager.EnsureExtensionInitializedAsync(dependent.Id));
    }

    [Fact]
    public async Task GetManifest_DescribesEachExtensionBundleWithInstalledVersionAndVersionedAssets()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-ui-bundles-{Guid.NewGuid():N}");
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");

        Directory.CreateDirectory(dataDir);
        Directory.CreateDirectory(extensionsDir);

        var alpha = await WriteUiBundleAsync(
            extensionsDir,
            "com.example.alpha",
            "2.3.4",
            new DateTime(2026, 7, 11, 1, 2, 3, DateTimeKind.Utc),
            new DateTime(2026, 7, 11, 1, 2, 4, DateTimeKind.Utc));
        var beta = await WriteUiBundleAsync(
            extensionsDir,
            "com.example.beta",
            "5.6.7",
            new DateTime(2026, 7, 11, 1, 2, 5, DateTimeKind.Utc),
            new DateTime(2026, 7, 11, 1, 2, 6, DateTimeKind.Utc));

        try
        {
            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = dataDir,
                CoveVersion = "1.0.0",
            });

            manager.DiscoverExtensions(extensionsDir);
            manager.Register(new ComponentOverrideExtension(alpha.ExtensionId, "AlphaComponent"), "local");
            manager.Register(new ComponentOverrideExtension(beta.ExtensionId, "BetaComponent"), "local");

            var controller = CreateController(manager);
            var result = controller.GetManifest();
            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var manifest = Assert.IsType<UIManifest>(ok.Value);

            var descriptors = manifest.ExtensionBundles;
            Assert.Collection(
                descriptors.OrderBy(item => item.ExtensionId, StringComparer.Ordinal),
                descriptor => AssertBundleDescriptor(descriptor, alpha),
                descriptor => AssertBundleDescriptor(descriptor, beta));
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
    public void AggregatedManifest_OrdersEqualPriorityComponentOverridesByExtensionAndComponentName()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });

        manager.Register(new ComponentOverrideExtension("z.extension", "MiddleComponent"), "local");
        manager.Register(new ComponentOverrideExtension("a.extension", "ZetaComponent", "AlphaComponent"), "local");

        var manifest = manager.GetAggregatedManifest();

        Assert.Equal(
            [
                ("a.extension", "AlphaComponent"),
                ("a.extension", "ZetaComponent"),
                ("z.extension", "MiddleComponent"),
            ],
            manifest.ComponentOverrides
                .Select(componentOverride => (componentOverride.ExtensionId, componentOverride.ComponentName))
                .ToArray());
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
    public void AggregatedManifest_stamps_executable_filter_with_actual_extension_owner()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new SpoofedExecutableFilterExtension(), "local");

        var filter = Assert.Single(manager.GetAggregatedManifest().ListFilters);

        Assert.Equal(SpoofedExecutableFilterExtension.ExtensionId, filter.ExtensionId);
        Assert.Equal("owned-filter", filter.FilterId);
        Assert.NotEqual("victim.extension", filter.ExtensionId);
    }

    [Fact]
    public void Executable_filter_property_preserves_the_original_positional_record_abi()
    {
        var constructor = Assert.Single(typeof(UIListFilterContribution).GetConstructors());
        Assert.Equal(12, constructor.GetParameters().Length);
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.Name == "FilterId");

        var deconstruct = Assert.Single(
            typeof(UIListFilterContribution).GetMethods(),
            method => method.Name == "Deconstruct");
        Assert.Equal(12, deconstruct.GetParameters().Length);
        Assert.True(typeof(UIListFilterContribution).GetProperty(nameof(UIListFilterContribution.FilterId))?.CanWrite);
    }

    [Fact]
    public void Executable_filters_support_generic_entity_types_in_the_sdk_and_aggregated_manifest()
    {
        var builder = new UIManifestBuilder("com.example.builder");
        Assert.Throws<ArgumentException>(() => builder.AddExtensionListFilter(
            " ", "owned", "Owned", "boolean", "owned-filter"));

        var normalized = new UIManifestBuilder("com.example.builder")
            .AddExtensionListFilter(
                " Segments ",
                " owned ",
                " Owned ",
                " BOOLEAN ",
                " owned-filter ",
                modifiers: [" equals ", "equals", " notEquals ", "includesAll", "is-null", " "])
            .Build();
        var normalizedFilter = Assert.Single(normalized.ListFilters);
        Assert.Equal("segments", normalizedFilter.EntityType);
        Assert.Equal("owned", normalizedFilter.Id);
        Assert.Equal("boolean", normalizedFilter.CriterionType);
        Assert.Equal("owned-filter", normalizedFilter.FilterId);
        Assert.Equal(["EQUALS", "NOT_EQUALS", "INCLUDES_ALL", "IS_NULL"], normalizedFilter.Modifiers);

        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        manager.Register(new NonTagExecutableFilterExtension(), "local");

        var aggregated = Assert.Single(manager.GetAggregatedManifest().ListFilters);
        Assert.Equal("videos", aggregated.EntityType);
        Assert.Equal(NonTagExecutableFilterExtension.ExtensionId, aggregated.ExtensionId);
        Assert.Equal("owned-filter", aggregated.FilterId);
    }

    [Fact]
    public async Task Entity_filter_runtime_executes_non_tag_predicates_on_the_generic_lease()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var provider = new MatchingFilterProvider(11, "segments");
        var extension = new FilterLifecycleExtension(
            provider,
            "segments",
            registerConcreteAlias: true);
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var services = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(services);
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));
        Assert.True(RebuildRuntimeProvider(manager, extension.Id.ToUpperInvariant()));

        var runtime = new ExtensionEntityFilterRuntime(manager);
        using var execution = Assert.IsAssignableFrom<IExtensionEntityFilterExecution>(
            await runtime.OpenEntityFilterAsync(extension.Id, "segments", "owned-filter", default));
        var result = await execution.ResolveAsync(
            new ExtensionEntityFilterRequest(
                extension.Id,
                "segments",
                "owned-filter",
                "equals",
                JsonSerializer.SerializeToElement(true),
                [11, 12],
                new ExtensionFilterPrincipal(null, "system", "System", [], ["*"])),
            default);

        Assert.Equal("segments", execution.Declaration.EntityType);
        Assert.Equal(extension.Id, execution.Declaration.ExtensionId);
        Assert.Equal([11], result.MatchingEntityIds);

        await manager.DisableExtensionAsync(extension.Id);
    }

    [Fact]
    public async Task Built_in_entity_filter_providers_are_resolved_by_extension_owner()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var first = new FilterLifecycleExtension(
            new MatchingFilterProvider(1),
            extensionId: "com.example.first-filter",
            registrationKey: "com.example.second-filter",
            registerConcreteAlias: true);
        var second = new FilterLifecycleExtension(
            new MatchingFilterProvider(2),
            extensionId: "com.example.second-filter",
            registrationKey: "com.example.first-filter");
        manager.Register(first, "local");
        manager.Register(second, "local");
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.ConfigureServices(hostServices);
        using var services = hostServices.BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(first.Id, services));
        Assert.True(await manager.InitializeExtensionAsync(second.Id, services));

        var runtime = new ExtensionEntityFilterRuntime(manager);
        using var firstExecution = Assert.IsAssignableFrom<IExtensionEntityFilterExecution>(
            await runtime.OpenEntityFilterAsync(first.Id, "tags", "owned-filter", default));
        using var secondExecution = Assert.IsAssignableFrom<IExtensionEntityFilterExecution>(
            await runtime.OpenEntityFilterAsync(second.Id, "tags", "owned-filter", default));
        var request = new ExtensionEntityFilterRequest(
            first.Id,
            "tags",
            "owned-filter",
            "equals",
            JsonSerializer.SerializeToElement(true),
            [1, 2],
            new ExtensionFilterPrincipal(null, "system", "System", [], ["*"]));

        var firstResult = await firstExecution.ResolveAsync(request, default);
        var secondResult = await secondExecution.ResolveAsync(request with { ExtensionId = second.Id }, default);

        Assert.Equal([1], firstResult.MatchingEntityIds);
        Assert.Equal([2], secondResult.MatchingEntityIds);
    }


    [Fact]
    public void GetExtensions_UsesManifestCategoriesForLoadedExtensions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-category-manifest-{Guid.NewGuid():N}");
        var extensionDir = Path.Combine(root, RuntimeCategoryFallbackExtension.ExtensionId);
        Directory.CreateDirectory(extensionDir);
        File.WriteAllText(Path.Combine(extensionDir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = RuntimeCategoryFallbackExtension.ExtensionId,
            Name = "Runtime Category Fallback",
            Version = "1.0.0",
            Categories = ["scraper", "metadata"],
        }));

        try
        {
            var manager = new ExtensionManager(new ExtensionContext
            {
                Configuration = new ConfigurationBuilder().Build(),
                DataDirectory = root,
                CoveVersion = "1.0.0",
            });
            manager.DiscoverExtensions(root);
            manager.Register(new RuntimeCategoryFallbackExtension(), "local");

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
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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

            var uninstallResult = await controller.RegistryUninstall(
                new RegistryUninstallRequest { ExtensionId = "docs.full" });
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
    public async Task DisableExtensionAsync_ShutsDownOnceAndReenableInitializesAgain()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new LifecycleExtension("lifecycle");
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();

        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        await manager.DisableExtensionAsync(extension.Id);
        await manager.DisableExtensionAsync(extension.Id);

        Assert.Equal(1, extension.InitializeCount);
        Assert.Equal(1, extension.ShutdownCount);

        await manager.EnableExtensionAsync(extension.Id);
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        Assert.Equal(2, extension.InitializeCount);
        Assert.Equal(1, extension.ShutdownCount);
        Assert.Equal(["initialize", "shutdown", "initialize"], extension.Events);
    }

    [Fact]
    public async Task DisableExtensionAsync_retires_without_waiting_for_inflight_filter_provider()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var provider = new BlockingFilterProvider();
        var extension = new FilterLifecycleExtension(provider);
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var services = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(services);
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        var runtime = new ExtensionEntityFilterRuntime(manager);
        var execution = Assert.IsAssignableFrom<IExtensionEntityFilterExecution>(
            await runtime.OpenEntityFilterAsync(extension.Id, "tags", "owned-filter", default));
        var resolve = execution.ResolveAsync(
            new ExtensionEntityFilterRequest(
                extension.Id,
                "tags",
                "owned-filter",
                "equals",
                JsonSerializer.SerializeToElement(true),
                [1],
                new ExtensionFilterPrincipal(null, "system", "System", [], ["*"])),
            default);
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disable = manager.DisableExtensionAsync(extension.Id);
        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, extension.ShutdownCount);
        Assert.Equal(0, provider.DisposeCount);

        execution.Dispose();
        await Task.Delay(50);
        Assert.False(resolve.IsCompleted);
        Assert.Equal(0, provider.DisposeCount);

        provider.Release.TrySetResult();
        var result = await resolve;

        Assert.Equal([1], result.MatchingEntityIds);
        Assert.Equal(1, extension.ShutdownCount);
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task Timed_out_filter_execution_drains_its_retired_provider_after_late_completion()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var provider = new BlockingFilterProvider(observeCancellation: false);
        var extension = new FilterLifecycleExtension(provider);
        manager.Register(extension, "local");
        MarkAsRuntimeExtension(manager, extension.Id);
        var hostServices = new ServiceCollection();
        hostServices.AddLogging();
        manager.CaptureHostServices(hostServices);
        using var services = hostServices.BuildServiceProvider();
        manager.PrepareRuntimeServices(services);
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));
        var filters = new ExtensionEntityFilterService(
            new ExtensionEntityFilterRuntime(manager),
            providerTimeout: TimeSpan.FromMilliseconds(100));

        var error = await Assert.ThrowsAsync<ExtensionEntityFilterProviderException>(() =>
            filters.ApplyAsync(
                "tags",
                [new ExtensionFilterCriterion
                {
                    ExtensionId = extension.Id,
                    FilterId = "owned-filter",
                    Modifier = "equals",
                    Value = JsonSerializer.SerializeToElement(true),
                }],
                [1],
                CovePrincipal.System(),
                default));

        Assert.Contains("timed out", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(provider.Entered.Task.IsCompletedSuccessfully);
        Assert.Equal(0, provider.DisposeCount);

        await manager.DisableExtensionAsync(extension.Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, extension.ShutdownCount);
        Assert.Equal(0, provider.DisposeCount);

        provider.Release.TrySetResult();
        await provider.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, provider.DisposeCount);
    }

    [Fact]
    public async Task DisableExtensionAsync_ShutsDownDependentsBeforeDependencies()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var shutdownOrder = new List<string>();
        var baseExtension = new LifecycleExtension("base", shutdownOrder: shutdownOrder);
        var middleExtension = new LifecycleExtension(
            "middle",
            new Dictionary<string, string> { ["base"] = ">=1.0.0" },
            shutdownOrder);
        var leafExtension = new LifecycleExtension(
            "leaf",
            new Dictionary<string, string> { ["middle"] = ">=1.0.0" },
            shutdownOrder);
        manager.Register(baseExtension, "local");
        manager.Register(middleExtension, "local");
        manager.Register(leafExtension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(baseExtension.Id, services));
        Assert.True(await manager.InitializeExtensionAsync(middleExtension.Id, services));
        Assert.True(await manager.InitializeExtensionAsync(leafExtension.Id, services));

        await manager.DisableExtensionAsync(baseExtension.Id);

        Assert.Equal(["leaf", "middle", "base"], shutdownOrder);
    }

    [Fact]
    public async Task DisableExtensionAsync_ShutdownFailureDoesNotPreventReinitializationOrDependentCleanup()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var shutdownOrder = new List<string>();
        var baseExtension = new LifecycleExtension("base", shutdownOrder: shutdownOrder);
        var failingDependent = new LifecycleExtension(
            "dependent",
            new Dictionary<string, string> { ["base"] = ">=1.0.0" },
            shutdownOrder,
            throwOnShutdown: true);
        manager.Register(baseExtension, "local");
        manager.Register(failingDependent, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(baseExtension.Id, services));
        Assert.True(await manager.InitializeExtensionAsync(failingDependent.Id, services));

        await manager.DisableExtensionAsync(baseExtension.Id);
        await manager.DisableExtensionAsync(baseExtension.Id);

        Assert.Equal(["dependent", "base"], shutdownOrder);
        Assert.Equal(1, failingDependent.ShutdownCount);
        Assert.Equal(1, baseExtension.ShutdownCount);

        await manager.EnableExtensionAsync(failingDependent.Id);
        Assert.True(await manager.InitializeExtensionAsync(baseExtension.Id, services));
        Assert.True(await manager.InitializeExtensionAsync(failingDependent.Id, services));

        Assert.Equal(2, baseExtension.InitializeCount);
        Assert.Equal(2, failingDependent.InitializeCount);
    }

    [Fact]
    public async Task ShutdownAllAsync_DoesNotShutdownAnExtensionAgainAfterDisable()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new LifecycleExtension("lifecycle");
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        await manager.DisableExtensionAsync(extension.Id);
        await manager.ShutdownAllAsync();

        Assert.Equal(1, extension.ShutdownCount);
    }

    [Fact]
    public async Task InitializeExtensionAsync_SerializesConcurrentInitialization()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new BlockingLifecycleExtension("concurrent-initialize", blockInitialize: true);
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();

        var first = manager.InitializeExtensionAsync(extension.Id, services);
        await extension.InitializeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = manager.InitializeExtensionAsync(extension.Id, services);

        Assert.Equal(1, extension.InitializeCount);

        extension.ReleaseInitialize();

        Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, extension.InitializeCount);
    }

    [Fact]
    public async Task InitializeAllAsync_DoesNotReinitializeRunningExtensions()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new LifecycleExtension("reload-idempotence");
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        await manager.InitializeAllAsync(services);

        Assert.Equal(1, extension.InitializeCount);
        Assert.Equal(0, extension.ShutdownCount);
    }

    [Fact]
    public async Task InitializeAllAsync_WaitsForConcurrentDisableAndDoesNotReinitialize()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new BlockingLifecycleExtension("reload-disable", blockShutdown: true);
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        var disable = manager.DisableExtensionAsync(extension.Id);
        await extension.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reload = manager.InitializeAllAsync(services);

        Assert.False(reload.IsCompleted);
        Assert.Equal(1, extension.InitializeCount);

        extension.ReleaseShutdown();

        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        await reload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(manager.IsEnabled(extension.Id));
        Assert.Equal(1, extension.InitializeCount);
        Assert.Equal(1, extension.ShutdownCount);
    }

    [Fact]
    public async Task DisableExtensionAsync_WaitsForInitializationThenShutsDown()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new BlockingLifecycleExtension("initialize-disable", blockInitialize: true);
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();

        var initialize = manager.InitializeExtensionAsync(extension.Id, services);
        await extension.InitializeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var disable = manager.DisableExtensionAsync(extension.Id);

        Assert.False(disable.IsCompleted);
        Assert.Equal(0, extension.ShutdownCount);

        extension.ReleaseInitialize();

        Assert.True(await initialize.WaitAsync(TimeSpan.FromSeconds(5)));
        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["initialize", "shutdown"], extension.Events);
        Assert.Equal(1, extension.InitializeCount);
        Assert.Equal(1, extension.ShutdownCount);
        Assert.False(manager.IsEnabled(extension.Id));
    }

    [Fact]
    public async Task EnableExtensionAsync_WaitsForInProgressShutdown()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new BlockingLifecycleExtension("disable-enable", blockShutdown: true);
        manager.Register(extension, "local");
        using var services = new ServiceCollection().BuildServiceProvider();
        Assert.True(await manager.InitializeExtensionAsync(extension.Id, services));

        var disable = manager.DisableExtensionAsync(extension.Id);
        await extension.ShutdownEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var enable = manager.EnableExtensionAsync(extension.Id);

        Assert.False(enable.IsCompleted);

        extension.ReleaseShutdown();

        await disable.WaitAsync(TimeSpan.FromSeconds(5));
        await enable.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(manager.IsEnabled(extension.Id));
        Assert.Equal(1, extension.ShutdownCount);
    }

    [Fact]
    public async Task InitializeExtensionAsync_ShutsDownWhenEndpointPublicationFailsAfterInitialization()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "1.0.0",
        });
        var extension = new FailingEndpointLifecycleExtension("failing-endpoint-publication");
        manager.Register(extension, "local");
        await using var app = WebApplication.CreateBuilder().Build();
        manager.SetRouteBuilder(app);
        manager.SetupDynamicEndpoints();

        Assert.False(await manager.InitializeExtensionAsync(extension.Id, app.Services));
        await extension.WorkerStopped.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, extension.InitializeCount);
        Assert.Equal(1, extension.ShutdownCount);
        Assert.False(manager.IsEnabled(extension.Id));
        Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints));
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

            var previewResult = await controller.RegistryUninstall(
                new RegistryUninstallRequest { ExtensionId = "base.pack" });
            var previewOk = Assert.IsType<OkObjectResult>(previewResult);
            var preview = JsonSerializer.SerializeToElement(previewOk.Value);
            Assert.True(preview.GetProperty("requiresDependents").GetBoolean());
            Assert.Equal("dependent.bundle", preview.GetProperty("dependents")[0].GetProperty("Id").GetString());
            Assert.True(Directory.Exists(baseDir));
            Assert.True(Directory.Exists(dependentDir));

            var uninstallResult = await controller.RegistryUninstall(
                new RegistryUninstallRequest
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
        public IReadOnlyDictionary<string, string> Dependencies { get; } = new Dictionary<string, string>();

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }

    private sealed class LiteralBraceMigrationExtension : IExtension, IDataExtension
    {
        public const string ExtensionId = "com.example.literal-brace-migration";

        public string Id => ExtensionId;
        public string Name => "Literal Brace Migration";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public void ConfigureModel(ModelBuilder modelBuilder)
        {
        }

        public IReadOnlyList<ExtensionMigration> GetMigrations() =>
        [
            new("001_literal_braces", "CREATE TABLE brace_migration (payload TEXT NOT NULL DEFAULT '{}')")
        ];
    }

    private sealed class ComponentOverrideExtension(string id, params string[] componentNames) : CoveExtensionBase
    {
        public override string Id => id;
        public override string Name => id;
        public override string Version => "0.0.0-runtime";

        public override UIManifest GetUIManifest()
        {
            var builder = ManifestBuilder();
            foreach (var componentName in componentNames)
            {
                builder.OverrideComponent("sample.panel", componentName, priority: 100);
            }

            return builder.Build();
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

    private sealed class SpoofedExecutableFilterExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.actual-owner";
        public override string Id => ExtensionId;
        public override string Name => "Spoofed Filter Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest() => new()
        {
            ListFilters = [new UIListFilterContribution(
                "owned",
                "tags",
                "Owned filter",
                "boolean",
                "victim.extension",
                Modifiers: ["equals"])
            {
                FilterId = " owned-filter ",
            }],
        };
    }

    private sealed class NonTagExecutableFilterExtension : CoveExtensionBase
    {
        public const string ExtensionId = "com.example.unsupported-filter";
        public override string Id => ExtensionId;
        public override string Name => "Unsupported Filter Extension";
        public override string Version => "1.0.0";

        public override UIManifest GetUIManifest() => new()
        {
            ListFilters = [new UIListFilterContribution(
                "owned",
                "videos",
                "Owned filter",
                "boolean",
                Id)
            {
                FilterId = " owned-filter ",
            }],
        };
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

    private sealed class LifecycleExtension(
        string id,
        IReadOnlyDictionary<string, string>? dependencies = null,
        List<string>? shutdownOrder = null,
        bool throwOnShutdown = false) : IExtension
    {
        public string Id => id;
        public string Name => id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public IReadOnlyDictionary<string, string> Dependencies { get; } = dependencies ?? new Dictionary<string, string>();
        public int InitializeCount { get; private set; }
        public int ShutdownCount { get; private set; }
        public List<string> Events { get; } = [];

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
        {
            InitializeCount++;
            Events.Add("initialize");
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct = default)
        {
            ShutdownCount++;
            Events.Add("shutdown");
            shutdownOrder?.Add(Id);
            return throwOnShutdown
                ? Task.FromException(new InvalidOperationException("Expected shutdown failure."))
                : Task.CompletedTask;
        }
    }

    private static void MarkAsRuntimeExtension(ExtensionManager manager, string extensionId)
    {
        var field = typeof(ExtensionManager).GetField(
            "_overlayExtensionIds",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var ids = Assert.IsType<HashSet<string>>(field?.GetValue(manager));
        ids.Add(extensionId);
    }

    private static bool RebuildRuntimeProvider(ExtensionManager manager, string extensionId)
    {
        var method = typeof(ExtensionManager).GetMethod(
            "BuildExtensionProviderCore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        return Assert.IsType<bool>(method?.Invoke(manager, [extensionId]));
    }

    private sealed class FilterLifecycleExtension(
        IExtensionEntityFilterProvider provider,
        string entityType = "tags",
        string? extensionId = null,
        string? registrationKey = null,
        bool registerConcreteAlias = false) : IUIExtension
    {
        public string Id => extensionId ?? "com.example.filter-lifecycle";
        public string Name => "Filter lifecycle";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public IReadOnlyDictionary<string, string> Dependencies { get; } = new Dictionary<string, string>();
        public int ShutdownCount { get; private set; }

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
            if (registerConcreteAlias)
            {
                services.AddSingleton(provider.GetType(), provider);
                services.AddSingleton(
                    typeof(IExtensionEntityFilterProvider),
                    serviceProvider => serviceProvider.GetRequiredService(provider.GetType()));
            }
            else if (registrationKey is null)
            {
                services.AddSingleton<IExtensionEntityFilterProvider>(_ => provider);
            }
            else
            {
                services.AddKeyedSingleton<IExtensionEntityFilterProvider>(registrationKey, (_, _) => provider);
            }
        }
        public Task ShutdownAsync(CancellationToken ct = default)
        {
            ShutdownCount++;
            return Task.CompletedTask;
        }

        public UIManifest GetUIManifest() => new()
        {
            ListFilters = [new UIListFilterContribution(
                "owned",
                entityType,
                "Owned filter",
                "boolean",
                Id,
                Modifiers: ["equals"])
            {
                FilterId = " owned-filter ",
            }],
        };
    }

    private sealed class BlockingFilterProvider(
        string entityType = "tags",
        bool observeCancellation = true) : IExtensionEntityFilterProvider, IDisposable
    {
        public IReadOnlyCollection<ExtensionEntityFilterDefinition> Filters { get; } =
            [new("owned-filter", entityType)];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }

        public async Task<ExtensionEntityFilterResult> ResolveAsync(ExtensionEntityFilterRequest request, CancellationToken ct)
        {
            Entered.TrySetResult();
            if (observeCancellation)
                await Release.Task.WaitAsync(ct);
            else
                await Release.Task;
            return new ExtensionEntityFilterResult(request.CandidateIds, "revision");
        }

        public void Dispose()
        {
            DisposeCount++;
            Disposed.TrySetResult();
        }
    }

    private sealed class MatchingFilterProvider(
        int matchingId,
        string entityType = "tags") : IExtensionEntityFilterProvider
    {
        public IReadOnlyCollection<ExtensionEntityFilterDefinition> Filters { get; } =
            [new("owned-filter", entityType)];

        public Task<ExtensionEntityFilterResult> ResolveAsync(
            ExtensionEntityFilterRequest request,
            CancellationToken ct)
            => Task.FromResult(new ExtensionEntityFilterResult(
                request.CandidateIds.Where(id => id == matchingId).ToArray(),
                "revision"));
    }

    private sealed class BlockingLifecycleExtension : IExtension
    {
        private readonly TaskCompletionSource _initializeRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _shutdownRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _blockInitialize;
        private readonly bool _blockShutdown;
        private readonly object _eventsGate = new();
        private int _initializeCount;
        private int _shutdownCount;

        public BlockingLifecycleExtension(string id, bool blockInitialize = false, bool blockShutdown = false)
        {
            Id = id;
            _blockInitialize = blockInitialize;
            _blockShutdown = blockShutdown;
        }

        public string Id { get; }
        public string Name => Id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public int ShutdownCount => Volatile.Read(ref _shutdownCount);
        public TaskCompletionSource InitializeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ShutdownEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Events { get; } = [];

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public async Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _initializeCount);
            lock (_eventsGate)
                Events.Add("initialize");
            InitializeEntered.TrySetResult();
            if (_blockInitialize)
                await _initializeRelease.Task.WaitAsync(ct);
        }

        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _shutdownCount);
            lock (_eventsGate)
                Events.Add("shutdown");
            ShutdownEntered.TrySetResult();
            if (_blockShutdown)
                await _shutdownRelease.Task.WaitAsync(ct);
        }

        public void ReleaseInitialize() => _initializeRelease.TrySetResult();
        public void ReleaseShutdown() => _shutdownRelease.TrySetResult();
    }

    private sealed class FailingEndpointLifecycleExtension(string id) : IApiExtension, IBackgroundExtension
    {
        private int _initializeCount;
        private int _shutdownCount;

        public string Id => id;
        public string Name => id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public int InitializeCount => Volatile.Read(ref _initializeCount);
        public int ShutdownCount => Volatile.Read(ref _shutdownCount);
        public TaskCompletionSource WorkerEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource WorkerStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public Task InitializeAsync(IServiceProvider services, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _initializeCount);
            return Task.CompletedTask;
        }

        public Task ShutdownAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _shutdownCount);
            return Task.CompletedTask;
        }

        public async Task RunAsync(IServiceProvider services, CancellationToken ct)
        {
            WorkerEntered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                WorkerStopped.TrySetResult();
            }
        }

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            if (!WorkerEntered.Task.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Background worker did not start before endpoint publication.");

            throw new InvalidOperationException("Expected endpoint publication failure.");
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

    private static async Task<ExpectedBundleDescriptor> WriteUiBundleAsync(
        string extensionsDir,
        string extensionId,
        string version,
        DateTime jsTimestamp,
        DateTime cssTimestamp)
    {
        const string jsBundle = "ui/index.mjs";
        const string cssBundle = "ui/index.css";
        var extensionDir = Path.Combine(extensionsDir, extensionId);
        var jsPath = Path.Combine(extensionDir, jsBundle);
        var cssPath = Path.Combine(extensionDir, cssBundle);

        Directory.CreateDirectory(Path.GetDirectoryName(jsPath)!);
        await File.WriteAllTextAsync(jsPath, "export default { components: {} };\n");
        await File.WriteAllTextAsync(cssPath, ".fixture { display: block; }\n");
        File.SetLastWriteTimeUtc(jsPath, jsTimestamp);
        File.SetLastWriteTimeUtc(cssPath, cssTimestamp);

        await File.WriteAllTextAsync(
            Path.Combine(extensionDir, "extension.json"),
            JsonSerializer.Serialize(new ExtensionManifestFile
            {
                Id = extensionId,
                Name = extensionId,
                Version = version,
                Kind = "bundle",
                JsBundle = jsBundle,
                CssBundle = cssBundle,
            }));

        return new ExpectedBundleDescriptor(
            extensionId,
            version,
            $"/api/extensions/assets/{extensionId}/{jsBundle}?v={File.GetLastWriteTimeUtc(jsPath).Ticks}&extensionVersion={version}",
            $"/api/extensions/assets/{extensionId}/{cssBundle}?v={File.GetLastWriteTimeUtc(cssPath).Ticks}&extensionVersion={version}");
    }

    private static void AssertBundleDescriptor(UIExtensionBundle descriptor, ExpectedBundleDescriptor expected)
    {
        Assert.Equal(expected.ExtensionId, descriptor.ExtensionId);
        Assert.Equal(expected.Version, descriptor.Version);
        Assert.Equal(expected.JsBundleUrl, descriptor.JsBundleUrl);
        Assert.Equal(expected.CssBundleUrl, descriptor.CssBundleUrl);
    }

    private sealed record ExpectedBundleDescriptor(
        string ExtensionId,
        string Version,
        string JsBundleUrl,
        string CssBundleUrl);

    private sealed class BlockingUninstallExtension : IExtension
    {
        public string Id => "blocking.extension";
        public string Name => "Blocking extension";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public TaskCompletionSource UninstallEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseUninstall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public async Task OnUninstallAsync(IServiceProvider services, CancellationToken ct = default)
        {
            UninstallEntered.TrySetResult();
            await ReleaseUninstall.Task.WaitAsync(ct);
        }
    }

    private class DependentExtension(string id, string dependencyId) : IExtension
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public IReadOnlyDictionary<string, string> Dependencies { get; } =
            new Dictionary<string, string> { [dependencyId] = ">=1.0.0" };

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }
    }

    private sealed class BlockingShutdownExtension(string id, string dependencyId) : IExtension
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;
        public IReadOnlyDictionary<string, string> Dependencies { get; } =
            new Dictionary<string, string> { [dependencyId] = ">=1.0.0" };
        public TaskCompletionSource ShutdownEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseShutdown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public async Task ShutdownAsync(CancellationToken ct = default)
        {
            ShutdownEntered.TrySetResult();
            await ReleaseShutdown.Task.WaitAsync(ct);
        }
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
