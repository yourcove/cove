using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cove.Plugins;

/// <summary>
/// Extension registry backed by a GitHub repository.
/// The registry repo contains an index.json manifest listing all extensions,
/// with each extension referencing its source repository and release assets.
///
/// Registry repo structure (yourcove/officialextensionregistry):
///   index.json          — master index of all extensions
///   extensions/
///     {extensionId}/
///       metadata.json   — full extension metadata
///       icon.png        — optional icon
///       README.md       — optional readme
///
/// Extension releases are GitHub releases on the extension's own repository.
/// The registry just indexes metadata; actual packages are downloaded from
/// the extension repo's GitHub Releases.
/// </summary>
public class GitHubExtensionRegistry : IExtensionRegistry
{
    private readonly HttpClient _http;
    private readonly string _registryOwner;
    private readonly string _registryRepo;
    private readonly string _branch;

    // Cache the index for 5 minutes to avoid hammering GitHub
    private RegistryIndex? _cachedIndex;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public GitHubExtensionRegistry(
        HttpClient http,
        string registryOwner = "yourcove",
        string registryRepo = "officialextensionregistry",
        string branch = "main")
    {
        _http = http;
        _registryOwner = registryOwner;
        _registryRepo = registryRepo;
        _branch = branch;
    }

    private string RawUrl(string path) =>
        $"https://raw.githubusercontent.com/{_registryOwner}/{_registryRepo}/{_branch}/{path}";

    private async Task<RegistryIndex> GetIndexAsync(CancellationToken ct)
    {
        if (_cachedIndex != null && DateTime.UtcNow < _cacheExpiry)
            return _cachedIndex;

        var url = RawUrl("index.json");
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        var index = JsonSerializer.Deserialize<RegistryIndex>(json, JsonOpts) ?? new RegistryIndex();
        _cachedIndex = index;
        _cacheExpiry = DateTime.UtcNow + CacheDuration;
        return index;
    }

    private async Task<RegistryExtensionMetadata?> GetMetadataAsync(string extensionId, CancellationToken ct)
    {
        var url = RawUrl($"extensions/{extensionId}/metadata.json");
        try
        {
            var response = await _http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode) return null;
            var json = await response.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<RegistryExtensionMetadata>(json, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    public async Task<RegistrySearchResult> SearchAsync(RegistrySearchRequest request, CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct);
        var items = index.Extensions.AsEnumerable();

        // Filter by query
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.Trim();
            items = items.Where(e =>
                (e.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Author?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        // Filter by categories
        if (request.Categories is { Count: > 0 })
        {
            items = items.Where(e =>
                request.Categories.All(c =>
                    e.Categories?.Contains(c, StringComparer.OrdinalIgnoreCase) ?? false));
        }

        var list = items.ToList();
        var totalCount = list.Count;

        // Sort
        list = request.SortBy?.ToLower() switch
        {
            "name" => list.OrderBy(e => e.Name).ToList(),
            "updated" => list.OrderByDescending(e => e.UpdatedAt).ToList(),
            "downloads" => list.OrderByDescending(e => e.Downloads).ToList(),
            _ => list, // relevance = default order
        };

        // Paginate
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new RegistrySearchResult
        {
            Items = paged.Select(ToSummary).ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<RegistryExtensionDetail?> GetExtensionAsync(string extensionId, CancellationToken ct = default)
    {
        var meta = await GetMetadataAsync(extensionId, ct);
        if (meta == null) return null;

        // Try to load README
        string? readme = null;
        try
        {
            var readmeUrl = RawUrl($"extensions/{extensionId}/README.md");
            var resp = await _http.GetAsync(readmeUrl, ct);
            if (resp.IsSuccessStatusCode)
                readme = await resp.Content.ReadAsStringAsync(ct);
        }
        catch { /* ignore */ }

        return new RegistryExtensionDetail
        {
            Id = meta.Id ?? extensionId,
            Name = meta.Name ?? extensionId,
            Version = meta.Version ?? "0.0.0",
            Description = meta.Description,
            Author = meta.Author,
            IconUrl = meta.IconUrl,
            Url = meta.Url,
            Categories = meta.Categories ?? [],
            Downloads = meta.Downloads,
            UpdatedAt = meta.UpdatedAt,
            MinCoveVersion = meta.MinCoveVersion,
            Dependencies = meta.Dependencies ?? [],
            Readme = readme,
            Changelog = meta.Changelog,
            Screenshots = meta.Screenshots ?? [],
            Versions = meta.Versions?.Select(v => new RegistryVersionInfo
            {
                Version = v.Version ?? "0.0.0",
                ReleasedAt = v.ReleasedAt,
                Changelog = v.Changelog,
                MinCoveVersion = v.MinCoveVersion,
                Checksum = v.Checksum,
            }).ToList() ?? [],
        };
    }

    public async Task<string> DownloadAsync(string extensionId, string version, string targetDir, CancellationToken ct = default)
    {
        var meta = await GetMetadataAsync(extensionId, ct);
        if (meta == null)
            throw new InvalidOperationException($"Extension '{extensionId}' not found in registry.");

        // Find the download URL for this version
        var versionInfo = meta.Versions?.FirstOrDefault(v =>
            string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

        string downloadUrl;
        if (versionInfo?.DownloadUrl != null)
        {
            downloadUrl = versionInfo.DownloadUrl;
        }
        else if (meta.RepositoryUrl != null)
        {
            // Convention: GitHub release asset named {extensionId}-{version}.zip
            downloadUrl = $"{meta.RepositoryUrl}/releases/download/v{version}/{extensionId}-{version}.zip";
        }
        else
        {
            throw new InvalidOperationException($"No download URL found for {extensionId} v{version}.");
        }

        // Download the zip
        var response = await _http.GetAsync(downloadUrl, ct);
        response.EnsureSuccessStatusCode();

        var extensionDir = Path.Combine(targetDir, extensionId);
        Directory.CreateDirectory(extensionDir);

        // Extract the zip
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directory entries

            var destPath = Path.Combine(extensionDir, entry.FullName);
            var destDir = Path.GetDirectoryName(destPath)!;

            // Security: prevent path traversal
            if (!Path.GetFullPath(destPath).StartsWith(Path.GetFullPath(extensionDir), StringComparison.OrdinalIgnoreCase))
                continue;

            Directory.CreateDirectory(destDir);
            entry.ExtractToFile(destPath, overwrite: true);
        }

        return extensionDir;
    }

    public async Task<List<RegistryUpdateInfo>> CheckForUpdatesAsync(
        IEnumerable<(string Id, string Version)> installed,
        CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct);
        var updates = new List<RegistryUpdateInfo>();

        foreach (var (id, currentVersion) in installed)
        {
            var entry = index.Extensions.FirstOrDefault(e =>
                string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
            if (entry == null) continue;

            if (IsNewerVersion(entry.Version ?? "0.0.0", currentVersion))
            {
                updates.Add(new RegistryUpdateInfo
                {
                    ExtensionId = id,
                    CurrentVersion = currentVersion,
                    LatestVersion = entry.Version ?? "0.0.0",
                });
            }
        }

        return updates;
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var index = await GetIndexAsync(ct);
        return index.Extensions
            .SelectMany(e => e.Categories ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    private static RegistryExtensionSummary ToSummary(RegistryIndexEntry e) => new()
    {
        Id = e.Id ?? "",
        Name = e.Name ?? e.Id ?? "",
        Version = e.Version ?? "0.0.0",
        Description = e.Description,
        Author = e.Author,
        IconUrl = e.IconUrl,
        Categories = e.Categories ?? [],
        Downloads = e.Downloads,
        UpdatedAt = e.UpdatedAt,
        MinCoveVersion = e.MinCoveVersion,
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Returns true if <paramref name="candidate"/> is a newer semver than <paramref name="current"/>.</summary>
    private static bool IsNewerVersion(string candidate, string current)
    {
        if (Version.TryParse(candidate.TrimStart('v'), out var c) && Version.TryParse(current.TrimStart('v'), out var cur))
            return c > cur;
        return string.Compare(candidate, current, StringComparison.OrdinalIgnoreCase) > 0;
    }

    // ===== Internal DTOs for registry JSON files =====

    private class RegistryIndex
    {
        public string? SchemaVersion { get; set; }
        public DateTime? GeneratedAt { get; set; }
        public List<RegistryIndexEntry> Extensions { get; set; } = [];
    }

    private class RegistryIndexEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? IconUrl { get; set; }
        public List<string>? Categories { get; set; }
        public int Downloads { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? MinCoveVersion { get; set; }
    }

    private class RegistryExtensionMetadata
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? IconUrl { get; set; }
        public string? Url { get; set; }
        public string? RepositoryUrl { get; set; }
        public List<string>? Categories { get; set; }
        public int Downloads { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string? MinCoveVersion { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public string? Changelog { get; set; }
        public List<string>? Screenshots { get; set; }
        public List<RegistryVersionEntry>? Versions { get; set; }
    }

    private class RegistryVersionEntry
    {
        public string? Version { get; set; }
        public DateTime ReleasedAt { get; set; }
        public string? Changelog { get; set; }
        public string? MinCoveVersion { get; set; }
        public string? Checksum { get; set; }
        public string? DownloadUrl { get; set; }
    }
}
