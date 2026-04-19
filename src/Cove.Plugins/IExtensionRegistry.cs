namespace Cove.Plugins;

// ============================================================================
// EXTENSION REGISTRY — Interface for discovering and installing extensions
// from a remote registry. This is the "app store" for Cove extensions.
// ============================================================================

/// <summary>
/// Interface for a remote extension registry. Extensions can be searched,
/// downloaded, and installed from the registry.
/// </summary>
public interface IExtensionRegistry
{
    /// <summary>Search the registry for extensions matching a query.</summary>
    Task<RegistrySearchResult> SearchAsync(RegistrySearchRequest request, CancellationToken ct = default);

    /// <summary>Get detailed info about a specific extension from the registry.</summary>
    Task<RegistryExtensionDetail?> GetExtensionAsync(string extensionId, CancellationToken ct = default);

    /// <summary>Download an extension package to the local extensions directory.</summary>
    Task<string> DownloadAsync(string extensionId, string version, string targetDir, CancellationToken ct = default);

    /// <summary>Check if newer versions are available for installed extensions.</summary>
    Task<List<RegistryUpdateInfo>> CheckForUpdatesAsync(IEnumerable<(string Id, string Version)> installed, CancellationToken ct = default);

    /// <summary>Get all available categories from the registry.</summary>
    Task<List<string>> GetCategoriesAsync(CancellationToken ct = default);
}

/// <summary>Search request for the extension registry.</summary>
public class RegistrySearchRequest
{
    public string? Query { get; set; }
    /// <summary>Filter by categories (AND logic — results must match all).</summary>
    public List<string>? Categories { get; set; }
    /// <summary>Sort by: "relevance", "downloads", "updated", "name".</summary>
    public string SortBy { get; set; } = "relevance";
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

/// <summary>Paginated search results from the registry.</summary>
public class RegistrySearchResult
{
    public List<RegistryExtensionSummary> Items { get; set; } = [];
    public int TotalCount { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

/// <summary>Brief extension summary in registry search results.</summary>
public class RegistryExtensionSummary
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public required string Version { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string? IconUrl { get; set; }
    public List<string> Categories { get; set; } = [];
    /// <summary>Auto-set by registry CI from the latest version's release date.</summary>
    public DateTime? UpdatedAt { get; set; }
    public string? MinCoveVersion { get; set; }
}

/// <summary>Full extension detail from the registry.</summary>
public class RegistryExtensionDetail : RegistryExtensionSummary
{
    public string? Url { get; set; }
    public string? Readme { get; set; }
    public string? Changelog { get; set; }
    public List<string> Screenshots { get; set; } = [];
    public Dictionary<string, string> Dependencies { get; set; } = [];
    public List<RegistryVersionInfo> Versions { get; set; } = [];
}

/// <summary>Version history entry for a registry extension.</summary>
public class RegistryVersionInfo
{
    public required string Version { get; set; }
    /// <summary>Auto-set by registry CI from the GitHub release date.</summary>
    public DateTime? ReleasedAt { get; set; }
    public string? Changelog { get; set; }
    public string? MinCoveVersion { get; set; }
    public string? Checksum { get; set; }
}

/// <summary>Update availability info.</summary>
public class RegistryUpdateInfo
{
    public required string ExtensionId { get; set; }
    public required string CurrentVersion { get; set; }
    public required string LatestVersion { get; set; }
    public string? Changelog { get; set; }
}

// ============================================================================
// STUB IMPLEMENTATION — Returns empty results until a real registry is built
// ============================================================================

/// <summary>
/// Stub registry implementation. Returns empty results for all operations.
/// Replace with a real HTTP-based registry client when the registry server is available.
/// </summary>
public class StubExtensionRegistry : IExtensionRegistry
{
    public Task<RegistrySearchResult> SearchAsync(RegistrySearchRequest request, CancellationToken ct = default)
        => Task.FromResult(new RegistrySearchResult { Page = request.Page, PageSize = request.PageSize });

    public Task<RegistryExtensionDetail?> GetExtensionAsync(string extensionId, CancellationToken ct = default)
        => Task.FromResult<RegistryExtensionDetail?>(null);

    public Task<string> DownloadAsync(string extensionId, string version, string targetDir, CancellationToken ct = default)
        => throw new NotImplementedException("Remote registry not yet available. Install extensions manually by placing them in the extensions directory.");

    public Task<List<RegistryUpdateInfo>> CheckForUpdatesAsync(IEnumerable<(string Id, string Version)> installed, CancellationToken ct = default)
        => Task.FromResult(new List<RegistryUpdateInfo>());

    public Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
        => Task.FromResult(new List<string>());
}
