using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Cove.Core.Common;

namespace Cove.Plugins;

/// <summary>
/// Extension registry backed by a GitHub repository.
/// The registry repo contains an index.json manifest listing all extensions,
/// with each extension referencing its source repository and release assets.
///
/// Registry repo structure (yourcove/officialextensionregistry):
///   index.json          — master index of all extensions
///   extensions/
///     {extensionId}.json — full extension metadata and version history
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
    private readonly string? _coveVersion;
    private readonly Uri? _registryBaseUri;

    // Cache the index for 5 minutes to avoid hammering GitHub
    private RegistryIndex? _cachedIndex;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    // Cache the fully resolved summary list so search/filter/paging does not
    // re-fetch every extension metadata file on each request.
    private List<RegistryExtensionSummary>? _cachedSummaries;
    private DateTime _summariesExpiry = DateTime.MinValue;
    private readonly SemaphoreSlim _summariesLock = new(1, 1);

    // The registry can list hundreds of extensions (e.g. YAML scraper packs).
    // Resolve their metadata concurrently with a bounded fan-out instead of
    // one sequential request at a time.
    private const int MaxConcurrentMetadataFetches = 16;

    public GitHubExtensionRegistry(
        HttpClient http,
        string registryOwner = "yourcove",
        string registryRepo = "officialextensionregistry",
        string branch = "main",
        string? coveVersion = null,
        string? registryBaseUrl = null)
    {
        _http = http;
        _registryOwner = registryOwner;
        _registryRepo = registryRepo;
        _branch = branch;
        _coveVersion = coveVersion;
        if (!string.IsNullOrWhiteSpace(registryBaseUrl))
        {
            var normalizedBaseUrl = registryBaseUrl.Trim().TrimEnd('/') + "/";
            if (!Uri.TryCreate(normalizedBaseUrl, UriKind.Absolute, out var registryBaseUri)
                || (!string.Equals(registryBaseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    && !(string.Equals(registryBaseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                        && registryBaseUri.IsLoopback)))
            {
                throw new ArgumentException(
                    "The extension registry base URL must use HTTPS, except for loopback HTTP test servers.",
                    nameof(registryBaseUrl));
            }

            _registryBaseUri = registryBaseUri;
        }
    }

    private string RawUrl(string path) =>
        _registryBaseUri is null
            ? $"https://raw.githubusercontent.com/{_registryOwner}/{_registryRepo}/{_branch}/{path}"
            : new Uri(_registryBaseUri, path).AbsoluteUri;

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
        var url = RawUrl($"extensions/{extensionId}.json");
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
            meta.HomepageUrl ??= source.Url;
            meta.IconUrl ??= source.IconUrl;
            meta.Kind ??= source.Kind;
            meta.Categories ??= source.Categories;
            meta.Dependencies ??= source.Dependencies;
            meta.ExternalDependencies ??= source.ExternalDependencies;
            meta.Settings ??= source.Settings;
            meta.ScraperFiles ??= source.ScraperFiles;
            meta.SourceMinCoveVersion ??= source.MinCoveVersion;
            meta.Version ??= source.Version;
        }
        catch
        {
            // Metadata remains usable even if source manifest is unavailable.
        }

        return meta;
    }

    /// <summary>
    /// A source-tracked scraper pack is content-only (YAML) served directly from the
    /// extension repository source tree. These are intentionally unversioned: there are
    /// no release zips, checksums, or CI. Install always fetches the current source files.
    /// </summary>
    private static bool IsSourcePack(RegistryExtensionMetadata meta) =>
        string.Equals(meta.Kind, "scraper-pack", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(meta.SourceManifestUrl)
        && (meta.Versions == null || meta.Versions.Count == 0);

    private static bool HasTypeTag(RegistryExtensionSummary summary, string tag) =>
        summary.Categories?.Any(category => string.Equals(category.Trim(), tag, StringComparison.OrdinalIgnoreCase)) ?? false;

    private bool IsSourcePackCompatible(RegistryExtensionMetadata meta)
    {
        if (string.IsNullOrWhiteSpace(meta.SourceMinCoveVersion) || string.IsNullOrWhiteSpace(_coveVersion))
            return true;

        return CoveVersionCompatibility.IsAtLeast(_coveVersion, meta.SourceMinCoveVersion);
    }

    private static RegistryVersionInfo BuildSourcePackVersionInfo(RegistryExtensionMetadata meta) => new()
    {
        Version = string.IsNullOrWhiteSpace(meta.Version) ? "1.0.0" : meta.Version!,
        ReleasedAt = null,
        Changelog = meta.Changelog,
        MinCoveVersion = meta.SourceMinCoveVersion,
        Checksum = null,
        Dependencies = meta.Dependencies != null ? new Dictionary<string, string>(meta.Dependencies, StringComparer.OrdinalIgnoreCase) : [],
    };

    /// <summary>
    /// The dependencies that apply to a specific version: its own per-version dependencies when declared,
    /// otherwise the extension-level dependencies (older registry entries that predate per-version deps).
    /// </summary>
    private static Dictionary<string, string> EffectiveVersionDependencies(RegistryVersionEntry version, RegistryExtensionMetadata meta)
    {
        var source = version.Dependencies ?? meta.Dependencies;
        return source != null
            ? new Dictionary<string, string>(source, StringComparer.OrdinalIgnoreCase)
            : [];
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
                request.Categories.All(requestedCategory =>
                    e.Categories?.Any(category => string.Equals(category.Trim(), requestedCategory.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false));
        }

        if (!string.IsNullOrWhiteSpace(request.Type))
        {
            var type = request.Type.Trim().ToLowerInvariant();
            items = type switch
            {
                "scraper" => items.Where(e => HasTypeTag(e, "scraper")),
                "downloader" => items.Where(e => HasTypeTag(e, "downloader")),
                "extension" => items.Where(e => !HasTypeTag(e, "scraper") && !HasTypeTag(e, "downloader")),
                _ => items,
            };
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
            .Where(v => IsInstallableVersion(v) && IsCompatibleWithCove(v))
            .ToList();
        if (validVersions.Count == 0)
        {
            if (IsSourcePack(meta) && IsSourcePackCompatible(meta))
                return await BuildSourcePackDetailAsync(meta, extensionId, ct);
            return null;
        }

        var latestVersion = validVersions
            .OrderByDescending(v => ParseSemverOrFallback(v.Version))
            .ThenByDescending(v => v.ReleasedAt ?? DateTime.MinValue)
            .First();

        // Try to load README from an explicit external URL. The registry keeps
        // extension metadata as one JSON file per extension; docs live with the
        // extension source repo unless a registry entry points elsewhere.
        string? readme = null;
        if (!string.IsNullOrWhiteSpace(meta.ReadmeUrl))
        {
            try
            {
                var resp = await _http.GetAsync(meta.ReadmeUrl, ct);
                if (resp.IsSuccessStatusCode)
                    readme = await resp.Content.ReadAsStringAsync(ct);
            }
            catch { /* ignore */ }
        }

        return new RegistryExtensionDetail
        {
            Id = meta.Id ?? extensionId,
            Name = meta.Name ?? extensionId,
            Version = latestVersion.Version ?? "0.0.0",
            Description = meta.Description,
            Author = meta.Author,
            IconUrl = meta.IconUrl,
            Kind = meta.Kind ?? "extension",
            Url = meta.HomepageUrl ?? meta.Url ?? meta.RepositoryUrl,
            Categories = meta.Categories ?? [],
            UpdatedAt = validVersions.Max(v => v.ReleasedAt),
            MinCoveVersion = latestVersion.MinCoveVersion,
            // Extension-level Dependencies reflects the latest version's effective deps (for display);
            // dependency resolution uses each version's own deps via Versions[].Dependencies below.
            Dependencies = EffectiveVersionDependencies(latestVersion, meta),
            ExternalDependencies = meta.ExternalDependencies ?? [],
            Settings = meta.Settings ?? [],
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
                Dependencies = EffectiveVersionDependencies(v, meta),
            }).ToList(),
        };
    }

    public async Task<string> DownloadAsync(string extensionId, string version, string targetDir, CancellationToken ct = default)
    {
        var meta = await GetResolvedMetadataAsync(extensionId, ct);
        if (meta == null)
            throw new InvalidOperationException($"Extension '{extensionId}' not found in registry.");

        // Source-tracked scraper packs have no release zip; fetch the YAML directly.
        if (IsSourcePack(meta))
            return await DownloadSourcePackAsync(meta, extensionId, targetDir, ct);

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
        await DeleteDirectoryIfExistsAsync(extensionDir, ct);
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
            await TryDeleteFileWithRetriesAsync(zipPath, ct);
            throw new InvalidOperationException(
                $"Checksum validation failed for {extensionId} v{version}. Expected {expectedChecksum}, got {actualChecksum}.");
        }

        // Extract the zip
        try
        {
            using (var stream = System.IO.File.OpenRead(zipPath))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
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
                    File.SetLastWriteTimeUtc(destPath, DateTime.UtcNow);
                }
            }
        }
        finally
        {
            await TryDeleteFileWithRetriesAsync(zipPath, ct);
        }

        return extensionDir;
    }

    private async Task<RegistryExtensionDetail> BuildSourcePackDetailAsync(
        RegistryExtensionMetadata meta, string extensionId, CancellationToken ct)
    {
        string? readme = null;
        if (!string.IsNullOrWhiteSpace(meta.ReadmeUrl))
        {
            try
            {
                var resp = await _http.GetAsync(meta.ReadmeUrl, ct);
                if (resp.IsSuccessStatusCode)
                    readme = await resp.Content.ReadAsStringAsync(ct);
            }
            catch { /* ignore */ }
        }

        var versionInfo = BuildSourcePackVersionInfo(meta);

        return new RegistryExtensionDetail
        {
            Id = meta.Id ?? extensionId,
            Name = meta.Name ?? extensionId,
            Version = versionInfo.Version,
            Description = meta.Description,
            Author = meta.Author,
            IconUrl = meta.IconUrl,
            Kind = meta.Kind ?? "scraper-pack",
            Url = meta.HomepageUrl ?? meta.Url ?? meta.RepositoryUrl,
            Categories = meta.Categories ?? [],
            UpdatedAt = null,
            MinCoveVersion = meta.SourceMinCoveVersion,
            Dependencies = meta.Dependencies ?? [],
            ExternalDependencies = meta.ExternalDependencies ?? [],
            Settings = meta.Settings ?? [],
            Readme = readme,
            Changelog = meta.Changelog,
            Screenshots = meta.Screenshots ?? [],
            Versions = [versionInfo],
        };
    }

    private async Task<string> DownloadSourcePackAsync(
        RegistryExtensionMetadata meta, string extensionId, string targetDir, CancellationToken ct)
    {
        var manifestUrl = meta.SourceManifestUrl!;
        var lastSlash = manifestUrl.LastIndexOf('/');
        if (lastSlash < 0)
            throw new InvalidOperationException($"Source manifest URL for '{extensionId}' is not a valid file URL.");
        var baseUrl = manifestUrl[..(lastSlash + 1)];

        var scraperFiles = meta.ScraperFiles ?? [];
        if (scraperFiles.Count == 0)
            throw new InvalidOperationException(
                $"Source scraper pack '{extensionId}' does not list any scraperFiles in its manifest.");

        var extensionDir = Path.Combine(targetDir, extensionId);
        await DeleteDirectoryIfExistsAsync(extensionDir, ct);
        Directory.CreateDirectory(extensionDir);
        var extensionRoot = Path.GetFullPath(extensionDir);

        // Persist the manifest verbatim so Cove discovers the pack like any other extension.
        var manifestJson = await GetStringAsync(manifestUrl, ct);
        await System.IO.File.WriteAllTextAsync(Path.Combine(extensionDir, "extension.json"), manifestJson, ct);

        foreach (var relative in scraperFiles)
        {
            if (string.IsNullOrWhiteSpace(relative))
                continue;

            var normalized = relative.Replace('\\', '/').TrimStart('/');
            var destPath = Path.GetFullPath(Path.Combine(extensionDir, normalized));

            // Security: prevent path traversal outside the extension directory.
            if (!destPath.StartsWith(extensionRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            var fileUrl = baseUrl + string.Join('/', normalized.Split('/').Select(Uri.EscapeDataString));
            var content = await GetStringAsync(fileUrl, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await System.IO.File.WriteAllTextAsync(destPath, content, ct);
        }

        return extensionDir;
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        var response = await _http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
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
            .Where(category => !string.IsNullOrWhiteSpace(category))
            .Select(category => category.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c)
            .ToList();
    }

    private async Task<List<RegistryExtensionSummary>> ResolveSummariesAsync(CancellationToken ct)
    {
        if (_cachedSummaries != null && DateTime.UtcNow < _summariesExpiry)
            return _cachedSummaries;

        await _summariesLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring the lock so a concurrent caller that
            // just populated the cache wins instead of re-resolving everything.
            if (_cachedSummaries != null && DateTime.UtcNow < _summariesExpiry)
                return _cachedSummaries;

            var index = await GetIndexAsync(ct);

            using var throttler = new SemaphoreSlim(MaxConcurrentMetadataFetches);
            var tasks = index.Extensions
                .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
                .Select(async entry =>
                {
                    await throttler.WaitAsync(ct);
                    try
                    {
                        // Summaries only need registry-level metadata. The registry
                        // CI already syncs name/description/kind/categories into each
                        // extension JSON, so we skip the extra per-pack source manifest
                        // fetch here (it is still used for the detail/install views).
                        var meta = await GetMetadataAsync(entry.Id!, ct);
                        return meta == null ? null : BuildSummary(meta, entry.Id!);
                    }
                    finally
                    {
                        throttler.Release();
                    }
                })
                .ToList();

            var resolved = await Task.WhenAll(tasks);
            var summaries = resolved.Where(s => s != null).Select(s => s!).ToList();

            _cachedSummaries = summaries;
            _summariesExpiry = DateTime.UtcNow + CacheDuration;
            return summaries;
        }
        finally
        {
            _summariesLock.Release();
        }
    }

    private RegistryExtensionSummary? BuildSummary(RegistryExtensionMetadata meta, string fallbackId)
    {
        var validVersions = (meta.Versions ?? [])
            .Where(v => IsInstallableVersion(v) && IsCompatibleWithCove(v))
            .ToList();

        if (validVersions.Count == 0)
        {
            if (IsSourcePack(meta) && IsSourcePackCompatible(meta))
            {
                var sourceVersion = BuildSourcePackVersionInfo(meta);
                return new RegistryExtensionSummary
                {
                    Id = meta.Id ?? fallbackId,
                    Name = meta.Name ?? fallbackId,
                    Version = sourceVersion.Version,
                    Description = meta.Description,
                    Author = meta.Author,
                    IconUrl = meta.IconUrl,
                    Kind = meta.Kind ?? "scraper-pack",
                    Categories = meta.Categories ?? [],
                    UpdatedAt = null,
                    MinCoveVersion = meta.SourceMinCoveVersion,
                };
            }
            return null;
        }

        var latest = validVersions
            .OrderByDescending(v => ParseSemverOrFallback(v.Version))
            .ThenByDescending(v => v.ReleasedAt ?? DateTime.MinValue)
            .First();

        return new RegistryExtensionSummary
        {
            Id = meta.Id ?? fallbackId,
            Name = meta.Name ?? fallbackId,
            Version = latest.Version ?? "0.0.0",
            Description = meta.Description,
            Author = meta.Author,
            IconUrl = meta.IconUrl,
            Kind = meta.Kind ?? "extension",
            Categories = meta.Categories ?? [],
            UpdatedAt = latest.ReleasedAt,
            MinCoveVersion = latest.MinCoveVersion,
        };
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

    private bool IsCompatibleWithCove(RegistryVersionEntry version)
    {
        if (string.IsNullOrWhiteSpace(version.MinCoveVersion) || string.IsNullOrWhiteSpace(_coveVersion))
            return true;

        return CoveVersionCompatibility.IsAtLeast(_coveVersion, version.MinCoveVersion);
    }

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

    private static Task DeleteDirectoryIfExistsAsync(string directoryPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Directory.Exists(directoryPath))
            return Task.CompletedTask;

        RemoveReadOnlyAttributes(directoryPath);
        Directory.Delete(directoryPath, recursive: true);
        return Task.CompletedTask;
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

    private static async Task TryDeleteFileWithRetriesAsync(string filePath, CancellationToken ct)
    {
        if (!System.IO.File.Exists(filePath))
            return;

        IOException? ioError = null;
        UnauthorizedAccessException? authError = null;

        for (var attempt = 1; attempt <= 8; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                System.IO.File.Delete(filePath);
                return;
            }
            catch (IOException ex)
            {
                ioError = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                authError = ex;
            }

            await Task.Delay(50 * attempt, ct);
        }

        if (ioError != null) throw ioError;
        if (authError != null) throw authError;
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
        public string? Kind { get; set; }
        public string? HomepageUrl { get; set; }
        public string? Url { get; set; }
        public string? RepositoryUrl { get; set; }
        public string? ReadmeUrl { get; set; }
        public List<string>? Categories { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public List<ExtensionExternalDependency>? ExternalDependencies { get; set; }
        public List<ExtensionSettingManifest>? Settings { get; set; }
        public string? Changelog { get; set; }
        public List<string>? Screenshots { get; set; }
        public List<RegistryVersionEntry>? Versions { get; set; }
        public List<string>? ScraperFiles { get; set; }
        public string? SourceMinCoveVersion { get; set; }
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
        public string? Kind { get; set; }
        public string? MinCoveVersion { get; set; }
        public List<string>? Categories { get; set; }
        public Dictionary<string, string>? Dependencies { get; set; }
        public List<ExtensionExternalDependency>? ExternalDependencies { get; set; }
        public List<ExtensionSettingManifest>? Settings { get; set; }
        public List<string>? ScraperFiles { get; set; }
    }

    private class RegistryVersionEntry
    {
        public string? Version { get; set; }
        public DateTime? ReleasedAt { get; set; }
        public string? Changelog { get; set; }
        public string? MinCoveVersion { get; set; }
        public string? Checksum { get; set; }
        public string? DownloadUrl { get; set; }
        /// <summary>Per-version extension dependencies. Falls back to the extension-level dependencies
        /// (older registry entries) when a version doesn't declare its own.</summary>
        public Dictionary<string, string>? Dependencies { get; set; }
    }
}
