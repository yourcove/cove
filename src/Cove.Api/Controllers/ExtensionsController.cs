using Microsoft.AspNetCore.Mvc;
using Cove.Plugins;
using Cove.Core.Interfaces;
using System.IO;

namespace Cove.Api.Controllers;

internal static class FrontendRuntimeContract
{
    public const string Version = "v1";
}

[ApiController]
[Route("api/[controller]")]
public class ExtensionsController(ExtensionManager extensionManager) : ControllerBase
{
    /// <summary>Returns the aggregated UI manifest from all registered extensions.</summary>
    [HttpGet("manifest")]
    public ActionResult<UIManifest> GetManifest()
    {
        var manifest = extensionManager.GetAggregatedManifest();
        manifest.FrontendRuntimeVersion = FrontendRuntimeContract.Version;

        var jsBundles = extensionManager.GetEnabledJsBundles();
        if (jsBundles.Count == 1)
        {
            var (extId, path) = jsBundles[0];
            manifest.JsBundleUrl = $"/api/extensions/assets/{Uri.EscapeDataString(extId)}/{path}";
        }
        else if (jsBundles.Count > 1)
        {
            manifest.JsBundleUrl = "/api/extensions/bundles/ui.mjs";
        }

        var cssBundles = extensionManager.GetEnabledCssBundles();
        if (cssBundles.Count == 1)
        {
            var (extId, path) = cssBundles[0];
            manifest.CssBundleUrl = $"/api/extensions/assets/{Uri.EscapeDataString(extId)}/{path}";
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
            return Content("export default { components: {} };", "application/javascript");
        }

        var lines = new List<string>();
        for (var i = 0; i < jsBundles.Count; i++)
        {
            var (extId, path) = jsBundles[i];
            var url = $"/api/extensions/assets/{Uri.EscapeDataString(extId)}/{path}";
            lines.Add($"import * as m{i} from '{url}';");
        }

        lines.Add("const components = {};");
        for (var i = 0; i < jsBundles.Count; i++)
        {
            lines.Add($"Object.assign(components, (m{i}.default && m{i}.default.components) || {{}});");
        }
        lines.Add("export default { components };\n");

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
            var url = $"/api/extensions/assets/{Uri.EscapeDataString(extId)}/{path}";
            lines.Add($"@import url('{url}');");
        }

        return Content(string.Join("\n", lines), "text/css");
    }

    /// <summary>Returns a list of all registered extensions with capability and category info.</summary>
    [HttpGet]
    public ActionResult<IEnumerable<ExtensionInfo>> GetExtensions([FromQuery] string? category = null) =>
        Ok(extensionManager.Extensions
            .Where(e => category == null || e.Categories.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
            .Select(e =>
        {
            var install = extensionManager.GetInstallation(e.Id);
            return new ExtensionInfo(
                e.Id,
                e.Name,
                e.Version,
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
                e.Categories.ToList(),
                e.MinCoveVersion,
                e.Dependencies.ToDictionary(kv => kv.Key, kv => kv.Value),
                install?.Source ?? "unknown",
                install?.InstalledAt,
                e is IJobExtension je ? je.Jobs.Select(j => new JobInfo(j.Id, j.Name, j.Description)).ToList() : []);
        }));

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
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == id);
        if (ext == null) return NotFound();
        return Ok(extensionManager.GetMissingDependencies(id));
    }

    /// <summary>Enable an extension.</summary>
    [HttpPost("{id}/enable")]
    public async Task<IActionResult> Enable(string id, CancellationToken ct)
    {
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == id);
        if (ext == null) return NotFound();
        await extensionManager.EnableExtensionAsync(id, ct);
        return Ok();
    }

    /// <summary>Disable an extension.</summary>
    [HttpPost("{id}/disable")]
    public async Task<IActionResult> Disable(string id, CancellationToken ct)
    {
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == id);
        if (ext == null) return NotFound();
        await extensionManager.DisableExtensionAsync(id, ct);
        return Ok();
    }

    /// <summary>Get extension key-value store data.</summary>
    [HttpGet("{id}/data")]
    public async Task<IActionResult> GetData(string id, CancellationToken ct)
    {
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == id) as IStatefulExtension;
        if (ext == null) return NotFound("Extension not found or not stateful");

        var factory = HttpContext.RequestServices.GetService<IExtensionStoreFactory>();
        if (factory == null) return StatusCode(500, "Store not available");

        var store = factory.CreateStore(id);
        var data = await store.GetAllAsync(ct);
        return Ok(data);
    }

    /// <summary>Set a key-value pair in extension store.</summary>
    [HttpPut("{id}/data/{key}")]
    public async Task<IActionResult> SetData(string id, string key, [FromBody] string value, CancellationToken ct)
    {
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == id) as IStatefulExtension;
        if (ext == null) return NotFound("Extension not found or not stateful");

        var factory = HttpContext.RequestServices.GetService<IExtensionStoreFactory>();
        if (factory == null) return StatusCode(500, "Store not available");

        var store = factory.CreateStore(id);
        await store.SetAsync(key, value, ct);
        return Ok();
    }

    /// <summary>Trigger a job defined by an extension.</summary>
    [HttpPost("{id}/jobs/{jobId}/run")]
    public IActionResult RunJob(string id, string jobId, [FromBody] Dictionary<string, string>? parameters,
        [FromServices] IJobService jobService)
    {
        var ext = extensionManager.Extensions.OfType<IJobExtension>().FirstOrDefault(e => e.Id == id);
        if (ext == null) return NotFound("Extension not found or has no jobs");

        var job = ext.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return NotFound($"Job '{jobId}' not found");

        // Run through the core job service for proper queuing, progress tracking, and SignalR updates
        var coreJobId = jobService.Enqueue(
            $"ext:{ext.Id}:{jobId}",
            $"[{ext.Name}] {job.Name}",
            async (coreProgress, ct) =>
            {
                var bridge = new JobProgressBridge(coreProgress);
                await ext.RunJobAsync(jobId, parameters, bridge, ct);
            },
            exclusive: false);

        return Accepted(new { message = $"Job '{job.Name}' started", jobId = coreJobId });
    }

    /// <summary>Serve static assets from an extension's data directory.</summary>
    [HttpGet("assets/{extensionId}/{**path}")]
    public IActionResult GetAsset(string extensionId, string path)
    {
        var ext = extensionManager.Extensions.FirstOrDefault(e => e.Id == extensionId);
        if (ext == null) return NotFound();

        var basePath = Path.Combine(extensionManager.Context.DataDirectory, extensionId);
        var fullPath = Path.GetFullPath(Path.Combine(basePath, path));

        // Security: prevent path traversal
        if (!fullPath.StartsWith(Path.GetFullPath(basePath)))
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

        return PhysicalFile(fullPath, contentType);
    }

    // ========================================================================
    // REGISTRY ENDPOINTS
    // ========================================================================

    /// <summary>Search the extension registry.</summary>
    [HttpGet("registry/search")]
    public async Task<IActionResult> RegistrySearch(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromServices] IExtensionRegistry registry = null!,
        CancellationToken ct = default)
    {
        var result = await registry.SearchAsync(new RegistrySearchRequest
        {
            Query = q,
            Categories = category != null ? [category] : null,
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
        var installed = extensionManager.Extensions.Select(e => (e.Id, e.Version));
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

        // Find the specific version's details (or use latest if no deps differ per version)
        var missingDeps = new List<DependencyInfo>();
        var installedIds = new HashSet<string>(extensionManager.Extensions.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);

        if (detail.Dependencies.Count > 0)
        {
            foreach (var (depId, versionConstraint) in detail.Dependencies)
            {
                if (installedIds.Contains(depId)) continue;

                var depDetail = await registry.GetExtensionAsync(depId, ct);
                if (depDetail == null)
                {
                    missingDeps.Add(new DependencyInfo(depId, versionConstraint, null, null, false));
                    continue;
                }

                missingDeps.Add(new DependencyInfo(depId, versionConstraint, depDetail.Name, depDetail.Version, true));
            }
        }

        // If there are missing deps and the client didn't opt in to auto-install, return them
        if (missingDeps.Count > 0 && !request.InstallDependencies)
        {
            return Ok(new
            {
                requiresDependencies = true,
                extension = new { detail.Id, detail.Name, detail.Version },
                missingDependencies = missingDeps,
            });
        }

        // Install missing dependencies first
        var installedExtensions = new List<string>();
        foreach (var dep in missingDeps.Where(d => d.Available))
        {
            await registry.DownloadAsync(dep.Id, dep.ResolvedVersion!, extensionsDir, ct);
            extensionManager.DiscoverExtensions(extensionsDir);
            await extensionManager.InitializeExtensionAsync(dep.Id, HttpContext.RequestServices, ct);
            installedExtensions.Add(dep.Id);
        }

        // Install the requested extension
        var installPath = await registry.DownloadAsync(request.ExtensionId, request.Version, extensionsDir, ct);

        // Reload discovered extensions and hot-initialize the newly installed one.
        extensionManager.DiscoverExtensions(extensionsDir);
        var initialized = await extensionManager.InitializeExtensionAsync(request.ExtensionId, HttpContext.RequestServices, ct);
        if (!initialized)
        {
            return StatusCode(500, new
            {
                message = $"Extension '{request.ExtensionId}' was downloaded but failed to initialize.",
                path = installPath,
            });
        }

        return Ok(new
        {
            message = $"Extension '{request.ExtensionId}' v{request.Version} installed.",
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

        var installedIds = new HashSet<string>(extensionManager.Extensions.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var deps = new List<DependencyInfo>();

        foreach (var (depId, versionConstraint) in detail.Dependencies)
        {
            var isInstalled = installedIds.Contains(depId);
            var depDetail = await registry.GetExtensionAsync(depId, ct);
            deps.Add(new DependencyInfo(
                depId,
                versionConstraint,
                depDetail?.Name,
                depDetail?.Version,
                depDetail != null,
                isInstalled));
        }

        return Ok(deps);
    }

    /// <summary>Uninstall an extension by removing its directory.</summary>
    [HttpPost("registry/uninstall")]
    public async Task<IActionResult> RegistryUninstall(
        [FromBody] RegistryUninstallRequest request,
        CancellationToken ct = default)
    {
        var unloaded = await extensionManager.UnloadExtensionAsync(request.ExtensionId, HttpContext.RequestServices, ct);
        if (!unloaded)
            return NotFound($"Extension '{request.ExtensionId}' not found.");

        // Remove the extension directory
        var extensionsDir = Path.Combine(extensionManager.Context.DataDirectory, "..", "extensions");
        extensionsDir = Path.GetFullPath(extensionsDir);
        var extDir = Path.Combine(extensionsDir, request.ExtensionId);

        if (Directory.Exists(extDir))
        {
            const int maxAttempts = 8;
            Exception? lastError = null;
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    RemoveReadOnlyAttributes(extDir);
                    Directory.Delete(extDir, recursive: true);
                    lastError = null;
                    break;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    await Task.Delay(TimeSpan.FromMilliseconds(150 * attempt), ct);
                }
            }

            if (lastError != null && Directory.Exists(extDir))
            {
                return Conflict(new
                {
                    message = $"Extension '{request.ExtensionId}' was unloaded but files are still locked by another process.",
                    extensionId = request.ExtensionId,
                    path = extDir,
                    detail = lastError.Message,
                });
            }
        }

        return Ok(new { message = $"Extension '{request.ExtensionId}' uninstalled." });
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

public record RegistryUninstallRequest
{
    public required string ExtensionId { get; init; }
}

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
