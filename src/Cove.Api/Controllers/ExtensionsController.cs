using Microsoft.AspNetCore.Mvc;
using Cove.Plugins;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Api.Services;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace Cove.Api.Controllers;

internal static class FrontendRuntimeContract
{
    public const string Version = "v1";
}

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.ExtensionsRead)]
public class ExtensionsController(ExtensionManager extensionManager, ScraperService scraperService) : ControllerBase
{
    /// <summary>Returns the aggregated UI manifest from all registered extensions.</summary>
    [HttpGet("manifest")]
    public ActionResult<UIManifest> GetManifest()
    {
        var manifest = extensionManager.GetAggregatedManifest();
        manifest.FrontendRuntimeVersion = FrontendRuntimeContract.Version;

        var jsBundles = extensionManager.GetEnabledJsBundles();
        var cssBundles = extensionManager.GetEnabledCssBundles();
        var assetsByExtension = new Dictionary<string, (string? JsPath, string? CssPath)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (extensionId, path) in jsBundles)
        {
            assetsByExtension[extensionId] = (path, null);
        }
        foreach (var (extensionId, path) in cssBundles)
        {
            var assets = assetsByExtension.GetValueOrDefault(extensionId);
            assetsByExtension[extensionId] = (assets.JsPath, path);
        }

        manifest.ExtensionBundles = assetsByExtension
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry =>
            {
                var extensionId = entry.Key;
                var manifestFile = extensionManager.GetManifestFile(extensionId);
                var version = extensionManager.GetInstallation(extensionId)?.Version
                    ?? manifestFile?.Version
                    ?? (extensionManager.GetExtension(extensionId) is { } extension
                        ? extensionManager.ExecuteExtension(extension, () => extension.Version)
                        : null)
                    ?? "0.0.0";
                return new UIExtensionBundle(
                    extensionId,
                    version,
                    entry.Value.JsPath is { } jsPath ? BuildAssetUrl(extensionId, jsPath, version) : null,
                    entry.Value.CssPath is { } cssPath ? BuildAssetUrl(extensionId, cssPath, version) : null);
            })
            .ToList();

        if (jsBundles.Count == 1)
        {
            var (extId, path) = jsBundles[0];
            manifest.JsBundleUrl = BuildAssetUrl(extId, path);
        }
        else if (jsBundles.Count > 1)
        {
            manifest.JsBundleUrl = "/api/extensions/bundles/ui.mjs";
        }

        if (cssBundles.Count == 1)
        {
            var (extId, path) = cssBundles[0];
            manifest.CssBundleUrl = BuildAssetUrl(extId, path);
        }
        else if (cssBundles.Count > 1)
        {
            manifest.CssBundleUrl = "/api/extensions/bundles/ui.css";
        }

        return Ok(manifest);
    }

    /// <summary>
    /// Returns a synthetic ESM module that imports all enabled extension UI bundles and
    /// merges their `default.components` exports into one object for the frontend runtime.
    /// </summary>
    [HttpGet("bundles/ui.mjs")]
    public IActionResult GetCombinedUiBundleModule()
    {
        var jsBundles = extensionManager.GetEnabledJsBundles();
        if (jsBundles.Count == 0)
        {
            return Content("export default { components: {}, actionHandlers: {}, handlers: {} };", "application/javascript");
        }

        var lines = new List<string>();
        for (var i = 0; i < jsBundles.Count; i++)
        {
            var (extId, path) = jsBundles[i];
            var url = BuildAssetUrl(extId, path);
            lines.Add($"import * as m{i} from '{url}';");
        }

        lines.Add("const components = {};");
        lines.Add("const actionHandlers = {};");
        for (var i = 0; i < jsBundles.Count; i++)
        {
            lines.Add($"Object.assign(components, (m{i}.default && m{i}.default.components) || {{}});");
            lines.Add($"Object.assign(actionHandlers, (m{i}.default && (m{i}.default.actionHandlers || m{i}.default.handlers)) || m{i}.actionHandlers || m{i}.handlers || {{}});");
        }
        lines.Add("export default { components, actionHandlers, handlers: actionHandlers };\n");

        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Pragma", "no-cache");
        Response.Headers.Append("Expires", "0");
        return Content(string.Join("\n", lines), "application/javascript");
    }

    [HttpGet("bundles/ui.css")]
    public IActionResult GetCombinedUiCssBundle()
    {
        var cssBundles = extensionManager.GetEnabledCssBundles();
        if (cssBundles.Count == 0)
        {
            return Content(string.Empty, "text/css");
        }

        var lines = new List<string>();
        foreach (var (extId, path) in cssBundles)
        {
            var url = BuildAssetUrl(extId, path);
            lines.Add($"@import url('{url}');");
        }

        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Pragma", "no-cache");
        Response.Headers.Append("Expires", "0");
        return Content(string.Join("\n", lines), "text/css");
    }

    private string BuildAssetUrl(string extensionId, string path, string? extensionVersion = null)
    {
        var url = $"/api/extensions/assets/{Uri.EscapeDataString(extensionId)}/{path}";
        var basePath = extensionManager.GetExtensionDirectory(extensionId);
        long? contentVersion = null;
        if (basePath != null)
        {
            var fullPath = Path.GetFullPath(Path.Combine(basePath, path));
            if (IsPathInsideDirectory(basePath, fullPath) && System.IO.File.Exists(fullPath))
            {
                contentVersion = System.IO.File.GetLastWriteTimeUtc(fullPath).Ticks;
            }
        }

        var query = new List<string>();
        if (contentVersion.HasValue) query.Add($"v={contentVersion.Value}");
        if (!string.IsNullOrWhiteSpace(extensionVersion))
        {
            query.Add($"extensionVersion={Uri.EscapeDataString(extensionVersion)}");
        }
        return query.Count > 0 ? $"{url}?{string.Join('&', query)}" : url;
    }

    /// <summary>Returns a list of all registered extensions with capability and category info.</summary>
    [HttpGet]
    public ActionResult<IEnumerable<ExtensionInfo>> GetExtensions([FromQuery] string? category = null)
    {
        var loadedIds = extensionManager.Extensions
            .Select(e => extensionManager.ExecuteExtensionMetadata(e, () => e.Id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var items = extensionManager.Extensions
            .Select(e =>
            {
                return extensionManager.ExecuteExtensionMetadata(e, () =>
                {
                    var install = extensionManager.GetInstallation(e.Id);
                    var manifest = extensionManager.GetManifestFile(e.Id);
                    var categories = ResolveCategories(e.Categories, manifest?.Categories, install?.Categories);
                    if (!MatchesCategory(categories, category))
                        return null;

                    return new ExtensionInfo(
                        e.Id,
                        e.Name,
                        install?.Version ?? manifest?.Version ?? e.Version,
                        e.Description,
                        e.Author,
                        e.Url,
                        e.IconUrl,
                        extensionManager.IsEnabled(e.Id),
                        e is IUIExtension,
                        e is IApiExtension,
                        e is IStatefulExtension,
                        e is IJobExtension,
                        e is IEventExtension,
                        e is IDataExtension,
                        e is IMiddlewareExtension,
                        e is IActionExtension,
                        categories,
                        e.MinCoveVersion,
                        e.Dependencies.ToDictionary(kv => kv.Key, kv => kv.Value),
                        GetExternalDependencies(manifest, e.Id),
                        GetSettings(manifest, e.Id),
                        manifest?.Kind ?? "extension",
                        install?.Source ?? "unknown",
                        install?.InstalledAt,
                        e is IJobExtension je ? je.Jobs.Select(j => new JobInfo(j.Id, j.Name, j.Description)).ToList() : []);
                });
            })
                .Where(info => info != null)
                .Cast<ExtensionInfo>()
            .ToList();

        items.AddRange(extensionManager.Installations.Values
            .Where(install => !loadedIds.Contains(install.ExtensionId) && extensionManager.IsManifestOnlyExtension(install.ExtensionId))
            .Select(install =>
            {
                var manifest = extensionManager.GetManifestFile(install.ExtensionId);
                if (manifest == null)
                    return null;

                var categories = ResolveCategories(null, manifest.Categories, install.Categories);
                if (!MatchesCategory(categories, category))
                    return null;

                return new ExtensionInfo(
                    manifest.Id,
                    manifest.Name,
                    install.Version,
                    manifest.Description,
                    manifest.Author,
                    manifest.Url,
                    manifest.IconUrl,
                    install.Enabled,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    false,
                    categories,
                    manifest.MinCoveVersion,
                    manifest.Dependencies,
                    GetExternalDependencies(manifest, manifest.Id),
                    GetSettings(manifest, manifest.Id),
                    manifest.Kind,
                    install.Source,
                    install.InstalledAt,
                    []);
            })
            .Where(info => info != null)
            .Cast<ExtensionInfo>());

        return Ok(items);
    }

    private static List<ExtensionExternalDependency> GetExternalDependencies(ExtensionManifestFile? manifest, string extensionId) =>
        manifest?.ExternalDependencies.Where(d => AppliesToExtension(d.ExtensionIds, extensionId)).ToList() ?? [];

    private static List<ExtensionSettingManifest> GetSettings(ExtensionManifestFile? manifest, string extensionId) =>
        manifest?.Settings.Where(s => AppliesToExtension(s.ExtensionIds, extensionId)).ToList() ?? [];

    private static bool AppliesToExtension(IReadOnlyList<string>? extensionIds, string extensionId) =>
        extensionIds == null
        || extensionIds.Count == 0
        || extensionIds.Any(id => string.Equals(id, extensionId, StringComparison.OrdinalIgnoreCase));

    private static bool MatchesCategory(IReadOnlyList<string> categories, string? category)
    {
        if (string.IsNullOrWhiteSpace(category))
            return true;

        var requestedCategory = category.Trim();
        return categories.Any(categoryName => string.Equals(categoryName, requestedCategory, StringComparison.OrdinalIgnoreCase));
    }

    private static List<string> ResolveCategories(
        IReadOnlyList<string>? runtimeCategories,
        IReadOnlyList<string>? manifestCategories,
        string? persistedCategories)
    {
        return (runtimeCategories ?? [])
            .Concat(manifestCategories ?? [])
            .Concat(SplitPersistedCategories(persistedCategories))
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SplitPersistedCategories(string? categories) =>
        string.IsNullOrWhiteSpace(categories)
            ? []
            : categories.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Get all available extension categories (from loaded extensions + registry).</summary>
    [HttpGet("categories")]
    public ActionResult<IEnumerable<string>> GetCategories() =>
        Ok(extensionManager.GetAllCategories());

    /// <summary>Validate all extension dependencies and return any problems.</summary>
    [HttpGet("dependencies/validate")]
    public ActionResult<IEnumerable<DependencyProblem>> ValidateDependencies() =>
        Ok(extensionManager.ValidateDependencies());

    /// <summary>Get missing dependencies for a specific extension (for install prompting).</summary>
    [HttpGet("{id}/dependencies/missing")]
    public ActionResult<IEnumerable<string>> GetMissingDependencies(string id)
    {
        var ext = extensionManager.GetExtension(id);
        if (ext == null) return NotFound();
        return Ok(extensionManager.GetMissingDependencies(id));
    }

    /// <summary>Enable an extension.</summary>
    [HttpPost("{id}/enable")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> Enable(string id, CancellationToken ct)
    {
        var ext = extensionManager.GetExtension(id);
        if (ext == null && extensionManager.GetInstallation(id) == null) return NotFound();
        var enabledExtensions = await extensionManager.EnableExtensionAsync(id, ct);
        foreach (var extensionId in enabledExtensions)
        {
            await extensionManager.InitializeExtensionAsync(extensionId, HttpContext.RequestServices, ct);
        }
        scraperService.ReloadScrapers();
        return Ok(new { enabledExtensions });
    }

    /// <summary>Disable an extension.</summary>
    [HttpPost("{id}/disable")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> Disable(string id, CancellationToken ct)
    {
        var ext = extensionManager.GetExtension(id);
        if (ext == null && extensionManager.GetInstallation(id) == null) return NotFound();
        var disabledExtensions = await extensionManager.DisableExtensionAsync(id, ct);
        scraperService.ReloadScrapers();
        return Ok(new { disabledExtensions });
    }

    /// <summary>Get extension key-value store data.</summary>
    [HttpGet("{id}/data")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> GetData(string id, CancellationToken ct)
    {
        var ext = extensionManager.GetExtension(id) as IStatefulExtension;
        if (ext == null) return NotFound("Extension not found or not stateful");

        var factory = HttpContext.RequestServices.GetService<IExtensionStoreFactory>();
        if (factory == null) return StatusCode(500, "Store not available");

        var store = factory.CreateStore(id);
        var data = await store.GetAllAsync(ct);
        return Ok(data);
    }

    /// <summary>Set a key-value pair in extension store.</summary>
    [HttpPut("{id}/data/{key}")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> SetData(string id, string key, [FromBody] string value, CancellationToken ct)
    {
        var ext = extensionManager.GetExtension(id) as IStatefulExtension;
        if (ext == null) return NotFound("Extension not found or not stateful");

        var factory = HttpContext.RequestServices.GetService<IExtensionStoreFactory>();
        if (factory == null) return StatusCode(500, "Store not available");

        var store = factory.CreateStore(id);
        await store.SetAsync(key, value, ct);
        return Ok();
    }

    /// <summary>Trigger a job defined by an extension.</summary>
    [HttpPost("{id}/jobs/{jobId}/run")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public IActionResult RunJob(string id, string jobId, [FromBody] Dictionary<string, string>? parameters,
        [FromServices] IJobService jobService)
    {
        var ext = extensionManager.GetExtension(id) as IJobExtension;
        if (ext == null) return NotFound("Extension not found or has no jobs");

        var execution = extensionManager.CaptureExtensionExecution(ext);
        var jobMetadata = extensionManager.ExecuteExtension(
            execution,
            () => (Job: ext.Jobs.FirstOrDefault(j => j.Id == jobId), ExtensionName: ext.Name));
        var job = jobMetadata.Job;
        if (job == null) return NotFound($"Job '{jobId}' not found");

        // Run through the core job service for proper queuing, progress tracking, and SignalR updates
        var coreJobId = jobService.Enqueue(
            $"ext:{ext.Id}:{jobId}",
            $"[{jobMetadata.ExtensionName}] {job.Name}",
            async (coreProgress, ct) =>
            {
                var bridge = new JobProgressBridge(coreProgress);
                await extensionManager.ExecuteExtensionAsync(
                    execution,
                    () => ext.RunJobAsync(jobId, parameters, bridge, ct));
            },
            exclusive: false);

        return Accepted(new { message = $"Job '{job.Name}' started", jobId = coreJobId });
    }

    /// <summary>Serve static assets from an extension's data directory.</summary>
    [HttpGet("assets/{extensionId}/{**path}")]
    public IActionResult GetAsset(string extensionId, string path)
    {
        var ext = extensionManager.GetExtension(extensionId);
        if (ext == null) return NotFound();

        var basePath = Path.Combine(extensionManager.Context.DataDirectory, extensionId);
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));

        // Security: prevent path traversal
        if (!IsPathInsideDirectory(basePath, fullPath))
            return BadRequest("Invalid path");

        if (!System.IO.File.Exists(fullPath)) return NotFound();

        var contentType = Path.GetExtension(fullPath).ToLowerInvariant() switch
        {
            ".js" => "application/javascript",
            ".mjs" => "application/javascript",
            ".css" => "text/css",
            ".json" => "application/json",
            ".html" => "text/html",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            _ => "application/octet-stream"
        };

        Response.Headers.Append("Cache-Control", "no-cache, no-store, must-revalidate");
        Response.Headers.Append("Pragma", "no-cache");
        Response.Headers.Append("Expires", "0");
        return PhysicalFile(fullPath, contentType);
    }

    /// <summary>Install an extension package from a user-provided URL after explicit trust confirmation.</summary>
    [HttpPost("install-from-url")]
    [RequiresPermission(Permissions.ExtensionsInstall)]
    public async Task<IActionResult> InstallFromUrl(
        [FromBody] InstallExtensionFromUrlRequest request,
        [FromServices] IHttpClientFactory httpClientFactory,
        CancellationToken ct)
    {
        if (!request.TrustUnverified)
            return BadRequest("Installing an extension from a URL requires explicit trust confirmation.");

        if (!Uri.TryCreate(request.Url?.Trim(), UriKind.Absolute, out var packageUri) || packageUri.Scheme is not ("http" or "https"))
            return BadRequest("Extension package URL must be an absolute http or https URL.");

        var extensionsDir = Path.Combine(extensionManager.Context.DataDirectory, "..", "extensions");
        extensionsDir = Path.GetFullPath(extensionsDir);
        Directory.CreateDirectory(extensionsDir);

        var tempRoot = Path.Combine(extensionsDir, $".url-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var http = httpClientFactory.CreateClient("ExtensionRegistry");
            using var response = await http.GetAsync(packageUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return BadRequest($"Extension package download failed with HTTP {(int)response.StatusCode}.");

            var zipPath = Path.Combine(tempRoot, "package.zip");
            await using (var zipFile = System.IO.File.Create(zipPath))
            await using (var input = await response.Content.ReadAsStreamAsync(ct))
            {
                await input.CopyToAsync(zipFile, ct);
            }

            var extractDir = Path.Combine(tempRoot, "extract");
            Directory.CreateDirectory(extractDir);
            await using (var stream = System.IO.File.OpenRead(zipPath))
            {
                try
                {
                    ExtractZipSafely(stream, extractDir);
                }
                catch (Exception ex) when (ex is InvalidDataException || ex is InvalidOperationException)
                {
                    return BadRequest(ex.Message);
                }
            }

            var packageRoot = FindExtensionPackageRoot(extractDir);
            if (packageRoot == null)
                return BadRequest("The package must contain an extension.json manifest at the root or in one top-level directory.");

            var manifestPath = Path.Combine(packageRoot, "extension.json");
            var manifestJson = await System.IO.File.ReadAllTextAsync(manifestPath, ct);
            var manifest = JsonSerializer.Deserialize<ExtensionManifestFile>(manifestJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (manifest == null || string.IsNullOrWhiteSpace(manifest.Id))
                return BadRequest("The package extension.json manifest is missing a valid id.");

            if (!IsSafeExtensionId(manifest.Id))
                return BadRequest("The package extension id contains invalid path characters.");

            if (!IsCoveVersionCompatible(manifest.MinCoveVersion))
                return BadRequest($"Extension '{manifest.Id}' requires Cove >= {manifest.MinCoveVersion}; this instance is {extensionManager.Context.CoveVersion}.");

            var extensionDir = Path.Combine(extensionsDir, manifest.Id);
            if (Directory.Exists(extensionDir))
            {
                await extensionManager.UnloadExtensionAsync(manifest.Id, HttpContext.RequestServices, ct);
                var deleteError = await DeleteDirectoryWithRetriesAsync(extensionDir, ct);
                if (deleteError != null)
                    return Conflict(new { message = $"Existing extension '{manifest.Id}' could not be replaced because files are locked.", detail = deleteError.Message, path = extensionDir });
            }

            Directory.Move(packageRoot, extensionDir);

            extensionManager.DiscoverExtensions(extensionsDir);
            var initialized = await extensionManager.InitializeExtensionAsync(manifest.Id, HttpContext.RequestServices, ct);
            if (!initialized)
                return StatusCode(500, new { message = $"Extension '{manifest.Id}' was downloaded but failed to initialize.", path = extensionDir });

            await extensionManager.SetInstallationSourceAsync(manifest.Id, "url", ct);
            scraperService.ReloadScrapers();

            return Ok(new
            {
                message = $"Extension '{manifest.Id}' v{manifest.Version} installed from URL.",
                extensionId = manifest.Id,
                version = manifest.Version,
                path = extensionDir,
            });
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                await DeleteDirectoryWithRetriesAsync(tempRoot, ct);
        }
    }

    // ========================================================================
    // REGISTRY ENDPOINTS
    // ========================================================================

    /// <summary>Search the extension registry.</summary>
    [HttpGet("registry/search")]
    public async Task<IActionResult> RegistrySearch(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] string? type,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var result = await registry.SearchAsync(new RegistrySearchRequest
        {
            Query = q,
            Categories = !string.IsNullOrWhiteSpace(category) ? [category.Trim()] : null,
            Type = type,
            SortBy = sort ?? "relevance",
            Page = page,
            PageSize = pageSize,
        }, ct);
        return Ok(result);
    }

    /// <summary>Get details for a specific registry extension.</summary>
    [HttpGet("registry/{extensionId}")]
    public async Task<IActionResult> RegistryGetExtension(
        string extensionId,
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var detail = await registry.GetExtensionAsync(extensionId, ct);
        if (detail == null) return NotFound();
        return Ok(detail);
    }

    /// <summary>Check for updates for all installed extensions.</summary>
    [HttpGet("registry/updates")]
    public async Task<IActionResult> RegistryCheckUpdates(
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var installed = extensionManager.Installations.Values.Select(i => (i.ExtensionId, i.Version));
        var updates = await registry.CheckForUpdatesAsync(installed, ct);
        return Ok(updates);
    }

    /// <summary>Get registry categories.</summary>
    [HttpGet("registry/categories")]
    public async Task<IActionResult> RegistryGetCategories(
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var categories = await registry.GetCategoriesAsync(ct);
        return Ok(categories);
    }

    /// <summary>Install an extension from the registry.</summary>
    [HttpPost("registry/install")]
    [RequiresPermission(Permissions.ExtensionsInstall)]
    public async Task<IActionResult> RegistryInstall(
        [FromBody] RegistryInstallRequest request,
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var extensionsDir = Path.Combine(extensionManager.Context.DataDirectory, "..", "extensions");
        extensionsDir = Path.GetFullPath(extensionsDir);
        Directory.CreateDirectory(extensionsDir);

        // Resolve dependencies first
        var detail = await registry.GetExtensionAsync(request.ExtensionId, ct);
        if (detail == null)
            return NotFound($"Extension '{request.ExtensionId}' not found in registry.");

        var selectedVersion = SelectRegistryVersion(detail, request.Version, null, out var selectedVersionError);
        if (selectedVersion == null)
            return BadRequest(selectedVersionError ?? $"No compatible version is available for '{request.ExtensionId}'.");

        var installedVersions = extensionManager.Installations.Values
            .Where(i => extensionManager.IsEffectivelyInstalled(i.ExtensionId))
            .ToDictionary(i => i.ExtensionId, i => i.Version, StringComparer.OrdinalIgnoreCase);
        var installDependencies = request.InstallDependencies;
        var dependencyPlan = new List<RegistryInstallPlanItem>();
        var dependencyInfos = new List<DependencyInfo>();
        var missingDeps = new Dictionary<string, DependencyInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await ResolveDependencyPlanAsync(
                registry,
                detail,
                selectedVersion,
                installedVersions,
                dependencyPlan,
                dependencyInfos,
                missingDeps,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        // If there are missing deps and the client didn't opt in to auto-install, return them
        if (dependencyInfos.Count > 0 && !installDependencies)
        {
            return Ok(new
            {
                requiresDependencies = true,
                extension = new { detail.Id, detail.Name, Version = selectedVersion.Version },
                missingDependencies = dependencyInfos,
            });
        }

        if (missingDeps.Count > 0)
            return BadRequest(new { message = "One or more required dependencies are not available from the registry.", missingDependencies = missingDeps.Values });

        // Unload any existing extensions that will be replaced before swapping their files.
        var idsToReplace = dependencyPlan.Select(d => d.Id).Append(request.ExtensionId).ToList();
        foreach (var extId in idsToReplace)
        {
            var existing = extensionManager.Extensions.FirstOrDefault(e => string.Equals(e.Id, extId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                await extensionManager.UnloadExtensionAsync(extId, HttpContext.RequestServices, ct);
            }
        }

        // Download the full upgrade/install batch before discovery. Rediscovering
        // between dependency updates can load old extensions against new dependencies.
        var installedExtensions = new List<string>();
        foreach (var dep in dependencyPlan)
        {
            await registry.DownloadAsync(dep.Id, dep.Version, extensionsDir, ct);
            installedExtensions.Add(dep.Id);
        }

        // Install the requested extension
        var installPath = await registry.DownloadAsync(request.ExtensionId, selectedVersion.Version, extensionsDir, ct);

        // Reload discovered extensions once all replaced files are in their final state,
        // then initialize dependencies before the requested extension.
        extensionManager.DiscoverExtensions(extensionsDir);
        foreach (var dep in dependencyPlan)
        {
            await extensionManager.InitializeExtensionAsync(dep.Id, HttpContext.RequestServices, ct);
            await extensionManager.SetInstallationMetadataAsync(dep.Id, "registry", dep.Version, ct);
        }

        var initialized = await extensionManager.InitializeExtensionAsync(request.ExtensionId, HttpContext.RequestServices, ct);
        if (!initialized)
        {
            return StatusCode(500, new
            {
                message = $"Extension '{request.ExtensionId}' was downloaded but failed to initialize.",
                path = installPath,
                detail = extensionManager.GetLastFailureReason(request.ExtensionId)
                    ?? $"Extension '{request.ExtensionId}' was not loaded during discovery.",
            });
        }

        await extensionManager.SetInstallationMetadataAsync(request.ExtensionId, "registry", selectedVersion.Version, ct);
        scraperService.ReloadScrapers();

        return Ok(new
        {
            message = $"Extension '{request.ExtensionId}' v{selectedVersion.Version} installed.",
            path = installPath,
            installedDependencies = installedExtensions,
        });
    }

    /// <summary>Resolve dependencies for an extension without installing.</summary>
    [HttpGet("registry/{extensionId}/dependencies")]
    public async Task<IActionResult> RegistryResolveDependencies(
        string extensionId,
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var detail = await registry.GetExtensionAsync(extensionId, ct);
        if (detail == null) return NotFound();

        // Resolve against the version that would actually be installed (latest host-compatible), so the
        // reported dependencies match that version's requirements.
        var resolveVersion = SelectRegistryVersion(detail, null, null, out _);
        if (resolveVersion == null)
            return Ok(new List<DependencyInfo>());

        var installedVersions = extensionManager.Installations.Values
            .Where(i => extensionManager.IsEffectivelyInstalled(i.ExtensionId))
            .ToDictionary(i => i.ExtensionId, i => i.Version, StringComparer.OrdinalIgnoreCase);
        var plan = new List<RegistryInstallPlanItem>();
        var deps = new List<DependencyInfo>();
        var missing = new Dictionary<string, DependencyInfo>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await ResolveDependencyPlanAsync(registry, detail, resolveVersion, installedVersions, plan, deps, missing, new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase), ct);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        return Ok(deps);
    }

    /// <summary>Uninstall an extension by removing its directory.</summary>
    [HttpPost("registry/uninstall")]
    [RequiresPermission(Permissions.ExtensionsUninstall)]
    public async Task<IActionResult> RegistryUninstall(
        [FromBody] RegistryUninstallRequest request,
        CancellationToken ct = default)
    {
        if (!extensionManager.Installations.ContainsKey(request.ExtensionId)
            && !extensionManager.Extensions.Any(extension => string.Equals(extension.Id, request.ExtensionId, StringComparison.OrdinalIgnoreCase)))
        {
            return NotFound($"Extension '{request.ExtensionId}' not found.");
        }

        var dependents = extensionManager.GetDependentExtensionIds(request.ExtensionId)
            .Select(CreateDependencyImpact)
            .ToList();

        if (dependents.Count > 0 && !request.UninstallDependents)
        {
            return Ok(new
            {
                requiresDependents = true,
                extension = CreateDependencyImpact(request.ExtensionId),
                dependents,
            });
        }

        var extensionsDir = Path.Combine(extensionManager.Context.DataDirectory, "..", "extensions");
        extensionsDir = Path.GetFullPath(extensionsDir);
        var idsToUninstall = dependents
            .Select(dependent => dependent.Id)
            .Append(request.ExtensionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var uninstalledExtensions = new List<string>();

        foreach (var extensionId in idsToUninstall)
        {
            var unloaded = await extensionManager.UnloadExtensionAsync(extensionId, HttpContext.RequestServices, ct);
            if (!unloaded)
            {
                if (string.Equals(extensionId, request.ExtensionId, StringComparison.OrdinalIgnoreCase))
                    return NotFound($"Extension '{request.ExtensionId}' not found.");

                continue;
            }

            var extDir = Path.Combine(extensionsDir, extensionId);
            if (Directory.Exists(extDir))
            {
                var deleteError = await DeleteDirectoryWithRetriesAsync(extDir, ct);
                if (deleteError != null && Directory.Exists(extDir))
                {
                    return Conflict(new
                    {
                        message = $"Extension '{extensionId}' was unloaded but files are still locked by another process.",
                        extensionId,
                        path = extDir,
                        detail = deleteError.Message,
                    });
                }
            }

            uninstalledExtensions.Add(extensionId);
        }

        scraperService.ReloadScrapers();
        return Ok(new
        {
            message = dependents.Count > 0
                ? $"Extension '{request.ExtensionId}' and {dependents.Count} dependent extension{(dependents.Count == 1 ? string.Empty : "s")} uninstalled."
                : $"Extension '{request.ExtensionId}' uninstalled.",
            requiresDependents = false,
            uninstalledExtensions,
        });
    }

    private ExtensionDependencyImpact CreateDependencyImpact(string extensionId)
    {
        var extension = extensionManager.Extensions.FirstOrDefault(candidate => string.Equals(candidate.Id, extensionId, StringComparison.OrdinalIgnoreCase));
        var installation = extensionManager.GetInstallation(extensionId);
        var manifest = extensionManager.GetManifestFile(extensionId);

        return new ExtensionDependencyImpact(
            extensionId,
            extension?.Name ?? manifest?.Name ?? extensionId,
            installation?.Version ?? manifest?.Version ?? extension?.Version ?? string.Empty,
            extensionManager.IsEnabled(extensionId),
            manifest?.Kind ?? "extension",
            installation?.Source ?? "unknown");
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

    private async Task ResolveDependencyPlanAsync(
        IExtensionRegistry registry,
        RegistryExtensionDetail detail,
        RegistryVersionInfo version,
        Dictionary<string, string> installedVersions,
        List<RegistryInstallPlanItem> plan,
        List<DependencyInfo> dependencyInfos,
        Dictionary<string, DependencyInfo> missingDependencies,
        HashSet<string> visiting,
        HashSet<string> visited,
        CancellationToken ct)
    {
        if (!visiting.Add(detail.Id))
            throw new InvalidOperationException($"Extension dependency cycle detected at '{detail.Id}'.");

        // Resolve against the dependencies of the SPECIFIC version being installed — not the extension's
        // latest — so installing an older, host-compatible version pulls that version's (older, compatible)
        // dependency requirements rather than the newest version's.
        foreach (var (depId, versionConstraint) in version.Dependencies)
        {
            if (installedVersions.TryGetValue(depId, out var installedVersion) && VersionSatisfies(installedVersion, versionConstraint))
                continue;

            var depDetail = await registry.GetExtensionAsync(depId, ct);
            if (depDetail == null)
            {
                missingDependencies[depId] = new DependencyInfo(depId, versionConstraint, null, null, false, installedVersions.ContainsKey(depId));
                dependencyInfos.Add(missingDependencies[depId]);
                continue;
            }

            var depVersion = SelectRegistryVersion(depDetail, null, versionConstraint, out _);
            if (depVersion == null)
            {
                missingDependencies[depId] = new DependencyInfo(depId, versionConstraint, depDetail.Name, null, false, installedVersions.ContainsKey(depId));
                dependencyInfos.Add(missingDependencies[depId]);
                continue;
            }

            if (!visited.Contains(depId))
                await ResolveDependencyPlanAsync(registry, depDetail, depVersion, installedVersions, plan, dependencyInfos, missingDependencies, visiting, visited, ct);

            if (!plan.Any(item => string.Equals(item.Id, depId, StringComparison.OrdinalIgnoreCase)))
                plan.Add(new RegistryInstallPlanItem(depId, depVersion.Version, depDetail.Name, installedVersions.ContainsKey(depId)));

            dependencyInfos.Add(new DependencyInfo(depId, versionConstraint, depDetail.Name, depVersion.Version, true, installedVersions.ContainsKey(depId)));
        }

        visiting.Remove(detail.Id);
        visited.Add(detail.Id);
    }

    private RegistryVersionInfo? SelectRegistryVersion(RegistryExtensionDetail detail, string? requestedVersion, string? versionConstraint, out string? error)
    {
        error = null;
        var compatibleVersions = detail.Versions
            .Where(v => IsCoveVersionCompatible(v.MinCoveVersion))
            .Where(v => string.IsNullOrWhiteSpace(versionConstraint) || VersionSatisfies(v.Version, versionConstraint))
            .OrderByDescending(v => ParseVersionOrZero(v.Version))
            .ThenByDescending(v => v.ReleasedAt ?? DateTime.MinValue)
            .ToList();

        if (!string.IsNullOrWhiteSpace(requestedVersion))
        {
            var requested = detail.Versions.FirstOrDefault(v => string.Equals(v.Version, requestedVersion, StringComparison.OrdinalIgnoreCase));
            if (requested == null)
            {
                error = $"Version '{requestedVersion}' was not found for extension '{detail.Id}'.";
                return null;
            }

            if (!IsCoveVersionCompatible(requested.MinCoveVersion))
            {
                error = $"Extension '{detail.Id}' v{requested.Version} requires Cove >= {requested.MinCoveVersion}; this instance is {extensionManager.Context.CoveVersion}.";
                return null;
            }

            if (!string.IsNullOrWhiteSpace(versionConstraint) && !VersionSatisfies(requested.Version, versionConstraint))
            {
                error = $"Extension '{detail.Id}' v{requested.Version} does not satisfy required version '{versionConstraint}'.";
                return null;
            }

            return requested;
        }

        var selected = compatibleVersions.FirstOrDefault();
        if (selected == null)
            error = $"No compatible version of '{detail.Id}' satisfies '{versionConstraint ?? "any version"}' for Cove {extensionManager.Context.CoveVersion}.";
        return selected;
    }

    private bool IsCoveVersionCompatible(string? minCoveVersion)
    {
        if (string.IsNullOrWhiteSpace(minCoveVersion))
            return true;

        if (!TryParseVersion(extensionManager.Context.CoveVersion, out var coveVersion) || !TryParseVersion(minCoveVersion, out var minimumVersion))
            return true;

        return coveVersion >= minimumVersion;
    }

    private static bool VersionSatisfies(string version, string? constraint)
    {
        if (string.IsNullOrWhiteSpace(constraint) || constraint.Trim() == "*")
            return true;

        var range = constraint.Trim();
        string op;
        string target;

        if (range.StartsWith(">=", StringComparison.Ordinal))
        {
            op = ">=";
            target = range[2..].Trim();
        }
        else if (range.StartsWith("<=", StringComparison.Ordinal))
        {
            op = "<=";
            target = range[2..].Trim();
        }
        else if (range.StartsWith('>'))
        {
            op = ">";
            target = range[1..].Trim();
        }
        else if (range.StartsWith('<'))
        {
            op = "<";
            target = range[1..].Trim();
        }
        else if (range.StartsWith('='))
        {
            op = "=";
            target = range[1..].Trim();
        }
        else
        {
            op = "=";
            target = range;
        }

        if (!TryParseVersion(version, out var current) || !TryParseVersion(target, out var required))
            return false;

        var comparison = current.CompareTo(required);
        return op switch
        {
            ">=" => comparison >= 0,
            "<=" => comparison <= 0,
            ">" => comparison > 0,
            "<" => comparison < 0,
            "=" => comparison == 0,
            _ => false,
        };
    }

    private static Version ParseVersionOrZero(string version) =>
        TryParseVersion(version, out var parsed) ? parsed : new Version(0, 0, 0, 0);

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Trim().TrimStart('v');
        var separator = normalized.IndexOfAny(new[] { '-', '+' });
        if (separator >= 0)
            normalized = normalized[..separator];

        if (Version.TryParse(normalized, out var parsed))
        {
            version = parsed;
            return true;
        }

        version = new Version(0, 0, 0, 0);
        return false;
    }

    private static void ExtractZipSafely(Stream zipStream, string destinationDirectory)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        var destinationRootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
        foreach (var entry in archive.Entries)
        {
            var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
            if (!string.Equals(destinationPath, destinationRoot, StringComparison.OrdinalIgnoreCase)
                && !destinationPath.StartsWith(destinationRootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Extension package contains a path outside the extraction directory.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destinationPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            entry.ExtractToFile(destinationPath, overwrite: true);
            System.IO.File.SetLastWriteTimeUtc(destinationPath, DateTime.UtcNow);
        }
    }

    private static string? FindExtensionPackageRoot(string extractDir)
    {
        if (System.IO.File.Exists(Path.Combine(extractDir, "extension.json")))
            return extractDir;

        var candidates = Directory.GetDirectories(extractDir)
            .Where(dir => System.IO.File.Exists(Path.Combine(dir, "extension.json")))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static bool IsSafeExtensionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || Path.IsPathRooted(id) || id.Contains("..", StringComparison.Ordinal))
            return false;

        return id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }

    private static bool IsPathInsideDirectory(string basePath, string candidatePath)
    {
        var root = Path.GetFullPath(basePath);
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(candidatePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return candidate.StartsWith(rootWithSeparator, comparison);
    }

    private static async Task<Exception?> DeleteDirectoryWithRetriesAsync(string directoryPath, CancellationToken ct)
    {
        if (!Directory.Exists(directoryPath))
            return null;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            try
            {
                RemoveReadOnlyAttributes(directoryPath);
                Directory.Delete(directoryPath, recursive: true);
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
            }
        }

        return lastError;
    }

    private sealed record RegistryInstallPlanItem(string Id, string Version, string Name, bool Installed);
}

public record ExtensionInfo(
    string Id,
    string Name,
    string Version,
    string? Description,
    string? Author,
    string? Url,
    string? IconUrl,
    bool Enabled,
    bool HasUI,
    bool HasApi,
    bool HasState,
    bool HasJobs,
    bool HasEvents,
    bool HasData,
    bool HasMiddleware,
    bool HasActions,
    List<string> Categories,
    string? MinCoveVersion,
    Dictionary<string, string> Dependencies,
    List<ExtensionExternalDependency> ExternalDependencies,
    List<ExtensionSettingManifest> Settings,
    string Kind,
    string Source,
    DateTime? InstalledAt,
    List<JobInfo> Jobs);

public record JobInfo(string Id, string Name, string? Description);

public record RegistryInstallRequest
{
    public required string ExtensionId { get; init; }
    public required string Version { get; init; }
    /// <summary>When true, automatically install missing dependencies.</summary>
    public bool InstallDependencies { get; init; }
}

public record InstallExtensionFromUrlRequest
{
    public required string Url { get; init; }
    public bool TrustUnverified { get; init; }
}

public record RegistryUninstallRequest
{
    public required string ExtensionId { get; init; }
    /// <summary>When true, uninstall extensions that depend on the requested extension too.</summary>
    public bool UninstallDependents { get; init; }
}

public record ExtensionDependencyImpact(
    string Id,
    string Name,
    string Version,
    bool Enabled,
    string Kind,
    string Source);

public record DependencyInfo(
    string Id,
    string VersionConstraint,
    string? Name,
    string? ResolvedVersion,
    bool Available,
    bool Installed = false
);

/// <summary>Bridges extension IJobProgress to core IJobProgress.</summary>
internal class JobProgressBridge(Cove.Core.Interfaces.IJobProgress coreProgress) : Cove.Plugins.IJobProgress
{
    public void Report(double percent, string? message = null) => coreProgress.Report(percent, message);
}
