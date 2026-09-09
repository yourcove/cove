using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Core.Helpers;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace Cove.Api.Services;

public class PerformerScrapeService(
    CoveContext db,
    ScraperService scraperService,
    IBlobService? blobService = null,
    IHttpClientFactory? httpClientFactory = null,
    ILogger<PerformerScrapeService>? logger = null,
    IFieldProvenanceService? fieldProvenanceService = null,
    ITagProvenanceService? tagProvenanceService = null)
{
    public async Task<ScrapedPerformerDto?> ScrapeByUrlAsync(string url, CancellationToken ct = default)
    {
        return await ScrapeByUrlAsync(url, scraperId: null, ct);
    }

    public async Task<ScrapedPerformerDto?> ScrapeByUrlAsync(string url, string? scraperId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (!string.IsNullOrWhiteSpace(scraperId))
        {
            var result = await scraperService.ScrapeUrlAsync(scraperId, "performer", url, ct);
            return result == null ? null : ConvertScrapeResult(result, url, scraperId);
        }

        var hit = await scraperService.ScrapeUrlAutoAsync(url, "performer", ct);
        return hit == null ? null : ConvertScrapeResult(hit.Value.Result, url, hit.Value.ScraperId);
    }

    public async Task<ScrapedPerformerDto?> ScrapeByNameAsync(string name, string? scraperId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (!string.IsNullOrWhiteSpace(scraperId))
        {
            return await TryScrapeByNameAsync(scraperId, name, ct)
                ?? await TryScrapeGeneratedUrlsAsync(
                    scraperService.GetScrapers().Where(candidate => string.Equals(candidate.Id, scraperId, StringComparison.OrdinalIgnoreCase)).ToList(),
                    name,
                    ct);
        }

        var performerScrapers = scraperService.GetScrapers()
            .Where(candidate => string.Equals(candidate.EntityType, "performer", StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var scraper in performerScrapers.Where(candidate => candidate.SupportedScrapes.Any(kind => string.Equals(kind, "Name", StringComparison.OrdinalIgnoreCase))))
        {
            var scraped = await TryScrapeByNameAsync(scraper.Id, name, ct);
            if (scraped != null)
                return scraped;
        }

        return await TryScrapeGeneratedUrlsAsync(performerScrapers, name, ct);
    }

    public async Task ApplyAsync(
        Performer performer,
        ScrapedPerformerDto scraped,
        bool createMissingTags,
        IReadOnlyCollection<string>? replaceFields = null,
        IReadOnlyDictionary<string, string>? collectionModes = null,
        CancellationToken ct = default)
    {
        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(scraped.SourceScraperId);
        var replaceFieldSet = replaceFields?.ToHashSet(StringComparer.OrdinalIgnoreCase);

        bool ShouldApplyField(params string[] names) => replaceFieldSet == null || names.Any(replaceFieldSet.Contains);
        string GetCollectionMode(string field)
        {
            if (collectionModes == null || !collectionModes.TryGetValue(field, out var mode) || string.IsNullOrWhiteSpace(mode))
                return "merge";
            return mode.Trim().ToLowerInvariant() switch
            {
                "skip" or "ignore" => "skip",
                "replace" or "overwrite" => "replace",
                _ => "merge",
            };
        }

        if (ShouldApplyField("name") && !string.IsNullOrWhiteSpace(scraped.Name))
        {
            performer.Name = scraped.Name.Trim();
            fieldProvenance["name"] = performer.Name;
        }

        if (ShouldApplyField("disambiguation") && !string.IsNullOrWhiteSpace(scraped.Disambiguation))
        {
            performer.Disambiguation = scraped.Disambiguation.Trim();
            fieldProvenance["disambiguation"] = performer.Disambiguation;
        }

        if (ShouldApplyField("gender") && !string.IsNullOrWhiteSpace(scraped.Gender) && TryParseEnum(scraped.Gender, out GenderEnum gender))
        {
            performer.Gender = gender;
            fieldProvenance["gender"] = gender.ToString();
        }

        if (ShouldApplyField("birthdate", "birthDate") && !string.IsNullOrWhiteSpace(scraped.Birthdate) && TryParseDate(scraped.Birthdate, out var birthdate))
        {
            performer.Birthdate = birthdate.Value;
            performer.BirthdatePrecision = birthdate.Precision;
            fieldProvenance["birthdate"] = birthdate.ToString();
        }

        if (ShouldApplyField("country") && !string.IsNullOrWhiteSpace(scraped.Country))
        {
            performer.Country = scraped.Country.Trim();
            fieldProvenance["country"] = performer.Country;
        }

        if (ShouldApplyField("ethnicity") && !string.IsNullOrWhiteSpace(scraped.Ethnicity))
        {
            performer.Ethnicity = scraped.Ethnicity.Trim();
            fieldProvenance["ethnicity"] = performer.Ethnicity;
        }

        if (ShouldApplyField("eyeColor", "eye_color") && !string.IsNullOrWhiteSpace(scraped.EyeColor))
        {
            performer.EyeColor = scraped.EyeColor.Trim();
            fieldProvenance["eye_color"] = performer.EyeColor;
        }

        if (ShouldApplyField("hairColor", "hair_color") && !string.IsNullOrWhiteSpace(scraped.HairColor))
        {
            performer.HairColor = scraped.HairColor.Trim();
            fieldProvenance["hair_color"] = performer.HairColor;
        }

        if (ShouldApplyField("heightCm", "height_cm") && scraped.HeightCm.HasValue)
        {
            performer.HeightCm = scraped.HeightCm.Value;
            fieldProvenance["height_cm"] = performer.HeightCm;
        }

        if (ShouldApplyField("weight") && scraped.Weight.HasValue)
        {
            performer.Weight = scraped.Weight.Value;
            fieldProvenance["weight"] = performer.Weight;
        }

        if (ShouldApplyField("measurements") && !string.IsNullOrWhiteSpace(scraped.Measurements))
        {
            performer.Measurements = scraped.Measurements.Trim();
            fieldProvenance["measurements"] = performer.Measurements;
        }

        if (ShouldApplyField("tattoos") && !string.IsNullOrWhiteSpace(scraped.Tattoos))
        {
            performer.Tattoos = scraped.Tattoos.Trim();
            fieldProvenance["tattoos"] = performer.Tattoos;
        }

        if (ShouldApplyField("piercings") && !string.IsNullOrWhiteSpace(scraped.Piercings))
        {
            performer.Piercings = scraped.Piercings.Trim();
            fieldProvenance["piercings"] = performer.Piercings;
        }

        if (ShouldApplyField("details") && !string.IsNullOrWhiteSpace(scraped.Details))
        {
            performer.Details = scraped.Details.Trim();
            fieldProvenance["details"] = performer.Details;
        }

        var urls = NormalizeNames(scraped.Urls);
        var urlsMode = GetCollectionMode("urls");
        if (urlsMode != "skip")
        {
            if (urlsMode == "replace")
                performer.Urls.Clear();
            if (urls.Count > 0)
                fieldProvenance["urls"] = urls;
            MergeValues(performer.Urls, urls, item => item.Url, value => new PerformerUrl { Url = value, Performer = performer }, NormalizeUrlKey);
        }

        var aliases = NormalizeNames(scraped.Aliases);
        var aliasesMode = GetCollectionMode("aliases");
        if (aliasesMode != "skip")
        {
            if (aliasesMode == "replace")
                performer.Aliases.Clear();
            if (aliases.Count > 0)
                fieldProvenance["aliases"] = aliases;
            MergeValues(performer.Aliases, aliases, item => item.Alias, value => new PerformerAlias { Alias = value, Performer = performer });
        }

        if (ShouldApplyField("image", "imageUrl", "image_url"))
            await TryApplyImageAsync(performer, scraped.ImageUrl, ct);
        if (ShouldApplyField("image", "imageUrl", "image_url") && !string.IsNullOrWhiteSpace(scraped.ImageUrl))
            fieldProvenance["image_url"] = scraped.ImageUrl.Trim();

        var normalizedTagNames = NormalizeNames(scraped.TagNames);
        var tagsMode = GetCollectionMode("tags");
        if (tagsMode != "skip" && normalizedTagNames.Count > 0)
        {
            if (tagsMode == "replace")
                performer.PerformerTags.Clear();

            var lookup = await RelationNameResolver.ResolveTagsAsync(db, normalizedTagNames, ct);

            var existingTagIds = performer.PerformerTags.Select(item => item.TagId).ToHashSet();
            var existingTagNames = performer.PerformerTags
                .Select(item => item.Tag?.Name)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var appliedTagNames = new List<string>();
            foreach (var tagName in normalizedTagNames)
            {
                if (!lookup.TryGetValue(tagName, out var tag))
                {
                    if (!createMissingTags)
                        continue;

                    tag = new Tag { Name = tagName };
                    db.Tags.Add(tag);
                    lookup[tagName] = tag;
                }

                appliedTagNames.Add(tag.Name);

                if (!existingTagIds.Contains(tag.Id) && existingTagNames.Add(tag.Name))
                {
                    existingTagIds.Add(tag.Id);
                    performer.PerformerTags.Add(new PerformerTag { Performer = performer, Tag = tag, TagId = tag.Id });
                }

                if (tagProvenanceService != null)
                    await tagProvenanceService.RecordAsync(AffinityHostType.Performer, performer.Id, tag, sourceKey, cancellationToken: ct);
            }

            if (appliedTagNames.Count > 0)
                fieldProvenance["tags"] = appliedTagNames;
        }

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Performer, performer.Id, fieldProvenance, sourceKey, cancellationToken: ct);
    }

    internal static ScrapedPerformerDto? ConvertScrapeResult(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        string? GetString(params string[] keys)
        {
            foreach (var key in keys)
            {
                foreach (var (entryKey, entryValue) in result)
                {
                    if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entryValue is string text && !string.IsNullOrWhiteSpace(text))
                        return text.Trim();

                    if (entryValue is not null && entryValue is not System.Collections.IEnumerable)
                        return entryValue.ToString();
                }
            }

            return null;
        }

        List<string> GetStringList(ScrapeListValueKind valueKind, params string[] keys)
        {
            var values = new List<string>();
            foreach (var key in keys)
            {
                foreach (var (entryKey, entryValue) in result)
                {
                    if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                        continue;
                    values.AddRange(ExtractStringValues(entryValue, valueKind, splitScalarString: true));
                }
            }

            return values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var resolvedImageUrl = ResolveAbsoluteUrl(GetString("Image", "image", "ImageUrl", "imageUrl"), sourceUrl);
        var dto = new ScrapedPerformerDto
        {
            SourceScraperId = sourceScraperId,
            Name = GetString("Name", "name", "Title", "title"),
            Disambiguation = GetString("Disambiguation", "disambiguation"),
            Gender = GetString("Gender", "gender"),
            Birthdate = GetString("Birthdate", "birthdate", "Date", "date"),
            Country = GetString("Country", "country"),
            Ethnicity = GetString("Ethnicity", "ethnicity"),
            EyeColor = GetString("EyeColor", "eyeColor", "Eye Colour"),
            HairColor = GetString("HairColor", "hairColor", "Hair Colour"),
            HeightCm = TryParseInt(GetString("HeightCm", "heightCm", "Height", "height")),
            Weight = TryParseInt(GetString("Weight", "weight")),
            Measurements = GetString("Measurements", "measurements"),
            Tattoos = GetString("Tattoos", "tattoos"),
            Piercings = GetString("Piercings", "piercings"),
            Details = GetString("Details", "details", "Description", "description", "Bio", "bio"),
            ImageUrl = resolvedImageUrl,
            Urls = GetStringList(ScrapeListValueKind.Url, "URLs", "urls", "URL", "url")
                .Select(url => ResolveAbsoluteUrl(url, sourceUrl) ?? url)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Aliases = GetStringList(ScrapeListValueKind.Name, "Aliases", "aliases", "Alias", "alias"),
            TagNames = GetStringList(ScrapeListValueKind.Name, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
        };

        if (!string.IsNullOrWhiteSpace(sourceUrl) && !dto.Urls.Contains(sourceUrl, StringComparer.OrdinalIgnoreCase))
            dto.Urls.Add(sourceUrl);

        var hasContent = !string.IsNullOrWhiteSpace(dto.Name)
            || !string.IsNullOrWhiteSpace(dto.Details)
            || !string.IsNullOrWhiteSpace(dto.Country)
            || !string.IsNullOrWhiteSpace(dto.Birthdate)
            || dto.Aliases.Count > 0
            || dto.TagNames.Count > 0;

        return hasContent ? dto : null;
    }

    private static bool TryParseEnum<TEnum>(string value, out TEnum parsed) where TEnum : struct
        => Enum.TryParse(value, true, out parsed);

    private static string BuildScraperSourceKey(string? scraperId)
        => string.IsNullOrWhiteSpace(scraperId) ? "scraper" : $"scraper:{scraperId.Trim()}";

    private async Task<ScrapedPerformerDto?> TryScrapeByNameAsync(string scraperId, string name, CancellationToken ct)
    {
        var candidates = await scraperService.ScrapeNameAsync(scraperId, "performer", name, ct);
        if (candidates == null || candidates.Count == 0)
            return null;

        return candidates
            .Select(candidate => ConvertScrapeResult(candidate, ExtractCandidateUrl(candidate) ?? string.Empty, scraperId))
            .OfType<ScrapedPerformerDto>()
            .OrderByDescending(candidate => ScoreCandidate(candidate, name))
            .FirstOrDefault();
    }

    private async Task<ScrapedPerformerDto?> TryScrapeGeneratedUrlsAsync(IReadOnlyList<ScraperSummaryDto> scrapers, string name, CancellationToken ct)
    {
        foreach (var scraper in scrapers.Where(candidate => candidate.SupportedScrapes.Any(kind => string.Equals(kind, "URL", StringComparison.OrdinalIgnoreCase))))
        {
            foreach (var candidateUrl in BuildGeneratedProfileUrls(scraper, name))
            {
                var scraped = await ScrapeByUrlAsync(candidateUrl, scraper.Id, ct);
                if (scraped != null)
                    return scraped;
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildGeneratedProfileUrls(ScraperSummaryDto scraper, string name)
    {
        var slugs = BuildSlugCandidates(name);
        var urls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pattern in scraper.Urls)
        {
            foreach (var baseUrl in NormalizePatternBases(pattern))
            {
                foreach (var slug in slugs)
                {
                    var candidate = $"{baseUrl}{slug}";
                    if (urls.Add(candidate))
                        yield return candidate;
                }
            }
        }
    }

    private static IEnumerable<string> NormalizePatternBases(string pattern)
    {
        var trimmed = pattern.Trim();
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.Contains('*') || trimmed.Contains('?') || trimmed.Contains('='))
            yield break;

        var normalized = trimmed.TrimEnd('/');
        var variants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            variants.Add($"{normalized}/");
        }
        else
        {
            variants.Add($"https://{normalized}/");
            if (!normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
                variants.Add($"https://www.{normalized}/");
        }

        foreach (var variant in variants)
            yield return variant;
    }

    private static List<string> BuildSlugCandidates(string name)
    {
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = name.Trim().ToLowerInvariant();
        var builder = new StringBuilder(normalized.Length);
        var lastWasSeparator = false;

        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                lastWasSeparator = false;
                continue;
            }

            if (character == '&')
            {
                if (!lastWasSeparator && builder.Length > 0)
                    builder.Append('-');
                builder.Append("and");
                lastWasSeparator = false;
                continue;
            }

            if (lastWasSeparator || builder.Length == 0)
                continue;

            builder.Append('-');
            lastWasSeparator = true;
        }

        var dashed = builder.ToString().Trim('-');
        if (!string.IsNullOrWhiteSpace(dashed))
            slugs.Add(dashed);

        var compact = dashed.Replace("-", string.Empty, StringComparison.Ordinal);
        if (!string.IsNullOrWhiteSpace(compact))
            slugs.Add(compact);

        return slugs.ToList();
    }

    private static List<string> NormalizeNames(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int ScoreCandidate(ScrapedPerformerDto candidate, string searchTerm)
    {
        var normalizedSearchTerm = NormalizeSearchText(searchTerm);
        var bestScore = 0;

        foreach (var value in new[] { candidate.Name, candidate.Disambiguation, candidate.Urls.FirstOrDefault() })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (string.Equals(value, searchTerm, StringComparison.OrdinalIgnoreCase))
                bestScore = Math.Max(bestScore, 1000);
            else if (string.Equals(NormalizeSearchText(value), normalizedSearchTerm, StringComparison.Ordinal))
                bestScore = Math.Max(bestScore, 900);
            else if (NormalizeSearchText(value).Contains(normalizedSearchTerm, StringComparison.Ordinal))
                bestScore = Math.Max(bestScore, 400);
        }

        return bestScore;
    }

    private static string NormalizeSearchText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        var lastWasSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
                lastWasSpace = false;
                continue;
            }

            if (lastWasSpace)
                continue;

            builder.Append(' ');
            lastWasSpace = true;
        }

        return builder.ToString().Trim();
    }

    internal static string? ExtractCandidateUrl(IReadOnlyDictionary<string, object> candidate)
    {
        foreach (var (field, value) in candidate)
        {
            if (!string.Equals(field, "url", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(field, "urls", StringComparison.OrdinalIgnoreCase))
                continue;

            var extracted = ExtractStringValues(value, ScrapeListValueKind.Url, splitScalarString: false).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(extracted))
                return extracted;
        }

        return null;
    }

    private static IEnumerable<string> ExtractStringValues(
        object? value,
        ScrapeListValueKind valueKind,
        bool splitScalarString)
    {
        switch (value)
        {
            case null:
                yield break;
            case string text:
                if (splitScalarString)
                {
                    foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        yield return item;
                }
                else if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return text.Trim();
                }
                yield break;
            case System.Text.Json.JsonElement element:
                switch (element.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.String:
                        foreach (var item in ExtractStringValues(element.GetString(), valueKind, splitScalarString))
                            yield return item;
                        break;
                    case System.Text.Json.JsonValueKind.Array:
                        foreach (var child in element.EnumerateArray())
                            foreach (var item in ExtractStringValues(child, valueKind, splitScalarString: false))
                                yield return item;
                        break;
                    case System.Text.Json.JsonValueKind.Object:
                        var jsonValue = FindObjectValue(element.EnumerateObject().Select(property => (property.Name, (object?)property.Value)), valueKind);
                        foreach (var item in ExtractStringValues(jsonValue, valueKind, splitScalarString: false))
                            yield return item;
                        break;
                }
                yield break;
            case IDictionary<string, string> map:
                var stringMapValue = FindObjectValue(map.Select(entry => (entry.Key, (object?)entry.Value)), valueKind);
                foreach (var item in ExtractStringValues(stringMapValue, valueKind, splitScalarString: false))
                    yield return item;
                yield break;
            case System.Collections.IDictionary map:
                var mapValues = map.Cast<System.Collections.DictionaryEntry>()
                    .Where(entry => entry.Key is string)
                    .Select(entry => ((string)entry.Key, entry.Value));
                var mapValue = FindObjectValue(mapValues, valueKind);
                foreach (var item in ExtractStringValues(mapValue, valueKind, splitScalarString: false))
                    yield return item;
                yield break;
            case System.Collections.IEnumerable list:
                foreach (var child in list)
                    foreach (var item in ExtractStringValues(child, valueKind, splitScalarString: false))
                        yield return item;
                yield break;
        }
    }

    private static object? FindObjectValue(IEnumerable<(string Key, object? Value)> entries, ScrapeListValueKind valueKind)
    {
        var values = entries.ToList();
        var preferredKeys = valueKind == ScrapeListValueKind.Url
            ? new[] { "url" }
            : new[] { "name", "title" };

        foreach (var preferredKey in preferredKeys)
        {
            var match = values.FirstOrDefault(entry => entry.Key.Equals(preferredKey, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
                continue;

            var extracted = ExtractStringValues(match.Value, valueKind, splitScalarString: false)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (extracted is not null)
                return extracted;
        }

        return null;
    }

    private enum ScrapeListValueKind
    {
        Name,
        Url,
    }

    private async Task TryApplyImageAsync(Performer performer, string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) || blobService == null || httpClientFactory == null)
            return;

        try
        {
            using var response = await httpClientFactory.CreateClient("scraper").GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            if (!string.IsNullOrWhiteSpace(performer.ImageBlobId))
                await blobService.DeleteBlobAsync(performer.ImageBlobId, ct);

            performer.ImageBlobId = await blobService.StoreBlobAsync(stream, contentType, ct);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to download scraped performer image for {Name}", performer.Name);
        }
    }

    private static string? ResolveAbsoluteUrl(string? url, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var trimmedUrl = url.Trim();

        // Treat file:// results from root-relative paths like "/images/foo.jpg" as relative
        // web URLs that still need to be resolved against the scraper source URL.
        if (Uri.TryCreate(trimmedUrl, UriKind.Absolute, out var absoluteUri) && !absoluteUri.IsFile)
            return absoluteUri.ToString();

        if (!string.IsNullOrWhiteSpace(baseUrl) && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            if (trimmedUrl.StartsWith("//", StringComparison.Ordinal))
                return $"{baseUri.Scheme}:{trimmedUrl}";

            if (Uri.TryCreate(baseUri, trimmedUrl, out var resolved))
                return resolved.ToString();
        }

        return trimmedUrl;
    }

    private static bool TryParseDate(string value, out PartialDate parsed)
    {
        if (PartialDate.TryParse(value, out parsed) && parsed.Value.HasValue)
            return true;

        if (DateOnly.TryParseExact(value, ["yyyyMMdd", "MM/dd/yyyy"], out var exactDate))
        {
            parsed = new PartialDate(exactDate, DatePrecision.Day);
            return true;
        }

        if (DateTime.TryParse(value, out var dateTime))
        {
            parsed = new PartialDate(DateOnly.FromDateTime(dateTime), DatePrecision.Day);
            return true;
        }

        parsed = default;
        return false;
    }

    private static int? TryParseInt(string? value)
        => int.TryParse(value, out var parsed) ? parsed : null;

    private static void MergeValues<TItem>(ICollection<TItem> current, IEnumerable<string> incoming, Func<TItem, string> selector, Func<string, TItem> factory, Func<string, string>? keySelector = null)
    {
        keySelector ??= static value => value;
        var existing = current.Select(item => keySelector(selector(item))).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var value in incoming.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()))
        {
            if (existing.Add(keySelector(value)))
                current.Add(factory(value));
        }
    }

    internal static string NormalizeUrlKey(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var trimmed = url.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            var path = uri.AbsolutePath.TrimEnd('/');
            if (path.Length == 0)
                path = "/";
            var query = uri.Query;
            return string.Concat(host.ToLowerInvariant(), path.ToLowerInvariant(), query.ToLowerInvariant());
        }

        return trimmed.TrimEnd('/').ToLowerInvariant();
    }
}
