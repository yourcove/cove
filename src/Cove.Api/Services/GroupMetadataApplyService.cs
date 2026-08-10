using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public interface IGroupMetadataApplyService
{
    Task<bool> ApplyAsync(
        int groupId,
        ScrapedGroupDto metadata,
        DownloaderMetadataApplyOptions? options = null,
        IEnumerable<string>? replaceFields = null,
        IDictionary<string, string>? collectionModes = null,
        IReadOnlyDictionary<string, string>? tagSelections = null,
        string? sourceRunId = null,
        CancellationToken ct = default);
}

public sealed class GroupMetadataApplyService(
    CoveContext db,
    IBlobService blobService,
    IHttpClientFactory httpClientFactory,
    IEventBus eventBus,
    IUserEngagementService engagementService,
    ITagProvenanceService tagProvenanceService,
    IFieldProvenanceService? fieldProvenanceService,
    ILogger<GroupMetadataApplyService> logger) : IGroupMetadataApplyService
{
    public async Task<bool> ApplyAsync(
        int groupId,
        ScrapedGroupDto metadata,
        DownloaderMetadataApplyOptions? options = null,
        IEnumerable<string>? replaceFields = null,
        IDictionary<string, string>? collectionModes = null,
        IReadOnlyDictionary<string, string>? tagSelections = null,
        string? sourceRunId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new DownloaderMetadataApplyOptions();
        collectionModes ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var fieldSet = replaceFields == null
            ? null
            : new HashSet<string>(replaceFields.Where(item => !string.IsNullOrWhiteSpace(item)), StringComparer.OrdinalIgnoreCase);

        var group = await db.Groups
            .Include(item => item.Urls)
            .Include(item => item.GroupTags).ThenInclude(item => item.Tag)
            .Include(item => item.Studio)
            .FirstOrDefaultAsync(item => item.Id == groupId, ct);

        if (group == null)
            return false;

        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(metadata.SourceScraperId);

        if (ShouldApply(fieldSet, "name") && !string.IsNullOrWhiteSpace(metadata.Name))
        {
            group.Name = metadata.Name.Trim();
            fieldProvenance["name"] = group.Name;
        }

        if (ShouldApply(fieldSet, "duration") && metadata.Duration.HasValue)
        {
            group.Duration = metadata.Duration.Value;
            fieldProvenance["duration"] = metadata.Duration.Value;
        }

        if (ShouldApply(fieldSet, "director") && !string.IsNullOrWhiteSpace(metadata.Director))
        {
            group.Director = metadata.Director.Trim();
            fieldProvenance["director"] = group.Director;
        }

        var synopsis = !string.IsNullOrWhiteSpace(metadata.Synopsis) ? metadata.Synopsis : metadata.Details;
        if ((ShouldApply(fieldSet, "details") || ShouldApply(fieldSet, "synopsis")) && !string.IsNullOrWhiteSpace(synopsis))
        {
            group.Synopsis = synopsis.Trim();
            fieldProvenance["synopsis"] = group.Synopsis;
        }

        if (ShouldApply(fieldSet, "date") && ScrapedVideoDateParser.TryParse(metadata.Date, out var parsedDate))
        {
            group.Date = parsedDate;
            fieldProvenance["date"] = group.Date.Value.ToString("yyyy-MM-dd");
        }

        if (ShouldApply(fieldSet, "rating") && metadata.Rating.HasValue)
        {
            await engagementService.SetRatingAsync(AffinityHostType.Group, group.Id, metadata.Rating, cancellationToken: ct);
            fieldProvenance["rating"] = metadata.Rating.Value;
        }

        if (ShouldApply(fieldSet, "image") && await TryApplyRemoteFrontImageAsync(group, metadata.ImageUrl, ct))
            fieldProvenance["image_url"] = metadata.ImageUrl?.Trim();

        var aliases = NormalizeNames(metadata.Aliases);
        if (aliases.Count > 0 && ApplyAliases(group, aliases, collectionModes))
            fieldProvenance["aliases"] = aliases;

        var urls = NormalizeNames(metadata.Urls);
        if (urls.Count > 0 && ApplyUrls(group, urls, collectionModes))
            fieldProvenance["urls"] = urls;

        var tagNames = NormalizeNames(metadata.TagNames);
        var appliedTagNames = await ApplyTagsAsync(group, tagNames, collectionModes, options.CreateMissingTags, tagSelections, sourceKey, sourceRunId, ct);
        if (appliedTagNames.Count > 0)
            fieldProvenance["tags"] = appliedTagNames;

        var studioName = string.IsNullOrWhiteSpace(metadata.StudioName) ? null : metadata.StudioName.Trim();
        if (await ApplyStudioAsync(group, studioName, collectionModes, options.CreateMissingStudio, ct))
            fieldProvenance["studio"] = studioName;

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Group, group.Id, fieldProvenance, sourceKey, sourceRunId: sourceRunId, cancellationToken: ct);

        await db.SaveChangesAsync(ct);
        eventBus.Publish(new EntityEvent(EventType.GroupUpdated, "Group", group.Id));
        return true;
    }

    private static bool ShouldApply(HashSet<string>? replaceFields, string field)
        => replaceFields == null || replaceFields.Contains(field);

    private static string BuildScraperSourceKey(string? scraperId)
        => string.IsNullOrWhiteSpace(scraperId) ? "scraper" : $"scraper:{scraperId.Trim()}";

    private static bool ApplyAliases(Group group, IReadOnlyList<string> aliases, IDictionary<string, string> collectionModes)
    {
        var mode = GetMode(collectionModes, "aliases");
        if (mode == "skip")
            return false;

        var nextAliases = mode == "replace"
            ? aliases.ToList()
            : SplitAliases(group.Aliases).Concat(aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        group.Aliases = nextAliases.Count == 0 ? null : string.Join(", ", nextAliases);
        return true;
    }

    private static bool ApplyUrls(Group group, IReadOnlyList<string> urls, IDictionary<string, string> collectionModes)
    {
        var mode = GetMode(collectionModes, "urls");
        if (mode == "skip")
            return false;

        var existing = group.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (mode == "replace")
        {
            group.Urls.Clear();
            existing.Clear();
        }

        var changed = false;
        foreach (var url in urls)
        {
            if (!existing.Add(url))
                continue;

            group.Urls.Add(new GroupUrl { GroupId = group.Id, Url = url });
            changed = true;
        }

        return changed || mode == "replace";
    }

    private async Task<IReadOnlyList<string>> ApplyTagsAsync(
        Group group,
        IReadOnlyList<string> tagNames,
        IDictionary<string, string> collectionModes,
        bool createMissing,
        IReadOnlyDictionary<string, string>? selections,
        string sourceKey,
        string? sourceRunId,
        CancellationToken ct)
    {
        var mode = GetMode(collectionModes, "tags");
        if (mode == "skip" || tagNames.Count == 0)
            return [];

        var selectedTagNames = ResolveSelectedRelationNames(tagNames, selections, createMissing);
        if (selectedTagNames.Count == 0)
        {
            if (mode == "replace")
                group.GroupTags.Clear();
            return [];
        }

        var tagLookup = await RelationNameResolver.ResolveTagsAsync(db, selectedTagNames.Select(item => item.Name).ToArray(), ct);

        if (mode == "replace")
            group.GroupTags.Clear();

        var existingTagIds = group.GroupTags.Select(item => item.TagId).ToHashSet();
        var applied = new List<string>();

        foreach (var selectedTag in selectedTagNames)
        {
            if (!tagLookup.TryGetValue(selectedTag.Name, out var tag))
            {
                if (!selectedTag.AllowCreate)
                    continue;

                tag = new Tag { Name = selectedTag.Name };
                db.Tags.Add(tag);
                await db.SaveChangesAsync(ct);
                tagLookup[selectedTag.Name] = tag;
            }

            applied.Add(tag.Name);
            if (existingTagIds.Add(tag.Id))
                group.GroupTags.Add(new GroupTag { GroupId = group.Id, TagId = tag.Id, Tag = tag });

            await tagProvenanceService.RecordAsync(AffinityHostType.Group, group.Id, tag, sourceKey, sourceRunId: sourceRunId, cancellationToken: ct);
        }

        return applied.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<bool> ApplyStudioAsync(Group group, string? studioName, IDictionary<string, string> collectionModes, bool createMissing, CancellationToken ct)
    {
        var mode = GetMode(collectionModes, "studio");
        if (mode == "skip" || string.IsNullOrWhiteSpace(studioName))
            return false;

        var normalizedStudioName = studioName.Trim();
        var studio = await db.Studios.FirstOrDefaultAsync(item => item.Name.ToLower() == normalizedStudioName.ToLower(), ct);
        if (studio == null)
        {
            if (!createMissing)
                return false;

            studio = new Studio { Name = normalizedStudioName };
            db.Studios.Add(studio);
        }

        group.Studio = studio;
        group.StudioId = studio.Id == 0 ? null : studio.Id;
        return true;
    }

    private async Task<bool> TryApplyRemoteFrontImageAsync(Group group, string? imageUrl, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(imageUrl))
            return false;

        try
        {
            var client = httpClientFactory.CreateClient("scraper");
            using var response = await client.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return false;

            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0)
                return false;

            var detectedContentType = DetectImageContentType(bytes);
            var declaredContentType = response.Content.Headers.ContentType?.MediaType;
            var contentType = detectedContentType
                ?? (declaredContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? declaredContentType : null);

            if (string.IsNullOrWhiteSpace(contentType))
                return false;

            await using var stream = new MemoryStream(bytes);
            var newBlobId = await blobService.StoreBlobAsync(stream, contentType, ct);
            var previousBlobId = group.FrontImageBlobId;
            group.FrontImageBlobId = newBlobId;

            if (!string.IsNullOrWhiteSpace(previousBlobId) && !string.Equals(previousBlobId, newBlobId, StringComparison.Ordinal))
                await blobService.DeleteBlobAsync(previousBlobId, ct);

            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to apply remote front image for group {GroupId}", group.Id);
            return false;
        }
    }

    private static string GetMode(IDictionary<string, string> collectionModes, string key)
        => collectionModes.TryGetValue(key, out var mode) && !string.IsNullOrWhiteSpace(mode)
            ? mode.Trim().ToLowerInvariant()
            : key == "studio" ? "replace" : "merge";

    private static List<string> NormalizeNames(IEnumerable<string> values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static List<string> SplitAliases(string? aliases)
        => string.IsNullOrWhiteSpace(aliases)
            ? []
            : aliases.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    private sealed record SelectedRelationName(string Name, bool AllowCreate);

    private static List<SelectedRelationName> ResolveSelectedRelationNames(IReadOnlyList<string> names, IReadOnlyDictionary<string, string>? selections, bool createMissing)
    {
        var selected = new List<SelectedRelationName>();
        foreach (var name in names)
        {
            var normalizedName = NormalizeSelectionName(name);
            if (string.IsNullOrWhiteSpace(normalizedName))
                continue;

            if (selections == null)
            {
                selected.Add(new SelectedRelationName(normalizedName, createMissing));
                continue;
            }

            if (!selections.TryGetValue(normalizedName, out var action) || NormalizeSelectionAction(action) == "exclude")
                continue;

            selected.Add(new SelectedRelationName(normalizedName, NormalizeSelectionAction(action) == "create"));
        }

        return selected
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Any(item => item.AllowCreate)
                ? new SelectedRelationName(group.First().Name, true)
                : group.First())
            .ToList();
    }

    private static string? NormalizeSelectionName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            trimmed = trimmed[1..^1].Trim();

        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string? NormalizeSelectionAction(string? value)
    {
        var normalized = value?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "include" => "include",
            "create" => "create",
            "exclude" or "skip" => "exclude",
            _ => null,
        };
    }

    private static string? DetectImageContentType(byte[] data)
    {
        if (data.Length < 4)
            return null;

        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        if (data.Length >= 8
            && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return "image/png";

        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "image/gif";

        if (data.Length >= 12
            && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";

        if (data.Length >= 12
            && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70
            && data[8] == 0x61 && data[9] == 0x76 && data[10] == 0x69 && data[11] == 0x66)
            return "image/avif";

        if (data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";

        var littleEndianTiff = data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00;
        var bigEndianTiff = data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A;
        if (littleEndianTiff || bigEndianTiff)
            return "image/tiff";

        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0x0A)
            return "image/jxl";

        if (data.Length >= 8
            && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C
            && data[4] == 0x4A && data[5] == 0x58 && data[6] == 0x4C && data[7] == 0x20)
            return "image/jxl";

        if (LooksLikeSvg(data))
            return "image/svg+xml";

        return null;
    }

    private static bool LooksLikeSvg(byte[] data)
    {
        var head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256));
        var trimmed = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }
}
