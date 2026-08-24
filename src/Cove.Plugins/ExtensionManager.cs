using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Cove.Core.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Manages extension discovery, loading, dependency resolution, lifecycle,
/// migrations, and capability wiring. This is the heart of the Cove extension system.
/// </summary>
public class ExtensionManager : IExtensionContributionRuntime
{
    private const int MaxPolicylessRoutesInWarning = 10;
    private readonly object _extensionRegistryGate = new();
    private readonly object _extensionSetMutationGate = new();
    private ExtensionRegistrySnapshot _extensionRegistry = ExtensionRegistrySnapshot.Empty;
    private readonly ExtensionContext _context;
    private readonly ConcurrentDictionary<string, AssemblyLoadContext> _loadContexts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _overlayExtensionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _overlayExtensionIdsGate = new();
    private readonly ConcurrentDictionary<string, string> _loadCacheSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _extensionDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExtensionManifestFile> _manifestFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ExtensionInstallation> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _installationStateGate = new();
    private readonly HashSet<string> _initializedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _initializedExtensionsGate = new();
    private readonly object _extensionLifecycleGatesGate = new();
    private readonly Dictionary<string, SemaphoreSlim> _extensionLifecycleGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _extensionMigrationGate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _startupDisabledExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _extensionFailureReasons = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task<bool>>> _unloadOperations = new(StringComparer.OrdinalIgnoreCase);
    private readonly AsyncLocal<IReadOnlySet<string>?> _activeUnloadOperations = new();
    private IServiceScopeFactory? _scopeFactory;
    private IServiceProvider? _rootServices;
    private ILogger<ExtensionManager>? _logger;
    private IEndpointRouteBuilder? _routeBuilder;
    private ExtensionEndpointRegistry? _endpointRegistry;
    private IReadOnlyList<ServiceDescriptor>? _hostDescriptors;
    private ExtensionServiceOverlay? _overlay;

    public IReadOnlyList<IExtension> Extensions => GetExtensionRegistry().PublicExtensions;
    public ExtensionContext Context => _context;

    public IExtension? GetExtension(string id)
        => GetExtensionRegistry().Map.TryGetValue(id, out var extension) ? extension : null;

    /// <summary>
    /// Invoked when the set of loaded <see cref="IDataExtension"/>s changes at runtime (install or
    /// uninstall), so the host can refresh the EF model registration (see CoveContext.SetDataExtensions).
    /// ExtensionManager stays unaware of the data layer; the host wires this up.
    /// </summary>
    public Action? DataExtensionsChanged { get; set; }

    public ExtensionManager(ExtensionContext context)
    {
        _context = context;
        // Surface (once per assembly) when an extension ships a copy of a host-provided assembly. The host
        // copy is always used regardless; this just nudges authors to slim the package. Reads _logger lazily,
        // so it works even though the logger is wired up after construction.
        ExtensionLoadContext.HostAssemblyBundledWarning = assemblyName =>
            _logger?.LogWarning(
                "Extension shipped host-provided assembly '{Assembly}'; the host copy is being used. Remove it "
                + "from the package — bundling host assemblies bloats it and risks load-context type mismatches.",
                assemblyName);
    }

    private ExtensionRegistrySnapshot GetExtensionRegistry() => Volatile.Read(ref _extensionRegistry);

    private void AddOrReplaceExtension(IExtension extension)
    {
        lock (_extensionRegistryGate)
        {
            var current = _extensionRegistry;
            var extensions = current.Extensions.ToList();
            var existingIndex = extensions.FindIndex(candidate =>
                string.Equals(candidate.Id, extension.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
                extensions[existingIndex] = extension;
            else
                extensions.Add(extension);

            var map = current.Map.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            map[extension.Id] = extension;
            Volatile.Write(ref _extensionRegistry, new ExtensionRegistrySnapshot(extensions.ToArray(), map));
        }
    }

    private IExtension? RemoveExtension(string id)
    {
        lock (_extensionRegistryGate)
        {
            var current = _extensionRegistry;
            if (!current.Map.TryGetValue(id, out var existing))
                return null;

            var extensions = current.Extensions
                .Where(candidate => !string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var map = current.Map.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase);
            map.Remove(id);
            Volatile.Write(ref _extensionRegistry, new ExtensionRegistrySnapshot(extensions, map));
            return existing;
        }
    }

    private sealed class ExtensionRegistrySnapshot(
        IExtension[] extensions,
        Dictionary<string, IExtension> map)
    {
        public static ExtensionRegistrySnapshot Empty { get; } = new(
            [],
            new Dictionary<string, IExtension>(StringComparer.OrdinalIgnoreCase));

        public IExtension[] Extensions { get; } = extensions;
        public IReadOnlyList<IExtension> PublicExtensions { get; } = Array.AsReadOnly(extensions);
        public IReadOnlyDictionary<string, IExtension> Map { get; } = map;
    }

    // ========================================================================
    // REGISTRATION
    // ========================================================================

    /// <summary>
    /// Register an extension instance (built-in or discovered). Once runtime services are available,
    /// a loaded id must be unloaded before a different instance can be registered.
    /// </summary>
    public void Register(IExtension extension, string source = "builtin")
    {
        if (_unloadOperations.ContainsKey(extension.Id))
            throw new InvalidOperationException($"Extension '{extension.Id}' is currently being unloaded.");

        var lifecycleGate = GetExtensionLifecycleGate(extension.Id);
        lifecycleGate.Wait();
        try
        {
            lock (_extensionSetMutationGate)
            {
                if (_unloadOperations.ContainsKey(extension.Id))
                    throw new InvalidOperationException($"Extension '{extension.Id}' is currently being unloaded.");

                var existingExtension = GetExtension(extension.Id);
                if (existingExtension != null
                    && !ReferenceEquals(existingExtension, extension)
                    && _rootServices != null)
                {
                    throw new InvalidOperationException(
                        $"Extension '{extension.Id}' is already loaded. Unload it before registering a replacement.");
                }

                AddOrReplaceExtension(extension);
                var dependenciesEnabled = extension.Dependencies.Keys.All(IsEnabled);
                // Create an in-memory installation record for built-in extensions.
                if (!_installations.TryAdd(extension.Id, new ExtensionInstallation
                {
                    ExtensionId = extension.Id,
                    Version = extension.Version,
                    Enabled = dependenciesEnabled,
                    Source = source,
                    Categories = extension.Categories.Count > 0 ? string.Join(",", extension.Categories) : null,
                }) && !dependenciesEnabled)
                {
                    TryUpdateInstallation(extension.Id, install => install.Enabled = false);
                }

                if (!dependenciesEnabled)
                    DisableDependentInstallationStates(extension.Id);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Store the ASP.NET Core route builder so endpoints can be registered at runtime.</summary>
    public void SetRouteBuilder(IEndpointRouteBuilder routeBuilder)
    {
        _routeBuilder = routeBuilder;
    }

    /// <summary>
    /// Capture the built root provider and build the extension overlay. Call this after the host is
    /// built and before <see cref="SetupDynamicEndpoints"/>, so DLL extension endpoints are built
    /// against a provider that knows their services.
    /// </summary>
    public void PrepareRuntimeServices(IServiceProvider rootServices)
    {
        _rootServices = rootServices;
        CaptureScopeFactory(rootServices);
        _logger ??= rootServices.GetService<ILogger<ExtensionManager>>();
        foreach (var ext in GetInitializationOrder())
            if (IsOverlayExtension(ext.Id) && IsEnabled(ext.Id))
                BuildExtensionProviderForStartup(ext.Id);
    }

    /// <summary>
    /// Discover and load .NET extension assemblies from a directory.
    /// Each subdirectory may contain an optional extension.json manifest and one or more DLLs.
    /// </summary>
    public void DiscoverExtensions(string extensionsDir)
    {
        if (!Directory.Exists(extensionsDir)) return;

        var extensionDirectories = Directory.GetDirectories(extensionsDir)
            .Where(dir => !string.Equals(Path.GetFileName(dir), ".load-cache", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        CleanupStaleLoadCaches(extensionsDir, extensionDirectories);
        ExtensionLoadContext.PreloadSharedAssemblies(extensionsDir, extensionDirectories);

        foreach (var dir in extensionDirectories)
        {
            try
            {
                // Try to load extension.json manifest first
                ExtensionManifestFile? manifestFile = null;
                var manifestPath = Path.Combine(dir, "extension.json");
                if (File.Exists(manifestPath))
                {
                    var json = File.ReadAllText(manifestPath);
                    manifestFile = JsonSerializer.Deserialize<ExtensionManifestFile>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                    if (manifestFile != null)
                    {
                        var publicationGate = TryAcquireExtensionPublicationGate(manifestFile.Id);
                        if (publicationGate == null)
                        {
                            _logger?.LogTrace("Skipping extension manifest {Id} while it is being unloaded", manifestFile.Id);
                            continue;
                        }

                        try
                        {
                            lock (_extensionSetMutationGate)
                            {
                                if (_unloadOperations.ContainsKey(manifestFile.Id))
                                {
                                    _logger?.LogTrace("Skipping extension manifest {Id} while it is being unloaded", manifestFile.Id);
                                    continue;
                                }

                                _manifestFiles[manifestFile.Id] = manifestFile;
                                _extensionDirectories[manifestFile.Id] = dir;
                            }
                        }
                        finally
                        {
                            publicationGate.Release();
                        }

                        if (IsManifestOnlyKind(manifestFile.Kind))
                        {
                            var lifecycleGate = TryAcquireExtensionPublicationGate(manifestFile.Id);
                            if (lifecycleGate == null)
                                continue;
                            try
                            {
                                lock (_extensionSetMutationGate)
                                {
                                    if (_unloadOperations.ContainsKey(manifestFile.Id))
                                    {
                                        _logger?.LogTrace("Skipping extension manifest {Id} while it is being unloaded", manifestFile.Id);
                                        continue;
                                    }

                                    _manifestFiles[manifestFile.Id] = manifestFile;
                                    _extensionDirectories[manifestFile.Id] = dir;
                                    var source = manifestFile.RegistryUrl != null ? "registry" : "local";
                                    var existingInstall = GetInstallation(manifestFile.Id);
                                    var dependenciesEnabled = manifestFile.Dependencies.Keys.All(IsEnabled);
                                    _installations[manifestFile.Id] = new ExtensionInstallation
                                    {
                                        ExtensionId = manifestFile.Id,
                                        Version = manifestFile.Version,
                                        Enabled = (existingInstall?.Enabled ?? true) && dependenciesEnabled,
                                        Source = source,
                                        InstalledAt = existingInstall?.InstalledAt ?? DateTime.UtcNow,
                                        UpdatedAt = DateTime.UtcNow,
                                        ManifestJson = json,
                                        Categories = manifestFile.Categories.Count > 0 ? string.Join(",", manifestFile.Categories) : null,
                                    };
                                }
                            }
                            finally
                            {
                                lifecycleGate.Release();
                            }
                            continue;
                        }
                    }
                }

                // Skip extensions already loaded in this process. Runtime re-discovery (triggered by
                // installing or updating another extension) re-scans every directory; reloading an
                // unchanged extension into a fresh AssemblyLoadContext would give its types new identities
                // while the EF model, overlay containers, workers, and endpoints still reference the old
                // ones — producing "entity type X is of type X but the generic type provided is of type X"
                // splits that only a restart clears. An extension being installed or updated is always
                // unloaded by the caller before re-discovery, so anything still loaded here is current.
                if (manifestFile?.Id is string alreadyLoadedId && GetExtension(alreadyLoadedId) != null)
                    continue;

                // Determine which DLL to load
                var sourceDllsToLoad = manifestFile?.EntryDll != null
                    ? new[] { Path.Combine(dir, manifestFile.EntryDll) }
                    : Directory.GetFiles(dir, "*.dll");

                var binaryCache = PrepareExtensionBinaryCache(extensionsDir, dir, manifestFile?.Id);

                foreach (var sourceDll in sourceDllsToLoad)
                {
                    if (!File.Exists(sourceDll)) continue;
                    try
                    {
                        var cachedDll = binaryCache.GetCachedPath(sourceDll);
                        if (!File.Exists(cachedDll)) continue;

                        var loadContext = new ExtensionLoadContext(sourceDll, binaryCache.SourceRoot, binaryCache.CacheRoot);
                        var assembly = loadContext.LoadFromAssemblyPath(Path.GetFullPath(cachedDll));
                        var extensionTypes = assembly.GetTypes()
                            .Where(t => typeof(IExtension).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                        foreach (var type in extensionTypes)
                        {
                            if (Activator.CreateInstance(type) is IExtension ext)
                            {
                                // Hand the parsed manifest to the instance before any metadata is read,
                                // so manifest-backed extensions (CoveExtensionBase) surface Id/Name/
                                // Version/etc from extension.json instead of duplicating them in code.
                                if (manifestFile != null && ext is IManifestAware manifestAware)
                                    manifestAware.ApplyManifest(manifestFile);

                                var lifecycleGate = TryAcquireExtensionPublicationGate(ext.Id);
                                if (lifecycleGate == null)
                                    continue;
                                try
                                {
                                    lock (_extensionSetMutationGate)
                                    {
                                        if (_unloadOperations.ContainsKey(ext.Id))
                                        {
                                            _logger?.LogTrace("Skipping extension {Id} while it is being unloaded", ext.Id);
                                            continue;
                                        }

                                        if (GetExtension(ext.Id) is { } existing)
                                        {
                                            var existingSource = GetInstallation(existing.Id)?.Source;
                                            _logger?.LogWarning(
                                                "Skipping duplicate extension {Id}; the loaded {Source} instance must be unloaded before replacement",
                                                ext.Id,
                                                existingSource ?? "unknown");
                                            continue;
                                        }

                                        AddOrReplaceExtension(ext);
                                        _loadContexts[ext.Id] = loadContext;
                                        lock (_overlayExtensionIdsGate)
                                            _overlayExtensionIds.Add(ext.Id);
                                        _loadCacheSlots[binaryCache.CacheKey] = binaryCache.Slot;
                                        _loadCacheSlots[ext.Id] = binaryCache.Slot;
                                        _extensionDirectories[ext.Id] = dir;
                                        _extensionFailureReasons.TryRemove(ext.Id, out _);

                                        var existingInstall = GetInstallation(ext.Id);
                                        var source = manifestFile?.RegistryUrl != null ? "registry" : existingInstall?.Source ?? "local";
                                        var dependenciesEnabled = ext.Dependencies.Keys
                                            .Concat(manifestFile?.Dependencies.Keys.AsEnumerable() ?? Enumerable.Empty<string>())
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .All(IsEnabled);
                                        _installations[ext.Id] = new ExtensionInstallation
                                        {
                                            ExtensionId = ext.Id,
                                            Version = ResolveInstalledVersion(ext.Version, manifestFile, existingInstall, source),
                                            Enabled = (existingInstall?.Enabled ?? true) && dependenciesEnabled,
                                            Source = source,
                                            InstalledAt = existingInstall?.InstalledAt ?? DateTime.UtcNow,
                                            UpdatedAt = DateTime.UtcNow,
                                            ManifestJson = manifestFile != null ? File.ReadAllText(manifestPath) : null,
                                            Categories = SerializeCategories(ext.Categories, manifestFile),
                                        };

                                        if (manifestFile != null)
                                            _manifestFiles[ext.Id] = manifestFile;

                                        if (!dependenciesEnabled)
                                            DisableDependentInstallationStates(ext.Id);
                                    }
                                }
                                finally
                                {
                                    lifecycleGate.Release();
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to load extension DLL {Dll}", sourceDll);
                        if (ex is System.Reflection.ReflectionTypeLoadException rtle)
                        {
                            foreach (var le in rtle.LoaderExceptions ?? [])
                                _logger?.LogError(le, "Loader exception while loading extension DLL {Dll}", sourceDll);
                        }
                        if (manifestFile != null)
                            DisableExtensionForStartupFailure(manifestFile.Id, ex, "discover");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to process extension directory {Dir}", dir);
            }
        }
    }

    // ========================================================================
    // DEPENDENCY RESOLUTION
    // ========================================================================

    /// <summary>
    /// Validates all extension dependencies and returns any problems found.
    /// Checks: missing dependencies, version mismatches, core version requirements.
    /// </summary>
    public List<DependencyProblem> ValidateDependencies()
    {
        var problems = new List<DependencyProblem>();
        var registry = GetExtensionRegistry();
        foreach (var ext in registry.Extensions)
        {
            // Check core version requirement
            if (ext.MinCoveVersion != null
                && !CoveVersionCompatibility.IsAtLeast(_context.CoveVersion, ext.MinCoveVersion))
            {
                problems.Add(new DependencyProblem(ext.Id, null, $"Requires Cove >={ext.MinCoveVersion} but running {_context.CoveVersion}"));
            }

            // Check extension dependencies
            foreach (var (depId, versionRange) in ext.Dependencies)
            {
                if (!registry.Map.TryGetValue(depId, out var dep))
                {
                    problems.Add(new DependencyProblem(ext.Id, depId, $"Missing required extension '{depId}' ({versionRange})"));
                }
                else if (!SemverSatisfies(dep.Version, versionRange))
                {
                    problems.Add(new DependencyProblem(ext.Id, depId, $"Requires '{depId}' {versionRange} but found v{dep.Version}"));
                }
            }
        }
        return problems;
    }

    /// <summary>
    /// Returns extensions in topological order (dependencies first).
    /// Extensions with unmet dependencies are excluded and logged.
    /// </summary>
    public List<IExtension> GetInitializationOrder()
    {
        var registry = GetExtensionRegistry();
        var sorted = new List<IExtension>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in registry.Extensions)
        {
            if (!visited.Contains(ext.Id))
                TopologicalVisit(ext, registry.Map, visited, visiting, sorted);
        }

        return sorted;
    }

    private void TopologicalVisit(
        IExtension ext,
        IReadOnlyDictionary<string, IExtension> extensionMap,
        HashSet<string> visited,
        HashSet<string> visiting,
        List<IExtension> sorted)
    {
        if (visited.Contains(ext.Id)) return;
        if (visiting.Contains(ext.Id))
        {
            _logger?.LogWarning("Circular dependency detected involving extension {Id}", ext.Id);
            return;
        }

        visiting.Add(ext.Id);
        foreach (var (depId, _) in ext.Dependencies)
        {
            if (extensionMap.TryGetValue(depId, out var dep))
                TopologicalVisit(dep, extensionMap, visited, visiting, sorted);
        }
        visiting.Remove(ext.Id);
        visited.Add(ext.Id);
        sorted.Add(ext);
    }

    /// <summary>
    /// Returns the IDs of extensions that the given extension depends on (transitively)
    /// which are not currently installed. Used to prompt users to install missing deps.
    /// </summary>
    public List<string> GetMissingDependencies(string extensionId)
    {
        var registry = GetExtensionRegistry();
        if (!registry.Map.TryGetValue(extensionId, out var ext)) return [];
        var missing = new List<string>();
        CollectMissingDeps(ext, registry.Map, missing, []);
        return missing;
    }

    private void CollectMissingDeps(
        IExtension ext,
        IReadOnlyDictionary<string, IExtension> extensionMap,
        List<string> missing,
        HashSet<string> seen)
    {
        foreach (var (depId, _) in ext.Dependencies)
        {
            if (seen.Contains(depId)) continue;
            seen.Add(depId);
            if (!extensionMap.ContainsKey(depId))
            {
                missing.Add(depId);
            }
            else
            {
                CollectMissingDeps(extensionMap[depId], extensionMap, missing, seen);
            }
        }
    }

    /// <summary>
    /// Returns installed extensions that depend on the supplied extension, with transitive dependents first.
    /// </summary>
    public IReadOnlyList<string> GetDependentExtensionIds(string extensionId, bool enabledOnly = false)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            return [];

        var requestedId = extensionId.Trim();
        var dependentsByDependency = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidateId in GetKnownExtensionIds())
        {
            if (string.Equals(candidateId, requestedId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (enabledOnly && !IsEnabled(candidateId))
                continue;

            foreach (var dependencyId in GetDeclaredDependencies(candidateId).Keys)
            {
                if (string.IsNullOrWhiteSpace(dependencyId))
                    continue;

                if (!dependentsByDependency.TryGetValue(dependencyId, out var dependents))
                {
                    dependents = [];
                    dependentsByDependency[dependencyId] = dependents;
                }

                if (!dependents.Contains(candidateId, StringComparer.OrdinalIgnoreCase))
                    dependents.Add(candidateId);
            }
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { requestedId };

        void Visit(string dependencyId)
        {
            if (!dependentsByDependency.TryGetValue(dependencyId, out var directDependents))
                return;

            foreach (var dependentId in directDependents.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
            {
                if (!seen.Add(dependentId))
                    continue;

                Visit(dependentId);
                result.Add(dependentId);
            }
        }

        Visit(requestedId);
        return result;
    }

    /// <summary>
    /// Returns installed dependencies for the supplied extension, with dependencies before dependents.
    /// </summary>
    public IReadOnlyList<string> GetDependencyExtensionIds(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            return [];

        var knownIds = GetKnownExtensionIds().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (!visiting.Add(id))
                return;

            foreach (var dependencyId in GetDeclaredDependencies(id).Keys.OrderBy(depId => depId, StringComparer.OrdinalIgnoreCase))
            {
                if (!knownIds.Contains(dependencyId) || visited.Contains(dependencyId))
                    continue;

                Visit(dependencyId);
                if (visited.Add(dependencyId))
                    result.Add(dependencyId);
            }

            visiting.Remove(id);
        }

        Visit(extensionId.Trim());
        return result;
    }

    /// <summary>
    /// Publish a disabled state for an extension and its currently enabled dependent closure.
    /// The caller must hold <see cref="_extensionSetMutationGate"/>.
    /// </summary>
    private void DisableDependentInstallationStates(string extensionId)
    {
        foreach (var id in GetDependentExtensionIds(extensionId, enabledOnly: true)
                     .Append(extensionId)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            TryUpdateInstallation(id, install => install.Enabled = false);
        }
    }

    // ========================================================================
    // LIFECYCLE
    // ========================================================================

    /// <summary>
    /// True for extensions loaded from disk as DLLs (runtime-capable). These contribute their
    /// services into the rebuildable <see cref="ExtensionServiceOverlay"/> rather than the
    /// immutable root container, so they work identically whether present at boot or installed
    /// later. Built-in extensions compiled into the host go straight into the root container.
    /// </summary>
    private bool IsOverlayExtension(string id)
    {
        lock (_overlayExtensionIdsGate)
            return _overlayExtensionIds.Contains(id);
    }

    /// <summary>
    /// Call ConfigureServices for built-in (host-compiled) extensions, registering them into the
    /// root container. Runtime DLL extensions are intentionally skipped here — their services are
    /// contributed to their own per-extension container instead (see <see cref="ExtensionServiceOverlay.TryBuildProvider"/>).
    /// </summary>
    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var ext in GetInitializationOrder())
        {
            if (!IsEnabled(ext.Id))
                continue;

            if (IsOverlayExtension(ext.Id))
                continue;

            try
            {
                var contributionStartIndex = services.Count;
                ext.ConfigureServices(services, _context);
                ExtensionContributionServiceRegistration.KeyProvidersAddedSince(
                    services,
                    contributionStartIndex,
                    ext.Id);
            }
            catch (Exception ex)
            {
                DisableExtensionForStartupFailure(ext.Id, ex, "ConfigureServices");
            }
        }
    }

    /// <summary>
    /// Snapshot the host's service descriptors so the extension overlay can share the host's
    /// singletons and re-create its scoped services. Call this immediately before the host is
    /// built (after all core and built-in services are registered).
    /// </summary>
    public void CaptureHostServices(IServiceCollection services)
    {
        _hostDescriptors = services.ToList();
    }

    /// <summary>
    /// Build the initial isolated service container for one runtime DLL extension before lifecycle
    /// initialization. Runtime transitions use the gated initialize/shutdown paths instead.
    /// </summary>
    private void BuildExtensionProviderForStartup(string id)
    {
        var lifecycleGate = GetExtensionLifecycleGate(id);
        lifecycleGate.Wait();
        try
        {
            if (!BuildExtensionProviderCore(id))
            {
                _endpointRegistry?.RemoveExtension(id);
                _overlay?.Remove(id);
            }
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private bool BuildExtensionProviderCore(string id)
    {
        if (_rootServices == null || _hostDescriptors == null)
            return false;
        if (!IsOverlayExtension(id) || !IsEnabled(id))
            return false;
        if (GetExtension(id) is not { } ext)
            return false;

        // The container is being (re)built: stop any running worker and clear stale contributions so the
        // extension re-publishes against the new container on init.
        StopBackgroundWorker(ext.Id);
        WithdrawFromExchange(ext.Id);

        _overlay ??= new ExtensionServiceOverlay(_rootServices, _hostDescriptors, _logger);
        return _overlay.TryBuildProvider(
            ext.Id,
            ext,
            _context,
            (failedId, e) => DisableExtensionForStartupFailure(failedId, e, "provider build"));
    }

    /// <summary>
    /// Create a scope for running the given extension's code (HTTP request, job, scan pass).
    /// Returns the extension's own tracked container scope for a runtime DLL extension, or a root
    /// scope for a built-in extension. A runtime extension whose provider has been retired fails
    /// cleanly instead of executing against host DI. Callers own and must dispose the returned scope.
    /// </summary>
    public IServiceScope CreateExtensionScope(string extensionId)
    {
        if (GetExtension(extensionId) is { } extension)
            return CreateExtensionScope(extension);

        if (IsOverlayExtension(extensionId))
            throw new InvalidOperationException($"No current instance is available for runtime extension '{extensionId}'.");

        var factory = _scopeFactory ?? _rootServices?.GetService<IServiceScopeFactory>();
        if (factory == null)
            throw new InvalidOperationException("Extension service scope requested before the host service provider was available.");
        return factory.CreateScope();
    }

    /// <summary>Create a scope for the exact endpoint generation selected by routing.</summary>
    public IServiceScope CreateExtensionScope(ExtensionEndpointMetadata endpoint)
        => endpoint.Execution != null
            ? CreateExtensionScope(endpoint.Execution)
            : CreateExtensionScope(endpoint.ExtensionId);

    /// <summary>Create a scope bound to the expected extension/provider generation.</summary>
    internal IServiceScope CreateExtensionScope(IExtension extension)
        => CreateExtensionScope(CaptureExtensionExecution(extension));

    /// <summary>Create a scope bound to an already captured extension/provider generation.</summary>
    internal IServiceScope CreateExtensionScope(ExtensionExecutionHandle execution)
    {
        var extension = execution.Extension;
        if (IsOverlayExtension(extension.Id))
        {
            var overlay = _overlay;
            if (execution.Generation != null
                && overlay != null
                && overlay.TryCreateScope(extension.Id, extension, execution.Generation, out var extensionScope))
                return extensionScope;

            throw new InvalidOperationException($"No matching service-container generation is available for runtime extension '{extension.Id}'.");
        }

        var factory = _scopeFactory ?? _rootServices?.GetService<IServiceScopeFactory>();
        if (factory == null)
            throw new InvalidOperationException("Extension service scope requested before the host service provider was available.");
        return factory.CreateScope();
    }

    internal ExtensionExecutionHandle CaptureExtensionExecution(IExtension extension)
    {
        if (IsOverlayExtension(extension.Id))
        {
            var overlay = _overlay;
            if (overlay != null && overlay.TryGetGeneration(extension.Id, extension, out var generation))
                return new ExtensionExecutionHandle(extension, generation, allowUnleasedMetadata: false);

            throw new InvalidOperationException($"No matching service-container generation is available for runtime extension '{extension.Id}'.");
        }

        return new ExtensionExecutionHandle(extension, generation: null, allowUnleasedMetadata: false);
    }

    private ExtensionExecutionHandle CaptureExtensionMetadata(IExtension extension)
    {
        if (!IsOverlayExtension(extension.Id))
            return new ExtensionExecutionHandle(extension, generation: null, allowUnleasedMetadata: true);

        var overlay = _overlay;
        if (overlay != null && overlay.TryGetGeneration(extension.Id, extension, out var generation))
            return new ExtensionExecutionHandle(extension, generation, allowUnleasedMetadata: false);

        if (GetExtension(extension.Id) is { } current
            && ReferenceEquals(current, extension))
            return new ExtensionExecutionHandle(extension, generation: null, allowUnleasedMetadata: true);

        throw new InvalidOperationException($"No safe metadata generation is available for runtime extension '{extension.Id}'.");
    }

    private ExtensionExecutionLease CreateExtensionExecutionLease(ExtensionExecutionHandle execution)
    {
        var extension = execution.Extension;
        if (IsOverlayExtension(extension.Id))
        {
            if (execution.Generation == null)
            {
                if (execution.AllowUnleasedMetadata)
                    return new ExtensionExecutionLease(EmptyServiceProvider.Instance, innerLease: null);

                throw new InvalidOperationException($"No provider generation was captured for runtime extension '{extension.Id}'.");
            }

            var overlay = _overlay;
            if (overlay != null
                && overlay.TryCreateLease(extension.Id, extension, execution.Generation, out var providerLease))
                return new ExtensionExecutionLease(providerLease.Services, providerLease);

            throw new InvalidOperationException($"The captured service-container generation is no longer available for runtime extension '{extension.Id}'.");
        }

        var services = _rootServices ?? EmptyServiceProvider.Instance;
        return new ExtensionExecutionLease(services, innerLease: null);
    }

    /// <summary>Execute extension code while pinning its current service-provider generation.</summary>
    internal TResult ExecuteExtension<TResult>(IExtension extension, Func<TResult> operation)
        => ExecuteExtension(CaptureExtensionExecution(extension), operation);

    internal TResult ExecuteExtension<TResult>(ExtensionExecutionHandle execution, Func<TResult> operation)
    {
        using var lease = CreateExtensionExecutionLease(execution);
        return operation();
    }

    internal TResult ExecuteExtensionMetadata<TResult>(IExtension extension, Func<TResult> operation)
    {
        using var lease = CreateExtensionExecutionLease(CaptureExtensionMetadata(extension));
        return operation();
    }

    /// <summary>Execute extension code while pinning its current service-provider generation.</summary>
    internal async Task<TResult> ExecuteExtensionAsync<TResult>(IExtension extension, Func<Task<TResult>> operation)
        => await ExecuteExtensionAsync(CaptureExtensionExecution(extension), operation);

    internal async Task<TResult> ExecuteExtensionAsync<TResult>(ExtensionExecutionHandle execution, Func<Task<TResult>> operation)
    {
        using var lease = CreateExtensionExecutionLease(execution);
        return await operation();
    }

    /// <summary>Execute extension code while pinning its current service-provider generation.</summary>
    internal async Task ExecuteExtensionAsync(IExtension extension, Func<Task> operation)
        => await ExecuteExtensionAsync(CaptureExtensionExecution(extension), operation);

    internal async Task ExecuteExtensionAsync(ExtensionExecutionHandle execution, Func<Task> operation)
    {
        using var lease = CreateExtensionExecutionLease(execution);
        await operation();
    }

    public sealed class ExtensionExecutionHandle
    {
        internal ExtensionExecutionHandle(
            IExtension extension,
            object? generation,
            bool allowUnleasedMetadata)
        {
            Extension = extension;
            Generation = generation;
            AllowUnleasedMetadata = allowUnleasedMetadata;
        }

        public IExtension Extension { get; }
        internal object? Generation { get; }
        internal bool AllowUnleasedMetadata { get; }
    }

    private sealed class ExtensionExecutionLease(IServiceProvider services, IDisposable? innerLease) : IDisposable
    {
        public IServiceProvider Services { get; } = services;

        public void Dispose() => innerLease?.Dispose();
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static EmptyServiceProvider Instance { get; } = new();
        public object? GetService(Type serviceType) => null;
    }

    /// <summary>Withdraw an extension's published contributions from the cross-extension exchange.</summary>
    private void WithdrawFromExchange(string id)
        => _rootServices?.GetService<IExtensionServiceExchange>()?.WithdrawAll(id);

    /// <summary>
    /// Set up per-extension endpoint data sources at startup so routes can be rebuilt dynamically
    /// when extensions are installed or uninstalled at runtime.
    /// </summary>
    public void SetupDynamicEndpoints()
    {
        if (_routeBuilder == null) return;

        // One registry, added to the app's data sources exactly once. The matcher observes its change
        // token for the whole process lifetime, so endpoints added/removed at runtime go live without
        // a restart (adding NEW data sources after startup is not reliably observed by the matcher).
        _endpointRegistry = new ExtensionEndpointRegistry();
        _routeBuilder.DataSources.Add(_endpointRegistry);
    }

    /// <summary>Publish one initialized extension's endpoints while its lifecycle gate is held.</summary>
    private void PublishExtensionEndpoints(string id)
    {
        if (_routeBuilder == null || _endpointRegistry == null) return;
        if (GetExtension(id) is not IApiExtension apiExt) return;
        if (!IsEnabled(id)) return;

        // Build this extension's endpoints into a nested source (bound against its own provider for
        // correct minimal-API parameter classification), then publish it through the registry, which
        // fires the change token the matcher observes — making the routes live immediately.
        var execution = CaptureExtensionExecution(apiExt);
        var source = new ExtensionEndpointDataSource(
            _routeBuilder,
            id,
            EndpointBuildServices(execution),
            execution);
        _ = ExecuteExtension(execution, () =>
        {
            apiExt.MapEndpoints(source);
            source.MaterializeEndpoints();
            return true;
        });
        WarnAboutPolicylessEndpoints(id, source.Endpoints);
        _endpointRegistry.SetExtension(id, source);
    }

    private void WarnAboutPolicylessEndpoints(string extensionId, IReadOnlyList<Endpoint> endpoints)
    {
        var policylessRoutes = endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => !HasCoveAuthorizationMetadata(endpoint))
            .Select(DescribeRoute)
            .ToArray();
        if (policylessRoutes.Length == 0)
            return;

        var displayedRoutes = string.Join(", ", policylessRoutes.Take(MaxPolicylessRoutesInWarning));
        var remainingRouteCount = policylessRoutes.Length - MaxPolicylessRoutesInWarning;
        var routeSummary = remainingRouteCount > 0
            ? $"{displayedRoutes}, and {remainingRouteCount} more"
            : displayedRoutes;
        _logger?.LogWarning(
            "Extension {ExtensionId} registered {EndpointCount} endpoint(s) without a Cove authorization "
            + "policy. They allow anonymous access for backward compatibility: {Endpoints}. Declare "
            + "intent with RequireCovePermission, RequireCoveEntityAccess, "
            + "AllowWithoutCovePermission, or AllowCoveAnonymous.",
            extensionId,
            policylessRoutes.Length,
            routeSummary);
    }

    private static bool HasCoveAuthorizationMetadata(Endpoint endpoint)
        => endpoint.Metadata.GetMetadata<CovePermissionRequirementMetadata>() is not null
            || endpoint.Metadata.GetMetadata<CoveRouteEntityAccessRequirementMetadata>() is not null
            || endpoint.Metadata.GetMetadata<CoveAllowWithoutPermissionMetadata>() is not null
            || endpoint.Metadata.GetMetadata<CoveAllowAnonymousMetadata>() is not null;

    private static string DescribeRoute(RouteEndpoint endpoint)
    {
        var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
        var methodLabel = methods is { Count: > 0 } ? string.Join("|", methods) : "ANY";
        var route = endpoint.RoutePattern.RawText ?? endpoint.DisplayName ?? "<unknown>";
        return $"{methodLabel} {route}";
    }

    /// <summary>
    /// The provider used to build an extension's endpoints. For runtime DLL extensions this is the
    /// overlay (so minimal-API parameter binding sees the extension's services as DI services);
    /// built-in extensions build against the root container.
    /// </summary>
    private IServiceProvider? EndpointBuildServices(ExtensionExecutionHandle execution)
    {
        var extension = execution.Extension;
        return IsOverlayExtension(extension.Id)
            && execution.Generation != null
            ? _overlay?.GetProviderForEndpointBuild(extension.Id, extension, execution.Generation)
            : null;
    }

    /// <summary>
    /// Invoke the chain of enabled middleware extensions for one request, then the host continuation.
    /// The chain is built per request from the live set, so middleware contributed by a runtime-installed
    /// extension takes effect immediately. The host registers a single persistent dispatcher that calls this.
    /// </summary>
    public Task InvokeMiddlewareChainAsync(HttpContext context, RequestDelegate terminal)
    {
        var middleware = GetInitializationOrder()
            .OfType<IMiddlewareExtension>()
            .Where(ext => IsEnabled(ext.Id))
            .Select(ext => (Extension: ext, Execution: CaptureExtensionExecution(ext)))
            .ToList();

        if (middleware.Count == 0)
            return terminal(context);

        RequestDelegate next = terminal;
        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var current = middleware[i];
            var localNext = next;
            next = ctx => ExecuteExtensionAsync(
                current.Execution,
                () => current.Extension.InvokeAsync(ctx, localNext));
        }
        return next(context);
    }

    // ========================================================================
    // BACKGROUND WORKERS (IBackgroundExtension)
    // ========================================================================
    private readonly Dictionary<string, BackgroundWorkerRegistration> _backgroundWorkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Start the extension's long-lived background worker if it implements <see cref="IBackgroundExtension"/>
    /// and one isn't already running. The worker receives the extension's own provider and a token that is
    /// cancelled when the extension is disabled, uninstalled, rebuilt, or the host shuts down.
    /// </summary>
    public void StartBackgroundWorker(string id)
    {
        if (_rootServices == null) return;
        if (GetExtension(id) is not IBackgroundExtension worker) return;
        if (!IsEnabled(id)) return;

        BackgroundWorkerRegistration registration;
        lock (_backgroundWorkers)
        {
            if (_backgroundWorkers.ContainsKey(id)) return;

            // Acquire the provider-generation lease before publishing or scheduling the worker. A
            // concurrent rebuild can then retire this provider, but cannot dispose or replace the
            // services underneath this worker while it drains.
            var lease = CreateExtensionExecutionLease(CaptureExtensionExecution(worker));
            var cancellation = new CancellationTokenSource();
            var token = cancellation.Token;
            registration = new BackgroundWorkerRegistration(cancellation);
            _backgroundWorkers[id] = registration;

            try
            {
                registration.Task = Task.Run(async () =>
                {
                    try
                    {
                        await worker.RunAsync(lease.Services, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        // expected on stop
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Background worker for extension {Id} faulted", id);
                    }
                    finally
                    {
                        try
                        {
                            lease.Dispose();
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning(ex, "Error releasing the service lease for extension worker {Id}", id);
                        }
                        finally
                        {
                            lock (_backgroundWorkers)
                            {
                                if (_backgroundWorkers.TryGetValue(id, out var current)
                                    && ReferenceEquals(current, registration))
                                    _backgroundWorkers.Remove(id);
                            }
                            cancellation.Dispose();
                        }
                    }
                });
            }
            catch
            {
                _backgroundWorkers.Remove(id);
                lease.Dispose();
                cancellation.Dispose();
                throw;
            }
        }
        _logger?.LogInformation("Background worker started for extension {Id}", id);
    }

    /// <summary>Cancel the extension's background worker and wait for its provider lease to drain.</summary>
    public void StopBackgroundWorker(string id)
    {
        BackgroundWorkerRegistration? registration;
        lock (_backgroundWorkers)
        {
            if (!_backgroundWorkers.TryGetValue(id, out registration))
                return;

        }

        try
        {
            registration.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The worker completed naturally between lookup and cancellation.
        }
        catch (Exception ex)
        {
            // Cancellation callbacks are extension code and may throw. The worker must still drain
            // before lifecycle processing can rebuild or retire its provider.
            _logger?.LogWarning(ex, "A cancellation callback for extension worker {Id} failed", id);
        }
        finally
        {
            registration.Task.GetAwaiter().GetResult();
            lock (_backgroundWorkers)
            {
                if (_backgroundWorkers.TryGetValue(id, out var current)
                    && ReferenceEquals(current, registration))
                    _backgroundWorkers.Remove(id);
            }
        }
        _logger?.LogInformation("Background worker stopped for extension {Id}", id);
    }

    private sealed class BackgroundWorkerRegistration(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
    }

    /// <summary>
    /// Initialize all extensions after the app is built.
    /// Wires up capability interfaces, applies migrations, runs in dependency order.
    /// </summary>
    public async Task InitializeAllAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _rootServices ??= services;
        var runtimeServices = _rootServices;
        CaptureScopeFactory(runtimeServices);
        _logger = runtimeServices.GetService<ILogger<ExtensionManager>>();

        // Load installation state from DB
        await LoadInstallationStateAsync(runtimeServices, ct);

        // Clean up stale installation records for extensions that no longer exist on disk.
        var staleIds = _installations.Keys
            .Where(id => !IsEffectivelyInstalled(id) && !IsManifestOnlyExtension(id))
            .ToList();
        foreach (var staleId in staleIds)
        {
            var lifecycleGate = GetExtensionLifecycleGate(staleId);
            await lifecycleGate.WaitAsync(ct);
            try
            {
                if (IsEffectivelyInstalled(staleId) || IsManifestOnlyExtension(staleId))
                    continue;

                _installations.TryRemove(staleId, out _);
                await RemoveInstallationStateAsync(staleId, ct);
                _logger?.LogInformation("Removed stale installation record for {Id}", staleId);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        foreach (var extensionId in _startupDisabledExtensions.Keys.ToList())
        {
            var lifecycleGate = GetExtensionLifecycleGate(extensionId);
            await lifecycleGate.WaitAsync(ct);
            try
            {
                TryUpdateInstallation(extensionId, install => install.Enabled = false);
                await PersistInstallationStateAsync(extensionId, ct);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        // Validate dependencies
        var problems = ValidateDependencies();
        foreach (var p in problems)
            _logger?.LogWarning("Extension dependency issue: {Problem}", p.Message);

        // Initialize all enabled extensions in dependency order. Each DLL extension gets its own
        // container, built here if PrepareRuntimeServices hasn't already (boot and runtime-install
        // share this exact path).
        foreach (var ext in GetInitializationOrder())
        {
            var lifecycleGate = GetExtensionLifecycleGate(ext.Id);
            await lifecycleGate.WaitAsync(ct);
            try
            {
                if (!IsEnabled(ext.Id)) continue;
                if (IsExtensionInitialized(ext.Id)) continue;

                if (ext is IStatefulExtension stateful)
                {
                    var factory = runtimeServices.GetService<IExtensionStoreFactory>();
                    if (factory != null)
                        stateful.SetStore(factory.CreateStore(ext.Id));
                }

                if (IsOverlayExtension(ext.Id) && _overlay?.Has(ext.Id) != true
                    && !BuildExtensionProviderCore(ext.Id))
                {
                    await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
                    await PersistInstallationStateAsync(ext.Id, ct);
                    continue;
                }
                using var extensionLease = CreateExtensionExecutionLease(CaptureExtensionExecution(ext));
                var extServices = extensionLease.Services;

                if (ext is IDataExtension)
                    await ApplyExtensionMigrationsAsync(runtimeServices, ext.Id, ct);

                // Check if this is a new installation
                var install = GetInstallation(ext.Id);
                if (install == null)
                {
                    await ext.OnInstallAsync(extServices, ct);
                    await SaveInstallationAsync(runtimeServices, ext.Id, ct);
                    _logger?.LogInformation("Extension {Id} installed (v{Version})", ext.Id, ext.Version);
                }

                await ext.InitializeAsync(extServices, ct);
                MarkExtensionInitialized(ext.Id);
                StartBackgroundWorker(ext.Id);
                PublishExtensionEndpoints(ext.Id);
                _logger?.LogInformation("Extension {Id} ({Name} v{Version}) initialized", ext.Id, ext.Name, ext.Version);
            }
            catch (Exception ex)
            {
                await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
                DisableExtensionForStartupFailure(ext.Id, ex, "InitializeAsync");
                await PersistInstallationStateAsync(ext.Id, ct);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
    }

    private SemaphoreSlim GetExtensionLifecycleGate(string id)
    {
        lock (_extensionLifecycleGatesGate)
        {
            if (!_extensionLifecycleGates.TryGetValue(id, out var gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _extensionLifecycleGates[id] = gate;
            }

            return gate;
        }
    }

    private SemaphoreSlim? TryAcquireExtensionPublicationGate(string id)
    {
        var lifecycleGate = GetExtensionLifecycleGate(id);
        while (!lifecycleGate.Wait(TimeSpan.FromMilliseconds(25)))
        {
            if (_unloadOperations.ContainsKey(id))
                return null;
        }

        if (!_unloadOperations.ContainsKey(id))
            return lifecycleGate;

        lifecycleGate.Release();
        return null;
    }

    private bool IsExtensionInitialized(string id)
    {
        lock (_initializedExtensionsGate)
            return _initializedExtensions.Contains(id);
    }

    private void MarkExtensionInitialized(string id)
    {
        lock (_initializedExtensionsGate)
            _initializedExtensions.Add(id);
    }

    private void MarkExtensionUninitialized(string id)
    {
        lock (_initializedExtensionsGate)
            _initializedExtensions.Remove(id);
    }

    /// <summary>
    /// Stop one initialized extension while its service provider is still available. The per-extension
    /// async gate serializes the full transition, and clearing the marker before ShutdownAsync keeps
    /// repeated disable/unload requests idempotent even when shutdown reports an error.
    /// </summary>
    private async Task ShutdownExtensionAsync(string id, CancellationToken ct, bool retireOverlay = false)
    {
        var lifecycleGate = GetExtensionLifecycleGate(id);
        await lifecycleGate.WaitAsync(ct);
        try
        {
            await ShutdownExtensionCoreAsync(id, ct, retireOverlay);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task ShutdownExtensionCoreAsync(string id, CancellationToken ct, bool retireOverlay)
    {
        _endpointRegistry?.RemoveExtension(id);
        WithdrawFromExchange(id);
        StopBackgroundWorker(id);

        bool wasInitialized;
        lock (_initializedExtensionsGate)
            wasInitialized = _initializedExtensions.Remove(id);

        if (wasInitialized && GetExtension(id) is { } extension)
        {
            try
            {
                await ExecuteExtensionAsync(extension, () => extension.ShutdownAsync(ct));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "Shutdown failed for extension {Id}", id);
            }
        }

        if (retireOverlay && IsOverlayExtension(id))
            _overlay?.Remove(id);
    }

    /// <summary>Shut down all extensions gracefully (reverse dependency order).</summary>
    public async Task ShutdownAllAsync(CancellationToken ct = default)
    {
        var reversed = GetInitializationOrder().ToList();
        reversed.Reverse();
        foreach (var ext in reversed)
            await ShutdownExtensionAsync(ext.Id, ct);

        List<string> workerIds;
        lock (_backgroundWorkers)
            workerIds = _backgroundWorkers.Keys.ToList();
        foreach (var id in workerIds)
            StopBackgroundWorker(id);

        _overlay?.Dispose();
        _overlay = null;
    }

    /// <summary>
    /// Initialize a newly discovered extension without restarting the host process.
    /// </summary>
    public async Task<bool> InitializeExtensionAsync(string id, IServiceProvider services, CancellationToken ct = default)
    {
        CaptureScopeFactory(services);
        _rootServices ??= services;
        var runtimeServices = _rootServices ?? services;
        _logger ??= runtimeServices.GetService<ILogger<ExtensionManager>>();

        var lifecycleGate = GetExtensionLifecycleGate(id);
        await lifecycleGate.WaitAsync(ct);
        try
        {
            return await InitializeExtensionCoreAsync(id, runtimeServices, ct);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<bool> InitializeExtensionCoreAsync(string id, IServiceProvider runtimeServices, CancellationToken ct)
    {
        if (IsExtensionInitialized(id))
            return true;

        if (GetExtension(id) is not { } ext)
        {
            if (_installations.ContainsKey(id) && IsManifestOnlyExtension(id))
            {
                TryUpdateInstallation(id, install => install.UpdatedAt = DateTime.UtcNow);
                await PersistInstallationStateAsync(id, ct);
                return true;
            }

            return false;
        }

        if (ext is IStatefulExtension stateful)
        {
            var factory = runtimeServices.GetService<IExtensionStoreFactory>();
            if (factory != null)
                stateful.SetStore(factory.CreateStore(ext.Id));
        }

        if (!IsEnabled(ext.Id))
            return true;

        // Build this extension's own container BEFORE initializing, so its services (and endpoints)
        // resolve without a host restart. Other extensions' containers are untouched.
        if (IsOverlayExtension(ext.Id) && !BuildExtensionProviderCore(ext.Id))
        {
            await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
            await PersistInstallationStateAsync(ext.Id, ct);
            return false;
        }
        using var extensionLease = CreateExtensionExecutionLease(CaptureExtensionExecution(ext));
        var extServices = extensionLease.Services;

        // A data extension installed at runtime contributes new tables and entity types. Create its
        // schema (migrations are raw SQL and model-independent) and refresh the host EF model BEFORE it
        // installs/initializes or handles any query, so the host DbContext can resolve its DbSet<> types
        // without an app restart. The rebuilt model is a superset, so other extensions are unaffected.
        if (ext is IDataExtension)
        {
            await ApplyExtensionMigrationsAsync(runtimeServices, ext.Id, ct);
            DataExtensionsChanged?.Invoke();
        }

        try
        {
            await ext.OnInstallAsync(extServices, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OnInstall failed for extension {Id}", ext.Id);
        }

        try
        {
            await ext.InitializeAsync(extServices, ct);
            MarkExtensionInitialized(ext.Id);
            StartBackgroundWorker(ext.Id);
            _startupDisabledExtensions.TryRemove(ext.Id, out _);
            _extensionFailureReasons.TryRemove(ext.Id, out _);
            var manifest = GetManifestFile(ext.Id);
            TryUpdateInstallation(ext.Id, install =>
            {
                install.Version = ResolveInstalledVersion(ext.Version, manifest, install, install.Source);
                install.UpdatedAt = DateTime.UtcNow;
            });
            await PersistInstallationStateAsync(ext.Id, ct);
            PublishExtensionEndpoints(ext.Id);
            return true;
        }
        catch (Exception ex)
        {
            await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
            DisableExtensionForStartupFailure(ext.Id, ex, "hot-initialize");
            await PersistInstallationStateAsync(ext.Id, ct);
            return false;
        }
    }

    /// <summary>
    /// Ensure a loaded extension has completed InitializeAsync before it is used by a runtime capability surface.
    /// </summary>
    public async Task<bool> EnsureExtensionInitializedAsync(string id, CancellationToken ct = default)
    {
        var lifecycleGate = GetExtensionLifecycleGate(id);
        await lifecycleGate.WaitAsync(ct);
        try
        {
            return await EnsureExtensionInitializedCoreAsync(id, ct);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<bool> EnsureExtensionInitializedCoreAsync(string id, CancellationToken ct)
    {
        if (IsExtensionInitialized(id))
            return true;

        if (GetExtension(id) is not { } ext)
            return false;

        if (!IsEnabled(id))
            return false;

        var services = _rootServices;
        if (services == null)
            return true;

        _logger ??= services.GetService<ILogger<ExtensionManager>>();

        if (ext is IStatefulExtension stateful)
        {
            var factory = services.GetService<IExtensionStoreFactory>();
            if (factory != null)
                stateful.SetStore(factory.CreateStore(ext.Id));
        }

        if (IsOverlayExtension(ext.Id) && _overlay?.Has(ext.Id) != true
            && !BuildExtensionProviderCore(ext.Id))
        {
            await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
            await PersistInstallationStateAsync(ext.Id, ct);
            return false;
        }
        using var extensionLease = CreateExtensionExecutionLease(CaptureExtensionExecution(ext));
        var extServices = extensionLease.Services;

        // Mirror the runtime-install path: ensure a data extension's schema exists and the host EF model
        // includes its entity types before it initializes. Both calls are idempotent (already-applied
        // migrations are skipped; the model only rebuilds when the data-extension set actually changed).
        if (ext is IDataExtension)
        {
            await ApplyExtensionMigrationsAsync(services, ext.Id, ct);
            DataExtensionsChanged?.Invoke();
        }

        try
        {
            await ext.InitializeAsync(extServices, ct);
            MarkExtensionInitialized(ext.Id);
            StartBackgroundWorker(ext.Id);
            _startupDisabledExtensions.TryRemove(ext.Id, out _);
            _extensionFailureReasons.TryRemove(ext.Id, out _);
            PublishExtensionEndpoints(ext.Id);
            _logger?.LogInformation("Extension {Id} initialized on demand", ext.Id);
            return true;
        }
        catch (Exception ex)
        {
            await ShutdownExtensionCoreAsync(ext.Id, ct, retireOverlay: true);
            DisableExtensionForStartupFailure(ext.Id, ex, "on-demand initialize");
            await PersistInstallationStateAsync(ext.Id, ct);
            return false;
        }
    }

    /// <summary>
    /// Shutdown and unload a discovered extension. Returns false when extension cannot be removed.
    /// </summary>
    public async Task<bool> UnloadExtensionAsync(string id, IServiceProvider services, CancellationToken ct = default)
    {
        CaptureScopeFactory(services);
        _logger ??= services.GetService<ILogger<ExtensionManager>>();
        ct.ThrowIfCancellationRequested();
        if (_activeUnloadOperations.Value?.Contains(id) == true)
            throw new InvalidOperationException($"Extension '{id}' cannot recursively unload itself.");

        var proposed = new Lazy<Task<bool>>(
            () => UnloadExtensionTransactionAsync(id, services),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var operation = _unloadOperations.GetOrAdd(id, proposed);
        var task = operation.Value;
        if (ReferenceEquals(operation, proposed))
        {
            _ = task.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    RemoveUnloadOperation(id, operation);
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return await task.WaitAsync(ct);
    }

    private void RemoveUnloadOperation(string id, Lazy<Task<bool>> operation)
    {
        if (_unloadOperations.TryGetValue(id, out var current) && ReferenceEquals(current, operation))
            _unloadOperations.TryRemove(id, out _);
    }

    private async Task<bool> UnloadExtensionTransactionAsync(string id, IServiceProvider services)
    {
        var previousOperations = _activeUnloadOperations.Value;
        _activeUnloadOperations.Value = previousOperations == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { id }
            : previousOperations.Append(id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            return await UnloadExtensionTransactionCoreAsync(id, services);
        }
        finally
        {
            _activeUnloadOperations.Value = previousOperations;
        }
    }

    private async Task<bool> UnloadExtensionTransactionCoreAsync(string id, IServiceProvider services)
    {
        List<string> dependentIds;
        lock (_extensionSetMutationGate)
        {
            dependentIds = GetDependentExtensionIds(id, enabledOnly: true).ToList();
            DisableDependentInstallationStates(id);
        }

        // Once the disabled closure is published, complete it without cancellation so no dependent
        // keeps a live worker, endpoint, provider, or persisted enabled state against a removed dependency.
        foreach (var dependentId in dependentIds)
        {
            var dependentGate = GetExtensionLifecycleGate(dependentId);
            await dependentGate.WaitAsync(CancellationToken.None);
            try
            {
                await PersistInstallationStateAsync(dependentId, CancellationToken.None);
                await ShutdownExtensionCoreAsync(dependentId, CancellationToken.None, retireOverlay: true);
            }
            finally
            {
                dependentGate.Release();
            }
        }

        var lifecycleGate = GetExtensionLifecycleGate(id);
        await lifecycleGate.WaitAsync(CancellationToken.None);
        try
        {
            return await UnloadExtensionCoreAsync(id, services, CancellationToken.None);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    private async Task<bool> UnloadExtensionCoreAsync(string id, IServiceProvider services, CancellationToken ct)
    {
        if (GetExtension(id) is not { } ext)
        {
            if (_installations.ContainsKey(id) && IsManifestOnlyExtension(id))
            {
                lock (_extensionSetMutationGate)
                {
                    _manifestFiles.TryRemove(id, out _);
                    _installations.TryRemove(id, out _);
                }
                await RemoveInstallationStateAsync(id, ct);
                return true;
            }

            // It may still exist as a stale installation record.
            lock (_extensionSetMutationGate)
                _installations.TryRemove(id, out _);
            await RemoveInstallationStateAsync(id, ct);
            return false;
        }

        var wasDataExtension = ext is IDataExtension;
        using (var extensionLease = CreateExtensionExecutionLease(CaptureExtensionExecution(ext)))
        {
            try
            {
                await ext.OnUninstallAsync(extensionLease.Services, ct);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "OnUninstall failed for extension {Id}", id);
            }
        }

        await ShutdownExtensionCoreAsync(id, ct, retireOverlay: true);

        lock (_extensionSetMutationGate)
        {
            RemoveExtensionFromMemory(id);
            _installations.TryRemove(id, out _);
        }
        await RemoveInstallationStateAsync(id, ct);

        // A removed data extension's entity types should leave the EF model so the host stops mapping
        // tables that may be dropped. Refresh after the extension is gone from the loaded set.
        if (wasDataExtension)
            DataExtensionsChanged?.Invoke();

        // Encourage collectible AssemblyLoadContext cleanup. File operations do not
        // depend on this completing because extension binaries are loaded from cache.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return true;
    }

    // ========================================================================
    // MANIFEST AGGREGATION
    // ========================================================================

    private static string NamespaceKeyboardActionId(string extensionId, string actionId) =>
        actionId.StartsWith("extension:", StringComparison.Ordinal)
            || actionId.StartsWith("global.", StringComparison.Ordinal)
            || actionId.StartsWith("list.", StringComparison.Ordinal)
            || actionId.StartsWith("detail.", StringComparison.Ordinal)
            || actionId.StartsWith("player.", StringComparison.Ordinal)
            || actionId.StartsWith("viewer.", StringComparison.Ordinal)
            ? actionId
            : $"extension:{extensionId}:{actionId}";

    private static string? NamespaceKeyboardPresetId(string extensionId, string? presetId) =>
        string.IsNullOrWhiteSpace(presetId) || presetId.Contains(':', StringComparison.Ordinal)
            ? presetId
            : $"extension:{extensionId}:{presetId}";

    /// <summary>Get the aggregated UI manifest from all enabled extensions.</summary>
    public UIManifest GetAggregatedManifest()
    {
        var manifest = _context.UI.ToManifest();
        var tutorialTopicIds = new HashSet<string>(manifest.TutorialTopics.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);

        void AddTutorialTopics(IEnumerable<UITutorialTopic> topics, string? extensionId)
        {
            foreach (var topic in topics)
            {
                if (string.IsNullOrWhiteSpace(topic.Id) || string.IsNullOrWhiteSpace(topic.Title))
                    continue;

                var normalized = !string.IsNullOrWhiteSpace(extensionId)
                    ? topic with { ExtensionId = extensionId }
                    : topic;

                if (tutorialTopicIds.Add(normalized.Id))
                {
                    manifest.TutorialTopics.Add(normalized);
                }
            }
        }

        foreach (var ext in GetInitializationOrder().OfType<IUIExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            var extManifest = ExecuteExtensionMetadata(ext, ext.GetUIManifest);
            manifest.LoginMethods.AddRange(extManifest.LoginMethods.Select(method => method with { ExtensionId = ext.Id }));
            manifest.Pages.AddRange(extManifest.Pages.Select(page => page with { ExtensionId = ext.Id }));
            manifest.Slots.AddRange(extManifest.Slots.Select(slot => slot with { ExtensionId = ext.Id }));
            manifest.Tabs.AddRange(extManifest.Tabs.Select(tab => tab with { ExtensionId = ext.Id }));
            manifest.Panes.AddRange(extManifest.Panes.Select(pane => pane with { ExtensionId = ext.Id }));
            manifest.Features.AddRange(extManifest.Features.Select(feature => feature with { ExtensionId = ext.Id }));
            manifest.ComponentOverrides.AddRange(extManifest.ComponentOverrides.Select(componentOverride => componentOverride with { ExtensionId = ext.Id }));
            manifest.SelectorOverrides.AddRange(extManifest.SelectorOverrides.Select(selectorOverride => selectorOverride with { ExtensionId = ext.Id }));
            manifest.Themes.AddRange(extManifest.Themes);
            manifest.ComponentStyles.AddRange(extManifest.ComponentStyles);
            manifest.LayoutStyles.AddRange(extManifest.LayoutStyles);
            manifest.SettingsTabs.AddRange(extManifest.SettingsTabs.Select(tab => tab with { ExtensionId = ext.Id }));
            manifest.SettingsPanels.AddRange(extManifest.SettingsPanels.Select(panel => panel with { ExtensionId = ext.Id }));
            manifest.PageOverrides.AddRange(extManifest.PageOverrides.Select(pageOverride => pageOverride with { ExtensionId = ext.Id }));
            manifest.DialogOverrides.AddRange(extManifest.DialogOverrides.Select(dialogOverride => dialogOverride with { ExtensionId = ext.Id }));
            manifest.Actions.AddRange(extManifest.Actions.Select(action => action with { ExtensionId = ext.Id }));
            manifest.KeyboardActions.AddRange(extManifest.KeyboardActions.Select(action => action with
            {
                Id = $"extension:{ext.Id}:{action.Id}",
                ExtensionId = ext.Id,
            }));
            manifest.KeyboardShortcutPresets.AddRange(extManifest.KeyboardShortcutPresets.Select(preset => preset with
            {
                Id = $"extension:{ext.Id}:{preset.Id}",
                ExtensionId = ext.Id,
                BasePresetId = NamespaceKeyboardPresetId(ext.Id, preset.BasePresetId),
                Bindings = preset.Bindings.ToDictionary(
                    entry => NamespaceKeyboardActionId(ext.Id, entry.Key),
                    entry => entry.Value,
                    StringComparer.Ordinal),
            }));
            AddTutorialTopics(extManifest.TutorialTopics, ext.Id);
            manifest.ListFilters.AddRange(extManifest.ListFilters.Select(filter => filter with
            {
                ExtensionId = ext.Id,
                FilterId = string.IsNullOrWhiteSpace(filter.FilterId) ? null : filter.FilterId.Trim(),
            }));
            manifest.ListSorts.AddRange(extManifest.ListSorts.Select(sort => sort with { ExtensionId = ext.Id }));
        }

        var manifestIds = _manifestFiles.Keys
            .Concat(_installations.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var extensionId in manifestIds)
        {
            if (!IsEnabled(extensionId)) continue;
            var manifestFile = GetManifestFile(extensionId);
            if (manifestFile?.TutorialTopics.Count > 0)
            {
                AddTutorialTopics(manifestFile.TutorialTopics, manifestFile.Id);
            }
            if (manifestFile is not null && GetExtension(extensionId) is not IUIExtension)
            {
                manifest.KeyboardActions.AddRange(manifestFile.KeyboardActions.Select(action => action with
                {
                    Id = NamespaceKeyboardActionId(manifestFile.Id, action.Id),
                    ExtensionId = manifestFile.Id,
                }));
                manifest.KeyboardShortcutPresets.AddRange(manifestFile.KeyboardShortcutPresets.Select(preset => preset with
                {
                    Id = NamespaceKeyboardPresetId(manifestFile.Id, preset.Id)!,
                    ExtensionId = manifestFile.Id,
                    BasePresetId = NamespaceKeyboardPresetId(manifestFile.Id, preset.BasePresetId),
                    Bindings = preset.Bindings.ToDictionary(
                        entry => NamespaceKeyboardActionId(manifestFile.Id, entry.Key),
                        entry => entry.Value,
                        StringComparer.Ordinal),
                }));
            }
        }

        // Collect actions from IActionExtension instances
        foreach (var ext in GetInitializationOrder().OfType<IActionExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            var actions = ExecuteExtensionMetadata(ext, () => ext.GetActions().ToList());
            manifest.Actions.AddRange(actions.Select(action => action with { ExtensionId = ext.Id }));
        }

        manifest.Pages.Sort((a, b) => a.NavOrder.CompareTo(b.NavOrder));
        manifest.LoginMethods.Sort((a, b) =>
        {
            var order = a.Order.CompareTo(b.Order);
            if (order != 0) return order;

            var extensionId = string.Compare(a.ExtensionId, b.ExtensionId, StringComparison.Ordinal);
            return extensionId != 0
                ? extensionId
                : string.Compare(a.Id, b.Id, StringComparison.Ordinal);
        });
        manifest.Slots.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.Tabs.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.Panes.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ComponentOverrides.Sort((a, b) =>
        {
            var priority = b.Priority.CompareTo(a.Priority);
            if (priority != 0) return priority;

            var extensionId = string.Compare(a.ExtensionId, b.ExtensionId, StringComparison.Ordinal);
            return extensionId != 0
                ? extensionId
                : string.Compare(a.ComponentName, b.ComponentName, StringComparison.Ordinal);
        });
        manifest.SelectorOverrides.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        manifest.Actions.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.KeyboardActions.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.KeyboardShortcutPresets.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.TutorialTopics.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ListFilters.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ListSorts.Sort((a, b) => a.Order.CompareTo(b.Order));
        return manifest;
    }

    /// <summary>
    /// Return the safe, extension-owned subset of login methods that Cove may expose before
    /// authentication. Invalid, non-local, and duplicate declarations fail closed.
    /// </summary>
    public IReadOnlyList<ExtensionLoginMethod> GetExtensionLoginMethods()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var methods = new List<ExtensionLoginMethod>();
        foreach (var method in GetAggregatedManifest().LoginMethods)
        {
            var extensionId = method.ExtensionId?.Trim();
            var id = method.Id?.Trim();
            var label = method.Label?.Trim();
            var startUrl = method.StartUrl?.Trim();
            var linkStartUrl = method.LinkStartUrl?.Trim();
            if (!IsSafeLoginMethodValue(extensionId, 256)
                || !IsSafeLoginMethodValue(id, 128)
                || !IsSafeLoginMethodValue(label, 128)
                || !IsSafeLocalLoginStartUrl(startUrl)
                || (linkStartUrl is not null && !IsSafeLocalLoginStartUrl(linkStartUrl)))
            {
                continue;
            }

            var key = $"{extensionId}\n{id}";
            if (!seen.Add(key))
                continue;

            methods.Add(method with
            {
                ExtensionId = extensionId,
                Id = id!,
                Label = label!,
                StartUrl = startUrl!,
                LinkStartUrl = linkStartUrl,
            });
        }

        return methods;
    }

    private static bool IsSafeLoginMethodValue(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && !value.Any(char.IsControl);

    private static bool IsSafeLocalLoginStartUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 2048
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal)
            || value.Contains('\\')
            || value.Any(char.IsControl))
        {
            return false;
        }

        return Uri.TryCreate(value, UriKind.Relative, out _);
    }

    /// <summary>
    /// Resolve a namespaced contribution and atomically acquire its exact provider generation while
    /// holding the owner's lifecycle gate. The gate is released before this method returns; the
    /// returned execution alone pins the retired generation until its in-flight calls drain.
    /// </summary>
    async Task<IExtensionContributionExecution<TDeclaration, TRequest, TResult>?>
        IExtensionContributionRuntime.OpenContributionAsync<TDeclaration, TRequest, TResult>(
        string extensionId,
        string contributionId,
        Func<IExtension, IServiceProvider, string, ExtensionContributionBinding<TDeclaration, TRequest, TResult>?> bind,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extensionId)
            || string.IsNullOrWhiteSpace(contributionId))
            return null;
        ArgumentNullException.ThrowIfNull(bind);

        var normalizedExtensionId = extensionId.Trim();
        var normalizedContributionId = contributionId.Trim();
        if (normalizedExtensionId.Length > 256 || normalizedContributionId.Length > 256)
            return null;

        var lifecycleGate = GetExtensionLifecycleGate(normalizedExtensionId);
        await lifecycleGate.WaitAsync(ct);
        IServiceScope? provisionalScope = null;
        try
        {
            if (!IsEnabled(normalizedExtensionId)
                || !IsExtensionInitialized(normalizedExtensionId)
                || GetExtension(normalizedExtensionId) is not { } extension)
                return null;

            // Scope acquisition atomically checks the extension instance and provider generation.
            // Runtime extensions fail closed here; they never fall back to the host provider.
            provisionalScope = CreateExtensionScope(extension);
            var binding = bind(
                extension,
                provisionalScope.ServiceProvider,
                normalizedContributionId);
            if (binding is null)
                return null;

            var ownedScope = provisionalScope;
            provisionalScope = null;
            return new ExtensionContributionExecution<TDeclaration, TRequest, TResult>(
                new ExtensionContributionKey(extension.Id, normalizedContributionId),
                binding.Declaration,
                binding.ExecuteAsync,
                ownedScope);
        }
        finally
        {
            try
            {
                provisionalScope?.Dispose();
            }
            finally
            {
                lifecycleGate.Release();
            }
        }
    }

    private sealed class ExtensionContributionExecution<TDeclaration, TRequest, TResult>(
        ExtensionContributionKey key,
        TDeclaration declaration,
        Func<TRequest, CancellationToken, Task<TResult>> execute,
        IServiceScope scope) : IExtensionContributionExecution<TDeclaration, TRequest, TResult>
    {
        private readonly object _gate = new();
        private IServiceScope? _scope = scope;
        private int _activeCalls;
        private bool _disposeRequested;

        public ExtensionContributionKey Key { get; } = key;
        public TDeclaration Declaration { get; } = declaration;

        public Task<TResult> ExecuteAsync(TRequest request, CancellationToken ct)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposeRequested, this);
                _activeCalls++;
            }

            Task<TResult> task;
            try
            {
                task = execute(request, ct);
            }
            catch
            {
                ReleaseCall();
                throw;
            }

            return AwaitAndReleaseAsync(task);
        }

        public void Dispose()
        {
            IServiceScope? dispose = null;
            lock (_gate)
            {
                if (_disposeRequested)
                    return;

                _disposeRequested = true;
                if (_activeCalls == 0)
                {
                    dispose = _scope;
                    _scope = null;
                }
            }
            ReleaseResources(dispose);
        }

        private async Task<TResult> AwaitAndReleaseAsync(Task<TResult> task)
        {
            try
            {
                return await task;
            }
            finally
            {
                ReleaseCall();
            }
        }

        private void ReleaseCall()
        {
            IServiceScope? dispose = null;
            lock (_gate)
            {
                _activeCalls--;
                if (_activeCalls == 0 && _disposeRequested)
                {
                    dispose = _scope;
                    _scope = null;
                }
            }
            ReleaseResources(dispose);
        }

        private void ReleaseResources(IServiceScope? dispose)
        {
            dispose?.Dispose();
        }
    }

    /// <summary>Get enabled extension UI JS bundle asset paths (extensionId + relative path).</summary>
    public IReadOnlyList<(string ExtensionId, string Path)> GetEnabledJsBundles()
    {
        var bundles = new List<(string ExtensionId, string Path)>();
        foreach (var ext in GetInitializationOrder().OfType<IUIExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            if (_manifestFiles.TryGetValue(ext.Id, out var mf) && !string.IsNullOrWhiteSpace(mf.JsBundle))
            {
                bundles.Add((ext.Id, mf.JsBundle));
            }
        }
        return bundles;
    }

    /// <summary>Get enabled extension UI CSS bundle asset paths (extensionId + relative path).</summary>
    public IReadOnlyList<(string ExtensionId, string Path)> GetEnabledCssBundles()
    {
        var bundles = new List<(string ExtensionId, string Path)>();
        foreach (var ext in GetInitializationOrder().OfType<IUIExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            if (_manifestFiles.TryGetValue(ext.Id, out var mf) && !string.IsNullOrWhiteSpace(mf.CssBundle))
            {
                bundles.Add((ext.Id, mf.CssBundle));
            }
        }
        return bundles;
    }

    // ========================================================================
    // ENABLE / DISABLE
    // ========================================================================

    /// <summary>Check if an extension is enabled.</summary>
    public bool IsEnabled(string id)
    {
        lock (_installationStateGate)
            return _installations.TryGetValue(id, out var inst) ? inst.Enabled : true;
    }

    /// <summary>Enable an extension and any installed extensions it depends on. Persists the state to DB.</summary>
    public async Task<IReadOnlyList<string>> EnableExtensionAsync(string id, CancellationToken ct = default)
    {
        while (true)
        {
            ExtensionRegistrySnapshot registry;
            List<string> idsToEnable;
            lock (_extensionSetMutationGate)
            {
                registry = GetExtensionRegistry();
                idsToEnable = GetDependencyExtensionIds(id)
                    .Append(id)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (idsToEnable.Any(_unloadOperations.ContainsKey))
                    return [];
            }

            var enabledIds = new List<string>();
            var plannedIds = idsToEnable.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var restart = false;
            var blockedByUnload = false;
            foreach (var extensionId in idsToEnable)
            {
                var lifecycleGate = GetExtensionLifecycleGate(extensionId);
                await lifecycleGate.WaitAsync(ct);
                try
                {
                    var canEnable = false;
                    lock (_extensionSetMutationGate)
                    {
                        registry.Map.TryGetValue(extensionId, out var expectedExtension);
                        var currentExtension = GetExtension(extensionId);
                        var declaredDependencies = GetDeclaredDependencies(extensionId).Keys.ToList();
                        var knownDependencies = declaredDependencies
                            .Where(dependencyId => GetExtension(dependencyId) != null
                                || _installations.ContainsKey(dependencyId)
                                || _manifestFiles.ContainsKey(dependencyId))
                            .ToList();
                        var dependencyGenerationChanged = declaredDependencies.Any(dependencyId =>
                        {
                            registry.Map.TryGetValue(dependencyId, out var expectedDependency);
                            var currentDependency = GetExtension(dependencyId);
                            return !ReferenceEquals(expectedDependency, currentDependency);
                        });

                        if (_unloadOperations.ContainsKey(extensionId)
                            || declaredDependencies.Any(_unloadOperations.ContainsKey))
                        {
                            blockedByUnload = true;
                        }
                        else if (!ReferenceEquals(expectedExtension, currentExtension)
                            || dependencyGenerationChanged
                            || knownDependencies.Any(dependencyId => !plannedIds.Contains(dependencyId)))
                        {
                            restart = true;
                        }
                        else if (knownDependencies.Any(dependencyId => !IsEnabled(dependencyId)))
                        {
                            restart = true;
                        }
                        else if (knownDependencies.Count == declaredDependencies.Count
                            && EnsureInstallationRecord(extensionId) != null)
                        {
                            TryUpdateInstallation(extensionId, install =>
                            {
                                install.Enabled = true;
                                install.UpdatedAt = DateTime.UtcNow;
                            });
                            canEnable = true;
                        }
                    }

                    if (restart || blockedByUnload || !canEnable)
                        break;

                    await PersistInstallationStateAsync(extensionId, ct);
                    if (IsOverlayExtension(extensionId) && !IsExtensionInitialized(extensionId))
                    {
                        if (!BuildExtensionProviderCore(extensionId))
                        {
                            await ShutdownExtensionCoreAsync(extensionId, ct, retireOverlay: true);
                            await PersistInstallationStateAsync(extensionId, ct);
                            return enabledIds;
                        }
                    }

                    enabledIds.Add(extensionId);
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }

            if (blockedByUnload || !restart)
                return enabledIds;
        }
    }

    /// <summary>Disable an extension and any enabled extensions that depend on it. Persists the state to DB.</summary>
    public async Task<IReadOnlyList<string>> DisableExtensionAsync(string id, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        List<string> idsToDisable;
        lock (_extensionSetMutationGate)
        {
            idsToDisable = GetDependentExtensionIds(id, enabledOnly: true)
                .Append(id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Publish the disabled state for the whole dependency closure before releasing the set
            // mutation gate. A concurrently registered dependent will therefore inherit disabled.
            foreach (var extensionId in idsToDisable)
            {
                if (EnsureInstallationRecord(extensionId) != null)
                    TryUpdateInstallation(extensionId, install => install.Enabled = false);
            }
        }

        var disabledIds = new List<string>();

        foreach (var extensionId in idsToDisable)
        {
            var lifecycleGate = GetExtensionLifecycleGate(extensionId);
            await lifecycleGate.WaitAsync(CancellationToken.None);
            try
            {
                var inst = EnsureInstallationRecord(extensionId);
                if (inst == null)
                    continue;

                TryUpdateInstallation(extensionId, install =>
                {
                    install.Enabled = false;
                    install.UpdatedAt = DateTime.UtcNow;
                });
                await PersistInstallationStateAsync(extensionId, CancellationToken.None);
                disabledIds.Add(extensionId);
                await ShutdownExtensionCoreAsync(extensionId, CancellationToken.None, retireOverlay: true);
            }
            finally
            {
                lifecycleGate.Release();
            }
        }

        return disabledIds;
    }

    /// <summary>Update persisted install metadata for extensions installed after startup.</summary>
    public async Task SetInstallationMetadataAsync(string id, string source, string? version = null, CancellationToken ct = default)
    {
        var lifecycleGate = GetExtensionLifecycleGate(id);
        await lifecycleGate.WaitAsync(ct);
        try
        {
            var inst = EnsureInstallationRecord(id);
            if (inst == null) return;
            TryUpdateInstallation(id, install =>
            {
                install.Source = source;
                if (!string.IsNullOrWhiteSpace(version))
                    install.Version = version.Trim();
                install.UpdatedAt = DateTime.UtcNow;
            });
            await PersistInstallationStateAsync(id, ct);
        }
        finally
        {
            lifecycleGate.Release();
        }
    }

    /// <summary>Update only the persisted install source for an extension.</summary>
    public Task SetInstallationSourceAsync(string id, string source, CancellationToken ct = default) =>
        SetInstallationMetadataAsync(id, source, null, ct);

    /// <summary>Get the installation record for an extension.</summary>
    public ExtensionInstallation? GetInstallation(string id)
    {
        lock (_installationStateGate)
            return _installations.TryGetValue(id, out var inst) ? CloneInstallation(inst) : null;
    }

    /// <summary>Get the manifest metadata for an extension or bundle.</summary>
    public ExtensionManifestFile? GetManifestFile(string id)
    {
        if (_manifestFiles.TryGetValue(id, out var manifest))
            return manifest;

        var install = GetInstallation(id);
        if (!string.IsNullOrWhiteSpace(install?.ManifestJson))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(install.ManifestJson,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (manifest != null)
                {
                    _manifestFiles[id] = manifest;
                    return manifest;
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Returns true when the installation is metadata-only and has no runtime DLL.</summary>
    public bool IsManifestOnlyExtension(string id) =>
        IsManifestOnlyKind(GetManifestFile(id)?.Kind);

    /// <summary>Get installed manifest-only package directories for enabled packages of a specific kind.</summary>
    public IReadOnlyList<(string ExtensionId, string Directory)> GetEnabledManifestDirectories(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
            return [];

        return _manifestFiles.Values
            .Where(manifest => string.Equals(manifest.Kind, kind, StringComparison.OrdinalIgnoreCase))
            .Where(manifest => IsEnabled(manifest.Id))
            .Select(manifest => (manifest.Id, Directory: ResolveExtensionDirectory(manifest.Id)))
            .Where(item => item.Directory != null)
            .Select(item => (item.Id, item.Directory!))
            .ToList();
    }

    /// <summary>Get all installation records.</summary>
    public IReadOnlyDictionary<string, ExtensionInstallation> Installations
    {
        get
        {
            lock (_installationStateGate)
            {
                return _installations.ToDictionary(
                    pair => pair.Key,
                    pair => CloneInstallation(pair.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    private bool TryUpdateInstallation(string id, Action<ExtensionInstallation> update)
    {
        lock (_installationStateGate)
        {
            if (!_installations.TryGetValue(id, out var installation))
                return false;

            update(installation);
            return true;
        }
    }

    private static ExtensionInstallation CloneInstallation(ExtensionInstallation installation) => new()
    {
        ExtensionId = installation.ExtensionId,
        Version = installation.Version,
        Enabled = installation.Enabled,
        InstalledAt = installation.InstalledAt,
        UpdatedAt = installation.UpdatedAt,
        ManifestJson = installation.ManifestJson,
        Source = installation.Source,
        Categories = installation.Categories,
    };

    public string? GetExtensionDirectory(string id) => ResolveExtensionDirectory(id);

    public string? GetLastFailureReason(string id) =>
        _extensionFailureReasons.TryGetValue(id, out var reason) ? reason : null;

    public bool IsEffectivelyInstalled(string id)
    {
        if (GetExtension(id) != null) return true;
        var dir = ResolveExtensionDirectory(id);
        return dir != null;
    }

    private static bool IsManifestOnlyKind(string? kind) =>
        string.Equals(kind, "bundle", StringComparison.OrdinalIgnoreCase)
        || string.Equals(kind, "scraper-pack", StringComparison.OrdinalIgnoreCase);

    private string? ResolveExtensionDirectory(string id)
    {
        if (_extensionDirectories.TryGetValue(id, out var directory) && Directory.Exists(directory))
            return directory;

        var conventionalDirectory = Path.Combine(_context.DataDirectory, id);
        return Directory.Exists(conventionalDirectory) ? conventionalDirectory : null;
    }

    private static string ResolveInstalledVersion(
        string runtimeVersion,
        ExtensionManifestFile? manifest,
        ExtensionInstallation? install,
        string? source)
    {
        var effectiveSource = !string.IsNullOrWhiteSpace(source)
            ? source
            : install?.Source;

        if (string.Equals(effectiveSource, "registry", StringComparison.OrdinalIgnoreCase)
            || string.Equals(effectiveSource, "url", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(install?.Version))
                return install.Version;

            if (!string.IsNullOrWhiteSpace(manifest?.Version))
                return manifest.Version;
        }

        // The manifest is the authoritative declared version (it is what the registry
        // publishes), so prefer it over the compiled-in runtime Version property. A
        // code/manifest drift (runtime reporting an older version than the manifest)
        // must not be re-stamped onto the persisted installation on every restart,
        // otherwise the "update available" check never clears after updating.
        if (!string.IsNullOrWhiteSpace(manifest?.Version))
            return manifest.Version;

        if (!string.IsNullOrWhiteSpace(runtimeVersion))
            return runtimeVersion;

        return install?.Version ?? "0.0.0";
    }

    // ========================================================================
    // EVENTS
    // ========================================================================

    /// <summary>Dispatch an entity event to all enabled IEventExtension instances.</summary>
    public async Task DispatchEventAsync(ExtensionEvent evt, CancellationToken ct = default)
    {
        foreach (var ext in GetInitializationOrder().OfType<IEventExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            try
            {
                _logger?.LogTrace(
                    "Dispatching event {EventType} to extension {Id}",
                    evt.EventType,
                    ext.Id);
                var execution = CaptureExtensionExecution(ext);
                await ExecuteExtensionAsync(execution, () => ext.OnEventAsync(evt, ct));
                _logger?.LogTrace(
                    "Extension {Id} handled event {EventType}",
                    ext.Id,
                    evt.EventType);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Extension {Id} failed handling event {EventType}", ext.Id, evt.EventType);
            }
        }
    }

    // ========================================================================
    // SCAN PARTICIPANTS
    // ========================================================================

    /// <summary>Get all enabled extensions that participate in the core library scan.</summary>
    public IReadOnlyList<IScanParticipant> GetScanParticipants()
    {
        return GetInitializationOrder()
            .OfType<IScanParticipant>()
            .Where(ext => IsEnabled(ext.Id))
            .ToList();
    }

    /// <summary>Get all enabled scraper providers.</summary>
    public IReadOnlyList<IScraperProvider> GetScraperProviders()
    {
        return GetInitializationOrder()
            .OfType<IScraperProvider>()
            .Where(ext => IsEnabled(ext.Id))
            .ToList();
    }

    /// <summary>Get all enabled downloader providers.</summary>
    public IReadOnlyList<IDownloaderProvider> GetDownloaderProviders()
    {
        return GetInitializationOrder()
            .OfType<IDownloaderProvider>()
            .Where(ext => IsEnabled(ext.Id))
            .ToList();
    }

    // ========================================================================
    // JOBS
    // ========================================================================

    /// <summary>Get all job definitions across all enabled IJobExtension instances.</summary>
    public IEnumerable<(IJobExtension Extension, ExtensionJobDefinition Job)> GetAllJobs()
    {
        foreach (var ext in GetExtensionRegistry().Extensions.OfType<IJobExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            var jobs = ExecuteExtensionMetadata(ext, () => ext.Jobs.ToList());
            foreach (var job in jobs)
                yield return (ext, job);
        }
    }

    // ========================================================================
    // CATEGORIES
    // ========================================================================

    /// <summary>Get all unique categories across all extensions.</summary>
    public IReadOnlyList<string> GetAllCategories()
    {
        return GetExtensionRegistry().Extensions
            .SelectMany(e => ExecuteExtensionMetadata(e, () => e.Categories.ToList()))
            .Concat(_manifestFiles.Values.SelectMany(manifest => manifest.Categories))
            .Concat(Installations.Values
                .Where(i => i.Categories != null)
                .SelectMany(i => i.Categories!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    /// <summary>Get extensions matching any of the given categories.</summary>
    public IReadOnlyList<IExtension> GetExtensionsByCategory(params string[] categories)
    {
        var catSet = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        return GetExtensionRegistry().Extensions
            .Where(e => ExecuteExtensionMetadata(e, () => e.Categories.Any(catSet.Contains))
                || (_manifestFiles.TryGetValue(e.Id, out var manifest) && manifest.Categories.Any(catSet.Contains)))
            .ToList();
    }

    private static string? SerializeCategories(IReadOnlyList<string> runtimeCategories, ExtensionManifestFile? manifestFile)
    {
        var categories = runtimeCategories
            .Concat(manifestFile?.Categories ?? [])
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return categories.Count > 0 ? string.Join(",", categories) : null;
    }

    // ========================================================================
    // EXTENSION MIGRATIONS
    // ========================================================================

    private async Task ApplyExtensionMigrationsAsync(IServiceProvider services, string extensionId, CancellationToken ct)
    {
        var dataExtensions = GetInitializationOrder()
            .OfType<IDataExtension>()
            .Where(extension => string.Equals(extension.Id, extensionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (dataExtensions.Count == 0) return;

        await _extensionMigrationGate.WaitAsync(ct);
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetService<DbContext>();
            if (db?.Database is null) return;

            // Extension migrations can execute arbitrary schema SQL, including changes to shared core
            // tables, so serialize them globally even though lifecycle transitions are per extension.
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE IF NOT EXISTS extension_migrations (
                    extension_id VARCHAR(256) NOT NULL,
                    migration_name VARCHAR(512) NOT NULL,
                    applied_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                    PRIMARY KEY (extension_id, migration_name)
                )
                """, ct);

            foreach (var ext in dataExtensions)
            {
                if (!IsEnabled(ext.Id)) continue;
                var migrations = ext.GetMigrations();
                if (migrations.Count == 0) continue;

                // Get already-applied migrations for this extension
                var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    using var cmd = db.Database.GetDbConnection().CreateCommand();
                    cmd.CommandText = "SELECT migration_name FROM extension_migrations WHERE extension_id = @id";
                    var param = cmd.CreateParameter();
                    param.ParameterName = "@id";
                    param.Value = ext.Id;
                    cmd.Parameters.Add(param);

                    if (cmd.Connection?.State != System.Data.ConnectionState.Open)
                        await cmd.Connection!.OpenAsync(ct);

                    using var reader = await cmd.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct))
                        applied.Add(reader.GetString(0));
                }
                catch
                {
                    // Table might not exist yet on first run
                }

                // Apply pending migrations
                foreach (var migration in migrations)
                {
                    if (applied.Contains(migration.Name)) continue;
                    try
                    {
                        _logger?.LogInformation("Applying extension migration {ExtId}/{Name}", ext.Id, migration.Name);
                        await ApplyExtensionMigrationAsync(db, ext.Id, migration, ct);
                        _logger?.LogInformation("Applied extension migration {ExtId}/{Name}", ext.Id, migration.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Failed to apply extension migration {ExtId}/{Name}", ext.Id, migration.Name);
                        break; // Stop applying migrations for this extension on failure
                    }
                }
            }
        }
        finally
        {
            _extensionMigrationGate.Release();
        }
    }

    internal static async Task ApplyExtensionMigrationAsync(
        DbContext db,
        string extensionId,
        ExtensionMigration migration,
        CancellationToken ct)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            // A retry after an ambiguous commit must not run non-idempotent migration SQL twice.
            // The receipt is committed in the same transaction, so its presence verifies that the
            // preceding attempt completed.
            if (await HasExtensionMigrationReceiptAsync(db, extensionId, migration.Name, ct))
                return;

            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            try
            {
                // Extension migrations are complete SQL scripts, not composite format strings.
                // Execute them directly so literal braces such as PostgreSQL JSON defaults
                // are never interpreted as formatting placeholders.
                var connection = db.Database.GetDbConnection();
                if (connection.State != System.Data.ConnectionState.Open)
                    await connection.OpenAsync(ct);
                await using var migrationCommand = connection.CreateCommand();
                migrationCommand.Transaction = transaction.GetDbTransaction();
                migrationCommand.CommandText = migration.UpSql;
                await migrationCommand.ExecuteNonQueryAsync(ct);
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO extension_migrations (extension_id, migration_name) VALUES ({0}, {1})",
                    extensionId, migration.Name);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                // Preserve the operation/commit exception so the execution strategy can classify it.
                // A broken connection or an ambiguous successful commit can also make rollback fail;
                // that secondary failure must not prevent a retry from verifying the receipt.
                try
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // Disposal still releases the transaction. The original exception is rethrown.
                }
                throw;
            }
        });
    }

    private static async Task<bool> HasExtensionMigrationReceiptAsync(
        DbContext db,
        string extensionId,
        string migrationName,
        CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            await connection.OpenAsync(ct);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM extension_migrations
            WHERE extension_id = @extension_id AND migration_name = @migration_name
            """;
        var extensionIdParameter = command.CreateParameter();
        extensionIdParameter.ParameterName = "@extension_id";
        extensionIdParameter.Value = extensionId;
        command.Parameters.Add(extensionIdParameter);
        var migrationNameParameter = command.CreateParameter();
        migrationNameParameter.ParameterName = "@migration_name";
        migrationNameParameter.Value = migrationName;
        command.Parameters.Add(migrationNameParameter);
        return await command.ExecuteScalarAsync(ct) is not null;
    }

    // ========================================================================
    // INSTALLATION STATE PERSISTENCE
    // ========================================================================

    private async Task LoadInstallationStateAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            var extensionIds = new List<string>();
            using (var discoveryScope = services.CreateScope())
            {
                var discoveryDb = discoveryScope.ServiceProvider.GetService<DbContext>();
                if (discoveryDb?.Database is null) return;

                await discoveryDb.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE IF NOT EXISTS extension_installations (
                        extension_id VARCHAR(256) PRIMARY KEY,
                        version VARCHAR(64) NOT NULL,
                        enabled BOOLEAN NOT NULL DEFAULT TRUE,
                        installed_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                        manifest_json TEXT,
                        source VARCHAR(64) NOT NULL DEFAULT 'local',
                        categories TEXT
                    )
                    """, ct);

                using var idsCommand = discoveryDb.Database.GetDbConnection().CreateCommand();
                idsCommand.CommandText = "SELECT extension_id FROM extension_installations";
                if (idsCommand.Connection?.State != System.Data.ConnectionState.Open)
                    await idsCommand.Connection!.OpenAsync(ct);

                using var idsReader = await idsCommand.ExecuteReaderAsync(ct);
                while (await idsReader.ReadAsync(ct))
                    extensionIds.Add(idsReader.GetString(0));
            }

            foreach (var id in extensionIds)
            {
                var lifecycleGate = GetExtensionLifecycleGate(id);
                await lifecycleGate.WaitAsync(ct);
                try
                {
                    using var rowScope = services.CreateScope();
                    var rowDb = rowScope.ServiceProvider.GetService<DbContext>();
                    if (rowDb?.Database is null)
                        continue;

                    using var rowCommand = rowDb.Database.GetDbConnection().CreateCommand();
                    rowCommand.CommandText = "SELECT extension_id, version, enabled, installed_at, updated_at, manifest_json, source, categories FROM extension_installations WHERE extension_id = @id";
                    var idParameter = rowCommand.CreateParameter();
                    idParameter.ParameterName = "@id";
                    idParameter.Value = id;
                    rowCommand.Parameters.Add(idParameter);

                    if (rowCommand.Connection?.State != System.Data.ConnectionState.Open)
                        await rowCommand.Connection!.OpenAsync(ct);

                    using var reader = await rowCommand.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct))
                        continue;

                    if (_installations.ContainsKey(id))
                    {
                        // The row is read while this extension's lifecycle gate is held, so DB state
                        // cannot overwrite an in-flight enable/disable transition with a stale snapshot.
                        TryUpdateInstallation(id, installation =>
                        {
                            installation.Enabled = reader.GetBoolean(2);
                            installation.InstalledAt = EnsureUtc(reader.GetDateTime(3));
                            installation.UpdatedAt = EnsureUtc(reader.GetDateTime(4));
                            installation.ManifestJson = reader.IsDBNull(5) ? null : reader.GetString(5);
                            installation.Source = reader.GetString(6);
                            if (!reader.IsDBNull(7)) installation.Categories = reader.GetString(7);
                        });
                    }
                    else
                    {
                        // Extension in DB but not loaded (maybe removed from disk)
                        _installations[id] = new ExtensionInstallation
                        {
                            ExtensionId = id,
                            Version = reader.GetString(1),
                            Enabled = reader.GetBoolean(2),
                            InstalledAt = EnsureUtc(reader.GetDateTime(3)),
                            UpdatedAt = EnsureUtc(reader.GetDateTime(4)),
                            ManifestJson = reader.IsDBNull(5) ? null : reader.GetString(5),
                            Source = reader.GetString(6),
                            Categories = reader.IsDBNull(7) ? null : reader.GetString(7),
                        };
                    }
                }
                finally
                {
                    lifecycleGate.Release();
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load extension installation state from database");
        }
    }

    private async Task SaveInstallationAsync(IServiceProvider services, string extensionId, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetService<DbContext>();
            if (db?.Database is null) return;

            var install = GetInstallation(extensionId);
            if (install == null) return;

            await db.Database.ExecuteSqlRawAsync("""
                INSERT INTO extension_installations (extension_id, version, enabled, installed_at, updated_at, manifest_json, source, categories)
                VALUES ({0}, {1}, {2}, {3}, {4}, {5}, {6}, {7})
                ON CONFLICT (extension_id) DO UPDATE SET
                    version = EXCLUDED.version,
                    enabled = EXCLUDED.enabled,
                    updated_at = EXCLUDED.updated_at,
                    manifest_json = EXCLUDED.manifest_json,
                    source = EXCLUDED.source,
                    categories = EXCLUDED.categories
                """,
                install.ExtensionId, install.Version, install.Enabled,
                EnsureUtc(install.InstalledAt), DateTime.UtcNow, (object?)install.ManifestJson ?? DBNull.Value,
                install.Source, (object?)install.Categories ?? DBNull.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save extension installation state for {Id}", extensionId);
        }
    }

    private async Task PersistInstallationStateAsync(string extensionId, CancellationToken ct)
    {
        if (_scopeFactory == null) return;
        using var scope = _scopeFactory.CreateScope();
        await SaveInstallationAsync(scope.ServiceProvider, extensionId, ct);
    }

    private void DisableExtensionForStartupFailure(string extensionId, Exception ex, string phase)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            return;

        lock (_installationStateGate)
        {
            if (_installations.TryGetValue(extensionId, out var install))
            {
                install.Enabled = false;
            }
            else
            {
                _installations[extensionId] = new ExtensionInstallation
                {
                    ExtensionId = extensionId,
                    Version = "0.0.0",
                    Enabled = false,
                    Source = "local",
                };
            }
        }

        _startupDisabledExtensions[extensionId] = 0;
        MarkExtensionUninitialized(extensionId);
        _extensionFailureReasons[extensionId] = $"{phase}: {ex.GetType().Name}: {ex.Message}";
        _logger?.LogError(ex, "Extension {Id} failed during {Phase} and was disabled", extensionId, phase);
    }

    private async Task RemoveInstallationStateAsync(string extensionId, CancellationToken ct)
    {
        if (_scopeFactory == null) return;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<DbContext>();
            if (db?.Database is null) return;

            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM extension_installations WHERE extension_id = {0}",
                extensionId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not remove extension installation state for {Id}", extensionId);
        }
    }

    private void CaptureScopeFactory(IServiceProvider services)
    {
        _scopeFactory ??= services.GetService<IServiceScopeFactory>();
    }

    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private ExtensionInstallation? EnsureInstallationRecord(string id)
    {
        lock (_installationStateGate)
        {
            if (_installations.TryGetValue(id, out var existing))
                return existing;

            if (GetExtension(id) is not { } ext)
                return null;

            var manifest = GetManifestFile(id);
            var install = new ExtensionInstallation
            {
                ExtensionId = id,
                Version = ResolveInstalledVersion(ext.Version, manifest, null, manifest?.RegistryUrl != null ? "registry" : "local"),
                Enabled = ext.Dependencies.Keys.All(IsEnabled),
                Source = manifest?.RegistryUrl != null ? "registry" : "local",
                InstalledAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                ManifestJson = manifest != null ? JsonSerializer.Serialize(manifest, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) : null,
                Categories = ext.Categories.Count > 0 ? string.Join(",", ext.Categories) : null,
            };
            _installations[id] = install;
            return install;
        }
    }

    private IReadOnlyList<string> GetKnownExtensionIds() => _installations.Keys
        .Concat(GetExtensionRegistry().Map.Keys)
        .Concat(_manifestFiles.Keys)
        .Where(id => !string.IsNullOrWhiteSpace(id))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    private IReadOnlyDictionary<string, string> GetDeclaredDependencies(string id)
    {
        var dependencies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var manifest = GetManifestFile(id);
        if (manifest != null)
        {
            foreach (var dependency in manifest.Dependencies)
                dependencies[dependency.Key] = dependency.Value;
        }

        if (GetExtension(id) is { } extension)
        {
            foreach (var dependency in extension.Dependencies)
                dependencies[dependency.Key] = dependency.Value;
        }

        return dependencies;
    }

    private void RemoveExtensionFromMemory(string id)
    {
        MarkExtensionUninitialized(id);

        RemoveExtension(id);

        _manifestFiles.TryRemove(id, out _);
        _extensionDirectories.TryRemove(id, out _);
        // Drop the active-slot record so the now-unreferenced shadow-copy slot is reaped on the
        // next discovery pass (it stays protected only while the extension is loaded).
        _loadCacheSlots.TryRemove(id, out _);

        if (_loadContexts.TryRemove(id, out var context))
        {
            try
            {
                context.Unload();
            }
            catch
            {
                // Best-effort unload; file handles may still be released after GC.
            }
        }
    }

    // ========================================================================
    // INTERNALS
    // ========================================================================

    /// <summary>
    /// Basic semver comparison. Supports: ">=X.Y.Z", "<=X.Y.Z", ">X.Y.Z", "&lt;X.Y.Z", "=X.Y.Z", "X.Y.Z" (exact match).
    /// </summary>
    internal static bool SemverSatisfies(string version, string range)
    {
        range = range.Trim();
        string op;
        string target;

        if (range.StartsWith(">="))
        {
            op = ">="; target = range[2..].Trim();
        }
        else if (range.StartsWith("<="))
        {
            op = "<="; target = range[2..].Trim();
        }
        else if (range.StartsWith('>'))
        {
            op = ">"; target = range[1..].Trim();
        }
        else if (range.StartsWith('<'))
        {
            op = "<"; target = range[1..].Trim();
        }
        else if (range.StartsWith('='))
        {
            op = "="; target = range[1..].Trim();
        }
        else
        {
            op = "="; target = range;
        }

        if (!TryParseSemver(version, out var v) || !TryParseSemver(target, out var t))
            return false;

        var cmp = v.CompareTo(t);
        return op switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            "=" => cmp == 0,
            _ => false,
        };
    }

    private static bool TryParseSemver(string s, out Version version)
    {
        // Strip leading 'v' and any prerelease suffix
        s = s.TrimStart('v');
        var dashIdx = s.IndexOf('-');
        if (dashIdx >= 0) s = s[..dashIdx];
        return Version.TryParse(s, out version!);
    }

    private ExtensionBinaryCache PrepareExtensionBinaryCache(string extensionsRoot, string extensionDir, string? extensionId)
    {
        var cacheKey = !string.IsNullOrWhiteSpace(extensionId)
            ? extensionId
            : new DirectoryInfo(extensionDir).Name;
        var extensionCacheRoot = Path.Combine(extensionsRoot, ".load-cache", cacheKey);
        Directory.CreateDirectory(extensionCacheRoot);

        // Eagerly reap this extension's previous slots before allocating a new one, so a long-lived
        // session that installs/reinstalls many times cannot accumulate orphaned slots. The slot the
        // extension is currently loaded from (recorded in _loadCacheSlots) is preserved; any slot
        // still locked by a not-yet-collected load context fails to delete and is reaped on a later
        // pass. (Slots also get a full sweep at startup discovery, covering a force-terminated run.)
        CleanupExtensionLoadCacheSlots(cacheKey, extensionCacheRoot);

        // Copy the binaries into a brand-new slot directory on every load. A never-before-used
        // directory cannot be locked by an earlier AssemblyLoadContext that has not yet been
        // collected, so the copy can never fail with a sharing violation. Reusing a fixed "a"/"b"
        // slot used to throw UnauthorizedAccessException ("Access to the path 'X.dll' is denied")
        // when an extension was reinstalled before its previous load context unloaded, which
        // aborted discovery and left the extension permanently failing to initialize.
        var slot = "s-" + Guid.NewGuid().ToString("N");
        var cacheRoot = Path.Combine(extensionCacheRoot, slot);
        Directory.CreateDirectory(cacheRoot);

        foreach (var sourcePath in Directory.GetFiles(extensionDir, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extensionDir, sourcePath);
            var destinationPath = Path.Combine(cacheRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return new ExtensionBinaryCache(cacheKey, slot, extensionDir, cacheRoot);
    }

    private void CleanupStaleLoadCaches(string extensionsRoot, IReadOnlyCollection<string> extensionDirectories)
    {
        var loadCacheRoot = Path.Combine(extensionsRoot, ".load-cache");
        if (!Directory.Exists(loadCacheRoot))
            return;

        var installedExtensionIds = extensionDirectories
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cacheDir in Directory.GetDirectories(loadCacheRoot))
        {
            var cacheName = Path.GetFileName(cacheDir);
            if (string.Equals(cacheName, "__shared", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!installedExtensionIds.Contains(cacheName))
            {
                TryDeleteDirectory(cacheDir);
                continue;
            }

            CleanupExtensionLoadCacheSlots(cacheName, cacheDir);
        }
    }

    /// <summary>
    /// Delete every shadow-copy slot for one extension except the slot it is currently loaded from.
    /// Slots still locked by a load context that has not yet been collected simply fail to delete
    /// (best effort) and are reaped on a later discovery pass once the context is gone.
    /// </summary>
    private void CleanupExtensionLoadCacheSlots(string cacheKey, string extensionCacheRoot)
    {
        var activeSlot = _loadCacheSlots.GetValueOrDefault(cacheKey);
        foreach (var slotDir in Directory.GetDirectories(extensionCacheRoot))
        {
            var slotName = Path.GetFileName(slotDir);
            if (activeSlot != null && string.Equals(slotName, activeSlot, StringComparison.OrdinalIgnoreCase))
                continue;

            TryDeleteDirectory(slotDir);
        }
    }

    private static void RemoveReadOnlyAttributes(string rootPath)
    {
        var rootInfo = new DirectoryInfo(rootPath);
        foreach (var directory in rootInfo.EnumerateDirectories("*", SearchOption.AllDirectories))
            directory.Attributes = FileAttributes.Normal;

        foreach (var file in rootInfo.EnumerateFiles("*", SearchOption.AllDirectories))
            file.Attributes = FileAttributes.Normal;

        rootInfo.Attributes = FileAttributes.Normal;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                RemoveReadOnlyAttributes(path);
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only; stale shadow copies are safe.
        }
    }

    private sealed record ExtensionBinaryCache(string CacheKey, string Slot, string SourceRoot, string CacheRoot)
    {
        public string GetCachedPath(string sourcePath)
        {
            var relativePath = Path.GetRelativePath(SourceRoot, sourcePath);
            return Path.Combine(CacheRoot, relativePath);
        }
    }
}

internal sealed class ExtensionLoadContext : AssemblyLoadContext
{
    private static readonly JsonSerializerOptions ManifestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly object SharedAssemblyGate = new();
    private static readonly Dictionary<string, string> PreferredSharedAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> WarnedBundledHostAssemblies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised once per assembly when an extension ships a copy of an assembly the host already provides.
    /// The host copy is always used (so types never split across load contexts); this only surfaces the
    /// packaging mistake so the extension can be slimmed. Wired to the logger by <see cref="ExtensionManager"/>.
    /// </summary>
    internal static Action<string>? HostAssemblyBundledWarning;

    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _sourceRoot;
    private readonly string _cacheRoot;

    public ExtensionLoadContext(string mainAssemblyPath, string? sourceRoot = null, string? cacheRoot = null)
        : base($"extension:{Path.GetFileNameWithoutExtension(mainAssemblyPath)}:{Guid.NewGuid():N}", isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        _sourceRoot = Path.GetFullPath(sourceRoot ?? Path.GetDirectoryName(mainAssemblyPath)!);
        _cacheRoot = Path.GetFullPath(cacheRoot ?? _sourceRoot);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // 1. Prefer an assembly already loaded into the default (host) context — guarantees shared identity.
        var defaultAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName));
        if (defaultAssembly != null)
            return defaultAssembly;

        // 2. Cross-extension shared assemblies (e.g. AI.Extensions.Abstractions): one curated copy in Default
        //    so sibling extensions exchange the same types.
        if (assemblyName.Name is string sharedAssemblyName && PreferredSharedAssemblyPaths.ContainsKey(sharedAssemblyName))
        {
            var sharedAssembly = TryLoadSharedAssembly(assemblyName);
            if (sharedAssembly != null)
                return sharedAssembly;
        }

        // 3. The host owns its entire dependency closure (Cove.*, EF Core, Npgsql, Pgvector, …). If the
        //    default context can supply this assembly — even one the host has not loaded yet — use the host's
        //    copy so types never split across load contexts. This makes correctness independent of how an
        //    extension was packaged: even if it bundles host assemblies (a common packaging mistake), the
        //    bundled copy is ignored. Only genuinely extension-private assemblies fall through to step 4.
        var hostAssembly = TryLoadHostAssembly(assemblyName);
        if (hostAssembly != null)
            return hostAssembly;

        // 4. Genuinely extension-private dependency: load the shadow-copied bundled assembly in this context.
        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        var cachedPath = MapToCachePath(path);
        return cachedPath != null ? LoadFromAssemblyPath(Path.GetFullPath(cachedPath)) : null;
    }

    /// <summary>
    /// Returns the host's copy of an assembly if the default context can supply it (i.e. it is part of the
    /// host's dependency closure), otherwise null. When the extension also shipped the assembly, warns once
    /// so the package can be slimmed — shipping host assemblies is wasted weight and a latent identity hazard.
    /// </summary>
    private Assembly? TryLoadHostAssembly(AssemblyName assemblyName)
    {
        Assembly hostAssembly;
        try
        {
            hostAssembly = AssemblyLoadContext.Default.LoadFromAssemblyName(assemblyName);
        }
        catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            // Not part of the host closure — a genuine extension-private dependency.
            return null;
        }

        var name = assemblyName.Name;
        if (!string.IsNullOrEmpty(name) && _resolver.ResolveAssemblyToPath(assemblyName) is not null)
        {
            bool firstTime;
            lock (WarnedBundledHostAssemblies)
                firstTime = WarnedBundledHostAssemblies.Add(name);
            if (firstTime)
                HostAssemblyBundledWarning?.Invoke(name);
        }

        return hostAssembly;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        var cachedPath = MapToCachePath(path);
        return cachedPath != null ? LoadUnmanagedDllFromPath(cachedPath) : IntPtr.Zero;
    }

    private string? MapToCachePath(string? sourcePath)
    {
        if (sourcePath is null)
            return null;

        var fullSourcePath = Path.GetFullPath(sourcePath);
        var relativePath = Path.GetRelativePath(_sourceRoot, fullSourcePath);
        if (relativePath == ".."
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relativePath))
        {
            return fullSourcePath;
        }

        var cachedPath = Path.Combine(_cacheRoot, relativePath);
        return File.Exists(cachedPath) ? cachedPath : fullSourcePath;
    }

    internal static void PreloadSharedAssemblies(string extensionsRoot, IEnumerable<string> extensionDirectories)
    {
        foreach (var assemblyName in DiscoverSharedAssemblyNames(extensionDirectories))
        {
            var preferredSourcePath = extensionDirectories
                .Select(dir => Path.Combine(dir, $"{assemblyName}.dll"))
                .Where(File.Exists)
                .Select(path => new FileInfo(path))
                .OrderByDescending(static file => file.LastWriteTimeUtc)
                .ThenByDescending(static file => file.Length)
                .ThenBy(static file => file.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(static file => file.FullName)
                .FirstOrDefault();

            if (preferredSourcePath is null)
            {
                continue;
            }

            lock (SharedAssemblyGate)
            {
                if (!PreferredSharedAssemblyPaths.ContainsKey(assemblyName))
                {
                    PreferredSharedAssemblyPaths[assemblyName] = CreateSharedShadowCopy(extensionsRoot, assemblyName, preferredSourcePath);
                }
            }

            _ = TryLoadSharedAssembly(new AssemblyName(assemblyName));
        }
    }

    private static IReadOnlyList<string> DiscoverSharedAssemblyNames(IEnumerable<string> extensionDirectories)
    {
        var sharedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in extensionDirectories)
        {
            var manifestPath = Path.Combine(dir, "extension.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var manifestJson = File.ReadAllText(manifestPath);
                var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(manifestJson, ManifestJsonOptions);
                foreach (var assemblyName in manifest?.SharedAssemblies ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(assemblyName))
                    {
                        sharedAssemblies.Add(assemblyName.Trim());
                    }
                }
            }
            catch
            {
                // Ignore malformed manifests here; discovery will report them later.
            }
        }

        return [.. sharedAssemblies];
    }

    private static Assembly? TryLoadSharedAssembly(AssemblyName assemblyName)
    {
        var assemblyKey = assemblyName.Name ?? string.Empty;
        if (!PreferredSharedAssemblyPaths.TryGetValue(assemblyKey, out var preferredPath) || !File.Exists(preferredPath))
        {
            return null;
        }

        lock (SharedAssemblyGate)
        {
            var defaultAssembly = AssemblyLoadContext.Default.Assemblies
                .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName));
            if (defaultAssembly != null)
            {
                return defaultAssembly;
            }

            return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(preferredPath));
        }
    }

    private static string CreateSharedShadowCopy(string extensionsRoot, string assemblyName, string sourcePath)
    {
        // extensionsRoot may be supplied as a relative path (e.g. "cove/extensions"); AssemblyLoadContext
        // .LoadFromAssemblyPath requires an absolute path, so anchor everything to the full path here.
        var sharedDir = Path.GetFullPath(Path.Combine(extensionsRoot, ".load-cache", "__shared", assemblyName));
        Directory.CreateDirectory(sharedDir);

        var destinationPath = Path.Combine(sharedDir, Path.GetFileName(sourcePath));
        File.Copy(sourcePath, destinationPath, overwrite: true);
        return destinationPath;
    }
}

/// <summary>
/// Factory interface for creating extension stores. Implemented in Cove.Data.
/// </summary>
public interface IExtensionStoreFactory
{
    IExtensionStore CreateStore(string extensionId);
}

/// <summary>A dependency validation problem.</summary>
public record DependencyProblem(string ExtensionId, string? DependencyId, string Message);
