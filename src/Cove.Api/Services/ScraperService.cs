using HtmlAgilityPack;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using System.Text;

namespace Cove.Api.Services;

public sealed record AutoScrapeAttemptResult(string ScraperId, string ScraperName, bool ReturnedResults, string? Error);

public sealed record AutoScrapeResult(string? ScraperId, Dictionary<string, object>? Result, IReadOnlyList<AutoScrapeAttemptResult> Attempts);

public class ScraperService
{
    private static readonly string[] SupportedExtensions = [".yml", ".yaml"];
    private const string ScraperPackKind = "scraper-pack";
    private const string ScraperPackPayloadDirectoryName = "scrapers";

    private readonly CoveConfiguration _config;
    private readonly ILogger<ScraperService> _logger;
    private readonly IDeserializer _deserializer;
    private readonly HttpClient _httpClient;
    private readonly ExtensionManager _extensionManager;
    private readonly Lock _sync = new();
    private IReadOnlyList<ScraperSummaryDto> _cached = [];
    private readonly Dictionary<string, ScraperManifest> _manifestCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ExtensionScraperRegistration> _extensionScraperCache = new(StringComparer.OrdinalIgnoreCase);
    private const string BuiltinScraperSourcePath = "builtin:cove.core.scrapers";
    private static readonly Regex BracketTagRegex = new(@"\[[^\[\]\r\n]{1,80}\]", RegexOptions.Compiled);
    private static readonly JsonSerializerOptions ExtensionScrapeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public ScraperService(CoveConfiguration config, ILogger<ScraperService> logger, IHttpClientFactory httpClientFactory, ExtensionManager extensionManager)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("scraper");
        _extensionManager = extensionManager;
        _deserializer = new DeserializerBuilder()
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public IReadOnlyList<ScraperSummaryDto> GetScrapers()
    {
        lock (_sync)
        {
            if (_cached.Count == 0)
                _cached = LoadScrapers();

            return _cached;
        }
    }

    public IReadOnlyList<ScraperSummaryDto> ReloadScrapers()
    {
        lock (_sync)
        {
            _cached = LoadScrapers();
            return _cached;
        }
    }

    /// <summary>
    /// Find loaded scrapers whose URL patterns match the given URL.
    /// Built-in extension scrapers are preferred and listed first.
    /// </summary>
    public IReadOnlyList<ScraperSummaryDto> FindScrapersForUrl(string url, string? entityType = null)
    {
        if (string.IsNullOrWhiteSpace(url))
            return [];

        var normalized = url.Trim();
        var loweredUrl = normalized.ToLowerInvariant();

        return GetScrapers()
            .Where(s => string.IsNullOrWhiteSpace(entityType) ||
                        string.Equals(s.EntityType, entityType, StringComparison.OrdinalIgnoreCase))
            .Where(s => s.SupportedScrapes.Any(k => string.Equals(k, "URL", StringComparison.OrdinalIgnoreCase)))
            .Where(s => ScraperMatchesUrl(loweredUrl, s))
            .OrderBy(s => s.SourcePath.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(s => BestPatternStrength(loweredUrl, s.Urls.Concat(s.PreferenceSites ?? [])))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool ScraperMatchesUrl(string loweredUrl, ScraperSummaryDto scraper)
    {
        if (scraper.Urls.Any(pattern => UrlMatchesPattern(loweredUrl, pattern)))
            return true;

        var preferenceSites = scraper.PreferenceSites;
        return preferenceSites?.Any(site => UrlMatchesPreferenceSite(loweredUrl, site)) == true;
    }

    /// <summary>
    /// Pick the best loaded scraper for the URL/entity and run a URL scrape.
    /// Returns null if no scraper matched or all matching scrapers failed.
    /// </summary>
    public async Task<(string ScraperId, Dictionary<string, object> Result)?> ScrapeUrlAutoAsync(string url, string entityType, CancellationToken ct = default)
    {
        var result = await ScrapeUrlAutoDetailedAsync(url, entityType, ct);
        return result.Result is { Count: > 0 } && result.ScraperId is not null
            ? (result.ScraperId, result.Result)
            : null;
    }

    public async Task<AutoScrapeResult> ScrapeUrlAutoDetailedAsync(string url, string entityType, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new AutoScrapeResult(null, null, []);

        var candidates = FindScrapersForUrl(url, entityType);
        var attempts = new List<AutoScrapeAttemptResult>(candidates.Count);
        foreach (var candidate in candidates)
        {
            try
            {
                var result = await ScrapeUrlAsync(candidate.Id, entityType, url, ct);
                if (result is { Count: > 0 })
                {
                    attempts.Add(new AutoScrapeAttemptResult(candidate.Id, candidate.Name, true, null));
                    return new AutoScrapeResult(candidate.Id, result, attempts);
                }

                attempts.Add(new AutoScrapeAttemptResult(candidate.Id, candidate.Name, false, null));
            }
            catch (Exception ex)
            {
                attempts.Add(new AutoScrapeAttemptResult(candidate.Id, candidate.Name, false, ex.Message));
                _logger.LogDebug(ex, "Auto scraper {ScraperId} failed for URL {Url}", candidate.Id, url);
            }
        }

        if (attempts.Count > 0)
        {
            var summary = string.Join("; ", attempts.Select(attempt => attempt.Error is null
                ? $"{attempt.ScraperId}: no results"
                : $"{attempt.ScraperId}: {attempt.Error}"));
            _logger.LogWarning("Auto scrape matched {CandidateCount} scraper(s) for {EntityType} URL {Url}, but none returned results. Attempts: {Attempts}", attempts.Count, entityType, url, summary);
        }

        return new AutoScrapeResult(null, null, attempts);
    }

    private static bool UrlMatchesPattern(string loweredUrl, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        var loweredPattern = pattern.Trim().ToLowerInvariant();
        if (!loweredPattern.Contains('*'))
            return loweredUrl.Contains(loweredPattern, StringComparison.Ordinal);

        var fragments = loweredPattern.Split('*', StringSplitOptions.RemoveEmptyEntries);
        if (fragments.Length == 0)
            return true;

        var index = 0;
        foreach (var fragment in fragments)
        {
            var found = loweredUrl.IndexOf(fragment, index, StringComparison.Ordinal);
            if (found < 0)
                return false;
            index = found + fragment.Length;
        }
        return true;
    }

    private static int BestPatternStrength(string loweredUrl, IEnumerable<string> patterns)
    {
        var best = 0;
        foreach (var pattern in patterns)
        {
            if (UrlMatchesPattern(loweredUrl, pattern))
                best = Math.Max(best, pattern.Trim().Length);
        }
        return best;
    }

    private static bool UrlMatchesPreferenceSite(string loweredUrl, string site)
    {
        var normalizedSite = NormalizePreferenceSite(site);
        if (string.IsNullOrWhiteSpace(normalizedSite) || normalizedSite == "*")
            return false;

        var host = TryGetHost(loweredUrl);
        return host.Length > 0 && (host == normalizedSite || host.EndsWith($".{normalizedSite}", StringComparison.Ordinal));
    }

    private static string NormalizePreferenceSite(string site)
    {
        var trimmed = site.Trim().ToLowerInvariant();
        if (trimmed.Length == 0)
            return string.Empty;

        if (!trimmed.Contains("://", StringComparison.Ordinal))
            trimmed = $"https://{trimmed}";

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return RemoveLeadingWww(uri.Host.TrimStart('*', '.'));

        var normalized = site.Trim().ToLowerInvariant()
            .Replace("http://", string.Empty, StringComparison.Ordinal)
            .Replace("https://", string.Empty, StringComparison.Ordinal)
            .TrimStart('*', '.')
            .Split('/', '?', '#', '*')[0];

        return RemoveLeadingWww(normalized);
    }

    private static string TryGetHost(string loweredUrl)
    {
        if (Uri.TryCreate(loweredUrl, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return RemoveLeadingWww(uri.Host.ToLowerInvariant());

        return string.Empty;
    }

    private static string RemoveLeadingWww(string host)
        => host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;

    private IReadOnlyList<ScraperSummaryDto> LoadScrapers()
    {
        var summaries = new List<ScraperSummaryDto>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _manifestCache.Clear();
        _extensionScraperCache.Clear();

        foreach (var directory in _config.Scraping.ScraperDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Directory.Exists(directory))
                continue;

            foreach (var file in EnumerateScraperFiles(directory))
            {
                if (!seenFiles.Add(file))
                    continue;

                try
                {
                    summaries.AddRange(ParseScraperFile(file));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load scraper definition from {File}", file);
                }
            }
        }

        foreach (var (extensionId, directory) in _extensionManager.GetEnabledManifestDirectories(ScraperPackKind))
        {
            var scraperDirectory = Path.Combine(directory, ScraperPackPayloadDirectoryName);
            if (!Directory.Exists(scraperDirectory))
                continue;

            foreach (var file in EnumerateScraperFiles(scraperDirectory))
            {
                if (!seenFiles.Add(file))
                    continue;

                try
                {
                    summaries.AddRange(ParseScraperFile(file, BuildPackScraperId(extensionId, scraperDirectory, file)));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load installed scraper definition from {File}", file);
                }
            }
        }

        foreach (var provider in _extensionManager.GetScraperProviders())
        {
            IReadOnlyList<ScraperDescriptor> descriptors;
            try
            {
                descriptors = provider.GetScrapers();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load scraper descriptors from extension {ExtensionId}", provider.Id);
                continue;
            }

            foreach (var descriptor in descriptors)
            {
                _extensionScraperCache[descriptor.Id] = new ExtensionScraperRegistration(provider, descriptor);
                summaries.Add(new ScraperSummaryDto(
                    descriptor.Id,
                    descriptor.Name,
                    descriptor.Entity.ToString().ToLowerInvariant(),
                    GetSupportedScrapeNames(descriptor.Capabilities),
                    descriptor.SupportedUrls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).ToList(),
                    $"builtin:{provider.Id}",
                    NormalizePreferenceSites(descriptor.PreferenceSites)));
            }
        }

        summaries.AddRange(GetBuiltinUrlScrapers());

        return summaries
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.EntityType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<ScraperSummaryDto> GetBuiltinUrlScrapers()
    {
        var genericTextPatterns = new List<string>
        {
            "http://*",
            "https://*",
        };

        return
        [
            new ScraperSummaryDto("builtin.generic:text", "Generic Web Page", "text", ["URL"], genericTextPatterns, BuiltinScraperSourcePath),
        ];
    }

    private IEnumerable<string> EnumerateScraperFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate scraper directory {Directory}", directory);
            return [];
        }
    }

    private static string BuildPackScraperId(string extensionId, string scraperDirectory, string file)
    {
        var relativePath = Path.GetRelativePath(scraperDirectory, file);
        var relativeWithoutExtension = Path.Combine(
            Path.GetDirectoryName(relativePath) ?? string.Empty,
            Path.GetFileNameWithoutExtension(relativePath));

        var normalizedRelativePath = relativeWithoutExtension
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/')
            .Trim('/');

        return $"{extensionId}/{normalizedRelativePath}";
    }

    private IReadOnlyList<ScraperSummaryDto> ParseScraperFile(string file, string? scraperIdOverride = null)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream);
        var definition = _deserializer.Deserialize<ScraperManifest>(reader);

        var scraperId = string.IsNullOrWhiteSpace(scraperIdOverride)
            ? Path.GetFileNameWithoutExtension(file)
            : scraperIdOverride.Trim();
        var scraperName = string.IsNullOrWhiteSpace(definition.Name)
            ? scraperId
            : definition.Name.Trim();

        // Cache manifest for execution
        definition.FilePath = file;
        _manifestCache[scraperId] = definition;

        var summaries = new List<ScraperSummaryDto>();

        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "video",
            file,
            byName: definition.VideoByName ?? definition.SceneByName,
            byFragments: [definition.VideoByFragment ?? definition.SceneByFragment, definition.VideoByQueryFragment ?? definition.SceneByQueryFragment],
            byUrls: definition.VideoByUrl.Count > 0 ? definition.VideoByUrl : definition.SceneByUrl
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "performer",
            file,
            byName: definition.PerformerByName,
            byFragments: [definition.PerformerByFragment],
            byUrls: definition.PerformerByUrl
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "gallery",
            file,
            byFragments: [definition.GalleryByFragment],
            byUrls: definition.GalleryByUrl
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "image",
            file,
            byFragments: [definition.ImageByFragment],
            byUrls: definition.ImageByUrl
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "group",
            file,
            byUrls: [.. definition.GroupByUrl, .. definition.MovieByUrl]
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "audio",
            file,
            byUrls: definition.AudioByUrl
        );
        AddSummary(
            summaries,
            scraperId,
            scraperName,
            "text",
            file,
            byUrls: definition.TextByUrl
        );

        return summaries;
    }

    private static void AddSummary(
        ICollection<ScraperSummaryDto> summaries,
        string scraperId,
        string scraperName,
        string entityType,
        string file,
        ByNameDefinition? byName = null,
        IEnumerable<ByFragmentDefinition?>? byFragments = null,
        IEnumerable<ByUrlDefinition>? byUrls = null)
    {
        var supportedScrapes = new List<string>();
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (byName != null && IsSupportedAction(byName))
            supportedScrapes.Add("Name");

        if (byFragments?.Any(definition => definition != null && IsSupportedAction(definition)) == true)
            supportedScrapes.Add("Fragment");

        if (byUrls?.Any(IsSupportedAction) == true)
        {
            supportedScrapes.Add("URL");
            foreach (var url in byUrls.Where(IsSupportedAction).SelectMany(definition => definition.Url ?? []))
            {
                if (!string.IsNullOrWhiteSpace(url))
                    urls.Add(url.Trim());
            }
        }

        if (supportedScrapes.Count == 0)
            return;

        summaries.Add(new ScraperSummaryDto(
            Id: $"{scraperId}:{entityType}",
            Name: scraperName,
            EntityType: entityType,
            SupportedScrapes: supportedScrapes,
            Urls: urls.OrderBy(url => url, StringComparer.OrdinalIgnoreCase).ToList(),
            SourcePath: file
        ));
    }

    private static List<string>? NormalizePreferenceSites(IEnumerable<string>? preferenceSites)
    {
        var normalizedSites = preferenceSites?
            .Select(NormalizePreferenceSite)
            .Where(site => !string.IsNullOrWhiteSpace(site) && site != "*")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(site => site, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalizedSites is { Count: > 0 } ? normalizedSites : null;
    }

    private sealed class ScraperManifest
    {
        [YamlIgnore]
        public string FilePath { get; set; } = string.Empty;

        [YamlMember(Alias = "name")]
        public string? Name { get; init; }

        [YamlMember(Alias = "xPathScrapers")]
        public Dictionary<string, MappedScraperDef> XPathScrapers { get; init; } = new();

        [YamlMember(Alias = "jsonScrapers")]
        public Dictionary<string, MappedScraperDef> JsonScrapers { get; init; } = new();

        [YamlMember(Alias = "performerByName")]
        public ByNameDefinition? PerformerByName { get; init; }

        [YamlMember(Alias = "performerByFragment")]
        public ByFragmentDefinition? PerformerByFragment { get; init; }

        [YamlMember(Alias = "performerByURL")]
        public List<ByUrlDefinition> PerformerByUrl { get; init; } = [];

        [YamlMember(Alias = "videoByName")]
        public ByNameDefinition? VideoByName { get; init; }

        [YamlMember(Alias = "sceneByName")]
        public ByNameDefinition? SceneByName { get; init; }

        [YamlMember(Alias = "videoByFragment")]
        public ByFragmentDefinition? VideoByFragment { get; init; }

        [YamlMember(Alias = "sceneByFragment")]
        public ByFragmentDefinition? SceneByFragment { get; init; }

        [YamlMember(Alias = "videoByQueryFragment")]
        public ByFragmentDefinition? VideoByQueryFragment { get; init; }

        [YamlMember(Alias = "sceneByQueryFragment")]
        public ByFragmentDefinition? SceneByQueryFragment { get; init; }

        [YamlMember(Alias = "videoByURL")]
        public List<ByUrlDefinition> VideoByUrl { get; init; } = [];

        [YamlMember(Alias = "sceneByURL")]
        public List<ByUrlDefinition> SceneByUrl { get; init; } = [];

        [YamlMember(Alias = "galleryByFragment")]
        public ByFragmentDefinition? GalleryByFragment { get; init; }

        [YamlMember(Alias = "galleryByURL")]
        public List<ByUrlDefinition> GalleryByUrl { get; init; } = [];

        [YamlMember(Alias = "imageByFragment")]
        public ByFragmentDefinition? ImageByFragment { get; init; }

        [YamlMember(Alias = "imageByURL")]
        public List<ByUrlDefinition> ImageByUrl { get; init; } = [];

        [YamlMember(Alias = "groupByURL")]
        public List<ByUrlDefinition> GroupByUrl { get; init; } = [];

        [YamlMember(Alias = "audioByURL")]
        public List<ByUrlDefinition> AudioByUrl { get; init; } = [];

        [YamlMember(Alias = "textByURL")]
        public List<ByUrlDefinition> TextByUrl { get; init; } = [];

        [YamlMember(Alias = "movieByURL")]
        public List<ByUrlDefinition> MovieByUrl { get; init; } = [];

        [YamlMember(Alias = "driver")]
        public DriverDefinition? Driver { get; init; }
    }

    private sealed class DriverDefinition
    {
        [YamlMember(Alias = "headers")]
        public List<DriverHeaderDefinition> Headers { get; init; } = [];

        [YamlMember(Alias = "cookies")]
        public List<DriverCookieScopeDefinition> Cookies { get; init; } = [];
    }

    private sealed class DriverHeaderDefinition
    {
        [YamlMember(Alias = "Key")]
        public string? Key { get; init; }

        [YamlMember(Alias = "Value")]
        public string? Value { get; init; }
    }

    private sealed class DriverCookieScopeDefinition
    {
        [YamlMember(Alias = "CookieURL")]
        public string? CookieUrl { get; init; }

        [YamlMember(Alias = "Cookies")]
        public List<DriverCookieDefinition> Cookies { get; init; } = [];
    }

    private sealed class DriverCookieDefinition
    {
        [YamlMember(Alias = "Name")]
        public string? Name { get; init; }

        [YamlMember(Alias = "Value")]
        public string? Value { get; init; }
    }

    private sealed class ByNameDefinition : ActionDefinitionBase
    {
    }

    private sealed class ByFragmentDefinition : ActionDefinitionBase
    {
    }

    private sealed class RegexReplaceDefinition
    {
        [YamlMember(Alias = "regex")]
        public string? Regex { get; init; }

        [YamlMember(Alias = "with")]
        public string? With { get; init; }
    }

    private sealed class ByUrlDefinition
    {
        [YamlMember(Alias = "url")]
        public List<string> Url { get; init; } = [];

        [YamlMember(Alias = "queryURL")]
        public string? QueryUrl { get; init; }

        [YamlMember(Alias = "action")]
        public string? Action { get; init; }

        [YamlMember(Alias = "scraper")]
        public string? Scraper { get; init; }

        [YamlMember(Alias = "script")]
        public List<string>? Script { get; init; }
    }

    // ===== Execution Engine =====

    /// <summary>
    /// Scrape a URL using the specified scraper and entity type.
    /// </summary>
    public async Task<Dictionary<string, object>?> ScrapeUrlAsync(string scraperId, string entityType, string url, CancellationToken ct = default)
    {
        // Ensure scrapers are loaded
        GetScrapers();

        var builtinResult = await ScrapeUrlWithBuiltinAsync(scraperId, entityType, url, ct);
        if (builtinResult != null)
            return builtinResult;

        if (TryGetExtensionScraperRegistration(scraperId, entityType, out var extensionRegistration))
            return await ScrapeUrlWithExtensionAsync(extensionRegistration, url, ct);

        var baseId = GetBaseScraperId(scraperId);

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
        {
            _logger.LogWarning("Scraper {Id} not found", baseId);
            return null;
        }

        // Find matching URL definition
        var urlDefs = entityType switch
        {
            "video" => manifest.VideoByUrl.Count > 0 ? manifest.VideoByUrl : manifest.SceneByUrl,
            "performer" => manifest.PerformerByUrl,
            "gallery" => manifest.GalleryByUrl,
            "image" => manifest.ImageByUrl,
            "group" or "movie" => [.. manifest.GroupByUrl, .. manifest.MovieByUrl],
            "audio" => manifest.AudioByUrl,
            "text" => manifest.TextByUrl,
            _ => []
        };

        var matchingDef = urlDefs.FirstOrDefault(d => d.Url.Any(u => url.Contains(u, StringComparison.OrdinalIgnoreCase)));
        if (matchingDef == null)
        {
            _logger.LogWarning("No URL match for {Url} in scraper {Id}", url, baseId);
            return null;
        }

        var targetUrl = matchingDef.QueryUrl?.Replace("{url}", Uri.EscapeDataString(url)) ?? url;
        var action = matchingDef.Action ?? "scrapeXPath";
        var scraperName = matchingDef.Scraper;

        if (IsScriptAction(action))
        {
            LogScriptScraperUnsupported(baseId, entityType, action);
            return null;
        }

        return action switch
        {
            "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct),
            "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct),
            _ => null
        };
    }

    private async Task<Dictionary<string, object>?> ScrapeUrlWithBuiltinAsync(string scraperId, string entityType, string url, CancellationToken ct)
    {
        if (!scraperId.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            var normalizedEntityType = entityType.Trim().ToLowerInvariant();
            if (scraperId.Equals("builtin.generic:text", StringComparison.OrdinalIgnoreCase)
                && normalizedEntityType == "text")
            {
                return await ScrapeGenericTextPageAsync(url, ct);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Built-in scraper '{scraperId}' failed for URL '{url}': {ex.Message}", ex);
        }

        return null;
    }

    private async Task<Dictionary<string, object>?> ScrapeGenericTextPageAsync(string url, CancellationToken ct)
    {
        var html = await _httpClient.GetStringAsync(url, ct);
        var document = new HtmlDocument();
        document.LoadHtml(html);

        var rawTitle = ReadMetaContent(document, "og:title", "twitter:title", "dc.title")
            ?? ReadText(document, "//head/title")
            ?? ReadText(document, "//h1")
            ?? DeriveTitleFromScrapeUrl(url);
        var rawDetails = ReadMetaContent(document, "og:description", "description", "twitter:description");
        var author = ReadMetaContent(document, "author", "article:author", "dc.creator", "twitter:creator")
            ?? ReadText(document, "//*[@rel='author']");
        var title = CleanTextPageTitle(CleanBracketTaggedText(rawTitle));
        var details = CleanBracketTaggedText(rawDetails);
        var tags = MergeBracketTags(rawTitle, rawDetails);
        var urls = NormalizeScrapedUrls([
            ReadMetaContent(document, "og:url", "twitter:url"),
            ReadLinkHref(document, "canonical"),
            url,
        ]);

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        AddResultValue(result, "title", title);
        AddResultValue(result, "details", details);
        if (!string.IsNullOrWhiteSpace(author))
            AddResultValue(result, "performers", new[] { author });
        AddResultValue(result, "tags", tags);
        AddResultValue(result, "urls", urls);
        return result.Count == 0 ? null : result;
    }

    private static List<string> ExtractBracketTags(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        return BracketTagRegex.Matches(text)
            .Select(match => match.Value.Trim())
            .Where(value => value.Length > 2)
            .Select(value => value[1..^1].Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<string> MergeBracketTags(params string?[] values)
    {
        return values
            .SelectMany(ExtractBracketTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? CleanBracketTaggedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = BracketTagRegex.Replace(value, string.Empty).Trim();
        cleaned = Regex.Replace(cleaned, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? value.Trim() : cleaned;
    }

    private static string? CleanTextPageTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var cleaned = value.Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }

    private static List<string> NormalizeScrapedUrls(IEnumerable<string?> urls)
    {
        return urls
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!.Trim())
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddResultValue(Dictionary<string, object> result, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            result[key] = value.Trim();
    }

    private static void AddResultValue(Dictionary<string, object> result, string key, IReadOnlyList<string> values)
    {
        if (values.Count > 0)
            result[key] = values.ToList();
    }

    private static string? ReadMetaContent(HtmlDocument document, params string[] names)
    {
        foreach (var name in names)
        {
            var node = document.DocumentNode.SelectSingleNode($"//meta[translate(@property, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{name.ToLowerInvariant()}']")
                ?? document.DocumentNode.SelectSingleNode($"//meta[translate(@name, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')='{name.ToLowerInvariant()}']");
            var value = node?.GetAttributeValue("content", string.Empty);
            if (!string.IsNullOrWhiteSpace(value))
                return WebUtility.HtmlDecode(value).Trim();
        }

        return null;
    }

    private static string? ReadLinkHref(HtmlDocument document, string rel)
    {
        var node = document.DocumentNode.SelectSingleNode($"//link[contains(concat(' ', translate(@rel, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), ' '), ' {rel.ToLowerInvariant()} ')]");
        var value = node?.GetAttributeValue("href", string.Empty);
        return string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value).Trim();
    }

    private static string? ReadText(HtmlDocument document, string xpath)
    {
        var value = document.DocumentNode.SelectSingleNode(xpath)?.InnerText;
        return string.IsNullOrWhiteSpace(value) ? null : WebUtility.HtmlDecode(value).Trim();
    }

    private static string DeriveTitleFromScrapeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        var lastSegment = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(lastSegment)
            ? uri.Host
            : Uri.UnescapeDataString(lastSegment).Replace('-', ' ').Replace('_', ' ').Trim();
    }

    /// <summary>
    /// Scrape by name (search) using the specified scraper and entity type.
    /// </summary>
    public async Task<List<Dictionary<string, object>>?> ScrapeNameAsync(string scraperId, string entityType, string name, CancellationToken ct = default)
    {
        GetScrapers();

        if (TryGetExtensionScraperRegistration(scraperId, entityType, out var extensionRegistration))
            return await ScrapeNameWithExtensionAsync(extensionRegistration, name, ct);

        var baseId = GetBaseScraperId(scraperId);

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
            return null;

        var nameDef = entityType switch
        {
            "video" => manifest.VideoByName ?? manifest.SceneByName,
            "performer" => manifest.PerformerByName,
            _ => null
        };

        if (nameDef == null || string.IsNullOrEmpty(nameDef.QueryUrl))
            return null;
        var action = nameDef.Action ?? "scrapeXPath";
        var scraperName = nameDef.Scraper;

        if (IsScriptAction(action))
        {
            LogScriptScraperUnsupported(baseId, entityType, action);
            return null;
        }

        foreach (var searchTerm in BuildNameSearchTerms(name))
        {
            var targetUrl = BuildNameTargetUrl(nameDef.QueryUrl, searchTerm);
            var result = action switch
            {
                "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct, preserveCollections: true, isNameSearch: true),
                "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct, preserveCollections: true, isNameSearch: true),
                _ => null
            };

            var candidates = ExpandNameSearchResults(result);
            if (candidates is { Count: > 0 })
                return await EnrichNameSearchCandidatesAsync(scraperId, entityType, candidates, targetUrl, ct);
        }

        return null;
    }

    /// <summary>
    /// Scrape by fragment (entity data) using the specified scraper and entity type.
    /// </summary>
    public async Task<Dictionary<string, object>?> ScrapeFragmentAsync(string scraperId, string entityType, Dictionary<string, object> fragment, CancellationToken ct = default)
    {
        GetScrapers();

        if (TryGetExtensionScraperRegistration(scraperId, entityType, out var extensionRegistration))
            return await ScrapeFragmentWithExtensionAsync(extensionRegistration, fragment, ct);

        var baseId = GetBaseScraperId(scraperId);

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
            return null;

        var fragDefs = entityType switch
        {
            "video" => GetVideoFragmentDefinitions(manifest, fragment),
            "performer" => manifest.PerformerByFragment is null ? [] : [manifest.PerformerByFragment],
            "gallery" => manifest.GalleryByFragment is null ? [] : [manifest.GalleryByFragment],
            "image" => manifest.ImageByFragment is null ? [] : [manifest.ImageByFragment],
            _ => []
        };

        if (fragDefs.Count == 0)
            return null;

        foreach (var fragDef in fragDefs)
        {
            var targetUrl = BuildFragmentTargetUrl(fragDef, fragment);
            var action = fragDef.Action ?? "scrapeXPath";
            var scraperName = fragDef.Scraper;

            if (IsScriptAction(action))
            {
                LogScriptScraperUnsupported(baseId, entityType, action);
                return null;
            }

            if (string.IsNullOrEmpty(targetUrl))
                continue;

            var result = action switch
            {
                "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct),
                "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct),
                _ => null
            };

            if (result is { Count: > 0 })
                return result;
        }

        return null;
    }

    private static List<ActionDefinitionBase> GetVideoFragmentDefinitions(ScraperManifest manifest, IReadOnlyDictionary<string, object> fragment)
    {
        var definitions = new List<ActionDefinitionBase>();
        var hasUrl = !string.IsNullOrWhiteSpace(GetFragmentString(fragment, "url"))
            || GetFragmentStringList(fragment, "urls").Count > 0;

        if (hasUrl)
        {
            if (manifest.VideoByQueryFragment != null)
                definitions.Add(manifest.VideoByQueryFragment);
            else if (manifest.SceneByQueryFragment != null)
                definitions.Add(manifest.SceneByQueryFragment);
        }

        if (manifest.VideoByFragment != null)
            definitions.Add(manifest.VideoByFragment);
        else if (manifest.SceneByFragment != null)
            definitions.Add(manifest.SceneByFragment);

        if (!hasUrl)
        {
            if (manifest.VideoByQueryFragment != null)
                definitions.Add(manifest.VideoByQueryFragment);
            else if (manifest.SceneByQueryFragment != null)
                definitions.Add(manifest.SceneByQueryFragment);
        }

        return definitions;
    }

    private static string? BuildFragmentTargetUrl(ActionDefinitionBase definition, IReadOnlyDictionary<string, object> fragment)
    {
        var targetUrl = definition.QueryUrl;
        if (string.IsNullOrWhiteSpace(targetUrl))
            return null;

        foreach (var kv in fragment)
        {
            var placeholder = $"{{{kv.Key}}}";
            var rawValue = ConvertFragmentString(kv.Value) ?? string.Empty;
            var resolvedValue = ApplyQueryUrlReplacements(rawValue, kv.Key, definition.QueryUrlReplace);
            targetUrl = targetUrl.Replace(placeholder, ResolveQueryUrlPlaceholderValue(targetUrl, placeholder, resolvedValue));
        }

        return targetUrl;
    }

    private static string BuildNameTargetUrl(string queryUrl, string name)
    {
        var encodedName = Uri.EscapeDataString(name);
        return queryUrl
            .Replace("{}", encodedName, StringComparison.Ordinal)
            .Replace("{name}", encodedName, StringComparison.Ordinal)
            .Replace("{query}", encodedName, StringComparison.Ordinal);
    }

    private static List<string> BuildNameSearchTerms(string name)
    {
        var terms = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
                terms.Add(trimmed);
        }

        Add(name);
        Add(SanitizeNameSearchTerm(name));

        if (!string.IsNullOrWhiteSpace(name) && name.Contains(':', StringComparison.Ordinal))
            Add(name[(name.LastIndexOf(':') + 1)..]);

        return terms;
    }

    private static string SanitizeNameSearchTerm(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSpace = false;
                continue;
            }

            if (!char.IsWhiteSpace(character) && character is not ':' and not '-' and not '_' and not '/' and not '\\')
                continue;

            if (lastWasSpace)
                continue;

            builder.Append(' ');
            lastWasSpace = true;
        }

        return builder.ToString().Trim();
    }

    private async Task<List<Dictionary<string, object>>> EnrichNameSearchCandidatesAsync(
        string scraperId,
        string entityType,
        List<Dictionary<string, object>> candidates,
        string searchUrl,
        CancellationToken ct)
    {
        if (candidates.Count == 0)
            return candidates;

        using var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async (candidate, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                return (Index: index, Candidate: await EnrichNameSearchCandidateAsync(scraperId, entityType, candidate, searchUrl, ct));
            }
            finally
            {
                gate.Release();
            }
        });

        var enriched = await Task.WhenAll(tasks);
        return enriched.OrderBy(item => item.Index).Select(item => item.Candidate).ToList();
    }

    private async Task<Dictionary<string, object>> EnrichNameSearchCandidateAsync(
        string scraperId,
        string entityType,
        Dictionary<string, object> candidate,
        string searchUrl,
        CancellationToken ct)
    {
        var merged = new Dictionary<string, object>(candidate, StringComparer.OrdinalIgnoreCase);
        var candidateUrl = ExtractCandidateUrl(candidate);
        if (string.IsNullOrWhiteSpace(candidateUrl))
            return merged;

        var absoluteUrl = ResolveCandidateUrl(searchUrl, candidateUrl);
        if (!string.IsNullOrWhiteSpace(absoluteUrl))
            merged["URL"] = absoluteUrl;

        try
        {
            var scraped = await ScrapeUrlAsync(scraperId, entityType, absoluteUrl ?? candidateUrl, ct);
            if (scraped == null || scraped.Count == 0)
                return merged;

            foreach (var (field, value) in scraped)
                merged[field] = value;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Candidate enrichment failed for {EntityType} URL {Url}", entityType, absoluteUrl ?? candidateUrl);
        }

        return merged;
    }

    private static string? ExtractCandidateUrl(IReadOnlyDictionary<string, object> candidate)
    {
        foreach (var field in new[] { "URL", "Url" })
        {
            if (candidate.TryGetValue(field, out var value) && value is string text && !string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        if (candidate.TryGetValue("URLs", out var urlsValue) && urlsValue is List<string> urls && urls.Count > 0)
            return urls[0];

        return null;
    }

    private static string? ResolveCandidateUrl(string searchUrl, string candidateUrl)
    {
        if (Uri.TryCreate(candidateUrl, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        if (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var baseUri))
            return candidateUrl;

        return Uri.TryCreate(baseUri, candidateUrl, out var resolved)
            ? resolved.ToString()
            : candidateUrl;
    }

    private static string ResolveQueryUrlPlaceholderValue(string targetUrlTemplate, string placeholder, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Equals(targetUrlTemplate.Trim(), placeholder, StringComparison.Ordinal)
            ? value
            : Uri.EscapeDataString(value);
    }

    private static string NormalizeRequestUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
            return url;

        var decodedUrl = Uri.UnescapeDataString(url);
        return Uri.TryCreate(decodedUrl, UriKind.Absolute, out _) ? decodedUrl : url;
    }

    private static string GetBaseScraperId(string scraperId)
    {
        var separatorIndex = scraperId.LastIndexOf(':');
        return separatorIndex > 0 ? scraperId[..separatorIndex] : scraperId;
    }

    private static string ApplyQueryUrlReplacements(string value, string fieldName, IReadOnlyDictionary<string, List<RegexReplaceDefinition>>? replacements)
    {
        if (string.IsNullOrWhiteSpace(value)
            || replacements == null
            || !replacements.TryGetValue(fieldName, out var fieldReplacements)
            || fieldReplacements.Count == 0)
        {
            return value;
        }

        var current = value;
        foreach (var replacement in fieldReplacements)
        {
            if (string.IsNullOrWhiteSpace(replacement.Regex))
                continue;

            current = Regex.Replace(
                current,
                replacement.Regex,
                replacement.With ?? string.Empty,
                RegexOptions.Singleline);
        }

        return current;
    }

    private async Task<Dictionary<string, object>?> ScrapeXPathAsync(
        ScraperManifest manifest,
        string? scraperName,
        string entityType,
        string url,
        CancellationToken ct,
        bool preserveCollections = false,
        bool isNameSearch = false)
    {
        if (string.IsNullOrEmpty(scraperName) || !manifest.XPathScrapers.TryGetValue(scraperName, out var scraperDef))
        {
            _logger.LogWarning("XPath scraper definition '{Name}' not found", scraperName);
            return null;
        }

        var entitySelectors = GetEntitySelectors(scraperDef, entityType);
        if (entitySelectors == null || entitySelectors.Count == 0) return null;

        // Apply common substitutions
        var common = scraperDef.Common ?? new Dictionary<string, string>();

        try
        {
            _logger.LogDebug("Fetching URL for XPath scrape: {Url}", url);
            var html = await FetchContentAsync(manifest, url, ct, isNameSearch);
            if (html == null)
                return null; // No match (e.g. 404 during a title search).

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, selectorObj) in entitySelectors)
            {
                try
                {
                    if (IsRelationshipField(field))
                    {
                        var items = ExtractXPathRelationshipItems(doc.DocumentNode, selectorObj, common);
                        if (items.Count > 0)
                            result[field] = items;
                    }
                    else
                    {
                        var values = ExtractXPathValues(doc.DocumentNode, selectorObj, common, treatPlainStringsAsFixed: false);
                        var value = ConvertSelectorValues(values, preserveCollections);

                        if (value is string textValue && !string.IsNullOrWhiteSpace(textValue))
                            result[field] = textValue;
                        else if (value is List<string> listValue && listValue.Count > 0)
                            result[field] = listValue;
                        else if (value is not null)
                            result[field] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("XPath selector error for field {Field}: {Error}", field, ex.Message);
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"XPath scrape failed for URL '{url}': {ex.Message}", ex);
        }
    }

    private async Task<Dictionary<string, object>?> ScrapeJsonAsync(
        ScraperManifest manifest,
        string? scraperName,
        string entityType,
        string url,
        CancellationToken ct,
        bool preserveCollections = false,
        bool isNameSearch = false)
    {
        if (string.IsNullOrEmpty(scraperName) || !manifest.JsonScrapers.TryGetValue(scraperName, out var scraperDef))
        {
            _logger.LogWarning("JSON scraper definition '{Name}' not found", scraperName);
            return null;
        }

        var entitySelectors = GetEntitySelectors(scraperDef, entityType);
        if (entitySelectors == null || entitySelectors.Count == 0) return null;

        var common = scraperDef.Common ?? new Dictionary<string, string>();

        try
        {
            _logger.LogDebug("Fetching URL for JSON scrape: {Url}", url);
            var jsonStr = await FetchContentAsync(manifest, url, ct, isNameSearch);
            if (jsonStr == null)
                return null; // No match (e.g. 404 during a title search).
            var jsonDoc = JsonDocument.Parse(jsonStr);

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, selectorObj) in entitySelectors)
            {
                try
                {
                    if (IsRelationshipField(field))
                    {
                        var items = ExtractJsonRelationshipItems(jsonDoc.RootElement, selectorObj, common);
                        if (items.Count > 0)
                            result[field] = items;
                    }
                    else
                    {
                        var values = ExtractJsonValues(jsonDoc.RootElement, selectorObj, common, treatPlainStringsAsFixed: false);
                        var value = ConvertSelectorValues(values, preserveCollections);

                        if (value is string textValue && !string.IsNullOrWhiteSpace(textValue))
                            result[field] = textValue;
                        else if (value is List<string> listValue && listValue.Count > 0)
                            result[field] = listValue;
                        else if (value is not null)
                            result[field] = value;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug("JSON selector error for field {Field}: {Error}", field, ex.Message);
                }
            }

            return result.Count > 0 ? result : null;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"JSON scrape failed for URL '{url}': {ex.Message}", ex);
        }
    }

    private async Task<Dictionary<string, object>?> ScrapeScriptAsync(ScraperManifest manifest, List<string>? scriptCmd, object input, CancellationToken ct)
    {
        var scriptTarget = scriptCmd == null || scriptCmd.Count == 0 ? "<missing>" : string.Join(' ', scriptCmd);
        _logger.LogWarning("Blocked unsupported script scraper execution for {ScriptTarget} from {SourcePath}", scriptTarget, manifest.FilePath);
        await Task.CompletedTask;
        return null;
    }

    private bool TryGetExtensionScraperRegistration(string scraperId, string entityType, [NotNullWhen(true)] out ExtensionScraperRegistration? registration)
    {
        if (_extensionScraperCache.TryGetValue(scraperId, out registration)
            && string.Equals(registration.Descriptor.Entity.ToString(), entityType, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        registration = null;
        return false;
    }

    private async Task<Dictionary<string, object>?> ScrapeUrlWithExtensionAsync(ExtensionScraperRegistration registration, string url, CancellationToken ct)
    {
        if (!registration.Descriptor.Capabilities.HasFlag(ScraperCapabilities.ByUrl))
            return null;

        if (!await _extensionManager.EnsureExtensionInitializedAsync(registration.Provider.Id, ct))
            return null;

        List<string> urls = string.IsNullOrWhiteSpace(url) ? [] : [url];
        var permissions = BuildScraperPermissions(url);

        return registration.Descriptor.Entity switch
        {
            ScraperEntity.Video => ToResultDictionary(await registration.Provider.ScrapeVideoAsync(new ScraperRequest<VideoScrapeInput>(registration.Descriptor.Id, new VideoScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Performer => ToResultDictionary(await registration.Provider.ScrapePerformerAsync(new ScraperRequest<PerformerScrapeInput>(registration.Descriptor.Id, new PerformerScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Gallery => ToResultDictionary(await registration.Provider.ScrapeGalleryAsync(new ScraperRequest<GalleryScrapeInput>(registration.Descriptor.Id, new GalleryScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Image => ToResultDictionary(await registration.Provider.ScrapeImageAsync(new ScraperRequest<ImageScrapeInput>(registration.Descriptor.Id, new ImageScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Group => ToResultDictionary(await registration.Provider.ScrapeGroupAsync(new ScraperRequest<GroupScrapeInput>(registration.Descriptor.Id, new GroupScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Audio => ToResultDictionary(await registration.Provider.ScrapeAudioAsync(new ScraperRequest<AudioScrapeInput>(registration.Descriptor.Id, new AudioScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            ScraperEntity.Text => ToResultDictionary(await registration.Provider.ScrapeTextAsync(new ScraperRequest<TextScrapeInput>(registration.Descriptor.Id, new TextScrapeInput { Url = url, Urls = urls }, permissions), ct)),
            _ => null,
        };
    }

    private async Task<List<Dictionary<string, object>>?> ScrapeNameWithExtensionAsync(ExtensionScraperRegistration registration, string name, CancellationToken ct)
    {
        if (!registration.Descriptor.Capabilities.HasFlag(ScraperCapabilities.ByName))
            return null;

        if (!await _extensionManager.EnsureExtensionInitializedAsync(registration.Provider.Id, ct))
            return null;

        var request = new ScraperRequest<string>(registration.Descriptor.Id, name, new ScraperPermissions());
        return registration.Descriptor.Entity switch
        {
            ScraperEntity.Video => ToResultDictionaries(await registration.Provider.SearchVideosAsync(request, ct)),
            ScraperEntity.Performer => ToResultDictionaries(await registration.Provider.SearchPerformersAsync(request, ct)),
            ScraperEntity.Gallery => ToResultDictionaries(await registration.Provider.SearchGalleriesAsync(request, ct)),
            ScraperEntity.Image => ToResultDictionaries(await registration.Provider.SearchImagesAsync(request, ct)),
            ScraperEntity.Group => ToResultDictionaries(await registration.Provider.SearchGroupsAsync(request, ct)),
            ScraperEntity.Audio => ToResultDictionaries(await registration.Provider.SearchAudiosAsync(request, ct)),
            ScraperEntity.Text => ToResultDictionaries(await registration.Provider.SearchTextsAsync(request, ct)),
            _ => null,
        };
    }

    private async Task<Dictionary<string, object>?> ScrapeFragmentWithExtensionAsync(ExtensionScraperRegistration registration, Dictionary<string, object> fragment, CancellationToken ct)
    {
        if (!registration.Descriptor.Capabilities.HasFlag(ScraperCapabilities.ByFragment))
            return null;

        if (!await _extensionManager.EnsureExtensionInitializedAsync(registration.Provider.Id, ct))
            return null;

        switch (registration.Descriptor.Entity)
        {
            case ScraperEntity.Video:
            {
                var input = BuildVideoInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeVideoAsync(new ScraperRequest<VideoScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Performer:
            {
                var input = BuildPerformerInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapePerformerAsync(new ScraperRequest<PerformerScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Gallery:
            {
                var input = BuildGalleryInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeGalleryAsync(new ScraperRequest<GalleryScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Image:
            {
                var input = BuildImageInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeImageAsync(new ScraperRequest<ImageScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Group:
            {
                var input = BuildGroupInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeGroupAsync(new ScraperRequest<GroupScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Audio:
            {
                var input = BuildAudioInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeAudioAsync(new ScraperRequest<AudioScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            case ScraperEntity.Text:
            {
                var input = BuildTextInput(fragment);
                return ToResultDictionary(await registration.Provider.ScrapeTextAsync(new ScraperRequest<TextScrapeInput>(registration.Descriptor.Id, input, BuildScraperPermissions(input.Url)), ct));
            }
            default:
                return null;
        }
    }

    private static VideoScrapeInput BuildVideoInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new VideoScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Title = GetFragmentString(fragment, "title", "name"),
            Code = GetFragmentString(fragment, "code", "id", "viewkey"),
            Date = GetFragmentString(fragment, "date"),
            Details = GetFragmentString(fragment, "details", "description"),
            Director = GetFragmentString(fragment, "director"),
        };
    }

    private static PerformerScrapeInput BuildPerformerInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new PerformerScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Name = GetFragmentString(fragment, "name", "title"),
            Disambiguation = GetFragmentString(fragment, "disambiguation"),
            Gender = GetFragmentString(fragment, "gender"),
            Birthdate = GetFragmentString(fragment, "birthdate", "date"),
            Country = GetFragmentString(fragment, "country"),
            Ethnicity = GetFragmentString(fragment, "ethnicity"),
            EyeColor = GetFragmentString(fragment, "eyeColor", "eye_color"),
            HairColor = GetFragmentString(fragment, "hairColor", "hair_color"),
            Measurements = GetFragmentString(fragment, "measurements"),
            Details = GetFragmentString(fragment, "details", "description"),
            Aliases = GetFragmentStringList(fragment, "aliases", "alias"),
        };
    }

    private static GalleryScrapeInput BuildGalleryInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new GalleryScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Title = GetFragmentString(fragment, "title", "name"),
            Code = GetFragmentString(fragment, "code", "id"),
            Date = GetFragmentString(fragment, "date"),
            Details = GetFragmentString(fragment, "details", "description"),
            Photographer = GetFragmentString(fragment, "photographer", "artist"),
        };
    }

    private static ImageScrapeInput BuildImageInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new ImageScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Title = GetFragmentString(fragment, "title", "name"),
            Date = GetFragmentString(fragment, "date"),
            Details = GetFragmentString(fragment, "details", "description"),
            Photographer = GetFragmentString(fragment, "photographer", "artist"),
        };
    }

    private static GroupScrapeInput BuildGroupInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new GroupScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Name = GetFragmentString(fragment, "name", "title"),
            Aliases = GetFragmentString(fragment, "aliases", "alias"),
            Duration = GetFragmentInt(fragment, "duration", "durationSeconds"),
            Date = GetFragmentString(fragment, "date"),
            Director = GetFragmentString(fragment, "director"),
            Details = GetFragmentString(fragment, "details", "description"),
            Synopsis = GetFragmentString(fragment, "synopsis", "description"),
        };
    }

    private static AudioScrapeInput BuildAudioInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new AudioScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Title = GetFragmentString(fragment, "title", "name"),
            Code = GetFragmentString(fragment, "code", "id"),
            Date = GetFragmentString(fragment, "date"),
            Details = GetFragmentString(fragment, "details", "description"),
        };
    }

    private static TextScrapeInput BuildTextInput(IReadOnlyDictionary<string, object> fragment)
    {
        var (primaryUrl, urls) = BuildFragmentUrls(fragment);

        return new TextScrapeInput
        {
            Url = primaryUrl,
            Urls = urls,
            Title = GetFragmentString(fragment, "title", "name"),
            Code = GetFragmentString(fragment, "code", "id"),
            Date = GetFragmentString(fragment, "date"),
            Details = GetFragmentString(fragment, "details", "description"),
        };
    }

    private static (string? PrimaryUrl, List<string> Urls) BuildFragmentUrls(IReadOnlyDictionary<string, object> fragment)
    {
        var urls = GetFragmentStringList(fragment, "urls", "url");
        var primaryUrl = GetFragmentString(fragment, "url") ?? urls.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(primaryUrl) && !urls.Any(url => string.Equals(url, primaryUrl, StringComparison.OrdinalIgnoreCase)))
            urls.Insert(0, primaryUrl);

        return (primaryUrl, urls);
    }

    private static string? GetFragmentString(IReadOnlyDictionary<string, object> fragment, params string[] names)
    {
        foreach (var name in names)
        {
            var value = fragment.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            var converted = ConvertFragmentString(value);
            if (!string.IsNullOrWhiteSpace(converted))
                return converted;
        }

        return null;
    }

    private static int? GetFragmentInt(IReadOnlyDictionary<string, object> fragment, params string[] names)
    {
        foreach (var name in names)
        {
            var value = fragment.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
                    return intValue;
                if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsedValue))
                    return parsedValue;
            }
            else if (value is int intValue)
            {
                return intValue;
            }
            else if (value != null && int.TryParse(value.ToString(), out var parsedValue))
            {
                return parsedValue;
            }
        }

        return null;
    }

    private static List<string> GetFragmentStringList(IReadOnlyDictionary<string, object> fragment, params string[] names)
    {
        foreach (var name in names)
        {
            var entry = fragment.FirstOrDefault(item => string.Equals(item.Key, name, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(entry.Key))
                continue;

            var values = ConvertFragmentStringList(entry.Value);
            if (values.Count > 0)
                return values;
        }

        return [];
    }

    private static string? ConvertFragmentString(object? value)
    {
        return value switch
        {
            null => null,
            string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
            JsonElement element => element.ValueKind switch
            {
                JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString()!.Trim(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => element.ToString(),
                _ => null,
            },
            _ => value.ToString(),
        };
    }

    private static List<string> ConvertFragmentStringList(object? value)
    {
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element => element
                .EnumerateArray()
                .Select(item => ConvertFragmentString(item))
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => ConvertFragmentString(value) is { } singleValue
                ? [singleValue]
                : [],
        };
    }

    private static object? ConvertSelectorValues(List<string> values, bool preserveCollections)
    {
        if (values.Count == 0)
            return null;

        if (!preserveCollections)
            return values.Count == 1 ? values[0] : string.Join(", ", values);

        return values.Count == 1 ? values[0] : values;
    }

    private static List<Dictionary<string, object>>? ExpandNameSearchResults(Dictionary<string, object>? result)
    {
        if (result == null || result.Count == 0)
            return null;

        var candidateCount = result.Values.Select(GetCandidateValueCount).DefaultIfEmpty(0).Max();
        if (candidateCount <= 1)
            return [new Dictionary<string, object>(result, StringComparer.OrdinalIgnoreCase)];

        var candidates = new List<Dictionary<string, object>>();
        for (var index = 0; index < candidateCount; index++)
        {
            var candidate = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, value) in result)
            {
                var extractedValue = ExtractCandidateValue(value, index, candidateCount);
                if (extractedValue != null)
                    candidate[field] = extractedValue;
            }

            if (candidate.Count > 0 && HasMeaningfulCandidateValue(candidate))
                candidates.Add(candidate);
        }

        return candidates.Count > 0 ? candidates : [new Dictionary<string, object>(result, StringComparer.OrdinalIgnoreCase)];
    }

    private static int GetCandidateValueCount(object value)
    {
        return value switch
        {
            List<string> values => values.Count,
            List<Dictionary<string, string>> values => values.Count,
            _ => 1,
        };
    }

    private static object? ExtractCandidateValue(object value, int index, int candidateCount)
    {
        return value switch
        {
            List<string> values => ExtractCandidateString(values, index, candidateCount),
            List<Dictionary<string, string>> values => ExtractCandidateRelationship(values, index, candidateCount),
            string text when !string.IsNullOrWhiteSpace(text) => text,
            _ => value,
        };
    }

    private static object? ExtractCandidateString(List<string> values, int index, int candidateCount)
    {
        if (values.Count == 0)
            return null;

        if (values.Count == 1 || candidateCount == 1)
            return values[0];

        return index < values.Count && !string.IsNullOrWhiteSpace(values[index]) ? values[index] : null;
    }

    private static object? ExtractCandidateRelationship(List<Dictionary<string, string>> values, int index, int candidateCount)
    {
        if (values.Count == 0)
            return null;

        if (values.Count == 1 || candidateCount == 1)
            return new List<Dictionary<string, string>> { values[0] };

        return index < values.Count ? new List<Dictionary<string, string>> { values[index] } : null;
    }

    private static bool HasMeaningfulCandidateValue(Dictionary<string, object> candidate)
    {
        foreach (var (key, value) in candidate)
        {
            if (value is string text && !string.IsNullOrWhiteSpace(text))
                return true;

            if (value is List<Dictionary<string, string>> relationshipItems && relationshipItems.Count > 0)
                return true;

            if (string.Equals(key, "Title", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Name", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "URL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(key, "Url", StringComparison.OrdinalIgnoreCase))
            {
                return value != null;
            }
        }

        return false;
    }

    private static ScraperPermissions BuildScraperPermissions(string? url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return new ScraperPermissions([uri.Host]);

        return new ScraperPermissions();
    }

    private static Dictionary<string, object>? ToResultDictionary<T>(T? value)
    {
        if (value == null)
            return null;

        return JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(value, ExtensionScrapeJsonOptions),
            ExtensionScrapeJsonOptions);
    }

    private static List<Dictionary<string, object>> ToResultDictionaries<T>(IEnumerable<T> values)
        => values.Select(ToResultDictionary).OfType<Dictionary<string, object>>().ToList();

    private static List<string> GetSupportedScrapeNames(ScraperCapabilities capabilities)
    {
        var names = new List<string>();
        if (capabilities.HasFlag(ScraperCapabilities.ByUrl))
            names.Add("URL");
        if (capabilities.HasFlag(ScraperCapabilities.ByName))
            names.Add("Name");
        if (capabilities.HasFlag(ScraperCapabilities.ByFragment) || capabilities.HasFlag(ScraperCapabilities.ByQueryFragment))
            names.Add("Fragment");
        return names;
    }

    private sealed record ExtensionScraperRegistration(IScraperProvider Provider, ScraperDescriptor Descriptor);

    // Helper methods

    private static bool IsSupportedAction(ActionDefinitionBase definition) => !IsScriptAction(definition.Action ?? "scrapeXPath");

    private static bool IsSupportedAction(ByUrlDefinition definition) => !IsScriptAction(definition.Action ?? "scrapeXPath");

    private static bool IsScriptAction(string? action) => string.Equals(action, "script", StringComparison.OrdinalIgnoreCase);

    private void LogScriptScraperUnsupported(string scraperId, string entityType, string? action)
    {
        _logger.LogWarning("Blocked unsupported scraper action {Action} for {ScraperId}:{EntityType}", action, scraperId, entityType);
    }

    private static Dictionary<string, object>? GetEntitySelectors(MappedScraperDef scraperDef, string entityType)
    {
        return entityType switch
        {
            "video" => scraperDef.Video ?? scraperDef.Scene,
            "performer" => scraperDef.Performer,
            "gallery" => scraperDef.Gallery,
            "image" => scraperDef.Image,
            "group" or "movie" => scraperDef.Group,
            "audio" => scraperDef.Audio,
            "text" => scraperDef.Text,
            _ => null
        };
    }

    private static string? ResolveSelector(object selectorObj, Dictionary<string, string> common)
    {
        var selector = selectorObj switch
        {
            string s => s,
            Dictionary<object, object> dict when dict.TryGetValue("selector", out var s) => s?.ToString(),
            _ => null
        };

        if (selector == null) return null;

        foreach (var (key, value) in common)
            selector = selector.Replace(key, value);

        return selector;
    }

    private static Dictionary<string, string>? ResolveSubSelectors(object selectorObj, Dictionary<string, string> common)
    {
        if (selectorObj is not Dictionary<object, object> dict) return null;

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in dict)
        {
            var k = key.ToString();
            if (k is "selector" or "fixed" or "concat" or "split" or "postProcess") continue;

            var selector = value switch
            {
                string s => s,
                Dictionary<object, object> subDict when subDict.TryGetValue("selector", out var s) => s?.ToString(),
                _ => null
            };

            if (selector != null)
            {
                foreach (var (ck, cv) in common)
                    selector = selector.Replace(ck, cv);
                result[k!] = selector;
            }
        }

        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// Fetches raw scraper content for <paramref name="url"/>.
    /// </summary>
    /// <param name="isNameSearch">
    /// When true the fetch is part of a title/name search. In that mode an upstream non-success
    /// status with an empty body (notably HTTP 404, but also e.g. 500/empty that some sites
    /// return for unknown titles) means "no match on this site" rather than an error, so it is
    /// logged at Debug and surfaced as <c>null</c> instead of throwing. For direct-URL scrapes a
    /// 404 is meaningful, so the original throwing behavior is preserved. Genuine transport
    /// failures (connection reset / timeout / DNS) still throw in both modes.
    /// </param>
    private async Task<string?> FetchContentAsync(ScraperManifest manifest, string url, CancellationToken ct, bool isNameSearch = false)
    {
        var requestUrl = NormalizeRequestUrl(url);
        var cookieHeader = BuildCookieHeader(manifest, requestUrl);
        const int maxAttempts = 2;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUrl);

                foreach (var header in manifest.Driver?.Headers ?? [])
                {
                    if (!string.IsNullOrWhiteSpace(header.Key) && header.Value != null)
                        request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                if (!string.IsNullOrWhiteSpace(cookieHeader))
                    request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                var content = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        _logger.LogDebug(
                            "Scrape fetch for {Url} returned {StatusCode}; continuing with response body.",
                            requestUrl,
                            (int)response.StatusCode);
                        return content;
                    }

                    // For a title/name search an empty non-success body just means the title isn't
                    // on this site. Treat it as "no match" instead of throwing a noisy stack trace.
                    if (isNameSearch)
                    {
                        _logger.LogDebug(
                            "Scraper: HTTP {StatusCode} for title search '{Url}', treating as no match.",
                            (int)response.StatusCode,
                            requestUrl);
                        return null;
                    }

                    response.EnsureSuccessStatusCode();
                }

                return content;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientScrapeFetchException(ex, ct))
            {
                _logger.LogDebug(ex, "Retrying scraper fetch for {Url} after transient transport failure on attempt {Attempt}", requestUrl, attempt);
            }
        }

        throw new InvalidOperationException($"Failed to fetch scraper content for '{requestUrl}'.");
    }

    private static bool IsTransientScrapeFetchException(Exception exception, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }

        return exception switch
        {
            OperationCanceledException => false,
            HttpRequestException => true,
            IOException => true,
            SocketException socketException when socketException.SocketErrorCode is SocketError.ConnectionAborted or SocketError.ConnectionReset or SocketError.NetworkReset or SocketError.TimedOut => true,
            _ when exception.InnerException is not null => IsTransientScrapeFetchException(exception.InnerException, ct),
            _ => false,
        };
        }

    private static string? BuildCookieHeader(ScraperManifest manifest, string requestUrl)
    {
        var cookies = new List<string>();

        foreach (var scope in manifest.Driver?.Cookies ?? [])
        {
            if (!string.IsNullOrWhiteSpace(scope.CookieUrl) &&
                !requestUrl.StartsWith(scope.CookieUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var cookie in scope.Cookies)
            {
                if (!string.IsNullOrWhiteSpace(cookie.Name))
                    cookies.Add($"{cookie.Name}={cookie.Value ?? string.Empty}");
            }
        }

        return cookies.Count > 0 ? string.Join("; ", cookies) : null;
    }

    private static List<Dictionary<string, string>> ExtractXPathRelationshipItems(HtmlNode scope, object selectorObj, Dictionary<string, string> common)
    {
        var subSelectors = ResolveSubSelectorDefinitions(selectorObj);
        if (subSelectors.Count == 0)
            return [];

        var containerSelector = ResolveSelector(selectorObj, common);
        if (!string.IsNullOrWhiteSpace(containerSelector))
        {
            var containers = scope.SelectNodes(containerSelector);
            if (containers is { Count: > 0 })
            {
                var items = new List<Dictionary<string, string>>();
                foreach (var container in containers)
                {
                    var item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var (subField, subSelector) in subSelectors)
                    {
                        var entries = ExtractXPathValueEntries(container, subSelector, common, treatPlainStringsAsFixed: true);
                        if (entries.Count == 0)
                            continue;

                        item[subField] = entries[0].Value;
                        if (!item.ContainsKey("URL") && ShouldCaptureRelationshipUrl(subField) && !string.IsNullOrWhiteSpace(entries[0].Href))
                            item["URL"] = entries[0].Href!;
                    }

                    if (item.Count > 0)
                        items.Add(item);
                }

                if (items.Count > 0)
                    return items;
            }
        }

        var valuesByField = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (subField, subSelector) in subSelectors)
        {
            var entries = ExtractXPathValueEntries(scope, subSelector, common, treatPlainStringsAsFixed: true);
            valuesByField[subField] = entries.Select(entry => entry.Value).ToList();

            if (valuesByField.ContainsKey("URL") || !ShouldCaptureRelationshipUrl(subField))
                continue;

            var urls = entries
                .Select(entry => entry.Href)
                .Where(href => !string.IsNullOrWhiteSpace(href))
                .Select(href => href!)
                .ToList();

            if (urls.Count > 0)
                valuesByField["URL"] = urls;
        }

        return ZipRelationshipValues(valuesByField);
    }

    private static List<XPathValueEntry> ExtractXPathValueEntries(HtmlNode scope, object selectorObj, Dictionary<string, string> common, bool treatPlainStringsAsFixed)
    {
        if (TryGetFixedValue(selectorObj, treatPlainStringsAsFixed, out var fixedValue))
            return [new XPathValueEntry(fixedValue, null)];

        var selector = ResolveSelector(selectorObj, common);
        if (string.IsNullOrWhiteSpace(selector))
            return [];

        var navigator = scope.CreateNavigator();
        var iterator = navigator.Select(selector);
        var values = new List<XPathValueEntry>();

        while (iterator.MoveNext())
        {
            var current = iterator.Current;
            var rawValue = current?.Value;
            if (string.IsNullOrWhiteSpace(rawValue))
                continue;

            var value = ApplyPostProcesses(HtmlEntity.DeEntitize(rawValue.Trim()), selectorObj);
            if (string.IsNullOrWhiteSpace(value))
                continue;

            var href = current?.Name == "href"
                ? current.Value
                : current?.GetAttribute("href", string.Empty);

            values.Add(new XPathValueEntry(value, string.IsNullOrWhiteSpace(href) ? null : href.Trim()));
        }

        return values;
    }

    private static List<Dictionary<string, string>> ExtractJsonRelationshipItems(JsonElement scope, object selectorObj, Dictionary<string, string> common)
    {
        var subSelectors = ResolveSubSelectorDefinitions(selectorObj);
        if (subSelectors.Count == 0)
            return [];

        return ZipRelationshipValues(subSelectors.ToDictionary(
            selector => selector.Key,
            selector => ExtractJsonValues(scope, selector.Value, common, treatPlainStringsAsFixed: true),
            StringComparer.OrdinalIgnoreCase));
    }

    private static List<Dictionary<string, string>> ZipRelationshipValues(Dictionary<string, List<string>> valuesByField)
    {
        var count = valuesByField.Count == 0 ? 0 : valuesByField.Values.Max(values => values.Count);
        var items = new List<Dictionary<string, string>>();

        for (var index = 0; index < count; index++)
        {
            var item = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, values) in valuesByField)
            {
                if (index < values.Count && !string.IsNullOrWhiteSpace(values[index]))
                    item[field] = values[index];
            }

            if (item.Count > 0)
                items.Add(item);
        }

        return items;
    }

    private static List<string> ExtractXPathValues(HtmlNode scope, object selectorObj, Dictionary<string, string> common, bool treatPlainStringsAsFixed)
    {
        if (TryGetFixedValue(selectorObj, treatPlainStringsAsFixed, out var fixedValue))
            return [fixedValue];

        var selector = ResolveSelector(selectorObj, common);
        if (string.IsNullOrWhiteSpace(selector))
            return [];

        var navigator = scope.CreateNavigator();
        var iterator = navigator.Select(selector);
        var values = new List<string>();

        while (iterator.MoveNext())
        {
            var current = iterator.Current?.Value;
            if (!string.IsNullOrWhiteSpace(current))
                values.Add(current.Trim());
        }

        return values
            .Select(value => ApplyPostProcesses(HtmlEntity.DeEntitize(value), selectorObj))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static List<string> ExtractJsonValues(JsonElement scope, object selectorObj, Dictionary<string, string> common, bool treatPlainStringsAsFixed)
    {
        if (TryGetFixedValue(selectorObj, treatPlainStringsAsFixed, out var fixedValue))
            return [fixedValue];

        var selector = ResolveSelector(selectorObj, common);
        if (string.IsNullOrWhiteSpace(selector))
            return [];

        return GetJsonValues(scope, selector)
            .Select(value => ApplyPostProcesses(value, selectorObj))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
    }

    private static Dictionary<string, object> ResolveSubSelectorDefinitions(object selectorObj)
    {
        if (selectorObj is not Dictionary<object, object> dict)
            return [];

        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in dict)
        {
            var name = key.ToString();
            if (name is null || name is "selector" or "fixed" or "concat" or "split" or "postProcess")
                continue;

            result[name] = value;
        }

        return result;
    }

    private static bool TryGetFixedValue(object selectorObj, bool treatPlainStringsAsFixed, out string value)
    {
        switch (selectorObj)
        {
            case Dictionary<object, object> dict when dict.TryGetValue("fixed", out var fixedValue) && fixedValue != null:
                value = fixedValue.ToString()!.Trim();
                return !string.IsNullOrWhiteSpace(value);
            case string text when treatPlainStringsAsFixed && !LooksLikeSelector(text):
                value = text.Trim();
                return !string.IsNullOrWhiteSpace(value);
            default:
                value = string.Empty;
                return false;
        }
    }

    private static bool LooksLikeSelector(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return false;

        return trimmed.StartsWith("/")
            || trimmed.StartsWith(".")
            || trimmed.StartsWith("$")
            || trimmed.Contains("//", StringComparison.Ordinal)
            || trimmed.Contains('@')
            || trimmed.Contains('[', StringComparison.Ordinal)
            || trimmed.Contains('(', StringComparison.Ordinal)
            || trimmed.Contains('|', StringComparison.Ordinal)
            || trimmed.Contains("::", StringComparison.Ordinal);
    }

    private static string ApplyPostProcesses(string value, object selectorObj)
    {
        if (selectorObj is not Dictionary<object, object> dict || !dict.TryGetValue("postProcess", out var postProcess) || postProcess is not IEnumerable<object> steps)
            return value;

        var current = value;
        foreach (var step in steps)
        {
            if (step is not Dictionary<object, object> stepDict)
                continue;

            foreach (var (key, stepValue) in stepDict)
            {
                var operation = key.ToString();
                switch (operation)
                {
                    case "replace":
                        current = ApplyReplace(current, stepValue);
                        break;
                    case "parseDate":
                        current = ApplyParseDate(current, stepValue?.ToString());
                        break;
                    case "feetToCm":
                        current = ApplyFeetToCm(current);
                        break;
                    case "lbToKg":
                        current = ApplyLbToKg(current);
                        break;
                    case "map":
                        current = ApplyMap(current, stepValue);
                        break;
                }
            }
        }

        return current.Trim();
    }

    private static string ApplyReplace(string value, object? stepValue)
    {
        if (stepValue is not IEnumerable<object> replacements)
            return value;

        var current = value;
        foreach (var replacement in replacements)
        {
            if (replacement is not Dictionary<object, object> replacementDict)
                continue;

            var pattern = replacementDict.TryGetValue("regex", out var regexValue) ? regexValue?.ToString() : null;
            var replaceWith = replacementDict.TryGetValue("with", out var withValue) ? withValue?.ToString() ?? string.Empty : string.Empty;
            if (string.IsNullOrWhiteSpace(pattern))
                continue;

            current = Regex.Replace(current, pattern, replaceWith, RegexOptions.Singleline);
        }

        return current;
    }

    private static string ApplyParseDate(string value, string? format)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return value ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(format))
        {
            var fmt = format.Trim();

            // stash special-case formats: the raw value is an epoch timestamp.
            if (string.Equals(fmt, "unix", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"-?\d+");
                if (match.Success && long.TryParse(match.Value, out var seconds))
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else if (string.Equals(fmt, "unixmilli", StringComparison.OrdinalIgnoreCase)
                || string.Equals(fmt, "unixmillis", StringComparison.OrdinalIgnoreCase))
            {
                var match = Regex.Match(trimmed, @"-?\d+");
                if (match.Success && long.TryParse(match.Value, out var millis))
                    return DateTimeOffset.FromUnixTimeMilliseconds(millis).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            else
            {
                // stash uses Go reference-time layouts (e.g. "2006-01-02", "January 2, 2006").
                // Convert to an equivalent .NET custom format before exact parsing.
                var netFormat = ConvertGoLayoutToNetFormat(fmt);
                if (!string.IsNullOrEmpty(netFormat)
                    && DateTime.TryParseExact(trimmed, netFormat, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exactDate))
                    return exactDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

                // Tolerate yml that already used a .NET-style layout.
                if (DateTime.TryParseExact(trimmed, fmt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var rawDate))
                    return rawDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        return DateTime.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsedDate)
            ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : value ?? string.Empty;
    }

    // Go reference time is "Mon Jan 2 15:04:05 MST 2006". Each canonical token maps
    // to a .NET custom date/time specifier. Ordered longest-first so multi-character
    // tokens win before their single-character substrings during scanning.
    private static readonly (string Go, string Net)[] GoLayoutTokens = new[]
    {
        (".000000", ".ffffff"),
        ("January", "MMMM"),
        ("Z07:00", "K"),
        ("Monday", "dddd"),
        ("-07:00", "zzz"),
        (".000", ".fff"),
        ("Z0700", "K"),
        ("-0700", "zzz"),
        ("2006", "yyyy"),
        ("Jan", "MMM"),
        ("Mon", "ddd"),
        ("MST", ""),
        ("_2", "d"),
        ("06", "yy"),
        ("01", "MM"),
        ("02", "dd"),
        ("03", "hh"),
        ("04", "mm"),
        ("05", "ss"),
        ("15", "HH"),
        ("PM", "tt"),
        ("pm", "tt"),
        ("1", "M"),
        ("2", "d"),
        ("3", "h"),
        ("4", "m"),
        ("5", "s"),
    };

    private static readonly (string Go, string Net)[] GoLayoutTokensOrdered =
        GoLayoutTokens.OrderByDescending(t => t.Go.Length).ToArray();

    private static string ConvertGoLayoutToNetFormat(string goLayout)
    {
        var sb = new StringBuilder();
        var i = 0;
        while (i < goLayout.Length)
        {
            var matched = false;
            foreach (var (go, net) in GoLayoutTokensOrdered)
            {
                if (i + go.Length <= goLayout.Length
                    && string.CompareOrdinal(goLayout, i, go, 0, go.Length) == 0)
                {
                    sb.Append(net);
                    i += go.Length;
                    matched = true;
                    break;
                }
            }

            if (matched)
                continue;

            var c = goLayout[i];
            // Escape literal letters so .NET does not treat them as format specifiers.
            if (char.IsLetter(c))
                sb.Append('\\').Append(c);
            else
                sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    // stash feetToCm: "5'10\"" / "5 ft 10 in" -> centimetres (rounded integer).
    private static string ApplyFeetToCm(string value)
    {
        var numbers = Regex.Matches(value, @"\d+(?:\.\d+)?")
            .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture))
            .ToList();
        if (numbers.Count == 0)
            return value;

        var feet = numbers[0];
        var inches = numbers.Count > 1 ? numbers[1] : 0;
        var cm = (feet * 30.48) + (inches * 2.54);
        return Math.Round(cm, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    // stash lbToKg: "150 lbs" -> kilograms (rounded integer).
    private static string ApplyLbToKg(string value)
    {
        var match = Regex.Match(value, @"\d+(?:\.\d+)?");
        if (!match.Success)
            return value;

        var pounds = double.Parse(match.Value, CultureInfo.InvariantCulture);
        var kg = pounds * 0.45359237;
        return Math.Round(kg, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
    }

    private static string ApplyMap(string value, object? stepValue)
    {
        if (stepValue is not Dictionary<object, object> map)
            return value;

        foreach (var (mapKey, mapValue) in map)
        {
            if (string.Equals(mapKey?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                return mapValue?.ToString() ?? value;
        }

        return value;
    }

    private static bool IsRelationshipField(string field) =>
        field is "Tags" or "Performers" or "Studio" or "Movies" or "Groups";

    private static bool ShouldCaptureRelationshipUrl(string field)
        => field is "Name" or "Title";

    private sealed record XPathValueEntry(string Value, string? Href);

    private static List<string> GetJsonValues(JsonElement element, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = new List<JsonElement> { element };

        foreach (var part in parts)
        {
            var next = new List<JsonElement>();
            foreach (var candidate in current)
            {
                if (part == "#")
                {
                    if (candidate.ValueKind == JsonValueKind.Array)
                        next.AddRange(candidate.EnumerateArray());
                    continue;
                }

                if (int.TryParse(part, out var index))
                {
                    if (candidate.ValueKind == JsonValueKind.Array && index >= 0 && index < candidate.GetArrayLength())
                        next.Add(candidate[index]);
                    continue;
                }

                if (candidate.ValueKind != JsonValueKind.Object)
                    continue;

                if (TryGetJsonProperty(candidate, part, out var value))
                    next.Add(value);
            }

            current = next;
            if (current.Count == 0)
                return [];
        }

        return current
            .Select(value => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.ToString(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Object or JsonValueKind.Array => value.GetRawText(),
                _ => null,
            })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToList();
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    // ===== Enhanced YAML Model for Execution =====

    private sealed class MappedScraperDef
    {
        [YamlMember(Alias = "common")]
        public Dictionary<string, string>? Common { get; init; }

        [YamlMember(Alias = "video")]
        public Dictionary<string, object>? Video { get; init; }

        [YamlMember(Alias = "scene")]
        public Dictionary<string, object>? Scene { get; init; }

        [YamlMember(Alias = "performer")]
        public Dictionary<string, object>? Performer { get; init; }

        [YamlMember(Alias = "gallery")]
        public Dictionary<string, object>? Gallery { get; init; }

        [YamlMember(Alias = "image")]
        public Dictionary<string, object>? Image { get; init; }

        [YamlMember(Alias = "group")]
        public Dictionary<string, object>? Group { get; init; }

        [YamlMember(Alias = "audio")]
        public Dictionary<string, object>? Audio { get; init; }

        [YamlMember(Alias = "text")]
        public Dictionary<string, object>? Text { get; init; }
    }

    private abstract class ActionDefinitionBase
    {
        [YamlMember(Alias = "action")]
        public string? Action { get; init; }

        [YamlMember(Alias = "queryURL")]
        public string? QueryUrl { get; init; }

        [YamlMember(Alias = "queryURLReplace")]
        public Dictionary<string, List<RegexReplaceDefinition>>? QueryUrlReplace { get; init; }

        [YamlMember(Alias = "scraper")]
        public string? Scraper { get; init; }

        [YamlMember(Alias = "script")]
        public List<string>? Script { get; init; }
    }
}
