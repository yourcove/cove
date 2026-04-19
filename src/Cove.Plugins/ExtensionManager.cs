using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
    private readonly Dictionary<string, ExtensionInstallation> _installations = new(StringComparer.OrdinalIgnoreCase);
    private IServiceProvider? _lastServiceProvider;
    private ILogger<ExtensionManager>? _logger;
    private List<IExtension>? _initOrder;

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

    /// <summary>
    /// Discover and load .NET extension assemblies from a directory.
    /// Each subdirectory may contain an optional extension.json manifest and one or more DLLs.
    /// </summary>
    public void DiscoverExtensions(string extensionsDir)
    {
        if (!Directory.Exists(extensionsDir)) return;

        foreach (var dir in Directory.GetDirectories(extensionsDir))
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
                }

                // Determine which DLL to load
                var dllToLoad = manifestFile?.EntryDll != null
                    ? new[] { Path.Combine(dir, manifestFile.EntryDll) }
                    : Directory.GetFiles(dir, "*.dll");

                foreach (var dll in dllToLoad)
                {
                    if (!File.Exists(dll)) continue;
                    try
                    {
                        var loadContext = new AssemblyLoadContext(Path.GetFileNameWithoutExtension(dll), isCollectible: true);
                        var assembly = loadContext.LoadFromAssemblyPath(dll);
                        var extensionTypes = assembly.GetTypes()
                            .Where(t => typeof(IExtension).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                        foreach (var type in extensionTypes)
                        {
                            if (Activator.CreateInstance(type) is IExtension ext)
                            {
                                _extensions.Add(ext);
                                _extensionMap[ext.Id] = ext;
                                _loadContexts[ext.Id] = loadContext;

                                var source = manifestFile?.RegistryUrl != null ? "registry" : "local";
                                _installations[ext.Id] = new ExtensionInstallation
                                {
                                    ExtensionId = ext.Id,
                                    Version = ext.Version,
                                    Enabled = true,
                                    Source = source,
                                    ManifestJson = manifestFile != null ? File.ReadAllText(manifestPath) : null,
                                    Categories = ext.Categories.Count > 0 ? string.Join(",", ext.Categories) : null,
                                };
                            }
                        }
                    }
                    catch
                    {
                        // Skip DLLs that can't be loaded as extensions
                    }
                }
            }
            catch
            {
                // Skip directories that can't be processed
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

    // ========================================================================
    // LIFECYCLE
    // ========================================================================

    /// <summary>Call ConfigureServices on all registered extensions (in dependency order).</summary>
    public void ConfigureServices(IServiceCollection services)
    {
        foreach (var ext in GetInitializationOrder())
            ext.ConfigureServices(services, _context);
    }

    /// <summary>Map API endpoints from all enabled IApiExtension instances.</summary>
    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        foreach (var ext in GetInitializationOrder().OfType<IApiExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            ext.MapEndpoints(endpoints);
        }
    }

    /// <summary>Configure middleware from all enabled IMiddlewareExtension instances.</summary>
    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        foreach (var ext in GetInitializationOrder().OfType<IMiddlewareExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            ext.ConfigureMiddleware(app);
        }
    }

    /// <summary>
    /// Initialize all extensions after the app is built.
    /// Wires up capability interfaces, applies migrations, runs in dependency order.
    /// </summary>
    public async Task InitializeAllAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _lastServiceProvider = services;
        _logger = services.GetService<ILogger<ExtensionManager>>();

        // Load installation state from DB
        await LoadInstallationStateAsync(services, ct);

        // Validate dependencies
        var problems = ValidateDependencies();
        foreach (var p in problems)
            _logger?.LogWarning("Extension dependency issue: {Problem}", p.Message);

        // Wire stateful extensions with their DB-backed stores
        WireStatefulExtensions(services);

        // Apply extension database migrations
        await ApplyExtensionMigrationsAsync(services, ct);

        // Initialize all enabled extensions in dependency order
        foreach (var ext in GetInitializationOrder())
        {
            if (!IsEnabled(ext.Id)) continue;
            try
            {
                // Check if this is a new installation
                var install = GetInstallation(ext.Id);
                if (install == null)
                {
                    await ext.OnInstallAsync(services, ct);
                    await SaveInstallationAsync(services, ext, ct);
                    _logger?.LogInformation("Extension {Id} installed (v{Version})", ext.Id, ext.Version);
                }

                await ext.InitializeAsync(services, ct);
                _logger?.LogInformation("Extension {Id} ({Name} v{Version}) initialized", ext.Id, ext.Name, ext.Version);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Failed to initialize extension {Id}", ext.Id);
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
    }

    // ========================================================================
    // MANIFEST AGGREGATION
    // ========================================================================

    /// <summary>Get the aggregated UI manifest from all enabled extensions.</summary>
    public UIManifest GetAggregatedManifest()
    {
        var manifest = _context.UI.ToManifest();
        foreach (var ext in GetInitializationOrder().OfType<IUIExtension>())
        {
            if (!IsEnabled(ext.Id)) continue;
            var extManifest = ext.GetUIManifest();
            manifest.Pages.AddRange(extManifest.Pages);
            manifest.Slots.AddRange(extManifest.Slots);
            manifest.Tabs.AddRange(extManifest.Tabs);
            manifest.Themes.AddRange(extManifest.Themes);
            manifest.ComponentStyles.AddRange(extManifest.ComponentStyles);
            manifest.LayoutStyles.AddRange(extManifest.LayoutStyles);
            manifest.SettingsPanels.AddRange(extManifest.SettingsPanels);
            manifest.PageOverrides.AddRange(extManifest.PageOverrides);
            manifest.DialogOverrides.AddRange(extManifest.DialogOverrides);
            manifest.Actions.AddRange(extManifest.Actions);
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
        manifest.Actions.Sort((a, b) => a.Order.CompareTo(b.Order));
        return manifest;
    }

    // ========================================================================
    // ENABLE / DISABLE
    // ========================================================================

    /// <summary>Check if an extension is enabled.</summary>
    public bool IsEnabled(string id) => _installations.TryGetValue(id, out var inst) ? inst.Enabled : true;

    /// <summary>Enable an extension by ID. Persists the state to DB.</summary>
    public async Task EnableExtensionAsync(string id, CancellationToken ct = default)
    {
        if (_installations.TryGetValue(id, out var inst)) inst.Enabled = true;
        await PersistInstallationStateAsync(id, ct);
    }

    /// <summary>Disable an extension by ID. Persists the state to DB.</summary>
    public async Task DisableExtensionAsync(string id, CancellationToken ct = default)
    {
        if (_installations.TryGetValue(id, out var inst)) inst.Enabled = false;
        await PersistInstallationStateAsync(id, ct);
    }

    /// <summary>Get the installation record for an extension.</summary>
    public ExtensionInstallation? GetInstallation(string id) =>
        _installations.TryGetValue(id, out var inst) ? inst : null;

    /// <summary>Get all installation records.</summary>
    public IReadOnlyDictionary<string, ExtensionInstallation> Installations => _installations;

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
            .Concat(_installations.Values
                .Where(i => i.Categories != null)
                .SelectMany(i => i.Categories!.Split(',', StringSplitOptions.RemoveEmptyEntries)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    /// <summary>Get extensions matching any of the given categories.</summary>
    public IReadOnlyList<IExtension> GetExtensionsByCategory(params string[] categories)
    {
        var catSet = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        return _extensions
            .Where(e => e.Categories.Any(c => catSet.Contains(c)))
            .ToList();
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
                    existing.InstalledAt = reader.GetDateTime(3);
                    existing.UpdatedAt = reader.GetDateTime(4);
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
                        InstalledAt = reader.GetDateTime(3),
                        UpdatedAt = reader.GetDateTime(4),
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

    private async Task SaveInstallationAsync(IServiceProvider services, IExtension ext, CancellationToken ct)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetService<DbContext>();
            if (db?.Database is null) return;

            var install = _installations.GetValueOrDefault(ext.Id);
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
                install.InstalledAt, DateTime.UtcNow, (object?)install.ManifestJson ?? DBNull.Value,
                install.Source, (object?)install.Categories ?? DBNull.Value);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not save extension installation state for {Id}", ext.Id);
        }
    }

    private async Task PersistInstallationStateAsync(string extensionId, CancellationToken ct)
    {
        if (_lastServiceProvider == null) return;
        if (!_extensionMap.TryGetValue(extensionId, out var ext)) return;
        await SaveInstallationAsync(_lastServiceProvider, ext, ct);
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
