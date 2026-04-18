using HtmlAgilityPack;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;

namespace Cove.Api.Services;

public class ScraperService
{
    private static readonly string[] SupportedExtensions = [".yml", ".yaml"];

    private readonly CoveConfiguration _config;
    private readonly ILogger<ScraperService> _logger;
    private readonly IDeserializer _deserializer;
    private readonly HttpClient _httpClient;
    private readonly Lock _sync = new();
    private IReadOnlyList<ScraperSummaryDto> _cached = [];
    private readonly Dictionary<string, ScraperManifest> _manifestCache = new(StringComparer.OrdinalIgnoreCase);

    public ScraperService(CoveConfiguration config, ILogger<ScraperService> logger, IHttpClientFactory httpClientFactory)
    {
        _config = config;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("scraper");
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

    private IReadOnlyList<ScraperSummaryDto> LoadScrapers()
    {
        var summaries = new List<ScraperSummaryDto>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _manifestCache.Clear();

        foreach (var directory in _config.Scraping.ScraperDirectories.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            if (!Directory.Exists(directory))
                continue;

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(file => SupportedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to enumerate scraper directory {Directory}", directory);
                continue;
            }

            foreach (var file in files)
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

        return summaries
            .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(summary => summary.EntityType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IReadOnlyList<ScraperSummaryDto> ParseScraperFile(string file)
    {
        using var stream = File.OpenRead(file);
        using var reader = new StreamReader(stream);
        var definition = _deserializer.Deserialize<ScraperManifest>(reader);

        var scraperId = Path.GetFileNameWithoutExtension(file);
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
            "scene",
            file,
            byName: definition.SceneByName,
            byFragments: [definition.SceneByFragment, definition.SceneByQueryFragment],
            byUrls: definition.SceneByUrl
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

        if (byName != null)
            supportedScrapes.Add("Name");

        if (byFragments?.Any(definition => definition != null) == true)
            supportedScrapes.Add("Fragment");

        if (byUrls?.Any() == true)
        {
            supportedScrapes.Add("URL");
            foreach (var url in byUrls.SelectMany(definition => definition.Url ?? []))
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

        [YamlMember(Alias = "sceneByName")]
        public ByNameDefinition? SceneByName { get; init; }

        [YamlMember(Alias = "sceneByFragment")]
        public ByFragmentDefinition? SceneByFragment { get; init; }

        [YamlMember(Alias = "sceneByQueryFragment")]
        public ByFragmentDefinition? SceneByQueryFragment { get; init; }

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

        [YamlMember(Alias = "movieByURL")]
        public List<ByUrlDefinition> MovieByUrl { get; init; } = [];
    }

    private sealed class ByNameDefinition : ActionDefinitionBase
    {
    }

    private sealed class ByFragmentDefinition : ActionDefinitionBase
    {
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

        // Parse the base scraper id (format: "scraperId:entityType")
        var baseId = scraperId.Contains(':') ? scraperId.Split(':')[0] : scraperId;

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
        {
            _logger.LogWarning("Scraper {Id} not found", baseId);
            return null;
        }

        // Find matching URL definition
        var urlDefs = entityType switch
        {
            "scene" => manifest.SceneByUrl,
            "performer" => manifest.PerformerByUrl,
            "gallery" => manifest.GalleryByUrl,
            "image" => manifest.ImageByUrl,
            "group" or "movie" => [.. manifest.GroupByUrl, .. manifest.MovieByUrl],
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

        return action switch
        {
            "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct),
            "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct),
            "script" => await ScrapeScriptAsync(manifest, matchingDef.Script, new { url }, ct),
            _ => null
        };
    }

    /// <summary>
    /// Scrape by name (search) using the specified scraper and entity type.
    /// </summary>
    public async Task<List<Dictionary<string, object>>?> ScrapeNameAsync(string scraperId, string entityType, string name, CancellationToken ct = default)
    {
        GetScrapers();
        var baseId = scraperId.Contains(':') ? scraperId.Split(':')[0] : scraperId;

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
            return null;

        var nameDef = entityType switch
        {
            "scene" => manifest.SceneByName,
            "performer" => manifest.PerformerByName,
            _ => null
        };

        if (nameDef == null || string.IsNullOrEmpty(nameDef.QueryUrl))
            return null;

        var targetUrl = nameDef.QueryUrl.Replace("{}", Uri.EscapeDataString(name));
        var action = nameDef.Action ?? "scrapeXPath";
        var scraperName = nameDef.Scraper;

        var result = action switch
        {
            "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct),
            "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct),
            "script" => await ScrapeScriptAsync(manifest, nameDef.Script, new { name }, ct),
            _ => null
        };

        return result != null ? [result] : null;
    }

    /// <summary>
    /// Scrape by fragment (entity data) using the specified scraper and entity type.
    /// </summary>
    public async Task<Dictionary<string, object>?> ScrapeFragmentAsync(string scraperId, string entityType, Dictionary<string, object> fragment, CancellationToken ct = default)
    {
        GetScrapers();
        var baseId = scraperId.Contains(':') ? scraperId.Split(':')[0] : scraperId;

        if (!_manifestCache.TryGetValue(baseId, out var manifest))
            return null;

        var fragDef = entityType switch
        {
            "scene" => (ActionDefinitionBase?)manifest.SceneByFragment ?? manifest.SceneByQueryFragment,
            "performer" => manifest.PerformerByFragment,
            "gallery" => manifest.GalleryByFragment,
            "image" => manifest.ImageByFragment,
            _ => null
        };

        if (fragDef == null)
            return null;

        var targetUrl = fragDef.QueryUrl;
        if (targetUrl != null)
        {
            // Substitute fragment values
            foreach (var kv in fragment)
            {
                var placeholder = $"{{{kv.Key}}}";
                targetUrl = targetUrl.Replace(placeholder, Uri.EscapeDataString(kv.Value?.ToString() ?? ""));
            }
        }

        var action = fragDef.Action ?? "scrapeXPath";
        var scraperName = fragDef.Scraper;

        if (action == "script")
            return await ScrapeScriptAsync(manifest, fragDef.Script, fragment, ct);

        if (string.IsNullOrEmpty(targetUrl))
            return null;

        return action switch
        {
            "scrapeXPath" => await ScrapeXPathAsync(manifest, scraperName, entityType, targetUrl, ct),
            "scrapeJson" => await ScrapeJsonAsync(manifest, scraperName, entityType, targetUrl, ct),
            _ => null
        };
    }

    private async Task<Dictionary<string, object>?> ScrapeXPathAsync(ScraperManifest manifest, string? scraperName, string entityType, string url, CancellationToken ct)
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
            var html = await _httpClient.GetStringAsync(url, ct);

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, selectorObj) in entitySelectors)
            {
                var selector = ResolveSelector(selectorObj, common);
                if (string.IsNullOrEmpty(selector)) continue;

                try
                {
                    var nodes = doc.DocumentNode.SelectNodes(selector);
                    if (nodes == null || nodes.Count == 0) continue;

                    if (IsRelationshipField(field))
                    {
                        // Sub-entity selectors for Tags, Performers, Studio, etc.
                        var subSelectors = ResolveSubSelectors(selectorObj, common);
                        if (subSelectors != null)
                        {
                            var items = new List<Dictionary<string, string>>();
                            foreach (var node in nodes)
                            {
                                var item = new Dictionary<string, string>();
                                foreach (var (subField, subSelector) in subSelectors)
                                {
                                    var subNodes = node.SelectNodes(subSelector);
                                    if (subNodes?.Count > 0)
                                        item[subField] = subNodes[0].InnerText.Trim();
                                }
                                if (item.Count > 0) items.Add(item);
                            }
                            result[field] = items;
                        }
                    }
                    else
                    {
                        var value = string.Join(", ", nodes.Select(n => HtmlEntity.DeEntitize(n.InnerText.Trim())));
                        if (!string.IsNullOrWhiteSpace(value))
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
            _logger.LogWarning(ex, "Failed to scrape URL {Url}", url);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> ScrapeJsonAsync(ScraperManifest manifest, string? scraperName, string entityType, string url, CancellationToken ct)
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
            var jsonStr = await _httpClient.GetStringAsync(url, ct);
            var jsonDoc = JsonDocument.Parse(jsonStr);

            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var (field, selectorObj) in entitySelectors)
            {
                var selector = ResolveSelector(selectorObj, common);
                if (string.IsNullOrEmpty(selector)) continue;

                try
                {
                    var value = GetJsonValue(jsonDoc.RootElement, selector);
                    if (value != null)
                        result[field] = value;
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
            _logger.LogWarning(ex, "Failed to scrape JSON URL {Url}", url);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> ScrapeScriptAsync(ScraperManifest manifest, List<string>? scriptCmd, object input, CancellationToken ct)
    {
        if (scriptCmd == null || scriptCmd.Count == 0) return null;

        var workDir = Path.GetDirectoryName(manifest.FilePath) ?? ".";
        var cmd = scriptCmd[0];
        var args = string.Join(" ", scriptCmd.Skip(1));

        // Resolve python path
        if (cmd is "python" or "python3")
        {
            // Try to find python on PATH
            var pythonPath = "python";
            if (!string.IsNullOrEmpty(pythonPath)) cmd = pythonPath;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = cmd,
                Arguments = args,
                WorkingDirectory = workDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return null;

            var inputJson = JsonSerializer.Serialize(input);
            await process.StandardInput.WriteAsync(inputJson);
            process.StandardInput.Close();

            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var errors = await process.StandardError.ReadToEndAsync(ct);

            await process.WaitForExitAsync(ct);

            if (!string.IsNullOrWhiteSpace(errors))
                _logger.LogDebug("[Scrape] stderr: {Errors}", errors);

            if (string.IsNullOrWhiteSpace(output)) return null;

            return JsonSerializer.Deserialize<Dictionary<string, object>>(output, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Script scraper failed: {Cmd} {Args}", cmd, args);
            return null;
        }
    }

    // Helper methods

    private static Dictionary<string, object>? GetEntitySelectors(MappedScraperDef scraperDef, string entityType)
    {
        return entityType switch
        {
            "scene" => scraperDef.Scene,
            "performer" => scraperDef.Performer,
            "gallery" => scraperDef.Gallery,
            "image" => scraperDef.Image,
            "group" or "movie" => scraperDef.Group,
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

    private static bool IsRelationshipField(string field) =>
        field is "Tags" or "Performers" or "Studio" or "Movies" or "Groups";

    private static object? GetJsonValue(JsonElement element, string path)
    {
        // Simple dot-notation JSON path (e.g., "data.name", "results.0.title")
        var parts = path.Split('.');
        var current = element;

        foreach (var part in parts)
        {
            if (int.TryParse(part, out var index))
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength()) return null;
                current = current[index];
            }
            else
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out var next)) return null;
                current = next;
            }
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => current.GetRawText()
        };
    }

    // ===== Enhanced YAML Model for Execution =====

    private sealed class MappedScraperDef
    {
        [YamlMember(Alias = "common")]
        public Dictionary<string, string>? Common { get; init; }

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
    }

    private abstract class ActionDefinitionBase
    {
        [YamlMember(Alias = "action")]
        public string? Action { get; init; }

        [YamlMember(Alias = "queryURL")]
        public string? QueryUrl { get; init; }

        [YamlMember(Alias = "scraper")]
        public string? Scraper { get; init; }

        [YamlMember(Alias = "script")]
        public List<string>? Script { get; init; }
    }
}