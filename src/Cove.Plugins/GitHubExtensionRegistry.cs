using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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

    private async Task<RegistryExtensionMetadata?> GetResolvedMetadataAsync(string extensionId, CancellationToken ct)
    {
        var meta = await GetMetadataAsync(extensionId, ct);
        if (meta == null) return null;

        if (string.IsNullOrWhiteSpace(meta.SourceManifestUrl))
            return meta;

        try
        {
            var response = await _http.GetAsync(meta.SourceManifestUrl, ct);
            if (!response.IsSuccessStatusCode)
                return meta;

            var json = await response.Content.ReadAsStringAsync(ct);
            var source = JsonSerializer.Deserialize<ExtensionSourceManifest>(json, JsonOpts);
            if (source == null)
                return meta;

            meta.Name ??= source.Name;
            meta.Description ??= source.Description;
            meta.Author ??= source.Author;
            meta.Url ??= source.Url;
            meta.IconUrl ??= source.IconUrl;
            meta.Categories ??= source.Categories;
            meta.MinCoveVersion ??= source.MinCoveVersion;
            meta.Dependencies ??= source.Dependencies;

            if (meta.Versions != null)
            {
                foreach (var version in meta.Versions)
                {
                    version.MinCoveVersion ??= source.MinCoveVersion;
                }
            }
        }
        catch
        {
            // Metadata remains usable even if source manifest is unavailable.
        }

        return meta;
    }

    public async Task<RegistrySearchResult> SearchAsync(RegistrySearchRequest request, CancellationToken ct = default)
    {
        var summaries = await ResolveSummariesAsync(ct);
        IEnumerable<RegistryExtensionSummary> items = summaries;

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var q = request.Query.Trim();
            items = items.Where(e =>
                (e.Name?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Id?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (e.Author?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

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
            "updated" => list.OrderByDescending(e => e.UpdatedAt ?? DateTime.MinValue).ToList(),
            _ => list, // relevance = default order
        };

        // Paginate
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var paged = list.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new RegistrySearchResult
        {
            Items = paged,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
        };
    }

    public async Task<RegistryExtensionDetail?> GetExtensionAsync(string extensionId, CancellationToken ct = default)
    {
        var meta = await GetResolvedMetadataAsync(extensionId, ct);
        if (meta == null) return null;

        var validVersions = (meta.Versions ?? [])
            .Where(v => IsInstallableVersion(v))
            .ToList();
        if (validVersions.Count == 0)
            return null;

        var latestVersion = validVersions
            .OrderByDescending(v => ParseSemverOrFallback(v.Version))
            .ThenByDescending(v => v.ReleasedAt ?? DateTime.MinValue)
            .First();

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
            Version = latestVersion.Version ?? "0.0.0",
            Description = meta.Description,
            Author = meta.Author,
            IconUrl = meta.IconUrl,
            Url = meta.Url,
            Categories = meta.Categories ?? [],
            UpdatedAt = validVersions.Max(v => v.ReleasedAt) ?? meta.UpdatedAt,
            MinCoveVersion = latestVersion.MinCoveVersion ?? meta.MinCoveVersion,
            Dependencies = meta.Dependencies ?? [],
            Readme = readme,
            Changelog = latestVersion.Changelog ?? meta.Changelog,
            Screenshots = meta.Screenshots ?? [],
            Versions = validVersions.Select(v => new RegistryVersionInfo
            {
                Version = v.Version ?? "0.0.0",
                ReleasedAt = v.ReleasedAt,
                Changelog = v.Changelog,
                MinCoveVersion = v.MinCoveVersion,
                Checksum = v.Checksum,
            }).ToList(),
        };
    }

    public async Task<string> DownloadAsync(string extensionId, string version, string targetDir, CancellationToken ct = default)
    {
        var meta = await GetResolvedMetadataAsync(extensionId, ct);
        if (meta == null)
            throw new InvalidOperationException($"Extension '{extensionId}' not found in registry.");

        // Find the download URL for this version
        var versionInfo = meta.Versions?.FirstOrDefault(v =>
            string.Equals(v.Version, version, StringComparison.OrdinalIgnoreCase));

        if (versionInfo == null)
            throw new InvalidOperationException($"Version '{version}' not found for extension '{extensionId}'.");

        if (!IsInstallableVersion(versionInfo))
            throw new InvalidOperationException($"Registry entry for {extensionId} v{version} is not installable: missing or invalid checksum/downloadUrl.");

        string downloadUrl;
        if (versionInfo.DownloadUrl != null)
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
        if (Directory.Exists(extensionDir))
            Directory.Delete(extensionDir, recursive: true);
        Directory.CreateDirectory(extensionDir);

        var zipPath = Path.Combine(targetDir, $".{extensionId}-{version}.zip");
        await using (var fileStream = System.IO.File.Create(zipPath))
        {
            await response.Content.CopyToAsync(fileStream, ct);
        }

        var expectedChecksum = NormalizeChecksum(versionInfo.Checksum!);
        var actualChecksum = await ComputeSha256Async(zipPath, ct);
        if (!string.Equals(expectedChecksum, actualChecksum, StringComparison.OrdinalIgnoreCase))
        {
            System.IO.File.Delete(zipPath);
            throw new InvalidOperationException(
                $"Checksum validation failed for {extensionId} v{version}. Expected {expectedChecksum}, got {actualChecksum}.");
        }

        // Extract the zip
        using var stream = System.IO.File.OpenRead(zipPath);
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

        System.IO.File.Delete(zipPath);

        return extensionDir;
    }

    public async Task<List<RegistryUpdateInfo>> CheckForUpdatesAsync(
        IEnumerable<(string Id, string Version)> installed,
        CancellationToken ct = default)
    {
        var summaries = await ResolveSummariesAsync(ct);
        var byId = summaries.ToDictionary(s => s.Id, s => s, StringComparer.OrdinalIgnoreCase);
        var updates = new List<RegistryUpdateInfo>();

        foreach (var (id, currentVersion) in installed)
        {
            if (!byId.TryGetValue(id, out var entry)) continue;

            if (IsNewerVersion(entry.Version, currentVersion))
            {
                updates.Add(new RegistryUpdateInfo
                {
                    ExtensionId = id,
                    CurrentVersion = currentVersion,
                    LatestVersion = entry.Version,
                });
            }
        }

        return updates;
    }

    public async Task<List<string>> GetCategoriesAsync(CancellationToken ct = default)
    {
        var summaries = await ResolveSummariesAsync(ct);
        return summaries
            .SelectMany(e => e.Categories)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    private async Task<List<RegistryExtensionSummary>> ResolveSummariesAsync(CancellationToken ct)
    {
        var index = await GetIndexAsync(ct);
        var summaries = new List<RegistryExtensionSummary>();

        foreach (var entry in index.Extensions)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
                continue;

            var meta = await GetResolvedMetadataAsync(entry.Id, ct);
            if (meta?.Versions == null)
                continue;

            var validVersions = meta.Versions.Where(IsInstallableVersion).ToList();
            if (validVersions.Count == 0)
                continue;

            var latest = validVersions
                .OrderByDescending(v => ParseSemverOrFallback(v.Version))
                .ThenByDescending(v => v.ReleasedAt ?? DateTime.MinValue)
                .First();

            summaries.Add(new RegistryExtensionSummary
            {
                Id = meta.Id ?? entry.Id,
                Name = meta.Name ?? entry.Id,
                Version = latest.Version ?? "0.0.0",
                Description = meta.Description,
                Author = meta.Author,
                IconUrl = meta.IconUrl,
                Categories = meta.Categories ?? [],
                UpdatedAt = latest.ReleasedAt ?? meta.UpdatedAt,
                MinCoveVersion = latest.MinCoveVersion ?? meta.MinCoveVersion,
            });
        }

        return summaries;
    }

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

    private static bool IsInstallableVersion(RegistryVersionEntry? version)
    {
        if (version == null) return false;
        if (string.IsNullOrWhiteSpace(version.Version)) return false;
        if (string.IsNullOrWhiteSpace(version.DownloadUrl)) return false;
        if (string.IsNullOrWhiteSpace(version.Checksum)) return false;

        var normalized = NormalizeChecksum(version.Checksum);
        return Regex.IsMatch(normalized, "^[a-fA-F0-9]{64}$");
    }

    private static string NormalizeChecksum(string checksum)
    {
        const string shaPrefix = "sha256:";
        var trimmed = checksum.Trim();
        if (trimmed.StartsWith(shaPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed[shaPrefix.Length..];
        return trimmed;
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken ct)
    {
        await using var fs = System.IO.File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static Version ParseSemverOrFallback(string? version)
    {
        if (!string.IsNullOrWhiteSpace(version) && Version.TryParse(version.Trim().TrimStart('v'), out var parsed))
            return parsed;
        return new Version(0, 0, 0, 0);
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
    }

    private class RegistryExtensionMetadata
    {
        public string? Id { get; set; }
        public string? SourceManifestUrl { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? IconUrl { get; set; }
        public string? Url { get; set; }
        public string? RepositoryUrl { get; set; }
        public List<string>? Categories { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string? MinCoveVersion { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public string? Changelog { get; set; }
        public List<string>? Screenshots { get; set; }
        public List<RegistryVersionEntry>? Versions { get; set; }
    }

    private class ExtensionSourceManifest
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Version { get; set; }
        public string? Description { get; set; }
        public string? Author { get; set; }
        public string? Url { get; set; }
        public string? IconUrl { get; set; }
        public string? MinCoveVersion { get; set; }
        public List<string>? Categories { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
    }

    private class RegistryVersionEntry
    {
        public string? Version { get; set; }
        public DateTime? ReleasedAt { get; set; }
        public string? Changelog { get; set; }
        public string? MinCoveVersion { get; set; }
        public string? Checksum { get; set; }
        public string? DownloadUrl { get; set; }
    }
}
