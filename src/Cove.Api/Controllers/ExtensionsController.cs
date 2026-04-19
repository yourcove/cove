using Microsoft.AspNetCore.Mvc;
using Cove.Plugins;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ExtensionsController(ExtensionManager extensionManager) : ControllerBase
{
    /// <summary>Returns the aggregated UI manifest from all registered extensions.</summary>
    [HttpGet("manifest")]
    public ActionResult<UIManifest> GetManifest() =>
        Ok(extensionManager.GetAggregatedManifest());

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
    public async Task<IActionResult> RunJob(string id, string jobId, [FromBody] Dictionary<string, string>? parameters, CancellationToken ct)
    {
        var ext = extensionManager.Extensions.OfType<IJobExtension>().FirstOrDefault(e => e.Id == id);
        if (ext == null) return NotFound("Extension not found or has no jobs");

        var job = ext.Jobs.FirstOrDefault(j => j.Id == jobId);
        if (job == null) return NotFound($"Job '{jobId}' not found");

        // Run job in background
        _ = Task.Run(async () =>
        {
            var progress = new SimpleJobProgress();
            await ext.RunJobAsync(jobId, parameters, progress, CancellationToken.None);
        }, CancellationToken.None);

        return Accepted(new { message = $"Job '{job.Name}' started" });
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
    // REGISTRY ENDPOINTS (stubs — ready for when remote registry is built)
    // ========================================================================

    /// <summary>Search the extension registry.</summary>
    [HttpGet("registry/search")]
    public async Task<IActionResult> RegistrySearch(
        [FromQuery] string? q,
        [FromQuery] string? category,
        [FromQuery] string? sort,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromServices] IExtensionRegistry? registry = null,
        CancellationToken ct = default)
    {
        registry ??= new StubExtensionRegistry();
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
        [FromServices] IExtensionRegistry? registry = null,
        CancellationToken ct = default)
    {
        registry ??= new StubExtensionRegistry();
        var detail = await registry.GetExtensionAsync(extensionId, ct);
        if (detail == null) return NotFound();
        return Ok(detail);
    }

    /// <summary>Check for updates for all installed extensions.</summary>
    [HttpGet("registry/updates")]
    public async Task<IActionResult> RegistryCheckUpdates(
        [FromServices] IExtensionRegistry? registry = null,
        CancellationToken ct = default)
    {
        registry ??= new StubExtensionRegistry();
        var installed = extensionManager.Extensions.Select(e => (e.Id, e.Version));
        var updates = await registry.CheckForUpdatesAsync(installed, ct);
        return Ok(updates);
    }

    /// <summary>Get registry categories.</summary>
    [HttpGet("registry/categories")]
    public async Task<IActionResult> RegistryGetCategories(
        [FromServices] IExtensionRegistry? registry = null,
        CancellationToken ct = default)
    {
        registry ??= new StubExtensionRegistry();
        var categories = await registry.GetCategoriesAsync(ct);
        return Ok(categories);
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

/// <summary>Simple job progress reporter.</summary>
internal class SimpleJobProgress : IJobProgress
{
    public double Percent { get; private set; }
    public string? Message { get; private set; }
    public void Report(double percent, string? message = null) { Percent = percent; Message = message; }
}
