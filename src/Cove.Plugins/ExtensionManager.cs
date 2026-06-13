using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

/// <summary>
/// Manages extension discovery, loading, dependency resolution, lifecycle,
/// migrations, and capability wiring. This is the heart of the Cove extension system.
/// </summary>
public class ExtensionManager
{
    private readonly List<IExtension> _extensions = [];
    private readonly Dictionary<string, IExtension> _extensionMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly ExtensionContext _context;
    private readonly Dictionary<string, AssemblyLoadContext> _loadContexts = [];
    private readonly Dictionary<string, string> _loadCacheSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extensionDirectories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtensionManifestFile> _manifestFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtensionInstallation> _installations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initializedExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startupDisabledExtensions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _extensionFailureReasons = new(StringComparer.OrdinalIgnoreCase);
    private IServiceScopeFactory? _scopeFactory;
    private IServiceProvider? _rootServices;
    private ILogger<ExtensionManager>? _logger;
    private List<IExtension>? _initOrder;
    private IEndpointRouteBuilder? _routeBuilder;
    private ExtensionEndpointRegistry? _endpointRegistry;
    private IReadOnlyList<ServiceDescriptor>? _hostDescriptors;
    private ExtensionServiceOverlay? _overlay;

    public IReadOnlyList<IExtension> Extensions => _extensions;
    public ExtensionContext Context => _context;

    public ExtensionManager(ExtensionContext context)
    {
        _context = context;
    }

    // ========================================================================
    // REGISTRATION
    // ========================================================================

    /// <summary>Register an extension instance (built-in or discovered).</summary>
    public void Register(IExtension extension, string source = "builtin")
    {
        _extensions.Add(extension);
        _extensionMap[extension.Id] = extension;
        // Create an in-memory installation record for built-in extensions
        if (!_installations.ContainsKey(extension.Id))
        {
            _installations[extension.Id] = new ExtensionInstallation
            {
                ExtensionId = extension.Id,
                Version = extension.Version,
                Enabled = true,
                Source = source,
                Categories = extension.Categories.Count > 0 ? string.Join(",", extension.Categories) : null,
            };
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
                BuildExtensionProvider(ext.Id);
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
                        _manifestFiles[manifestFile.Id] = manifestFile;
                        _extensionDirectories[manifestFile.Id] = dir;

                        if (IsManifestOnlyKind(manifestFile.Kind))
                        {
                            var source = manifestFile.RegistryUrl != null ? "registry" : "local";
                            var existingInstall = _installations.GetValueOrDefault(manifestFile.Id);
                            _installations[manifestFile.Id] = new ExtensionInstallation
                            {
                                ExtensionId = manifestFile.Id,
                                Version = manifestFile.Version,
                                Enabled = existingInstall?.Enabled ?? true,
                                Source = source,
                                InstalledAt = existingInstall?.InstalledAt ?? DateTime.UtcNow,
                                UpdatedAt = DateTime.UtcNow,
                                ManifestJson = json,
                                Categories = manifestFile.Categories.Count > 0 ? string.Join(",", manifestFile.Categories) : null,
                            };
                            continue;
                        }
                    }
                }

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
                                if (_extensionMap.TryGetValue(ext.Id, out var existing))
                                {
                                    var existingSource = _installations.GetValueOrDefault(existing.Id)?.Source;
                                    // Preserve built-ins when IDs collide; skip discovered duplicate.
                                    if (string.Equals(existingSource, "builtin", StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    RemoveExtensionFromMemory(existing.Id);
                                }

                                _extensions.Add(ext);
                                _extensionMap[ext.Id] = ext;
                                _loadContexts[ext.Id] = loadContext;
                                _loadCacheSlots[binaryCache.CacheKey] = binaryCache.Slot;
                                _loadCacheSlots[ext.Id] = binaryCache.Slot;
                                _extensionDirectories[ext.Id] = dir;
                                _extensionFailureReasons.Remove(ext.Id);
                                _initOrder = null;

                                var existingInstall = _installations.GetValueOrDefault(ext.Id);
                                var source = manifestFile?.RegistryUrl != null ? "registry" : existingInstall?.Source ?? "local";
                                _installations[ext.Id] = new ExtensionInstallation
                                {
                                    ExtensionId = ext.Id,
                                    Version = ResolveInstalledVersion(ext.Version, manifestFile, existingInstall, source),
                                    Enabled = existingInstall?.Enabled ?? true,
                                    Source = source,
                                    InstalledAt = existingInstall?.InstalledAt ?? DateTime.UtcNow,
                                    UpdatedAt = DateTime.UtcNow,
                                    ManifestJson = manifestFile != null ? File.ReadAllText(manifestPath) : null,
                                    Categories = SerializeCategories(ext.Categories, manifestFile),
                                };

                                if (manifestFile != null)
                                {
                                    _manifestFiles[ext.Id] = manifestFile;
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
        foreach (var ext in _extensions)
        {
            // Check core version requirement
            if (ext.MinCoveVersion != null && !SemverSatisfies(_context.CoveVersion, $">={ext.MinCoveVersion}"))
            {
                problems.Add(new DependencyProblem(ext.Id, null, $"Requires Cove >={ext.MinCoveVersion} but running {_context.CoveVersion}"));
            }

            // Check extension dependencies
            foreach (var (depId, versionRange) in ext.Dependencies)
            {
                if (!_extensionMap.TryGetValue(depId, out var dep))
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
        if (_initOrder != null) return _initOrder;

        var sorted = new List<IExtension>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in _extensions)
        {
            if (!visited.Contains(ext.Id))
                TopologicalVisit(ext, visited, visiting, sorted);
        }

        _initOrder = sorted;
        return sorted;
    }

    private void TopologicalVisit(IExtension ext, HashSet<string> visited, HashSet<string> visiting, List<IExtension> sorted)
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
            if (_extensionMap.TryGetValue(depId, out var dep))
                TopologicalVisit(dep, visited, visiting, sorted);
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
        if (!_extensionMap.TryGetValue(extensionId, out var ext)) return [];
        var missing = new List<string>();
        CollectMissingDeps(ext, missing, []);
        return missing;
    }

    private void CollectMissingDeps(IExtension ext, List<string> missing, HashSet<string> seen)
    {
        foreach (var (depId, _) in ext.Dependencies)
        {
            if (seen.Contains(depId)) continue;
            seen.Add(depId);
            if (!_extensionMap.ContainsKey(depId))
            {
                missing.Add(depId);
            }
            else
            {
                CollectMissingDeps(_extensionMap[depId], missing, seen);
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

    // ========================================================================
    // LIFECYCLE
    // ========================================================================

    /// <summary>
    /// True for extensions loaded from disk as DLLs (runtime-capable). These contribute their
    /// services into the rebuildable <see cref="ExtensionServiceOverlay"/> rather than the
    /// immutable root container, so they work identically whether present at boot or installed
    /// later. Built-in extensions compiled into the host go straight into the root container.
    /// </summary>
    private bool IsOverlayExtension(string id) => _loadContexts.ContainsKey(id);

    /// <summary>
    /// Call ConfigureServices for built-in (host-compiled) extensions, registering them into the
    /// root container. Runtime DLL extensions are intentionally skipped here — their services are
    /// contributed to their own per-extension container instead (see <see cref="BuildExtensionProvider"/>).
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
                ext.ConfigureServices(services, _context);
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
    /// Build (or rebuild) the isolated service container for one runtime DLL extension. Only that
    /// extension's container is (re)built; every other extension's container — and its state — is
    /// untouched, so installing or removing one extension never disturbs another. Safe to call
    /// repeatedly for the same id.
    /// </summary>
    public void BuildExtensionProvider(string id)
    {
        if (_rootServices == null || _hostDescriptors == null)
            return;
        if (!IsOverlayExtension(id) || !IsEnabled(id))
            return;
        if (!_extensionMap.TryGetValue(id, out var ext))
            return;

        // The container is being (re)built: stop any running worker and clear stale contributions so the
        // extension re-publishes against the new container on init.
        StopBackgroundWorker(id);
        WithdrawFromExchange(id);

        _overlay ??= new ExtensionServiceOverlay(_rootServices, _hostDescriptors, _logger);
        _overlay.BuildProvider(
            id,
            ext,
            _context,
            (failedId, e) => DisableExtensionForStartupFailure(failedId, e, "ConfigureServices (overlay)"));
    }

    /// <summary>
    /// The service provider an extension should use to resolve its services. Runtime DLL extensions
    /// resolve from their own isolated container; built-in extensions resolve from the root container.
    /// </summary>
    private IServiceProvider ServicesFor(string id, IServiceProvider fallback)
        => IsOverlayExtension(id) ? (_overlay?.ProviderFor(id) ?? fallback) : fallback;

    /// <summary>
    /// Create a scope for running the given extension's code (HTTP request, job, scan/auto-tag pass).
    /// Returns the extension's own container scope when built; otherwise a plain root scope. Callers
    /// own the returned scope and must dispose it.
    /// </summary>
    public IServiceScope CreateExtensionScope(string extensionId)
    {
        if (_overlay?.Has(extensionId) == true)
            return _overlay.CreateScope(extensionId);

        var factory = _scopeFactory ?? _rootServices?.GetService<IServiceScopeFactory>();
        if (factory == null)
            throw new InvalidOperationException("Extension service scope requested before the host service provider was available.");
        return factory.CreateScope();
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

        foreach (var ext in GetInitializationOrder().OfType<IApiExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            RegisterExtensionEndpoints(ext.Id);
        }
    }

    /// <summary>
    /// Register endpoints for a single extension at runtime and trigger an ASP.NET Core route table rebuild.
    /// Call this after <see cref="InitializeExtensionAsync"/> succeeds for a newly installed extension.
    /// </summary>
    public void RegisterExtensionEndpoints(string id)
    {
        if (_routeBuilder == null || _endpointRegistry == null) return;
        if (!_extensionMap.TryGetValue(id, out var ext) || ext is not IApiExtension apiExt) return;
        if (!IsEnabled(id)) return;

        // Build this extension's endpoints into a nested source (bound against its own provider for
        // correct minimal-API parameter classification), then publish it through the registry, which
        // fires the change token the matcher observes — making the routes live immediately.
        var source = new ExtensionEndpointDataSource(_routeBuilder, id, EndpointBuildServices(id));
        apiExt.MapEndpoints(source);
        _endpointRegistry.SetExtension(id, source);
    }

    /// <summary>
    /// The provider used to build an extension's endpoints. For runtime DLL extensions this is the
    /// overlay (so minimal-API parameter binding sees the extension's services as DI services);
    /// built-in extensions build against the root container.
    /// </summary>
    private IServiceProvider? EndpointBuildServices(string id)
        => IsOverlayExtension(id) ? _overlay?.GetProvider(id) : null;

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
            .ToList();

        if (middleware.Count == 0)
            return terminal(context);

        RequestDelegate next = terminal;
        for (var i = middleware.Count - 1; i >= 0; i--)
        {
            var current = middleware[i];
            var localNext = next;
            next = ctx => current.InvokeAsync(ctx, localNext);
        }
        return next(context);
    }

    // ========================================================================
    // BACKGROUND WORKERS (IBackgroundExtension)
    // ========================================================================
    private readonly Dictionary<string, CancellationTokenSource> _backgroundWorkers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Start the extension's long-lived background worker if it implements <see cref="IBackgroundExtension"/>
    /// and one isn't already running. The worker receives the extension's own provider and a token that is
    /// cancelled when the extension is disabled, uninstalled, rebuilt, or the host shuts down.
    /// </summary>
    public void StartBackgroundWorker(string id)
    {
        if (_rootServices == null) return;
        if (!_extensionMap.TryGetValue(id, out var ext) || ext is not IBackgroundExtension worker) return;
        if (!IsEnabled(id)) return;

        CancellationTokenSource cts;
        lock (_backgroundWorkers)
        {
            if (_backgroundWorkers.ContainsKey(id)) return;
            cts = new CancellationTokenSource();
            _backgroundWorkers[id] = cts;
        }

        var provider = ServicesFor(id, _rootServices);
        _ = Task.Run(async () =>
        {
            try
            {
                await worker.RunAsync(provider, cts.Token);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                // expected on stop
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Background worker for extension {Id} faulted", id);
            }
        }, cts.Token);
        _logger?.LogInformation("Background worker started for extension {Id}", id);
    }

    /// <summary>Cancel the extension's background worker if running.</summary>
    public void StopBackgroundWorker(string id)
    {
        CancellationTokenSource? cts;
        lock (_backgroundWorkers)
        {
            if (!_backgroundWorkers.Remove(id, out cts))
                return;
        }
        try { cts.Cancel(); } catch { /* best effort */ }
        cts.Dispose();
        _logger?.LogInformation("Background worker stopped for extension {Id}", id);
    }

    /// <summary>
    /// Initialize all extensions after the app is built.
    /// Wires up capability interfaces, applies migrations, runs in dependency order.
    /// </summary>
    public async Task InitializeAllAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _rootServices = services;
        CaptureScopeFactory(services);
        _logger = services.GetService<ILogger<ExtensionManager>>();
        _initializedExtensions.Clear();

        // Load installation state from DB
        await LoadInstallationStateAsync(services, ct);

        // Clean up stale installation records for extensions that no longer exist on disk.
        var staleIds = _installations.Keys
            .Where(id => !IsEffectivelyInstalled(id) && !IsManifestOnlyExtension(id))
            .ToList();
        foreach (var staleId in staleIds)
        {
            _installations.Remove(staleId);
            await RemoveInstallationStateAsync(staleId, ct);
            _logger?.LogInformation("Removed stale installation record for {Id}", staleId);
        }

        ApplyStartupDisables();
        foreach (var extensionId in _startupDisabledExtensions)
            await PersistInstallationStateAsync(extensionId, ct);

        // Validate dependencies
        var problems = ValidateDependencies();
        foreach (var p in problems)
            _logger?.LogWarning("Extension dependency issue: {Problem}", p.Message);

        // Wire stateful extensions with their DB-backed stores
        WireStatefulExtensions(services);

        // Apply extension database migrations
        await ApplyExtensionMigrationsAsync(services, ct);

        // Initialize all enabled extensions in dependency order. Each DLL extension gets its own
        // container, built here if PrepareRuntimeServices hasn't already (boot and runtime-install
        // share this exact path).
        foreach (var ext in GetInitializationOrder())
        {
            if (!IsEnabled(ext.Id)) continue;
            try
            {
                if (IsOverlayExtension(ext.Id) && _overlay?.Has(ext.Id) != true)
                    BuildExtensionProvider(ext.Id);
                var extServices = ServicesFor(ext.Id, services);

                // Check if this is a new installation
                var install = GetInstallation(ext.Id);
                if (install == null)
                {
                    await ext.OnInstallAsync(extServices, ct);
                    await SaveInstallationAsync(services, ext.Id, ct);
                    _logger?.LogInformation("Extension {Id} installed (v{Version})", ext.Id, ext.Version);
                }

                await ext.InitializeAsync(extServices, ct);
                _initializedExtensions.Add(ext.Id);
                StartBackgroundWorker(ext.Id);
                _logger?.LogInformation("Extension {Id} ({Name} v{Version}) initialized", ext.Id, ext.Name, ext.Version);
            }
            catch (Exception ex)
            {
                DisableExtensionForStartupFailure(ext.Id, ex, "InitializeAsync");
                await PersistInstallationStateAsync(ext.Id, ct);
            }
        }
    }

    /// <summary>Shut down all extensions gracefully (reverse dependency order).</summary>
    public async Task ShutdownAllAsync(CancellationToken ct = default)
    {
        var reversed = GetInitializationOrder().ToList();
        reversed.Reverse();
        foreach (var ext in reversed)
        {
            try
            {
                await ext.ShutdownAsync(ct);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error shutting down extension {Id}", ext.Id);
            }
        }

        foreach (var id in _backgroundWorkers.Keys.ToList())
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

        if (_initializedExtensions.Contains(id))
            return true;

        if (!_extensionMap.TryGetValue(id, out var ext))
        {
            if (_installations.TryGetValue(id, out var install) && IsManifestOnlyExtension(id))
            {
                install.UpdatedAt = DateTime.UtcNow;
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
        if (IsOverlayExtension(ext.Id))
            BuildExtensionProvider(ext.Id);
        var extServices = ServicesFor(ext.Id, runtimeServices);

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
            _initializedExtensions.Add(ext.Id);
            StartBackgroundWorker(ext.Id);
            _extensionFailureReasons.Remove(ext.Id);
            var manifest = GetManifestFile(ext.Id);
            if (_installations.TryGetValue(ext.Id, out var install))
            {
                install.Version = ResolveInstalledVersion(ext.Version, manifest, install, install.Source);
                install.UpdatedAt = DateTime.UtcNow;
            }
            await PersistInstallationStateAsync(ext.Id, ct);
            return true;
        }
        catch (Exception ex)
        {
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
        if (_initializedExtensions.Contains(id))
            return true;

        if (!_extensionMap.TryGetValue(id, out var ext))
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

        if (IsOverlayExtension(ext.Id) && _overlay?.Has(ext.Id) != true)
            BuildExtensionProvider(ext.Id);
        var extServices = ServicesFor(ext.Id, services);

        try
        {
            await ext.InitializeAsync(extServices, ct);
            _initializedExtensions.Add(ext.Id);
            StartBackgroundWorker(ext.Id);
            _logger?.LogInformation("Extension {Id} initialized on demand", ext.Id);
            return true;
        }
        catch (Exception ex)
        {
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

        if (!_extensionMap.TryGetValue(id, out var ext))
        {
            if (_installations.ContainsKey(id) && IsManifestOnlyExtension(id))
            {
                _manifestFiles.Remove(id);
                _installations.Remove(id);
                await RemoveInstallationStateAsync(id, ct);
                return true;
            }

            // It may still exist as a stale installation record.
            _installations.Remove(id);
            await RemoveInstallationStateAsync(id, ct);
            return false;
        }

        if (_installations.TryGetValue(id, out var inst))
            inst.Enabled = false;

        var uninstallServices = ServicesFor(id, services);

        try
        {
            await ext.OnUninstallAsync(uninstallServices, ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "OnUninstall failed for extension {Id}", id);
        }

        try
        {
            await ext.ShutdownAsync(ct);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Shutdown failed for extension {Id}", id);
        }

        RemoveExtensionFromMemory(id);
        _installations.Remove(id);
        await RemoveInstallationStateAsync(id, ct);

        // Remove runtime endpoints so the routing DFA no longer includes this extension.
        _endpointRegistry?.RemoveExtension(id);

        // Drop this extension's container and withdraw its published contributions, and stop its worker.
        // Other extensions are untouched.
        StopBackgroundWorker(id);
        _overlay?.Remove(id);
        WithdrawFromExchange(id);

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

                var normalized = string.IsNullOrWhiteSpace(topic.ExtensionId) && !string.IsNullOrWhiteSpace(extensionId)
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
            var extManifest = ext.GetUIManifest();
            manifest.Pages.AddRange(extManifest.Pages);
            manifest.Slots.AddRange(extManifest.Slots);
            manifest.Tabs.AddRange(extManifest.Tabs);
            manifest.Panes.AddRange(extManifest.Panes);
            manifest.Features.AddRange(extManifest.Features);
            manifest.ComponentOverrides.AddRange(extManifest.ComponentOverrides);
            manifest.SelectorOverrides.AddRange(extManifest.SelectorOverrides);
            manifest.Themes.AddRange(extManifest.Themes);
            manifest.ComponentStyles.AddRange(extManifest.ComponentStyles);
            manifest.LayoutStyles.AddRange(extManifest.LayoutStyles);
            manifest.SettingsTabs.AddRange(extManifest.SettingsTabs);
            manifest.SettingsPanels.AddRange(extManifest.SettingsPanels);
            manifest.PageOverrides.AddRange(extManifest.PageOverrides);
            manifest.DialogOverrides.AddRange(extManifest.DialogOverrides);
            manifest.Actions.AddRange(extManifest.Actions);
            AddTutorialTopics(extManifest.TutorialTopics, ext.Id);
            manifest.ListFilters.AddRange(extManifest.ListFilters);
            manifest.ListSorts.AddRange(extManifest.ListSorts);
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
        }

        // Collect actions from IActionExtension instances
        foreach (var ext in GetInitializationOrder().OfType<IActionExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            manifest.Actions.AddRange(ext.GetActions());
        }

        manifest.Pages.Sort((a, b) => a.NavOrder.CompareTo(b.NavOrder));
        manifest.Slots.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.Tabs.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.Panes.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ComponentOverrides.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        manifest.SelectorOverrides.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        manifest.Actions.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.TutorialTopics.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ListFilters.Sort((a, b) => a.Order.CompareTo(b.Order));
        manifest.ListSorts.Sort((a, b) => a.Order.CompareTo(b.Order));
        return manifest;
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
    public bool IsEnabled(string id) => _installations.TryGetValue(id, out var inst) ? inst.Enabled : true;

    /// <summary>Enable an extension and any installed extensions it depends on. Persists the state to DB.</summary>
    public async Task<IReadOnlyList<string>> EnableExtensionAsync(string id, CancellationToken ct = default)
    {
        var enabledIds = new List<string>();
        var idsToEnable = GetDependencyExtensionIds(id)
            .Append(id)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var extensionId in idsToEnable)
        {
            var inst = EnsureInstallationRecord(extensionId);
            if (inst == null)
                continue;

            inst.Enabled = true;
            inst.UpdatedAt = DateTime.UtcNow;
            await PersistInstallationStateAsync(extensionId, ct);
            enabledIds.Add(extensionId);
        }

        foreach (var enabledId in enabledIds)
            if (IsOverlayExtension(enabledId))
                BuildExtensionProvider(enabledId);

        return enabledIds;
    }

    /// <summary>Disable an extension and any enabled extensions that depend on it. Persists the state to DB.</summary>
    public async Task<IReadOnlyList<string>> DisableExtensionAsync(string id, CancellationToken ct = default)
    {
        var disabledIds = new List<string>();
        var idsToDisable = new[] { id }
            .Concat(GetDependentExtensionIds(id, enabledOnly: true))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var extensionId in idsToDisable)
        {
            var inst = EnsureInstallationRecord(extensionId);
            if (inst == null)
                continue;

            inst.Enabled = false;
            inst.UpdatedAt = DateTime.UtcNow;
            await PersistInstallationStateAsync(extensionId, ct);
            disabledIds.Add(extensionId);
        }

        foreach (var disabledId in disabledIds)
        {
            if (!IsOverlayExtension(disabledId)) continue;
            StopBackgroundWorker(disabledId);
            _overlay?.Remove(disabledId);
            WithdrawFromExchange(disabledId);
        }

        return disabledIds;
    }

    /// <summary>Update persisted install metadata for extensions installed after startup.</summary>
    public async Task SetInstallationMetadataAsync(string id, string source, string? version = null, CancellationToken ct = default)
    {
        var inst = EnsureInstallationRecord(id);
        if (inst == null) return;
        inst.Source = source;
        if (!string.IsNullOrWhiteSpace(version))
            inst.Version = version.Trim();
        inst.UpdatedAt = DateTime.UtcNow;
        await PersistInstallationStateAsync(id, ct);
    }

    /// <summary>Update only the persisted install source for an extension.</summary>
    public Task SetInstallationSourceAsync(string id, string source, CancellationToken ct = default) =>
        SetInstallationMetadataAsync(id, source, null, ct);

    /// <summary>Get the installation record for an extension.</summary>
    public ExtensionInstallation? GetInstallation(string id) =>
        _installations.TryGetValue(id, out var inst) ? inst : null;

    /// <summary>Get the manifest metadata for an extension or bundle.</summary>
    public ExtensionManifestFile? GetManifestFile(string id)
    {
        if (_manifestFiles.TryGetValue(id, out var manifest))
            return manifest;

        if (_installations.TryGetValue(id, out var install) && !string.IsNullOrWhiteSpace(install.ManifestJson))
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
    public IReadOnlyDictionary<string, ExtensionInstallation> Installations => _installations;

    public string? GetExtensionDirectory(string id) => ResolveExtensionDirectory(id);

    public string? GetLastFailureReason(string id) =>
        _extensionFailureReasons.TryGetValue(id, out var reason) ? reason : null;

    public bool IsEffectivelyInstalled(string id)
    {
        if (_extensionMap.ContainsKey(id)) return true;
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

        if (!string.IsNullOrWhiteSpace(runtimeVersion))
            return runtimeVersion;

        if (!string.IsNullOrWhiteSpace(manifest?.Version))
            return manifest.Version;

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
                await ext.OnEventAsync(evt, ct);
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

    /// <summary>Get all enabled extensions that participate in auto-tagging.</summary>
    public IReadOnlyList<IAutoTagParticipant> GetAutoTagParticipants()
    {
        return GetInitializationOrder()
            .OfType<IAutoTagParticipant>()
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

    /// <summary>Get all enabled auto-tag matchers exposed by extensions.</summary>
    public IReadOnlyList<IAutoTagMatcher> GetAutoTagMatchers()
    {
        return GetInitializationOrder()
            .OfType<IAutoTagMatcherExtension>()
            .Where(ext => IsEnabled(ext.Id))
            .SelectMany(ext => ext.GetMatchers())
            .ToList();
    }

    // ========================================================================
    // JOBS
    // ========================================================================

    /// <summary>Get all job definitions across all enabled IJobExtension instances.</summary>
    public IEnumerable<(IJobExtension Extension, ExtensionJobDefinition Job)> GetAllJobs()
    {
        foreach (var ext in _extensions.OfType<IJobExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            foreach (var job in ext.Jobs)
                yield return (ext, job);
        }
    }

    // ========================================================================
    // CATEGORIES
    // ========================================================================

    /// <summary>Get all unique categories across all extensions.</summary>
    public IReadOnlyList<string> GetAllCategories()
    {
        return _extensions
            .SelectMany(e => e.Categories)
            .Concat(_manifestFiles.Values.SelectMany(manifest => manifest.Categories))
            .Concat(_installations.Values
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
        return _extensions
            .Where(e => e.Categories.Any(catSet.Contains)
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

    private async Task ApplyExtensionMigrationsAsync(IServiceProvider services, CancellationToken ct)
    {
        var dataExtensions = GetInitializationOrder().OfType<IDataExtension>().ToList();
        if (dataExtensions.Count == 0) return;

        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetService<DbContext>();
        if (db?.Database is null) return;

        // Ensure extension_migrations table exists
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
                    await db.Database.ExecuteSqlRawAsync(migration.UpSql, ct);

                    // Record the migration
                    await db.Database.ExecuteSqlRawAsync(
                        "INSERT INTO extension_migrations (extension_id, migration_name) VALUES ({0}, {1})",
                        ext.Id, migration.Name);
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

    // ========================================================================
    // INSTALLATION STATE PERSISTENCE
    // ========================================================================

    private async Task LoadInstallationStateAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetService<DbContext>();
            if (db?.Database is null) return;

            // Ensure extension_installations table exists
            await db.Database.ExecuteSqlRawAsync("""
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

            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT extension_id, version, enabled, installed_at, updated_at, manifest_json, source, categories FROM extension_installations";

            if (cmd.Connection?.State != System.Data.ConnectionState.Open)
                await cmd.Connection!.OpenAsync(ct);

            using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                if (_installations.TryGetValue(id, out var existing))
                {
                    // Merge DB state with in-memory (DB wins for enabled state)
                    existing.Enabled = reader.GetBoolean(2);
                    existing.InstalledAt = EnsureUtc(reader.GetDateTime(3));
                    existing.UpdatedAt = EnsureUtc(reader.GetDateTime(4));
                    existing.ManifestJson = reader.IsDBNull(5) ? null : reader.GetString(5);
                    existing.Source = reader.GetString(6);
                    if (!reader.IsDBNull(7)) existing.Categories = reader.GetString(7);
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

            var install = _installations.GetValueOrDefault(extensionId);
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

    private void ApplyStartupDisables()
    {
        foreach (var extensionId in _startupDisabledExtensions)
        {
            if (_installations.TryGetValue(extensionId, out var install))
            {
                install.Enabled = false;
            }
        }
    }

    private void DisableExtensionForStartupFailure(string extensionId, Exception ex, string phase)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
            return;

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

        _startupDisabledExtensions.Add(extensionId);
        _initializedExtensions.Remove(extensionId);
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
        if (_installations.TryGetValue(id, out var existing))
            return existing;

        if (!_extensionMap.TryGetValue(id, out var ext))
            return null;

        var manifest = GetManifestFile(id);
        var install = new ExtensionInstallation
        {
            ExtensionId = id,
            Version = ResolveInstalledVersion(ext.Version, manifest, null, manifest?.RegistryUrl != null ? "registry" : "local"),
            Enabled = true,
            Source = manifest?.RegistryUrl != null ? "registry" : "local",
            InstalledAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ManifestJson = manifest != null ? JsonSerializer.Serialize(manifest, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }) : null,
            Categories = ext.Categories.Count > 0 ? string.Join(",", ext.Categories) : null,
        };
        _installations[id] = install;
        return install;
    }

    private IReadOnlyList<string> GetKnownExtensionIds() => _installations.Keys
        .Concat(_extensionMap.Keys)
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

        if (_extensionMap.TryGetValue(id, out var extension))
        {
            foreach (var dependency in extension.Dependencies)
                dependencies[dependency.Key] = dependency.Value;
        }

        return dependencies;
    }

    private void RemoveExtensionFromMemory(string id)
    {
        _initializedExtensions.Remove(id);

        if (_extensionMap.TryGetValue(id, out var existing))
        {
            _extensions.Remove(existing);
            _extensionMap.Remove(id);
        }

        _manifestFiles.Remove(id);
        _extensionDirectories.Remove(id);
        _initOrder = null;

        if (_loadContexts.TryGetValue(id, out var context))
        {
            _loadContexts.Remove(id);
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

    private void WireStatefulExtensions(IServiceProvider services)
    {
        var factory = services.GetService<IExtensionStoreFactory>();
        if (factory is null)
        {
            _logger?.LogWarning("No IExtensionStoreFactory registered; stateful extensions won't have stores");
            return;
        }

        foreach (var ext in _extensions.OfType<IStatefulExtension>())
        {
            var store = factory.CreateStore(ext.Id);
            ext.SetStore(store);
            _logger?.LogDebug("Wired store for extension {Id}", ext.Id);
        }
    }

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
        var activeSlot = _loadCacheSlots.GetValueOrDefault(cacheKey);
        var nextSlot = string.Equals(activeSlot, "a", StringComparison.OrdinalIgnoreCase) ? "b" : "a";
        var cacheRoot = Path.Combine(extensionsRoot, ".load-cache", cacheKey, nextSlot);

        RecreateDirectory(cacheRoot);

        foreach (var sourcePath in Directory.GetFiles(extensionDir, "*.dll", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(extensionDir, sourcePath);
            var destinationPath = Path.Combine(cacheRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(sourcePath, destinationPath, overwrite: true);
        }

        return new ExtensionBinaryCache(cacheKey, nextSlot, extensionDir, cacheRoot);
    }

    private static void CleanupStaleLoadCaches(string extensionsRoot, IReadOnlyCollection<string> extensionDirectories)
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
            if (!string.Equals(cacheName, "__shared", StringComparison.OrdinalIgnoreCase)
                && !installedExtensionIds.Contains(cacheName))
            {
                TryDeleteDirectory(cacheDir);
                continue;
            }

            if (!string.Equals(cacheName, "__shared", StringComparison.OrdinalIgnoreCase))
            {
                CleanupLegacyLoadCacheSlots(cacheDir);
            }
        }
    }

    private static void CleanupLegacyLoadCacheSlots(string extensionCacheRoot)
    {
        foreach (var slotDir in Directory.GetDirectories(extensionCacheRoot))
        {
            var slotName = Path.GetFileName(slotDir);
            if (!string.Equals(slotName, "a", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(slotName, "b", StringComparison.OrdinalIgnoreCase))
            {
                TryDeleteDirectory(slotDir);
            }
        }
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            RemoveReadOnlyAttributes(path);
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
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
        var defaultAssembly = AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(a => AssemblyName.ReferenceMatchesDefinition(a.GetName(), assemblyName));
        if (defaultAssembly != null)
            return defaultAssembly;

        if (assemblyName.Name is string sharedAssemblyName && PreferredSharedAssemblyPaths.ContainsKey(sharedAssemblyName))
        {
            var sharedAssembly = TryLoadSharedAssembly(assemblyName);
            if (sharedAssembly != null)
                return sharedAssembly;
        }

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        var cachedPath = MapToCachePath(path);
        return cachedPath != null ? LoadFromAssemblyPath(Path.GetFullPath(cachedPath)) : null;
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
