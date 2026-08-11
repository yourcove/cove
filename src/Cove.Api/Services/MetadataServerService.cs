using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;

namespace Cove.Api.Services;

public enum VideoMetadataSearchStrategy
{
    RemoteIdAndFingerprintThenText,
    RemoteIdFingerprint,
    RemoteId,
    Fingerprint,
    Text,
}

public class MetadataServerService : IMetadataServerService
{
    private static readonly Regex LeadingVideoIndexRegex = new(@"^\s*(?:video\s+)?(?:\[\s*\d+\s*\]|\(\s*\d+\s*\)|\d+)\s*(?:[-â€“â€”:._)\]]\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Maximum hamming distance for phash to be considered a match.
    /// Different image processing libraries (Go vs C#) produce slightly different phashes
    /// for the same content, typically differing by 1-4 bits.
    /// </summary>
    private const int PhashMatchThreshold = 8;

    private const string PerformerFragment = """
fragment PerformerFields on Performer {
  id
  name
  disambiguation
  aliases
  gender
  deleted
  merged_into_id
  urls {
    url
  }
  images {
    url
  }
  birth_date
  death_date
  ethnicity
  country
  eye_color
  hair_color
  height
  measurements {
    band_size
    cup_size
    waist
    hip
  }
  breast_type
  career_start_year
  career_end_year
  tattoos {
    location
    description
  }
  piercings {
    location
    description
  }
}
""";

    private const string SearchPerformerQuery = """
query SearchPerformer($term: String!) {
  searchPerformer(term: $term) {
    ... PerformerFields
  }
}
""" + PerformerFragment;

    private const string FindPerformerByIdQuery = """
query FindPerformerByID($id: ID!) {
  findPerformer(id: $id) {
    ... PerformerFields
  }
}
""" + PerformerFragment;

        private const string SearchStudioQuery = """
query SearchStudio($term: String!) {
  searchStudio(term: $term) {
    ... StudioFields
  }
}
""" + StudioFragment;

                private const string FindStudioQuery = """
query FindStudio($id: ID, $name: String) {
    findStudio(id: $id, name: $name) {
        ... StudioFields
    }
}
""" + StudioFragment;

        private const string StudioFragment = """
fragment StudioFields on Studio {
    id
    name
    aliases
    urls {
        url
    }
    images {
        url
    }
    parent {
        id
        name
    }
}
""";

        private const string TagFragment = """
fragment TagFields on Tag {
    id
    name
    description
    aliases
}
""";

                private const string FindTagQuery = """
query FindTag($id: ID, $name: String) {
    findTag(id: $id, name: $name) {
        ... TagFields
    }
}
""" + TagFragment;

        private const string FingerprintFragment = """
fragment FingerprintFields on Fingerprint {
    algorithm
    hash
    duration
}
""";

        private const string VideoFragment = """
fragment VideoFields on Scene {
    id
    title
    code
    details
    director
    duration
    date
    urls {
        url
    }
    images {
        url
    }
    studio {
        ... StudioFields
    }
    tags {
        ... TagFields
    }
    performers {
        performer {
            ... PerformerFields
        }
    }
    fingerprints {
        ... FingerprintFields
    }
}
""" + StudioFragment + TagFragment + FingerprintFragment + PerformerFragment;

        private const string SearchVideoQuery = """
query SearchVideo($term: String!) {
    searchVideo: searchScene(term: $term) {
        ... VideoFields
    }
}
""" + VideoFragment;

        private const string FindVideoByIdQuery = """
query FindVideoByID($id: ID!) {
    findVideo: findScene(id: $id) {
        ... VideoFields
    }
}
""" + VideoFragment;

        private const string FindVideosByFingerprintsQuery = """
query FindVideosByVideoFingerprints($fingerprints: [[FingerprintQueryInput!]!]!) {
    findVideosByVideoFingerprints: findScenesBySceneFingerprints(fingerprints: $fingerprints) {
        ... VideoFields
    }
}
""" + VideoFragment;

    private const string MeQuery = """
query Me {
  me {
    name
  }
}
""";

    private readonly HttpClient _httpClient;
    private readonly CoveConfiguration _config;
    private readonly CoveContext _db;
    private readonly IBlobService _blobService;
    private readonly IVideoCoverService _videoCoverService;
    private readonly ITagProvenanceService _tagProvenanceService;
    private readonly IFieldProvenanceService? _fieldProvenanceService;
    private readonly IEventBus? _eventBus;
    private readonly ILogger<MetadataServerService> _logger;
    private Dictionary<string, int[]>? _performerIdentityIndex;
    private Dictionary<string, int[]>? _studioIdentityIndex;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MetadataServerService(HttpClient httpClient, CoveConfiguration config, CoveContext db, IBlobService blobService, IVideoCoverService videoCoverService, ITagProvenanceService tagProvenanceService, ILogger<MetadataServerService> logger, IFieldProvenanceService? fieldProvenanceService = null, IEventBus? eventBus = null)
    {
        _httpClient = httpClient;
        _config = config;
        _db = db;
        _blobService = blobService;
        _videoCoverService = videoCoverService;
        _tagProvenanceService = tagProvenanceService;
        _fieldProvenanceService = fieldProvenanceService;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<MetadataServerValidationResultDto> ValidateAsync(MetadataServerDto input, CancellationToken ct)
    {
        var box = ToConfigBox(input);

        try
        {
            var response = await SendQueryAsync<MetadataServerMeQueryResponse>(box, MeQuery, null, ct);
            var username = response.Me?.Name?.Trim();
            if (!string.IsNullOrWhiteSpace(username))
            {
                return new MetadataServerValidationResultDto(true, $"Successfully authenticated as {username}", username);
            }

            return new MetadataServerValidationResultDto(false, "Invalid or expired API key.", null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to validate metadata-server endpoint {Endpoint}", box.Endpoint);
            return new MetadataServerValidationResultDto(false, MapValidationError(ex), null);
        }
    }

    public async Task<IReadOnlyList<MetadataServerPerformerMatchDto>> SearchPerformersAsync(string term, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var boxes = ResolveBoxes(endpoint);
        var results = new List<MetadataServerPerformerMatchDto>();
        var strictEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var failedEndpoints = 0;

        foreach (var box in boxes)
        {
            try
            {
                var response = await SendQueryAsync<MetadataServerSearchPerformerResponse>(box, SearchPerformerQuery, new { term }, ct);
                results.AddRange(response.SearchPerformer.Select(remote => ToMatchDto(box, remote)));
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                failedEndpoints++;
                _logger.LogDebug(ex, "Skipping metadata-server performer search for {Endpoint}", box.Endpoint);
            }
        }

        if (!strictEndpoint && results.Count == 0 && boxes.Count > 0 && failedEndpoints == boxes.Count)
            _logger.LogWarning("Metadata-server performer search failed for all {EndpointCount} configured endpoint(s)", boxes.Count);

        return results
            .OrderByDescending(match => string.Equals(match.Name, term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(match => match.Deleted)
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MetadataServerPerformerMatchDto?> GetPerformerMatchAsync(string endpoint, string performerId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var performer = await GetRemotePerformerAsync(box, performerId, ct);
        if (performer == null)
            return null;

        if (!string.IsNullOrWhiteSpace(performer.MergedIntoId))
        {
            var merged = await GetRemotePerformerAsync(box, performer.MergedIntoId, ct);
            if (merged != null)
                performer = merged;
        }

        return ToMatchDto(box, performer);
    }

    public async Task<IReadOnlyList<MetadataServerPerformerMatchDto>> GetPerformerMatchesAsync(string endpoint, IEnumerable<string> performerIds, CancellationToken ct)
    {
        var results = new List<MetadataServerPerformerMatchDto>();
        foreach (var performerId in performerIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await GetPerformerMatchAsync(endpoint, performerId, ct);
            if (match != null)
                results.Add(match);
        }

        return results;
    }

    public Task<bool> MergePerformerAsync(Performer performer, string endpoint, string performerId, CancellationToken ct)
        => MergePerformerAsync(performer, endpoint, performerId, null, ct);

    public async Task<bool> MergePerformerAsync(Performer performer, string endpoint, string performerId, MetadataServerPerformerImportRequestDto? importConfig, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemotePerformerAsync(box, performerId, ct);
        if (remote == null)
            return false;

        if (!string.IsNullOrWhiteSpace(remote.MergedIntoId))
        {
            var merged = await GetRemotePerformerAsync(box, remote.MergedIntoId, ct);
            if (merged != null)
                remote = merged;
        }

        ApplyRemotePerformer(performer, box.Endpoint, remote, importConfig);
        await DownloadPerformerImageAsync(performer, remote, GetMetadataFieldStrategy(importConfig?.FieldStrategies, "image", MetadataFieldStrategy.Merge), ct);
        var fieldProvenance = BuildPerformerMetadataFieldProvenance(remote, importConfig, box.Endpoint);
        if (fieldProvenance.Count > 0 && _fieldProvenanceService != null)
            await _fieldProvenanceService.RecordManyAsync(AffinityHostType.Performer, performer.Id, fieldProvenance, BuildMetadataSourceKey(box.Endpoint), sourceRunId: box.Endpoint, cancellationToken: ct);
        return true;
    }

    // ===== Studio Metadata Server Methods =====

    public async Task<IReadOnlyList<MetadataServerStudioMatchDto>> SearchStudiosAsync(string term, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var boxes = ResolveBoxes(endpoint);
        var results = new List<MetadataServerStudioMatchDto>();
        var strictEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var failedEndpoints = 0;

        foreach (var box in boxes)
        {
            try
            {
                var response = await SendQueryAsync<MetadataServerSearchStudioResponse>(box, SearchStudioQuery, new { term }, ct);
                results.AddRange(response.SearchStudio.Select(remote => ToStudioMatchDto(box, remote)));
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                failedEndpoints++;
                _logger.LogDebug(ex, "Skipping metadata-server studio search for {Endpoint}", box.Endpoint);
            }
        }

        if (!strictEndpoint && results.Count == 0 && boxes.Count > 0 && failedEndpoints == boxes.Count)
            _logger.LogWarning("Metadata-server studio search failed for all {EndpointCount} configured endpoint(s)", boxes.Count);

        return results
            .OrderByDescending(m => string.Equals(m.Name, term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MetadataServerStudioMatchDto?> GetStudioMatchAsync(string endpoint, string studioId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var studio = await GetRemoteStudioAsync(box, studioId: studioId, studioName: null, ct);
        return studio == null ? null : ToStudioMatchDto(box, studio);
    }

    public async Task<IReadOnlyList<MetadataServerStudioMatchDto>> GetStudioMatchesAsync(string endpoint, IEnumerable<string> studioIds, CancellationToken ct)
    {
        var results = new List<MetadataServerStudioMatchDto>();
        foreach (var studioId in studioIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var match = await GetStudioMatchAsync(endpoint, studioId, ct);
            if (match != null)
                results.Add(match);
        }

        return results;
    }

    public Task<bool> MergeStudioAsync(Studio studio, string endpoint, string studioId, CancellationToken ct, bool createParentStudios = true)
        => MergeStudioAsync(studio, endpoint, studioId, null, ct, createParentStudios);

    public async Task<bool> MergeStudioAsync(Studio studio, string endpoint, string studioId, MetadataServerStudioImportRequestDto? importConfig, CancellationToken ct, bool createParentStudios = true)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemoteStudioAsync(box, studioId: studioId, studioName: null, ct);
        if (remote == null)
            return false;

        var fieldStrategies = importConfig?.FieldStrategies ?? [];
        var aliasMode = GetImportStrategy(fieldStrategies, "aliases", defaultStrategy: "merge");
        var urlMode = GetImportStrategy(fieldStrategies, "urls", defaultStrategy: "merge");
        var parentMode = GetImportStrategy(fieldStrategies, "parent", defaultStrategy: "merge");

        if (!IsIgnoredImportStrategy(GetImportStrategy(fieldStrategies, "name", defaultStrategy: "overwrite")))
            studio.Name = remote.Name.Trim();

        if (IsReplaceImportStrategy(aliasMode))
            ReplaceStudioAliases(studio, remote.Aliases);
        else if (!IsIgnoredImportStrategy(aliasMode))
            MergeAliases(studio, remote.Aliases);

        var remoteUrls = remote.Urls.Select(u => u.Url).ToList();
        if (IsReplaceImportStrategy(urlMode))
            ReplaceStudioUrls(studio, remoteUrls);
        else if (!IsIgnoredImportStrategy(urlMode))
            MergeUrls(studio, remoteUrls);

        UpsertRemoteId(studio.RemoteIds, box.Endpoint, remote.Id, id => id.Endpoint, id => id.RemoteId, (id, value) => id.RemoteId = value, value => new StudioRemoteId { Endpoint = box.Endpoint, RemoteId = value });
        // Default keeps an existing cover and only fills when missing; "replace"/"overwrite" replaces it.
        var studioImageMode = GetImportStrategy(fieldStrategies, "image", defaultStrategy: "merge");
        if (!IsIgnoredImportStrategy(studioImageMode))
            await DownloadStudioImageAsync(studio, remote, ct, overwrite: IsReplaceImportStrategy(studioImageMode));

        // Resolve parent studio
        if (createParentStudios && !IsIgnoredImportStrategy(parentMode) && (remote.Parent != null || IsReplaceImportStrategy(parentMode)))
        {
            if (remote.Parent == null)
            {
                studio.ParentId = null;
                studio.Parent = null;
            }
            else if (IsReplaceImportStrategy(parentMode) || studio.ParentId == null)
            {
                var parent = await _db.Studios
                    .Include(s => s.RemoteIds)
                    .FirstOrDefaultAsync(s => s.RemoteIds.Any(id => id.Endpoint == box.Endpoint && id.RemoteId == remote.Parent.Id), ct)
                    ?? await FindStudioByIdentityAsync(remote.Parent.Name, ct);

                if (parent == null)
                {
                    parent = new Studio { Name = remote.Parent.Name };
                    parent.RemoteIds.Add(new StudioRemoteId { Endpoint = box.Endpoint, RemoteId = remote.Parent.Id });
                    _db.Studios.Add(parent);
                }
                studio.Parent = parent;
            }
        }

        var fieldProvenance = BuildStudioMetadataFieldProvenance(remote, importConfig, box.Endpoint);
        if (fieldProvenance.Count > 0 && _fieldProvenanceService != null)
            await _fieldProvenanceService.RecordManyAsync(AffinityHostType.Studio, studio.Id, fieldProvenance, BuildMetadataSourceKey(box.Endpoint), sourceRunId: box.Endpoint, cancellationToken: ct);

        return true;
    }

    public async Task<IReadOnlyList<MetadataServerTagMatchDto>> SearchTagsAsync(string term, string? endpoint, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var boxes = ResolveBoxes(endpoint);
        var strictEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var results = new List<MetadataServerTagMatchDto>();
        var failedEndpoints = 0;

        foreach (var box in boxes)
        {
            try
            {
                var response = await SendQueryAsync<MetadataServerFindTagResponse>(box, FindTagQuery, new { id = (string?)null, name = term }, ct);
                var tag = response.FindTag;
                if (tag != null)
                    results.Add(ToTagMatchDto(box, tag));
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                failedEndpoints++;
                _logger.LogDebug(ex, "Skipping metadata-server tag search for {Endpoint}", box.Endpoint);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch tag {TagIdOrName} from {Endpoint}", term, box.Endpoint);
            }
        }

        if (!strictEndpoint && results.Count == 0 && boxes.Count > 0 && failedEndpoints == boxes.Count)
            _logger.LogWarning("Metadata-server tag search failed for all {EndpointCount} configured endpoint(s)", boxes.Count);

        return results
            .OrderByDescending(match => string.Equals(match.Name, term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(match => match.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MetadataServerTagMatchDto?> GetTagMatchAsync(string endpoint, string tagId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var tag = await GetRemoteTagAsync(box, tagId, tagName: null, ct);
        return tag == null ? null : ToTagMatchDto(box, tag);
    }

    public async Task<bool> MergeTagAsync(Tag tag, string endpoint, string tagId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemoteTagAsync(box, tagId, tagName: null, ct);
        if (remote == null)
            return false;

        tag.Name = remote.Name.Trim();
        tag.Description = Coalesce(tag.Description, remote.Description) ?? tag.Description;
        MergeAliases(tag, remote.Aliases);
        UpsertRemoteId(tag.RemoteIds, box.Endpoint, remote.Id, id => id.Endpoint, id => id.RemoteId, (id, value) => id.RemoteId = value, value => new TagRemoteId { Endpoint = box.Endpoint, RemoteId = value });
        var fieldProvenance = BuildTagMetadataFieldProvenance(remote, box.Endpoint);
        if (fieldProvenance.Count > 0 && _fieldProvenanceService != null)
            await _fieldProvenanceService.RecordManyAsync(AffinityHostType.Tag, tag.Id, fieldProvenance, BuildMetadataSourceKey(box.Endpoint), sourceRunId: box.Endpoint, cancellationToken: ct);
        return true;
    }

    public async Task<MetadataServerBatchTagResultDto> BatchTagPerformersAsync(string endpoint, IEnumerable<int> performerIds, bool refreshAlreadyTagged, IEnumerable<string>? excludeFields, IJobProgress? progress, CancellationToken ct)
    {
        var performers = await _db.Performers
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .Where(entity => performerIds.Contains(entity.Id))
            .OrderBy(entity => entity.Id)
            .ToListAsync(ct);

        var normalizedExcludeFields = NormalizeFieldNames(excludeFields);
        return await ExecuteBatchTagAsync(
            performers,
            progress,
            async performer =>
            {
                var remoteId = performer.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))?.RemoteId;
                if (!refreshAlreadyTagged && !string.IsNullOrWhiteSpace(remoteId))
                    return new MetadataServerBatchTagItemResultDto(performer.Id, performer.Name, "skipped", remoteId, "Already tagged for this endpoint");

                var match = !string.IsNullOrWhiteSpace(remoteId)
                    ? await GetPerformerMatchAsync(endpoint, remoteId, ct)
                    : await FindBestPerformerMatchAsync(endpoint, performer.Name, performer.Disambiguation, ct);
                if (match == null)
                    return new MetadataServerBatchTagItemResultDto(performer.Id, performer.Name, "skipped", null, "No remote match found");

                var snapshot = CapturePerformerSnapshot(performer);
                var imported = await MergePerformerAsync(performer, endpoint, match.Id, ct);
                if (!imported)
                    return new MetadataServerBatchTagItemResultDto(performer.Id, performer.Name, "failed", match.Id, "Remote performer no longer exists");

                await RestoreExcludedPerformerFieldsAsync(performer, snapshot, normalizedExcludeFields, ct);
                await _db.SaveChangesAsync(ct);
                _eventBus?.Publish(new EntityEvent(EventType.PerformerUpdated, "Performer", performer.Id));
                return new MetadataServerBatchTagItemResultDto(performer.Id, performer.Name, "updated", match.Id);
            },
            ct);
    }

    public async Task<MetadataServerBatchTagResultDto> BatchTagStudiosAsync(string endpoint, IEnumerable<int> studioIds, bool refreshAlreadyTagged, IEnumerable<string>? excludeFields, bool createParentStudios, IJobProgress? progress, CancellationToken ct)
    {
        var studios = await _db.Studios
            .Include(entity => entity.Parent)
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .Where(entity => studioIds.Contains(entity.Id))
            .OrderBy(entity => entity.Id)
            .ToListAsync(ct);

        var normalizedExcludeFields = NormalizeFieldNames(excludeFields);
        return await ExecuteBatchTagAsync(
            studios,
            progress,
            async studio =>
            {
                var remoteId = studio.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))?.RemoteId;
                if (!refreshAlreadyTagged && !string.IsNullOrWhiteSpace(remoteId))
                    return new MetadataServerBatchTagItemResultDto(studio.Id, studio.Name, "skipped", remoteId, "Already tagged for this endpoint");

                var match = !string.IsNullOrWhiteSpace(remoteId)
                    ? await GetStudioMatchAsync(endpoint, remoteId, ct)
                    : await FindBestStudioMatchAsync(endpoint, studio.Name, ct);
                if (match == null)
                    return new MetadataServerBatchTagItemResultDto(studio.Id, studio.Name, "skipped", null, "No remote match found");

                var snapshot = CaptureStudioSnapshot(studio);
                var imported = await MergeStudioAsync(studio, endpoint, match.Id, ct, createParentStudios);
                if (!imported)
                    return new MetadataServerBatchTagItemResultDto(studio.Id, studio.Name, "failed", match.Id, "Remote studio no longer exists");

                await RestoreExcludedStudioFieldsAsync(studio, snapshot, normalizedExcludeFields, ct);
                await _db.SaveChangesAsync(ct);
                _eventBus?.Publish(new EntityEvent(EventType.StudioUpdated, "Studio", studio.Id));
                return new MetadataServerBatchTagItemResultDto(studio.Id, studio.Name, "updated", match.Id);
            },
            ct);
    }

    public async Task<MetadataServerBatchTagResultDto> BatchTagTagsAsync(string endpoint, IEnumerable<int> tagIds, bool refreshAlreadyTagged, IEnumerable<string>? excludeFields, IJobProgress? progress, CancellationToken ct)
    {
        var tags = await _db.Tags
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Where(entity => tagIds.Contains(entity.Id))
            .OrderBy(entity => entity.Id)
            .ToListAsync(ct);

        var normalizedExcludeFields = NormalizeFieldNames(excludeFields);
        return await ExecuteBatchTagAsync(
            tags,
            progress,
            async tag =>
            {
                var remoteId = tag.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))?.RemoteId;
                if (!refreshAlreadyTagged && !string.IsNullOrWhiteSpace(remoteId))
                    return new MetadataServerBatchTagItemResultDto(tag.Id, tag.Name, "skipped", remoteId, "Already tagged for this endpoint");

                var match = !string.IsNullOrWhiteSpace(remoteId)
                    ? await GetTagMatchAsync(endpoint, remoteId, ct)
                    : await FindBestTagMatchAsync(endpoint, tag.Name, ct);
                if (match == null)
                    return new MetadataServerBatchTagItemResultDto(tag.Id, tag.Name, "skipped", null, "No remote match found");

                var snapshot = CaptureTagSnapshot(tag);
                var imported = await MergeTagAsync(tag, endpoint, match.Id, ct);
                if (!imported)
                    return new MetadataServerBatchTagItemResultDto(tag.Id, tag.Name, "failed", match.Id, "Remote tag no longer exists");

                await RestoreExcludedTagFieldsAsync(tag, snapshot, normalizedExcludeFields);
                await _db.SaveChangesAsync(ct);
                _eventBus?.Publish(new EntityEvent(EventType.TagUpdated, "Tag", tag.Id));
                return new MetadataServerBatchTagItemResultDto(tag.Id, tag.Name, "updated", match.Id);
            },
            ct);
    }

    private async Task<MetadataServerRemoteStudio?> GetRemoteStudioAsync(MetadataServerInstance box, string? studioId, string? studioName, CancellationToken ct)
    {
        try
        {
            var response = await SendQueryAsync<MetadataServerFindStudioResponse>(box, FindStudioQuery, new { id = studioId, name = studioName }, ct);
            return response.FindStudio;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch studio {StudioIdOrName} from {Endpoint}", studioId ?? studioName, box.Endpoint);
            return null;
        }
    }

    private async Task<MetadataServerRemoteTag?> GetRemoteTagAsync(MetadataServerInstance box, string? tagId, string? tagName, CancellationToken ct)
    {
        try
        {
            var response = await SendQueryAsync<MetadataServerFindTagResponse>(box, FindTagQuery, new { id = tagId, name = tagName }, ct);
            return response.FindTag;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch tag {TagIdOrName} from {Endpoint}", tagId ?? tagName, box.Endpoint);
            return null;
        }
    }

    private static MetadataServerStudioMatchDto ToStudioMatchDto(MetadataServerInstance box, MetadataServerRemoteStudio studio)
    {
        return new MetadataServerStudioMatchDto(
            Endpoint: box.Endpoint,
            MetadataServerName: string.IsNullOrWhiteSpace(box.Name) ? box.Endpoint : box.Name,
            Id: studio.Id,
            Name: studio.Name,
            ImageUrl: studio.Images.FirstOrDefault()?.Url,
            Aliases: studio.Aliases
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Urls: studio.Urls
                .Select(u => u.Url)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ParentName: studio.Parent?.Name
        );
    }

    private static MetadataServerTagMatchDto ToTagMatchDto(MetadataServerInstance box, MetadataServerRemoteTag tag)
    {
        return new MetadataServerTagMatchDto(
            Endpoint: box.Endpoint,
            MetadataServerName: string.IsNullOrWhiteSpace(box.Name) ? box.Endpoint : box.Name,
            Id: tag.Id,
            Name: tag.Name,
            Description: tag.Description,
            Aliases: CleanStrings(tag.Aliases)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        );
    }

    private async Task<MetadataServerPerformerMatchDto?> FindBestPerformerMatchAsync(
        string endpoint,
        string name,
        string? disambiguation,
        CancellationToken ct)
    {
        var matches = await SearchPerformersAsync(name, endpoint, ct);
        var identityKey = EntityNameRules.PerformerIdentityKey(name, disambiguation);
        return matches.FirstOrDefault(match =>
            !match.Deleted
            && EntityNameRules.PerformerIdentityKey(match.Name, match.Disambiguation) == identityKey);
    }

    private async Task<MetadataServerStudioMatchDto?> FindBestStudioMatchAsync(string endpoint, string name, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var exact = await GetRemoteStudioAsync(box, studioId: null, studioName: name, ct);
        var identityKey = EntityNameRules.StudioIdentityKey(name);
        if (exact != null && EntityNameRules.StudioIdentityKey(exact.Name) == identityKey)
            return ToStudioMatchDto(box, exact);

        var matches = await SearchStudiosAsync(name, endpoint, ct);
        return matches.FirstOrDefault(match => EntityNameRules.StudioIdentityKey(match.Name) == identityKey);
    }

    private async Task<MetadataServerTagMatchDto?> FindBestTagMatchAsync(string endpoint, string name, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var exact = await GetRemoteTagAsync(box, tagId: null, tagName: name, ct);
        return exact == null ? null : ToTagMatchDto(box, exact);
    }

    private async Task<MetadataServerBatchTagResultDto> ExecuteBatchTagAsync<T>(
        IReadOnlyList<T> items,
        IJobProgress? progress,
        Func<T, Task<MetadataServerBatchTagItemResultDto>> process,
        CancellationToken ct)
    {
        var results = new List<MetadataServerBatchTagItemResultDto>(items.Count);
        var updated = 0;
        var skipped = 0;
        var failed = 0;

        for (var index = 0; index < items.Count; index++)
        {
            ct.ThrowIfCancellationRequested();

            var originalItem = items[index];
            var (entityId, entityName) = DescribeBatchEntity(originalItem);
            progress?.Report(items.Count == 0 ? 1d : (double)index / items.Count, entityName);

            MetadataServerBatchTagItemResultDto result;
            try
            {
                // Every processor performs its one database save only after the remote result and
                // provenance graph have been prepared. Reload into a clean tracker so a failed item
                // cannot leave Modified/Added state that contaminates later items in this batch.
                _db.ChangeTracker.Clear();
                InvalidateEntityIdentityIndexes();
                var item = await ReloadBatchEntityAsync(originalItem, entityId, ct);
                result = await process(item);
            }
            catch (Exception ex)
            {
                _db.ChangeTracker.Clear();
                InvalidateEntityIdentityIndexes();
                _logger.LogWarning(ex, "Failed metadata batch tagging for {EntityType} {EntityId}", typeof(T).Name, entityId);
                result = new MetadataServerBatchTagItemResultDto(entityId, entityName, "failed", null, ex.Message);
            }

            results.Add(result);
            switch (result.Outcome.Trim().ToLowerInvariant())
            {
                case "updated":
                    updated++;
                    break;
                case "failed":
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }
        }

        progress?.Report(1d, $"Processed {items.Count} items");
        return new MetadataServerBatchTagResultDto(items.Count, updated, skipped, failed, results);
    }

    private void InvalidateEntityIdentityIndexes()
    {
        _performerIdentityIndex = null;
        _studioIdentityIndex = null;
    }

    private async Task<T> ReloadBatchEntityAsync<T>(T fallback, int entityId, CancellationToken ct)
    {
        object? entity = fallback switch
        {
            Performer => await _db.Performers
                .Include(item => item.RemoteIds)
                .Include(item => item.Aliases)
                .Include(item => item.Urls)
                .SingleOrDefaultAsync(item => item.Id == entityId, ct),
            Studio => await _db.Studios
                .Include(item => item.Parent)
                .Include(item => item.RemoteIds)
                .Include(item => item.Aliases)
                .Include(item => item.Urls)
                .SingleOrDefaultAsync(item => item.Id == entityId, ct),
            Tag => await _db.Tags
                .Include(item => item.RemoteIds)
                .Include(item => item.Aliases)
                .SingleOrDefaultAsync(item => item.Id == entityId, ct),
            _ => fallback,
        };
        if (entity is T typed)
            return typed;
        throw new InvalidOperationException($"{typeof(T).Name} {entityId} no longer exists or is no longer accessible.");
    }

    private static (int Id, string Name) DescribeBatchEntity<T>(T item)
    {
        return item switch
        {
            Performer performer => (performer.Id, performer.Name),
            Studio studio => (studio.Id, studio.Name),
            Tag tag => (tag.Id, tag.Name),
            _ => (0, typeof(T).Name),
        };
    }

    private static HashSet<string> NormalizeFieldNames(IEnumerable<string>? fieldNames)
    {
        return fieldNames?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeFieldName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? [];
    }

    private static string GetImportStrategy(IReadOnlyDictionary<string, string> fieldStrategies, string field, string defaultStrategy)
    {
        return fieldStrategies.TryGetValue(field, out var strategy) && !string.IsNullOrWhiteSpace(strategy)
            ? strategy.Trim().ToLowerInvariant()
            : defaultStrategy;
    }

    private static bool IsIgnoredImportStrategy(string strategy)
        => strategy is "ignore" or "skip" or "keep";

    private static bool IsReplaceImportStrategy(string strategy)
        => strategy is "replace" or "overwrite";

    private static string NormalizeFieldName(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private static bool ShouldExclude(IReadOnlySet<string> excludedFields, params string[] candidates)
    {
        return candidates.Select(NormalizeFieldName).Any(excludedFields.Contains);
    }

    private static PerformerSnapshot CapturePerformerSnapshot(Performer performer)
    {
        return new PerformerSnapshot(
            performer.Name,
            performer.Disambiguation,
            performer.Gender,
            performer.Birthdate,
            performer.DeathDate,
            performer.Country,
            performer.Ethnicity,
            performer.EyeColor,
            performer.HairColor,
            performer.HeightCm,
            performer.Measurements,
            performer.FakeTits,
            performer.CareerStart,
            performer.CareerEnd,
            performer.Tattoos,
            performer.Piercings,
            performer.ImageBlobId,
            performer.Aliases.Select(alias => alias.Alias).ToList(),
            performer.Urls.Select(url => url.Url).ToList()
        );
    }

    private async Task RestoreExcludedPerformerFieldsAsync(Performer performer, PerformerSnapshot snapshot, IReadOnlySet<string> excludedFields, CancellationToken ct)
    {
        if (excludedFields.Count == 0)
            return;

        if (ShouldExclude(excludedFields, "name")) performer.Name = snapshot.Name;
        if (ShouldExclude(excludedFields, "disambiguation")) performer.Disambiguation = snapshot.Disambiguation;
        if (ShouldExclude(excludedFields, "gender")) performer.Gender = snapshot.Gender;
        if (ShouldExclude(excludedFields, "birthdate", "birth")) performer.Birthdate = snapshot.Birthdate;
        if (ShouldExclude(excludedFields, "deathdate", "death")) performer.DeathDate = snapshot.DeathDate;
        if (ShouldExclude(excludedFields, "country")) performer.Country = snapshot.Country;
        if (ShouldExclude(excludedFields, "ethnicity")) performer.Ethnicity = snapshot.Ethnicity;
        if (ShouldExclude(excludedFields, "eyecolor")) performer.EyeColor = snapshot.EyeColor;
        if (ShouldExclude(excludedFields, "haircolor")) performer.HairColor = snapshot.HairColor;
        if (ShouldExclude(excludedFields, "height", "heightcm")) performer.HeightCm = snapshot.HeightCm;
        if (ShouldExclude(excludedFields, "measurements")) performer.Measurements = snapshot.Measurements;
        if (ShouldExclude(excludedFields, "faketits", "breasttype")) performer.FakeTits = snapshot.FakeTits;
        if (ShouldExclude(excludedFields, "career", "careerstart")) performer.CareerStart = snapshot.CareerStart;
        if (ShouldExclude(excludedFields, "career", "careerend")) performer.CareerEnd = snapshot.CareerEnd;
        if (ShouldExclude(excludedFields, "tattoos")) performer.Tattoos = snapshot.Tattoos;
        if (ShouldExclude(excludedFields, "piercings")) performer.Piercings = snapshot.Piercings;
        if (ShouldExclude(excludedFields, "aliases")) ReplacePerformerAliases(performer, snapshot.Aliases);
        if (ShouldExclude(excludedFields, "urls")) ReplacePerformerUrls(performer, snapshot.Urls);
        if (ShouldExclude(excludedFields, "image", "images", "imageblobid"))
            await RestoreBlobAsync(snapshot.ImageBlobId, performer.ImageBlobId, ct, blobId => performer.ImageBlobId = blobId);
    }

    private static StudioSnapshot CaptureStudioSnapshot(Studio studio)
    {
        return new StudioSnapshot(
            studio.Name,
            studio.ImageBlobId,
            studio.ParentId,
            studio.Parent,
            studio.Aliases.Select(alias => alias.Alias).ToList(),
            studio.Urls.Select(url => url.Url).ToList()
        );
    }

    private async Task RestoreExcludedStudioFieldsAsync(Studio studio, StudioSnapshot snapshot, IReadOnlySet<string> excludedFields, CancellationToken ct)
    {
        if (excludedFields.Count == 0)
            return;

        if (ShouldExclude(excludedFields, "name")) studio.Name = snapshot.Name;
        if (ShouldExclude(excludedFields, "aliases")) ReplaceStudioAliases(studio, snapshot.Aliases);
        if (ShouldExclude(excludedFields, "urls")) ReplaceStudioUrls(studio, snapshot.Urls);
        if (ShouldExclude(excludedFields, "parent", "parentstudio"))
        {
            studio.ParentId = snapshot.ParentId;
            studio.Parent = snapshot.Parent;
        }
        if (ShouldExclude(excludedFields, "image", "images", "imageblobid"))
            await RestoreBlobAsync(snapshot.ImageBlobId, studio.ImageBlobId, ct, blobId => studio.ImageBlobId = blobId);
    }

    private static TagSnapshot CaptureTagSnapshot(Tag tag)
    {
        return new TagSnapshot(
            tag.Name,
            tag.Description,
            tag.Aliases.Select(alias => alias.Alias).ToList()
        );
    }

    private Task RestoreExcludedTagFieldsAsync(Tag tag, TagSnapshot snapshot, IReadOnlySet<string> excludedFields)
    {
        if (excludedFields.Count == 0)
            return Task.CompletedTask;

        if (ShouldExclude(excludedFields, "name")) tag.Name = snapshot.Name;
        if (ShouldExclude(excludedFields, "description", "details")) tag.Description = snapshot.Description;
        if (ShouldExclude(excludedFields, "aliases")) ReplaceTagAliases(tag, snapshot.Aliases);
        return Task.CompletedTask;
    }

    private async Task RestoreBlobAsync(string? originalBlobId, string? currentBlobId, CancellationToken ct, Action<string?> restore)
    {
        if (!string.Equals(originalBlobId, currentBlobId, StringComparison.Ordinal))
        {
            if (!string.IsNullOrWhiteSpace(currentBlobId))
            {
                try
                {
                    await _blobService.DeleteBlobAsync(currentBlobId, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete replaced blob {BlobId}", currentBlobId);
                }
            }

            restore(originalBlobId);
        }
    }

    private static void ReplacePerformerAliases(Performer performer, IEnumerable<string> aliases)
    {
        performer.Aliases.Clear();
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase))
            performer.Aliases.Add(new PerformerAlias { Alias = alias.Trim(), PerformerId = performer.Id });
    }

    private static void ReplacePerformerUrls(Performer performer, IEnumerable<string> urls)
    {
        performer.Urls.Clear();
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
            performer.Urls.Add(new PerformerUrl { Url = url.Trim(), PerformerId = performer.Id });
    }

    private static void ReplaceStudioAliases(Studio studio, IEnumerable<string> aliases)
    {
        studio.Aliases.Clear();
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase))
            studio.Aliases.Add(new StudioAlias { Alias = alias.Trim(), StudioId = studio.Id });
    }

    private static void ReplaceStudioUrls(Studio studio, IEnumerable<string> urls)
    {
        studio.Urls.Clear();
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
            studio.Urls.Add(new StudioUrl { Url = url.Trim(), StudioId = studio.Id });
    }

    private static void ReplaceTagAliases(Tag tag, IEnumerable<string> aliases)
    {
        tag.Aliases.Clear();
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Distinct(StringComparer.OrdinalIgnoreCase))
            tag.Aliases.Add(new TagAlias { Alias = alias.Trim(), TagId = tag.Id });
    }

    private sealed record PerformerSnapshot(
        string Name,
        string? Disambiguation,
        GenderEnum? Gender,
        DateOnly? Birthdate,
        DateOnly? DeathDate,
        string? Country,
        string? Ethnicity,
        string? EyeColor,
        string? HairColor,
        int? HeightCm,
        string? Measurements,
        string? FakeTits,
        DateOnly? CareerStart,
        DateOnly? CareerEnd,
        string? Tattoos,
        string? Piercings,
        string? ImageBlobId,
        List<string> Aliases,
        List<string> Urls
    );

    private sealed record StudioSnapshot(
        string Name,
        string? ImageBlobId,
        int? ParentId,
        Studio? Parent,
        List<string> Aliases,
        List<string> Urls
    );

    private sealed record TagSnapshot(
        string Name,
        string? Description,
        List<string> Aliases
    );

    public async Task<IReadOnlyList<MetadataServerVideoMatchDto>> SearchVideosAsync(
        Video video,
        string? term,
        string? endpoint,
        VideoMetadataSearchStrategy? strategy,
        CancellationToken ct)
    {
        var effectiveStrategy = strategy ?? (string.IsNullOrWhiteSpace(term)
            ? VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText
            : VideoMetadataSearchStrategy.Text);
        var boxes = ResolveBoxes(endpoint);
        var strictEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var results = new List<MetadataServerVideoMatchDto>();
        var failedEndpoints = 0;
        var videoTitle = term ?? video.Title;
        var videoDuration = GetVideoDurationSeconds(video);
        var localFingerprints = video.Files.SelectMany(f => f.Fingerprints).ToList();
        var searchTerms = BuildVideoSearchTerms(string.IsNullOrWhiteSpace(term) ? video.Title : term);

        foreach (var box in boxes)
        {
            try
            {
                var collectLinkedAndFingerprint = effectiveStrategy == VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText;
                var useRemoteId = effectiveStrategy is VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText
                    or VideoMetadataSearchStrategy.RemoteIdFingerprint
                    or VideoMetadataSearchStrategy.RemoteId;
                var useFingerprint = effectiveStrategy is VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText
                    or VideoMetadataSearchStrategy.RemoteIdFingerprint
                    or VideoMetadataSearchStrategy.Fingerprint;
                var useText = effectiveStrategy is VideoMetadataSearchStrategy.RemoteIdAndFingerprintThenText
                    or VideoMetadataSearchStrategy.Text;
                var existingMatchAdded = false;

                if (useRemoteId)
                {
                    var existingRemoteId = video.RemoteIds.FirstOrDefault(remoteId => EndpointsMatch(remoteId.Endpoint, box.Endpoint));
                    if (existingRemoteId != null)
                    {
                        var existing = await GetVideoMatchAsync(box.Endpoint, existingRemoteId.RemoteId, localFingerprints, ct);
                        if (existing != null)
                        {
                            results.Add(existing);
                            if (!collectLinkedAndFingerprint)
                                continue;
                            existingMatchAdded = true;
                        }
                    }
                }

                if (useFingerprint)
                {
                    var fingerprintQueries = BuildFingerprintQueries(video);
                    if (fingerprintQueries.Count > 0)
                    {
                        var fingerprintCount = fingerprintQueries.Sum(batch => batch.Count);
                        _logger.LogTrace("Querying metadata-server {Endpoint} with {BatchCount} fingerprint batches ({FingerprintCount} fingerprints) for video {VideoId}",
                            box.Endpoint, fingerprintQueries.Count, fingerprintCount, video.Id);

                        var fingerprintResponse = await SendQueryAsync<MetadataServerFindVideosByFingerprintsResponse>(
                            box,
                            FindVideosByFingerprintsQuery,
                            new { fingerprints = fingerprintQueries },
                            ct);

                        var remoteMatches = fingerprintResponse.FindVideosByVideoFingerprints
                            .SelectMany(batch => batch)
                            .GroupBy(remote => remote.Id, StringComparer.OrdinalIgnoreCase)
                            .Select(group => group.First())
                            .ToList();
                        _logger.LogTrace("Metadata server returned {Count} unique fingerprint matches for video {VideoId}", remoteMatches.Count, video.Id);

                        foreach (var remote in remoteMatches)
                        {
                            results.Add(await ToVideoMatchDtoAsync(box, remote, localFingerprints, ct));
                        }
                        if (remoteMatches.Count > 0)
                            continue;

                        if (useText && !existingMatchAdded)
                        {
                            _logger.LogTrace(
                                "Fingerprint lookup returned no matches for video {VideoId}; falling back to {SearchTermCount} text search terms",
                                video.Id,
                                searchTerms.Count);
                        }
                        else
                        {
                            _logger.LogTrace("Fingerprint lookup returned no matches for video {VideoId}", video.Id);
                        }
                    }
                }

                if (collectLinkedAndFingerprint && existingMatchAdded)
                    continue;

                if (!useText || searchTerms.Count == 0)
                    continue;

                foreach (var searchTerm in searchTerms)
                {
                    var searchResponse = await SendQueryAsync<MetadataServerSearchVideoResponse>(box, SearchVideoQuery, new { term = searchTerm }, ct);
                    _logger.LogTrace(
                        "Metadata server text search returned {Count} matches for video {VideoId}",
                        searchResponse.SearchVideo.Count,
                        video.Id);
                    foreach (var remote in searchResponse.SearchVideo)
                    {
                        results.Add(await ToVideoMatchDtoAsync(box, remote, localFingerprints, ct));
                    }

                    if (searchResponse.SearchVideo.Count > 0)
                        break;
                }
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                failedEndpoints++;
                _logger.LogDebug(ex, "Skipping metadata-server video search for {Endpoint}", box.Endpoint);
            }
        }

        if (!strictEndpoint && results.Count == 0 && boxes.Count > 0 && failedEndpoints == boxes.Count)
            _logger.LogWarning("Metadata-server video search failed for all {EndpointCount} configured endpoint(s)", boxes.Count);

        return results
            .GroupBy(match => $"{match.Endpoint}::{match.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(match => string.Equals(match.Title, videoTitle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(match => GetDurationDifference(videoDuration, match.Duration))
            .ThenBy(match => match.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MetadataServerVideoMatchDto?> GetVideoMatchAsync(string endpoint, string videoId, CancellationToken ct)
        => await GetVideoMatchAsync(endpoint, videoId, null, ct);

    private async Task<MetadataServerVideoMatchDto?> GetVideoMatchAsync(string endpoint, string videoId, IReadOnlyCollection<FileFingerprint>? localFingerprints, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var video = await GetRemoteVideoAsync(box, videoId, ct);
        return video == null ? null : await ToVideoMatchDtoAsync(box, video, localFingerprints, ct);
    }

    public async Task<bool> MergeVideoAsync(Video video, string endpoint, string videoId, MetadataServerVideoImportRequestDto? importConfig, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemoteVideoAsync(box, videoId, ct);
        if (remote == null)
            return false;

        await ApplyRemoteVideoAsync(video, box.Endpoint, remote, importConfig, ct);
        return true;
    }

    private async Task<MetadataServerRemotePerformer?> GetRemotePerformerAsync(MetadataServerInstance box, string performerId, CancellationToken ct)
    {
        var response = await SendQueryAsync<MetadataServerFindPerformerResponse>(box, FindPerformerByIdQuery, new { id = performerId }, ct);
        return response.FindPerformer;
    }

    private async Task<MetadataServerRemoteVideo?> GetRemoteVideoAsync(MetadataServerInstance box, string videoId, CancellationToken ct)
    {
        var response = await SendQueryAsync<MetadataServerFindVideoResponse>(box, FindVideoByIdQuery, new { id = videoId }, ct);
        return response.FindVideo;
    }

    private async Task ApplyRemoteVideoAsync(Video video, string endpoint, MetadataServerRemoteVideo remote, MetadataServerVideoImportRequestDto? importConfig, CancellationToken ct)
    {
        var setCoverImage = importConfig?.SetCoverImage ?? true;
        var setTags = importConfig?.SetTags ?? true;
        var setPerformers = importConfig?.SetPerformers ?? true;
        var setStudio = importConfig?.SetStudio ?? true;
        var onlyExistingTags = importConfig?.OnlyExistingTags ?? false;
        var onlyExistingPerformers = importConfig?.OnlyExistingPerformers ?? false;
        var onlyExistingStudio = importConfig?.OnlyExistingStudio ?? false;
        var markOrganized = importConfig?.MarkOrganized ?? false;
        var excludedTagNames = importConfig?.ExcludedTagNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var excludedPerformerNames = importConfig?.ExcludedPerformerNames?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var studioOverride = MatchVideoEntityOverride(importConfig?.StudioOverride, remote.Studio?.Id, remote.Studio?.Name);
        var performerOverrides = importConfig?.PerformerOverrides;
        var tagOverrides = importConfig?.TagOverrides;
        var fieldStrategies = importConfig?.FieldStrategies;
        var defaultScalarStrategy = fieldStrategies == null ? MetadataFieldStrategy.Overwrite : MetadataFieldStrategy.Merge;
        var allowedPerformerGenders = BuildAllowedPerformerGenderSet(importConfig?.PerformerGenders);
        var skipSingleNamePerformers = importConfig?.SkipSingleNamePerformers ?? false;
        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildMetadataSourceKey(endpoint);

        ApplyMetadataStringField(fieldProvenance, "title", remote.Title, GetMetadataFieldStrategy(fieldStrategies, "title", defaultScalarStrategy), value => video.Title = value, video.Title);
        ApplyMetadataStringField(fieldProvenance, "code", remote.Code, GetMetadataFieldStrategy(fieldStrategies, "code", defaultScalarStrategy), value => video.Code = value, video.Code);
        ApplyMetadataStringField(fieldProvenance, "details", remote.Details, GetMetadataFieldStrategy(fieldStrategies, "details", defaultScalarStrategy), value => video.Details = value, video.Details);
        ApplyMetadataStringField(fieldProvenance, "director", remote.Director, GetMetadataFieldStrategy(fieldStrategies, "director", defaultScalarStrategy), value => video.Director = value, video.Director);
        var parsedRemoteDate = ParseDate(remote.Date);
        var dateStrategy = GetMetadataFieldStrategy(fieldStrategies, "date", defaultScalarStrategy);
        var mergedDate = MergeDateField(video.Date, parsedRemoteDate, dateStrategy);
        if (mergedDate.HasValue)
        {
            video.Date = mergedDate;
            if (parsedRemoteDate.HasValue && dateStrategy != MetadataFieldStrategy.Ignore)
                fieldProvenance["date"] = mergedDate.Value.ToString("yyyy-MM-dd");
        }
        if (markOrganized) video.Organized = true;

        var urlsStrategy = GetMetadataFieldStrategy(fieldStrategies, "urls", MetadataFieldStrategy.Merge);
        if (urlsStrategy != MetadataFieldStrategy.Ignore)
        {
            if (urlsStrategy == MetadataFieldStrategy.Overwrite)
                video.Urls.Clear();
            var remoteUrls = remote.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            MergeVideoUrls(video, remoteUrls);
            if (remoteUrls.Count > 0)
                fieldProvenance["urls"] = remoteUrls;
        }

        var studioStrategy = GetMetadataFieldStrategy(fieldStrategies, "studio", fieldStrategies == null ? MetadataFieldStrategy.Overwrite : MetadataFieldStrategy.Merge);
        if (setStudio && studioStrategy != MetadataFieldStrategy.Ignore && remote.Studio != null && (studioStrategy == MetadataFieldStrategy.Overwrite || video.StudioId == null))
        {
            var studio = await ResolveVideoStudioAsync(remote.Studio, endpoint, studioOverride, ct, allowCreate: !onlyExistingStudio);
            if (studio != null)
            {
                video.Studio = studio;
                video.StudioId = studio.Id == 0 ? null : studio.Id;
                fieldProvenance["studio"] = studio.Name;
            }
        }

        var tagsStrategy = GetMetadataFieldStrategy(fieldStrategies, "tags", MetadataFieldStrategy.Merge);
        if (setTags && tagsStrategy != MetadataFieldStrategy.Ignore)
        {
            if (tagsStrategy == MetadataFieldStrategy.Overwrite)
                video.VideoTags.Clear();

            var appliedTagNames = new List<string>();
            var appliedTagIds = new HashSet<int>();

            foreach (var remoteTag in remote.Tags)
            {
                var tagOverride = MatchVideoEntityOverride(tagOverrides, remoteTag.Id, remoteTag.Name);
                if (GetVideoEntityOverrideAction(tagOverride) == VideoEntityOverrideAction.Skip)
                    continue;
                if (tagOverride == null && excludedTagNames != null && excludedTagNames.Contains(remoteTag.Name))
                    continue;
                var tag = await ResolveVideoTagAsync(remoteTag, endpoint, tagOverride, ct, allowCreate: !onlyExistingTags);
                if (tag == null)
                    continue;
                appliedTagNames.Add(tag.Name);
                if (tag.Id > 0)
                    appliedTagIds.Add(tag.Id);
                var alreadyLinkedTag = tag.Id == 0
                    ? video.VideoTags.Any(link => ReferenceEquals(link.Tag, tag))
                    : video.VideoTags.Any(link => link.TagId == tag.Id);
                if (!alreadyLinkedTag)
                {
                    video.VideoTags.Add(new VideoTag { VideoId = video.Id, Tag = tag });
                }

                await _tagProvenanceService.RecordAsync(AffinityHostType.Video, video.Id, tag, sourceKey, sourceRunId: endpoint, cancellationToken: ct);
            }

            // Overwrite clears the manual VideoTags; also drop this source's stale provenance rows for
            // tags no longer applied, or they'd linger as "derived" effective tags (see ApplyTagsAsync).
            if (tagsStrategy == MetadataFieldStrategy.Overwrite)
                await _tagProvenanceService.RemoveHostSourceApplicationsExceptAsync(AffinityHostType.Video, video.Id, sourceKey, appliedTagIds, ct);

            if (appliedTagNames.Count > 0)
                fieldProvenance["tags"] = appliedTagNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        var performersStrategy = GetMetadataFieldStrategy(fieldStrategies, "performers", MetadataFieldStrategy.Merge);
        if (setPerformers && performersStrategy != MetadataFieldStrategy.Ignore)
        {
            if (performersStrategy == MetadataFieldStrategy.Overwrite)
                video.VideoPerformers.Clear();

            var appliedPerformerNames = new List<string>();

            foreach (var remotePerformer in remote.Performers.Select(appearance => appearance.Performer).OfType<MetadataServerRemotePerformer>())
            {
                if (skipSingleNamePerformers && IsSingleNamePerformer(remotePerformer.Name))
                    continue;
                if (!IsPerformerGenderAllowed(remotePerformer.Gender, allowedPerformerGenders))
                    continue;

                var performerOverride = MatchVideoEntityOverride(performerOverrides, remotePerformer.Id, remotePerformer.Name);
                if (GetVideoEntityOverrideAction(performerOverride) == VideoEntityOverrideAction.Skip)
                    continue;
                if (performerOverride == null && excludedPerformerNames != null && remotePerformer.Name != null && excludedPerformerNames.Contains(remotePerformer.Name))
                    continue;
                var performer = await ResolveVideoPerformerAsync(remotePerformer, endpoint, performerOverride, ct, allowCreate: !onlyExistingPerformers);
                if (performer == null)
                    continue;
                appliedPerformerNames.Add(performer.Name);
                var alreadyLinkedPerformer = performer.Id == 0
                    ? video.VideoPerformers.Any(link => ReferenceEquals(link.Performer, performer))
                    : video.VideoPerformers.Any(link => link.PerformerId == performer.Id);
                if (!alreadyLinkedPerformer)
                {
                    video.VideoPerformers.Add(new VideoPerformer { VideoId = video.Id, Performer = performer });
                }
            }

            if (appliedPerformerNames.Count > 0)
                fieldProvenance["performers"] = appliedPerformerNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        // Download video cover image. An auto-generated frame cover (ImageBlobId == null) is always
        // replaceable; an explicitly set cover is preserved unless the caller opted to overwrite it.
        var hasExplicitCover = !string.IsNullOrWhiteSpace(video.ImageBlobId);
        if (setCoverImage && remote.Images.Count > 0 && (!hasExplicitCover || importConfig?.OverwriteExplicitCover == true))
        {
            await _videoCoverService.TryApplyRemoteCoverAsync(video, remote.Images[0].Url, ct);
            fieldProvenance["image_url"] = remote.Images[0].Url;
        }

        var remoteId = video.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
        if (remoteId == null)
        {
            video.RemoteIds.Add(new VideoRemoteId { Endpoint = endpoint, RemoteId = remote.Id, VideoId = video.Id });
        }
        else
        {
            remoteId.RemoteId = remote.Id;
        }
        fieldProvenance["remote_ids"] = new[] { new { endpoint, remoteId = remote.Id } };

        if (fieldProvenance.Count > 0 && _fieldProvenanceService != null)
            await _fieldProvenanceService.RecordManyAsync(AffinityHostType.Video, video.Id, fieldProvenance, sourceKey, sourceRunId: endpoint, cancellationToken: ct);
    }

    // ===== Submissions =====

    private const string SubmitFingerprintMutation = """
        mutation SubmitFingerprint($input: FingerprintSubmission!) {
          submitFingerprint(input: $input)
        }
        """;

    private const string SubmitVideoDraftMutation = """
        mutation SubmitSceneDraft($input: SceneDraftInput!) {
          submitSceneDraft(input: $input) { id }
        }
        """;

    private const string SubmitPerformerDraftMutation = """
        mutation SubmitPerformerDraft($input: PerformerDraftInput!) {
          submitPerformerDraft(input: $input) { id }
        }
        """;

        private const string SubmitStudioDraftMutation = """
                mutation SubmitStudioDraft($input: StudioDraftInput!) {
                    submitStudioDraft(input: $input) { id }
                }
                """;

        private const string SubmitTagDraftMutation = """
                mutation SubmitTagDraft($input: TagDraftInput!) {
                    submitTagDraft(input: $input) { id }
                }
                """;

    public async Task SubmitFingerprintsAsync(Video video, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var videoRemoteId = video.RemoteIds.FirstOrDefault(id =>
            EndpointsMatch(id.Endpoint, endpoint));
        if (videoRemoteId == null)
            throw new InvalidOperationException("Video does not have a remote ID for this endpoint");

        foreach (var file in video.Files)
        {
            foreach (var fingerprint in file.Fingerprints)
            {
                var algorithm = fingerprint.Type.ToUpperInvariant() switch
                {
                    "MD5" => "MD5",
                    "OSHASH" => "OSHASH",
                    "PHASH" => "PHASH",
                    _ => null,
                };
                if (algorithm == null) continue;

                var input = new
                {
                    scene_id = videoRemoteId.RemoteId,
                    fingerprint = new
                    {
                        hash = algorithm == "OSHASH" ? NormalizeOshash(fingerprint.Value) : fingerprint.Value,
                        algorithm,
                        duration = (int)(file is VideoFile vf ? vf.Duration : 0),
                    },
                };

                await SendQueryAsync<object>(box, SubmitFingerprintMutation, new { input }, ct);
            }
        }
    }

    public async Task<string?> SubmitVideoDraftAsync(Video video, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var videoRemoteId = video.RemoteIds.FirstOrDefault(id =>
            EndpointsMatch(id.Endpoint, endpoint));

        var fingerprints = video.Files
            .SelectMany(f => f.Fingerprints.Select(fp => new { fp, file = f }))
            .Where(x => x.fp.Type is "md5" or "oshash" or "phash")
            .Select(x => new
            {
                hash = x.fp.Type.Equals("oshash", StringComparison.OrdinalIgnoreCase) ? NormalizeOshash(x.fp.Value) : x.fp.Value,
                algorithm = x.fp.Type.ToUpperInvariant(),
                duration = (int)(x.file is VideoFile vf ? vf.Duration : 0),
            })
            .ToList();

        var performers = video.VideoPerformers
            .Where(sp => sp.Performer != null)
            .Select(sp =>
            {
                var perfRemoteId = sp.Performer!.RemoteIds
                    .FirstOrDefault(id => EndpointsMatch(id.Endpoint, endpoint));
                return new { name = sp.Performer.Name, id = perfRemoteId?.RemoteId };
            })
            .ToList();

        var tags = video.VideoTags
            .Where(st => st.Tag != null)
            .Select(st =>
            {
                var tagRemoteId = st.Tag!.RemoteIds
                    .FirstOrDefault(id => EndpointsMatch(id.Endpoint, endpoint));
                return new { name = st.Tag.Name, id = tagRemoteId?.RemoteId };
            })
            .ToList();

        object? studio = null;
        if (video.Studio != null)
        {
            var studioRemoteId = video.Studio.RemoteIds
                .FirstOrDefault(id => EndpointsMatch(id.Endpoint, endpoint));
            studio = new { name = video.Studio.Name, id = studioRemoteId?.RemoteId };
        }

        var input = new
        {
            id = videoRemoteId?.RemoteId,
            title = video.Title,
            code = video.Code,
            details = video.Details,
            director = video.Director,
            url = video.Urls.Select(u => u.Url).FirstOrDefault(),
            date = video.Date?.ToString("yyyy-MM-dd"),
            studio,
            performers,
            tags,
            fingerprints,
        };

        var response = await SendQueryAsync<MetadataServerDraftSubmissionResponse>(box, SubmitVideoDraftMutation, new { input }, ct);
        return response.SubmitSceneDraft?.Id;
    }

    public async Task<string?> SubmitPerformerDraftAsync(Performer performer, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var remoteId = performer.RemoteIds.FirstOrDefault(id =>
            string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

        var input = new
        {
            id = remoteId?.RemoteId,
            name = performer.Name,
            disambiguation = performer.Disambiguation,
            aliases = string.Join(", ", performer.Aliases.Select(a => a.Alias)),
            gender = performer.Gender?.ToString().ToUpperInvariant(),
            birthdate = performer.Birthdate?.ToString("yyyy-MM-dd"),
            deathdate = performer.DeathDate?.ToString("yyyy-MM-dd"),
            urls = performer.Urls.Select(u => u.Url).ToList(),
            ethnicity = performer.Ethnicity,
            country = performer.Country,
            eye_color = performer.EyeColor,
            hair_color = performer.HairColor,
            height = performer.HeightCm?.ToString(),
            measurements = performer.Measurements,
            breast_type = performer.FakeTits,
            tattoos = performer.Tattoos,
            piercings = performer.Piercings,
            career_start_year = performer.CareerStart?.Year,
            career_end_year = performer.CareerEnd?.Year,
        };

        var response = await SendQueryAsync<MetadataServerDraftSubmissionResponse>(box, SubmitPerformerDraftMutation, new { input }, ct);
        return response.SubmitPerformerDraft?.Id;
    }

    public async Task<string?> SubmitStudioDraftAsync(Studio studio, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var remoteId = studio.RemoteIds.FirstOrDefault(id =>
            string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

        object? parent = null;
        if (studio.Parent != null)
        {
            var parentRemoteId = studio.Parent.RemoteIds
                .FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            parent = new { name = studio.Parent.Name, id = parentRemoteId?.RemoteId };
        }

        var input = new
        {
            id = remoteId?.RemoteId,
            name = studio.Name,
            aliases = string.Join(", ", studio.Aliases.Select(alias => alias.Alias)),
            urls = studio.Urls.Select(url => url.Url).ToList(),
            parent,
        };

        var response = await SendQueryAsync<MetadataServerDraftSubmissionResponse>(box, SubmitStudioDraftMutation, new { input }, ct);
        return response.SubmitStudioDraft?.Id;
    }

    public async Task<string?> SubmitTagDraftAsync(Tag tag, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var remoteId = tag.RemoteIds.FirstOrDefault(id =>
            string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

        var input = new
        {
            id = remoteId?.RemoteId,
            name = tag.Name,
            description = tag.Description,
            aliases = string.Join(", ", tag.Aliases.Select(alias => alias.Alias)),
        };

        var response = await SendQueryAsync<MetadataServerDraftSubmissionResponse>(box, SubmitTagDraftMutation, new { input }, ct);
        return response.SubmitTagDraft?.Id;
    }

    private sealed record MetadataServerDraftSubmissionResponse(
        MetadataServerDraftIdResult? SubmitSceneDraft = null,
        MetadataServerDraftIdResult? SubmitVideoDraft = null,
        MetadataServerDraftIdResult? SubmitPerformerDraft = null,
        MetadataServerDraftIdResult? SubmitStudioDraft = null,
        MetadataServerDraftIdResult? SubmitTagDraft = null
    );
    private sealed record MetadataServerDraftIdResult(string? Id);

    private async Task<Performer?> ResolveVideoPerformerAsync(
        MetadataServerRemotePerformer remote,
        string endpoint,
        MetadataServerVideoEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetVideoEntityOverrideAction(entityOverride) switch
        {
            VideoEntityOverrideAction.Skip => null,
            VideoEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Performers.FirstOrDefaultAsync(performer => performer.Id == localId, ct),
            VideoEntityOverrideAction.Create => await FindOrCreatePerformerAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreatePerformerAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private async Task<Studio?> ResolveVideoStudioAsync(
        MetadataServerRemoteStudio remote,
        string endpoint,
        MetadataServerVideoEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetVideoEntityOverrideAction(entityOverride) switch
        {
            VideoEntityOverrideAction.Skip => null,
            VideoEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Studios.FirstOrDefaultAsync(studio => studio.Id == localId, ct),
            VideoEntityOverrideAction.Create => await FindOrCreateStudioAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreateStudioAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private async Task<Tag?> ResolveVideoTagAsync(
        MetadataServerRemoteTag remote,
        string endpoint,
        MetadataServerVideoEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetVideoEntityOverrideAction(entityOverride) switch
        {
            VideoEntityOverrideAction.Skip => null,
            VideoEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Tags.FirstOrDefaultAsync(tag => tag.Id == localId, ct),
            VideoEntityOverrideAction.Create => await FindOrCreateTagAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreateTagAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private static MetadataServerVideoEntityOverrideDto? MatchVideoEntityOverride(
        IEnumerable<MetadataServerVideoEntityOverrideDto>? overrides,
        string? remoteId,
        string? name)
    {
        if (overrides == null)
            return null;

        if (!string.IsNullOrWhiteSpace(remoteId))
            return overrides.FirstOrDefault(entityOverride =>
                string.Equals(entityOverride.RemoteId, remoteId, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrWhiteSpace(name)
            ? null
            : overrides.FirstOrDefault(entityOverride =>
                string.Equals(entityOverride.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    private static MetadataServerVideoEntityOverrideDto? MatchVideoEntityOverride(
        MetadataServerVideoEntityOverrideDto? entityOverride,
        string? remoteId,
        string? name)
    {
        if (entityOverride == null)
            return null;

        return MatchVideoEntityOverride(new[] { entityOverride }, remoteId, name);
    }

    private static VideoEntityOverrideAction GetVideoEntityOverrideAction(MetadataServerVideoEntityOverrideDto? entityOverride)
    {
        return entityOverride?.Action.Trim().ToLowerInvariant() switch
        {
            "skip" => VideoEntityOverrideAction.Skip,
            "create" => VideoEntityOverrideAction.Create,
            "existing" => VideoEntityOverrideAction.Existing,
            _ => VideoEntityOverrideAction.Auto,
        };
    }

    private enum VideoEntityOverrideAction
    {
        Auto,
        Skip,
        Create,
        Existing,
    }

    private static Dictionary<string, object?> BuildPerformerMetadataFieldProvenance(MetadataServerRemotePerformer remote, MetadataServerPerformerImportRequestDto? importConfig, string endpoint)
    {
        var fields = new Dictionary<string, object?>();
        var strategies = importConfig?.FieldStrategies ?? [];

        void AddString(string field, string? value)
        {
            if (GetMetadataFieldStrategy(strategies, field, MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore && !string.IsNullOrWhiteSpace(value))
                fields[field] = value.Trim();
        }

        void AddValue(string field, object? value)
        {
            if (GetMetadataFieldStrategy(strategies, field, MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore && value != null)
                fields[field] = value;
        }

        AddString("name", remote.Name);
        AddString("disambiguation", remote.Disambiguation);
        AddString("gender", HumanizeGraphQlEnum(remote.Gender));
        AddString("birthdate", remote.BirthDate);
        AddString("deathDate", remote.DeathDate);
        AddString("country", remote.Country);
        AddString("ethnicity", HumanizeGraphQlEnum(remote.Ethnicity));
        AddString("eyeColor", HumanizeGraphQlEnum(remote.EyeColor));
        AddString("hairColor", HumanizeGraphQlEnum(remote.HairColor));
        if (remote.Height.HasValue && remote.Height.Value > 0)
            AddValue("heightCm", remote.Height.Value);
        AddString("measurements", FormatMeasurements(remote.Measurements));
        AddString("fakeTits", HumanizeGraphQlEnum(remote.BreastType));
        if (remote.CareerStartYear.HasValue && remote.CareerStartYear.Value > 0)
            AddValue("careerStart", remote.CareerStartYear.Value);
        if (remote.CareerEndYear.HasValue && remote.CareerEndYear.Value > 0)
            AddValue("careerEnd", remote.CareerEndYear.Value);
        AddString("tattoos", FormatBodyModifications(remote.Tattoos));
        AddString("piercings", FormatBodyModifications(remote.Piercings));

        var aliases = remote.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (aliases.Count > 0 && GetMetadataFieldStrategy(strategies, "aliases", MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore)
            fields["aliases"] = aliases;

        var urls = remote.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count > 0 && GetMetadataFieldStrategy(strategies, "urls", MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore)
            fields["urls"] = urls;

        var imageUrl = remote.Images.FirstOrDefault()?.Url;
        if (!string.IsNullOrWhiteSpace(imageUrl) && GetMetadataFieldStrategy(strategies, "image", MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore)
            fields["image_url"] = imageUrl.Trim();

        fields["remote_ids"] = new[] { new { endpoint, remoteId = remote.Id } };
        return fields;
    }

    private static Dictionary<string, object?> BuildStudioMetadataFieldProvenance(MetadataServerRemoteStudio remote, MetadataServerStudioImportRequestDto? importConfig, string endpoint)
    {
        var fields = new Dictionary<string, object?>();
        var strategies = importConfig?.FieldStrategies ?? [];

        if (!IsIgnoredImportStrategy(GetImportStrategy(strategies, "name", defaultStrategy: "overwrite")) && !string.IsNullOrWhiteSpace(remote.Name))
            fields["name"] = remote.Name.Trim();

        var aliases = remote.Aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (aliases.Count > 0 && !IsIgnoredImportStrategy(GetImportStrategy(strategies, "aliases", defaultStrategy: "merge")))
            fields["aliases"] = aliases;

        var urls = remote.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count > 0 && !IsIgnoredImportStrategy(GetImportStrategy(strategies, "urls", defaultStrategy: "merge")))
            fields["urls"] = urls;

        var imageUrl = remote.Images.FirstOrDefault()?.Url;
        if (!string.IsNullOrWhiteSpace(imageUrl) && !IsIgnoredImportStrategy(GetImportStrategy(strategies, "image", defaultStrategy: "overwrite")))
            fields["image_url"] = imageUrl.Trim();

        if (remote.Parent != null && !IsIgnoredImportStrategy(GetImportStrategy(strategies, "parent", defaultStrategy: "merge")))
            fields["parent"] = remote.Parent.Name;

        fields["remote_ids"] = new[] { new { endpoint, remoteId = remote.Id } };
        return fields;
    }

    private static Dictionary<string, object?> BuildTagMetadataFieldProvenance(MetadataServerRemoteTag remote, string endpoint)
    {
        var fields = new Dictionary<string, object?>
        {
            ["remote_ids"] = new[] { new { endpoint, remoteId = remote.Id } },
        };

        if (!string.IsNullOrWhiteSpace(remote.Name))
            fields["name"] = remote.Name.Trim();
        if (!string.IsNullOrWhiteSpace(remote.Description))
            fields["description"] = remote.Description.Trim();

        var aliases = CleanStrings(remote.Aliases).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (aliases.Count > 0)
            fields["aliases"] = aliases;

        return fields;
    }

    private void ApplyRemotePerformer(Performer performer, string endpoint, MetadataServerRemotePerformer remote, MetadataServerPerformerImportRequestDto? importConfig = null)
    {
        var aliases = remote.Aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Where(alias => !string.Equals(alias, remote.Name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var urls = remote.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).ToList();

        if (importConfig?.FieldStrategies == null)
        {
            performer.Name = remote.Name.Trim();
            performer.Disambiguation = string.IsNullOrWhiteSpace(remote.Disambiguation) ? performer.Disambiguation : remote.Disambiguation.Trim();
            performer.Gender = MapGender(remote.Gender) ?? performer.Gender;
            performer.Birthdate = ParseDate(remote.BirthDate) ?? performer.Birthdate;
            performer.DeathDate = ParseDate(remote.DeathDate) ?? performer.DeathDate;
            performer.Country = Coalesce(performer.Country, remote.Country);
            performer.Ethnicity = Coalesce(performer.Ethnicity, HumanizeGraphQlEnum(remote.Ethnicity));
            performer.EyeColor = Coalesce(performer.EyeColor, HumanizeGraphQlEnum(remote.EyeColor));
            performer.HairColor = Coalesce(performer.HairColor, HumanizeGraphQlEnum(remote.HairColor));
            performer.HeightCm = remote.Height > 0 ? remote.Height.Value : performer.HeightCm;
            performer.Measurements = Coalesce(performer.Measurements, FormatMeasurements(remote.Measurements));
            performer.FakeTits = Coalesce(performer.FakeTits, HumanizeGraphQlEnum(remote.BreastType));
            performer.CareerStart = remote.CareerStartYear > 0 ? new DateOnly(remote.CareerStartYear.Value, 1, 1) : performer.CareerStart;
            performer.CareerEnd = remote.CareerEndYear > 0 ? new DateOnly(remote.CareerEndYear.Value, 1, 1) : performer.CareerEnd;
            performer.Tattoos = Coalesce(performer.Tattoos, FormatBodyModifications(remote.Tattoos));
            performer.Piercings = Coalesce(performer.Piercings, FormatBodyModifications(remote.Piercings));
            MergeAliases(performer, aliases);
            MergeUrls(performer, urls);
        }
        else
        {
            var fieldStrategies = importConfig.FieldStrategies;

            void ApplyString(string field, string? value, Action<string> setter, string? currentValue)
            {
                var strategy = GetMetadataFieldStrategy(fieldStrategies, field, MetadataFieldStrategy.Merge);
                var merged = MergeStringField(currentValue, value, strategy);
                if (merged != null)
                    setter(merged);
            }

            void ApplyDate(string field, string? value, Action<DateOnly?> setter, DateOnly? currentValue)
            {
                var strategy = GetMetadataFieldStrategy(fieldStrategies, field, MetadataFieldStrategy.Merge);
                var parsed = ParseDate(value);
                var merged = MergeDateField(currentValue, parsed, strategy);
                setter(merged);
            }

            void ApplyInt(string field, int? value, Action<int?> setter, int? currentValue)
            {
                var strategy = GetMetadataFieldStrategy(fieldStrategies, field, MetadataFieldStrategy.Merge);
                if (strategy == MetadataFieldStrategy.Ignore || !value.HasValue || value.Value <= 0)
                    return;
                if (strategy == MetadataFieldStrategy.Overwrite || !currentValue.HasValue)
                    setter(value.Value);
            }

            ApplyString("name", remote.Name, value => performer.Name = value, performer.Name);
            ApplyString("disambiguation", remote.Disambiguation, value => performer.Disambiguation = value, performer.Disambiguation);
            var genderStrategy = GetMetadataFieldStrategy(fieldStrategies, "gender", MetadataFieldStrategy.Merge);
            var remoteGender = MapGender(remote.Gender);
            if (remoteGender.HasValue && genderStrategy != MetadataFieldStrategy.Ignore && (genderStrategy == MetadataFieldStrategy.Overwrite || performer.Gender == null))
                performer.Gender = remoteGender.Value;
            ApplyDate("birthdate", remote.BirthDate, value => performer.Birthdate = value, performer.Birthdate);
            ApplyDate("deathDate", remote.DeathDate, value => performer.DeathDate = value, performer.DeathDate);
            ApplyString("country", remote.Country, value => performer.Country = value, performer.Country);
            ApplyString("ethnicity", HumanizeGraphQlEnum(remote.Ethnicity), value => performer.Ethnicity = value, performer.Ethnicity);
            ApplyString("eyeColor", HumanizeGraphQlEnum(remote.EyeColor), value => performer.EyeColor = value, performer.EyeColor);
            ApplyString("hairColor", HumanizeGraphQlEnum(remote.HairColor), value => performer.HairColor = value, performer.HairColor);
            ApplyInt("heightCm", remote.Height, value => performer.HeightCm = value, performer.HeightCm);
            ApplyString("measurements", FormatMeasurements(remote.Measurements), value => performer.Measurements = value, performer.Measurements);
            ApplyString("fakeTits", HumanizeGraphQlEnum(remote.BreastType), value => performer.FakeTits = value, performer.FakeTits);
            if (GetMetadataFieldStrategy(fieldStrategies, "aliases", MetadataFieldStrategy.Merge) == MetadataFieldStrategy.Overwrite)
                performer.Aliases.Clear();
            if (GetMetadataFieldStrategy(fieldStrategies, "aliases", MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore)
                MergeAliases(performer, aliases);
            if (GetMetadataFieldStrategy(fieldStrategies, "urls", MetadataFieldStrategy.Merge) == MetadataFieldStrategy.Overwrite)
                performer.Urls.Clear();
            if (GetMetadataFieldStrategy(fieldStrategies, "urls", MetadataFieldStrategy.Merge) != MetadataFieldStrategy.Ignore)
                MergeUrls(performer, urls);
            ApplyString("tattoos", FormatBodyModifications(remote.Tattoos), value => performer.Tattoos = value, performer.Tattoos);
            ApplyString("piercings", FormatBodyModifications(remote.Piercings), value => performer.Piercings = value, performer.Piercings);
        }

        // Match on the registrable domain so a remote id already recorded under the pack's source endpoint
        // (e.g. "https://api.theporndb.net/") is refreshed in place rather than duplicated under the
        // configured server endpoint ("https://theporndb.net/graphql").
        var remoteId = performer.RemoteIds.FirstOrDefault(id => EndpointsMatch(id.Endpoint, endpoint));
        if (remoteId == null)
        {
            performer.RemoteIds.Add(new PerformerRemoteId
            {
                Endpoint = endpoint,
                RemoteId = remote.Id,
            });
        }
        else
        {
            remoteId.RemoteId = remote.Id;
        }
    }

    // imageStrategy gates the cover: Ignore skips entirely; Merge keeps an existing cover and only fills
    // when missing (the default, and what the auto face-import relies on); Overwrite replaces any existing
    // cover with the remote one (the user picked "Replace" in the tagger).
    private async Task DownloadPerformerImageAsync(Performer performer, MetadataServerRemotePerformer remote, MetadataFieldStrategy imageStrategy, CancellationToken ct)
    {
        if (imageStrategy == MetadataFieldStrategy.Ignore || remote.Images.Count == 0)
            return;

        if (performer.ImageBlobId != null)
        {
            var existing = await _blobService.GetBlobAsync(performer.ImageBlobId, ct);
            if (existing.HasValue)
            {
                existing.Value.Stream.Dispose();
                // Keep the current cover unless the caller explicitly asked to replace it.
                if (imageStrategy != MetadataFieldStrategy.Overwrite)
                    return;
                await _blobService.DeleteBlobAsync(performer.ImageBlobId, ct);
            }
            else
            {
                _logger.LogWarning("Performer {Name} has ImageBlobId {BlobId} but file is missing â€” re-downloading", performer.Name, performer.ImageBlobId);
            }
            performer.ImageBlobId = null;
        }

        try
        {
            var imageUrl = remote.Images[0].Url;
            using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            performer.ImageBlobId = await _blobService.StoreBlobAsync(stream, contentType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download performer image for {Name}", performer.Name);
        }
    }

    private async Task DownloadStudioImageAsync(Studio studio, MetadataServerRemoteStudio remote, CancellationToken ct, bool overwrite = false)
    {
        if (remote.Images.Count == 0)
            return;

        if (studio.ImageBlobId != null)
        {
            var existing = await _blobService.GetBlobAsync(studio.ImageBlobId, ct);
            if (existing.HasValue)
            {
                existing.Value.Stream.Dispose();
                // Keep the current cover unless the caller explicitly asked to replace it.
                if (!overwrite)
                    return;
                await _blobService.DeleteBlobAsync(studio.ImageBlobId, ct);
            }
            else
            {
                // Blob ID set but file missing on disk â€” clear it and re-download
                _logger.LogWarning("Studio {Name} has ImageBlobId {BlobId} but file is missing â€” re-downloading", studio.Name, studio.ImageBlobId);
            }
            studio.ImageBlobId = null;
        }

        try
        {
            var imageUrl = remote.Images[0].Url;
            using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;

            // Read into memory so we can sniff the real content type
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            var contentType = DetectImageContentType(bytes)
                              ?? response.Content.Headers.ContentType?.MediaType
                              ?? "image/png";

            using var stream = new MemoryStream(bytes);
            studio.ImageBlobId = await _blobService.StoreBlobAsync(stream, contentType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download studio image for {Name}", studio.Name);
        }
    }

    /// <summary>
    /// Detect image content type from magic bytes. Returns null if not recognized.
    /// </summary>
    private static string? DetectImageContentType(byte[] data)
    {
        if (data.Length < 4) return null;

        // PNG: 89 50 4E 47
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            return "image/png";

        // JPEG: FF D8 FF
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return "image/jpeg";

        // GIF: GIF87a or GIF89a
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38)
            return "image/gif";

        // WebP: RIFF....WEBP
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
            return "image/webp";

        // BMP: BM
        if (data[0] == 0x42 && data[1] == 0x4D)
            return "image/bmp";

        // AVIF/HEIF: ....ftypavif or ....ftypheic
        if (data.Length >= 12 && data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70)
        {
            var brand = System.Text.Encoding.ASCII.GetString(data, 8, 4);
            if (brand.StartsWith("avif", StringComparison.OrdinalIgnoreCase)) return "image/avif";
            if (brand.StartsWith("heic", StringComparison.OrdinalIgnoreCase)) return "image/heic";
        }

        if (LooksLikeSvg(data))
            return "image/svg+xml";

        // JPEG XL: FF 0A or 00 00 00 0C 4A 58 4C 20
        if (data[0] == 0xFF && data[1] == 0x0A)
            return "image/jxl";
        if (data.Length >= 8 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C
            && data[4] == 0x4A && data[5] == 0x58 && data[6] == 0x4C && data[7] == 0x20)
            return "image/jxl";

        return null;
    }

    private static bool LooksLikeSvg(byte[] data)
    {
        var head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256));
        var trimmed = head.TrimStart('\uFEFF', ' ', '\t', '\r', '\n');
        return trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (trimmed.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) && trimmed.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    private static void MergeAliases(Performer performer, IEnumerable<string> aliases)
    {
        var existing = performer.Aliases
            .Select(alias => alias.Alias)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var alias in aliases)
        {
            if (existing.Add(alias))
            {
                performer.Aliases.Add(new PerformerAlias { Alias = alias, PerformerId = performer.Id });
            }
        }
    }

    private static void MergeUrls(Performer performer, IEnumerable<string> urls)
    {
        var existing = performer.Urls
            .Select(url => url.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()))
        {
            if (existing.Add(url))
            {
                performer.Urls.Add(new PerformerUrl { Url = url, PerformerId = performer.Id });
            }
        }
    }

    private static void MergeVideoUrls(Video video, IEnumerable<string> urls)
    {
        var existing = video.Urls
            .Select(url => url.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()))
        {
            if (existing.Add(url))
            {
                video.Urls.Add(new VideoUrl { Url = url, VideoId = video.Id });
            }
        }
    }

    private async Task<Performer?> FindOrCreatePerformerAsync(MetadataServerRemotePerformer remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var performer = _db.Performers.Local.FirstOrDefault(entity =>
                entity.Id <= 0
                && _db.Entry(entity).State != EntityState.Deleted
                && entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id))
            ?? await _db.Performers
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct)
            ?? await FindPerformerByIdentityAsync(remote.Name, remote.Disambiguation, ct);

        if (performer == null && !allowCreate)
        {
            return null;
        }

        if (performer == null)
        {
            performer = new Performer
            {
                Name = EntityNameRules.NormalizeCanonicalName(remote.Name),
                Disambiguation = EntityNameRules.NormalizeDisambiguation(remote.Disambiguation),
            };
            _db.Performers.Add(performer);
        }

        ApplyRemotePerformer(performer, endpoint, remote);
        await DownloadPerformerImageAsync(performer, remote, MetadataFieldStrategy.Merge, ct);
        return performer;
    }

    private async Task<Studio?> FindOrCreateStudioAsync(MetadataServerRemoteStudio remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var studio = _db.Studios.Local.FirstOrDefault(entity =>
                entity.Id <= 0
                && _db.Entry(entity).State != EntityState.Deleted
                && entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id))
            ?? await _db.Studios
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct)
            ?? await FindStudioByIdentityAsync(remote.Name, ct);

        if (studio == null && !allowCreate)
        {
            return null;
        }

        if (studio == null)
        {
            studio = new Studio { Name = EntityNameRules.NormalizeCanonicalName(remote.Name) };
            _db.Studios.Add(studio);
        }

        studio.Name = remote.Name.Trim();
        MergeAliases(studio, remote.Aliases);
        MergeUrls(studio, remote.Urls.Select(url => url.Url));
        UpsertRemoteId(studio.RemoteIds, endpoint, remote.Id, id => id.Endpoint, id => id.RemoteId, (id, value) => id.RemoteId = value, value => new StudioRemoteId { Endpoint = endpoint, RemoteId = value });

        // Download studio image
        await DownloadStudioImageAsync(studio, remote, ct);

        // Resolve parent studio
        if (remote.Parent != null && studio.ParentId == null)
        {
            var parent = await _db.Studios
                .Include(s => s.RemoteIds)
                .FirstOrDefaultAsync(s => s.RemoteIds.Any(id => id.Endpoint == endpoint && id.RemoteId == remote.Parent.Id), ct)
                ?? await FindStudioByIdentityAsync(remote.Parent.Name, ct);

            if (parent == null)
            {
                parent = new Studio { Name = EntityNameRules.NormalizeCanonicalName(remote.Parent.Name) };
                parent.RemoteIds.Add(new StudioRemoteId { Endpoint = endpoint, RemoteId = remote.Parent.Id });
                _db.Studios.Add(parent);
            }
            studio.Parent = parent;

            // Download parent studio image if missing
            if (parent.ImageBlobId == null)
            {
                try
                {
                    var box = ResolveBox(endpoint);
                    var parentRemote = await GetRemoteStudioAsync(box, studioId: remote.Parent.Id, studioName: null, ct);
                    if (parentRemote != null)
                        await DownloadStudioImageAsync(parent, parentRemote, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to download parent studio image for {Name}", parent.Name);
                }
            }
        }

        return studio;
    }

    private async Task<Performer?> FindPerformerByIdentityAsync(
        string name,
        string? disambiguation,
        CancellationToken ct)
    {
        var identityKey = EntityNameRules.PerformerIdentityKey(name, disambiguation);
        _performerIdentityIndex ??= (await _db.Performers
                .AsNoTracking()
                .Select(entity => new { entity.Id, entity.Name, entity.Disambiguation })
                .ToListAsync(ct))
            .GroupBy(entity => EntityNameRules.PerformerIdentityKey(entity.Name, entity.Disambiguation), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.Id).Order().ToArray(), StringComparer.Ordinal);

        var trackedIds = _db.ChangeTracker.Entries<Performer>()
            .Where(entry => entry.Entity.Id > 0)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var persistedIds = _performerIdentityIndex.GetValueOrDefault(identityKey, [])
            .Where(id => !trackedIds.Contains(id));
        var local = _db.ChangeTracker.Entries<Performer>()
            .Where(entry => entry.State != EntityState.Deleted
                && EntityNameRules.PerformerIdentityKey(entry.Entity.Name, entry.Entity.Disambiguation) == identityKey)
            .Select(entry => entry.Entity)
            .ToArray();
        var persisted = persistedIds.ToArray();
        if (local.Length + persisted.Length > 1)
            throw new EntityNameConflictException(NameConflictEntityTypes.Performer);
        if (local.Length == 1)
        {
            if (local[0].Id <= 0)
                return local[0];
            return await _db.Performers
                .Include(entity => entity.RemoteIds)
                .Include(entity => entity.Aliases)
                .Include(entity => entity.Urls)
                .SingleAsync(entity => entity.Id == local[0].Id, ct);
        }
        if (persisted.Length == 0)
            return null;

        return await _db.Performers
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .SingleAsync(entity => entity.Id == persisted[0], ct);
    }

    private async Task<Studio?> FindStudioByIdentityAsync(string name, CancellationToken ct)
    {
        var identityKey = EntityNameRules.StudioIdentityKey(name);
        _studioIdentityIndex ??= (await _db.Studios
                .AsNoTracking()
                .Select(entity => new { entity.Id, entity.Name })
                .ToListAsync(ct))
            .GroupBy(entity => EntityNameRules.StudioIdentityKey(entity.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entity => entity.Id).Order().ToArray(), StringComparer.Ordinal);

        var trackedIds = _db.ChangeTracker.Entries<Studio>()
            .Where(entry => entry.Entity.Id > 0)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var persistedIds = _studioIdentityIndex.GetValueOrDefault(identityKey, [])
            .Where(id => !trackedIds.Contains(id));
        var local = _db.ChangeTracker.Entries<Studio>()
            .Where(entry => entry.State != EntityState.Deleted
                && EntityNameRules.StudioIdentityKey(entry.Entity.Name) == identityKey)
            .Select(entry => entry.Entity)
            .ToArray();
        var persisted = persistedIds.ToArray();
        if (local.Length + persisted.Length > 1)
            throw new EntityNameConflictException(NameConflictEntityTypes.Studio);
        if (local.Length == 1)
        {
            if (local[0].Id <= 0)
                return local[0];
            return await _db.Studios
                .Include(entity => entity.RemoteIds)
                .Include(entity => entity.Aliases)
                .Include(entity => entity.Urls)
                .SingleAsync(entity => entity.Id == local[0].Id, ct);
        }
        if (persisted.Length == 0)
            return null;

        return await _db.Studios
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .SingleAsync(entity => entity.Id == persisted[0], ct);
    }

    private async Task<Tag?> FindOrCreateTagAsync(MetadataServerRemoteTag remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var tag = await _db.Tags
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct);
        var matchedByRemoteId = tag != null;
        tag ??= (await RelationNameResolver.ResolveTagsAsync(_db, [remote.Name], ct)).GetValueOrDefault(remote.Name.Trim());

        if (tag == null && !allowCreate)
        {
            return null;
        }

        if (tag == null)
        {
            tag = new Tag { Name = remote.Name };
            _db.Tags.Add(tag);
        }

        if (tag.Id == 0 || matchedByRemoteId)
            tag.Name = remote.Name.Trim();
        tag.Description = Coalesce(tag.Description, remote.Description) ?? tag.Description;
        MergeAliases(tag, remote.Aliases);
        UpsertRemoteId(tag.RemoteIds, endpoint, remote.Id, id => id.Endpoint, id => id.RemoteId, (id, value) => id.RemoteId = value, value => new TagRemoteId { Endpoint = endpoint, RemoteId = value });
        return tag;
    }

    private static void MergeAliases(Studio studio, IEnumerable<string> aliases)
    {
        var existing = studio.Aliases.Select(alias => alias.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).Where(alias => !string.Equals(alias, studio.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing.Add(alias))
                studio.Aliases.Add(new StudioAlias { Alias = alias, StudioId = studio.Id });
        }
    }

    private static void MergeUrls(Studio studio, IEnumerable<string> urls)
    {
        var existing = studio.Urls.Select(url => url.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()))
        {
            if (existing.Add(url))
                studio.Urls.Add(new StudioUrl { Url = url, StudioId = studio.Id });
        }
    }

    private static void MergeAliases(Tag tag, IEnumerable<string>? aliases)
    {
        var existing = tag.Aliases.Select(alias => alias.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in CleanStrings(aliases).Where(alias => !string.Equals(alias, tag.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing.Add(alias))
                tag.Aliases.Add(new TagAlias { Alias = alias, TagId = tag.Id });
        }
    }

    private static IEnumerable<string> CleanStrings(IEnumerable<string>? values)
        => values == null
            ? []
            : values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim());

    private static void UpsertRemoteId<TRemoteId>(ICollection<TRemoteId> collection, string endpoint, string remoteId, Func<TRemoteId, string> getEndpoint, Func<TRemoteId, string> getRemoteId, Action<TRemoteId, string> setRemoteId, Func<string, TRemoteId> create)
    {
        var existing = collection.FirstOrDefault(item => string.Equals(getEndpoint(item), endpoint, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            collection.Add(create(remoteId));
            return;
        }

        if (!string.Equals(getRemoteId(existing), remoteId, StringComparison.OrdinalIgnoreCase))
            setRemoteId(existing, remoteId);
    }

    private IReadOnlyList<MetadataServerInstance> ResolveBoxes(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return _config.Scraping.MetadataServers;

        return [ResolveBox(endpoint)];
    }

    private MetadataServerInstance ResolveBox(string endpoint)
    {
        return _config.Scraping.MetadataServers.FirstOrDefault(box => EndpointsMatch(box.Endpoint, endpoint))
            ?? throw new InvalidOperationException($"Configured metadata-server endpoint not found: {endpoint}");
    }

    private static string NormalizeEndpoint(string? endpoint)
        => endpoint?.Trim().TrimEnd('/') ?? string.Empty;

    private static bool EndpointsMatch(string? a, string? b)
    {
        if (string.Equals(NormalizeEndpoint(a), NormalizeEndpoint(b), StringComparison.OrdinalIgnoreCase))
            return true;

        // Fall back to comparing the registrable (base) domain so a reference pack's source endpoint
        // matches a configured server on the same site even when the host or path differs — e.g. a pack
        // sourced from "https://api.theporndb.net/" matches the configured "https://theporndb.net/graphql".
        var domainA = GetRegistrableDomain(a);
        return domainA.Length > 0 && string.Equals(domainA, GetRegistrableDomain(b), StringComparison.OrdinalIgnoreCase);
    }

    // Reduces an endpoint to its registrable domain: the last two DNS labels of the host, with any
    // leading "www." dropped (api.theporndb.net -> theporndb.net, www.fansdb.cc -> fansdb.cc). This is a
    // deliberate simplification that treats multi-label public suffixes (e.g. ".co.uk") as two labels,
    // which is sufficient for the single-site metadata servers in use here.
    private static string GetRegistrableDomain(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        var trimmed = endpoint.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out uri);

        var host = uri?.Host;
        if (string.IsNullOrEmpty(host))
            return string.Empty;

        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length <= 2 ? host : $"{labels[^2]}.{labels[^1]}";
    }

    private async Task<T> SendQueryAsync<T>(MetadataServerInstance box, string query, object? variables, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, box.Endpoint);
        if (!string.IsNullOrWhiteSpace(box.ApiKey))
            request.Headers.TryAddWithoutValidation("ApiKey", box.ApiKey);

        request.Content = JsonContent.Create(new MetadataServerGraphQlRequest(query, variables), options: _jsonOptions);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var payload = await response.Content.ReadAsStringAsync(ct);

        if (payload.Contains("<!doctype", StringComparison.OrdinalIgnoreCase) || payload.Contains("<html", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid endpoint");

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(payload) ? response.ReasonPhrase ?? "Request failed" : payload);

        var graphQl = JsonSerializer.Deserialize<MetadataServerGraphQlResponse<T>>(payload, _jsonOptions)
            ?? throw new InvalidOperationException("Empty response from server");

        if (graphQl.Errors.Count > 0)
            throw new InvalidOperationException(string.Join("; ", graphQl.Errors.Select(error => error.Message)));

        if (graphQl.Data == null)
            throw new InvalidOperationException("No response from server");

        return graphQl.Data;
    }

    private static MetadataServerInstance ToConfigBox(MetadataServerDto dto) => new()
    {
        Endpoint = dto.Endpoint.Trim(),
        ApiKey = dto.ApiKey?.Trim() ?? string.Empty,
        Name = dto.Name?.Trim() ?? string.Empty,
        MaxRequestsPerMinute = dto.MaxRequestsPerMinute > 0 ? dto.MaxRequestsPerMinute : 240,
    };

    private static MetadataServerPerformerMatchDto ToMatchDto(MetadataServerInstance box, MetadataServerRemotePerformer performer)
    {
        return new MetadataServerPerformerMatchDto(
            Endpoint: box.Endpoint,
            MetadataServerName: string.IsNullOrWhiteSpace(box.Name) ? box.Endpoint : box.Name,
            Id: performer.Id,
            Name: performer.Name,
            Disambiguation: performer.Disambiguation,
            Gender: HumanizeGraphQlEnum(performer.Gender),
            BirthDate: performer.BirthDate,
            Country: performer.Country,
            ImageUrl: performer.Images.FirstOrDefault()?.Url,
            Deleted: performer.Deleted,
            MergedIntoId: performer.MergedIntoId,
            Aliases: performer.Aliases
                .Where(alias => !string.IsNullOrWhiteSpace(alias))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            Urls: performer.Urls
                .Select(url => url.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        );
    }

    private async Task<MetadataServerVideoMatchDto> ToVideoMatchDtoAsync(MetadataServerInstance box, MetadataServerRemoteVideo video, IReadOnlyCollection<FileFingerprint>? localFingerprints, CancellationToken ct)
    {
        var studioCandidate = await BuildStudioCandidateAsync(box.Endpoint, video.Studio, ct);
        var performerCandidates = await BuildPerformerCandidatesAsync(box.Endpoint, video, ct);
        var tagCandidates = await BuildTagCandidatesAsync(box.Endpoint, video, ct);

        // Compute which fingerprint algorithms actually matched between local and remote
        var matchedAlgorithms = new List<string>();
        var matchCount = 0;
        if (localFingerprints != null)
        {
            foreach (var local in localFingerprints)
            {
                var algorithm = local.Type.ToLowerInvariant() switch
                {
                    "md5" => "MD5",
                    "oshash" => "OSHASH",
                    "phash" => "PHASH",
                    _ => null,
                };
                if (algorithm == null || string.IsNullOrWhiteSpace(local.Value)) continue;

                // Count individual remote fingerprint submissions that match this local fingerprint
                foreach (var fp in video.Fingerprints)
                {
                    if (!string.Equals(fp.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase)) continue;

                    bool isMatch;
                    if (string.Equals(algorithm, "PHASH", StringComparison.OrdinalIgnoreCase))
                    {
                        isMatch = ComputePhashHammingDistance(local.Value, fp.Hash) <= PhashMatchThreshold;
                    }
                    else if (string.Equals(algorithm, "OSHASH", StringComparison.OrdinalIgnoreCase))
                    {
                        var normalizedLocal = NormalizeOshash(local.Value);
                        isMatch = string.Equals(NormalizeOshash(fp.Hash), normalizedLocal, StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        isMatch = string.Equals(fp.Hash, local.Value, StringComparison.OrdinalIgnoreCase);
                    }

                    if (isMatch) matchCount++;
                }

                if (!matchedAlgorithms.Contains(algorithm, StringComparer.OrdinalIgnoreCase))
                {
                    // Check if any remote fingerprint of this algorithm type matched
                    bool anyMatch = video.Fingerprints.Any(fp =>
                        string.Equals(fp.Algorithm, algorithm, StringComparison.OrdinalIgnoreCase) &&
                        (string.Equals(algorithm, "PHASH", StringComparison.OrdinalIgnoreCase)
                            ? ComputePhashHammingDistance(local.Value, fp.Hash) <= PhashMatchThreshold
                            : string.Equals(algorithm, "OSHASH", StringComparison.OrdinalIgnoreCase)
                                ? string.Equals(NormalizeOshash(fp.Hash), NormalizeOshash(local.Value), StringComparison.OrdinalIgnoreCase)
                                : string.Equals(fp.Hash, local.Value, StringComparison.OrdinalIgnoreCase)));
                    if (anyMatch)
                        matchedAlgorithms.Add(algorithm);
                }
            }
        }

        return new MetadataServerVideoMatchDto(
            Endpoint: box.Endpoint,
            MetadataServerName: string.IsNullOrWhiteSpace(box.Name) ? box.Endpoint : box.Name,
            Id: video.Id,
            Title: video.Title,
            Code: video.Code,
            Date: video.Date,
            Director: video.Director,
            Details: video.Details,
            StudioName: video.Studio?.Name,
            ImageUrl: video.Images.FirstOrDefault()?.Url,
            Duration: video.Duration,
            PerformerNames: performerCandidates.Select(candidate => candidate.Name).ToList(),
            TagNames: tagCandidates.Select(candidate => candidate.Name).ToList(),
            Urls: video.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FingerprintAlgorithms: matchedAlgorithms,
            MatchCount: matchCount,
            Fingerprints: video.Fingerprints.Select(fp => new MetadataServerFingerprintDto(fp.Algorithm, fp.Hash, fp.Duration)).ToList(),
            StudioCandidate: studioCandidate,
            PerformerCandidates: performerCandidates,
            TagCandidates: tagCandidates
        );
    }

    private async Task<MetadataServerEntityCandidateDto?> BuildStudioCandidateAsync(string endpoint, MetadataServerRemoteStudio? remoteStudio, CancellationToken ct)
    {
        if (remoteStudio == null || string.IsNullOrWhiteSpace(remoteStudio.Name))
            return null;

        var localId = await _db.Studios
            .Where(studio => studio.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remoteStudio.Id))
            .Select(studio => (int?)studio.Id)
            .FirstOrDefaultAsync(ct);
        localId ??= (await FindStudioByIdentityAsync(remoteStudio.Name, ct))?.Id;

        return new MetadataServerEntityCandidateDto(remoteStudio.Id, remoteStudio.Name.Trim(), localId.HasValue, localId);
    }

    private async Task<List<MetadataServerEntityCandidateDto>> BuildPerformerCandidatesAsync(string endpoint, MetadataServerRemoteVideo video, CancellationToken ct)
    {
        var remotePerformers = video.Performers
            .Select(appearance => appearance.Performer)
            .OfType<MetadataServerRemotePerformer>()
            .Where(performer => !string.IsNullOrWhiteSpace(performer.Name))
            .GroupBy(performer => performer.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (remotePerformers.Count == 0)
            return [];

        var remoteIds = remotePerformers.Select(performer => performer.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var matchedByRemoteId = remoteIds.Count == 0
            ? []
            : await _db.Performers
                .SelectMany(performer => performer.RemoteIds
                    .Where(remoteId => remoteId.Endpoint == endpoint && remoteIds.Contains(remoteId.RemoteId))
                    .Select(remoteId => new { remoteId.RemoteId, PerformerId = performer.Id }))
                .ToListAsync(ct);

        var idsByRemoteId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByRemoteId)
        {
            idsByRemoteId.TryAdd(match.RemoteId, match.PerformerId);
        }

        var result = new List<MetadataServerEntityCandidateDto>(remotePerformers.Count);
        foreach (var remotePerformer in remotePerformers)
        {
            var name = remotePerformer.Name.Trim();
            var exists = idsByRemoteId.TryGetValue(remotePerformer.Id, out var localId);
            if (!exists)
            {
                var identityMatch = await FindPerformerByIdentityAsync(name, remotePerformer.Disambiguation, ct);
                localId = identityMatch?.Id ?? 0;
                exists = identityMatch != null;
            }
            result.Add(new MetadataServerEntityCandidateDto(
                remotePerformer.Id,
                name,
                exists,
                exists ? localId : null,
                EntityNameRules.NormalizeDisambiguation(remotePerformer.Disambiguation)));
        }
        return result;
    }

    private async Task<List<MetadataServerEntityCandidateDto>> BuildTagCandidatesAsync(string endpoint, MetadataServerRemoteVideo video, CancellationToken ct)
    {
        var remoteTags = video.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (remoteTags.Count == 0)
            return [];

        var remoteIds = remoteTags.Select(tag => tag.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remoteNames = remoteTags.Select(tag => tag.Name.Trim()).Distinct(TagNameRules.NamespaceComparer).ToList();

        var matchedByRemoteId = remoteIds.Count == 0
            ? []
            : await _db.Tags
                .SelectMany(tag => tag.RemoteIds
                    .Where(remoteId => remoteId.Endpoint == endpoint && remoteIds.Contains(remoteId.RemoteId))
                    .Select(remoteId => new { remoteId.RemoteId, TagId = tag.Id }))
                .ToListAsync(ct);

        var matchedByName = await RelationNameResolver.ResolveTagsAsync(_db, remoteNames, ct);

        var idsByRemoteId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByRemoteId)
        {
            idsByRemoteId.TryAdd(match.RemoteId, match.TagId);
        }

        return remoteTags.Select(remoteTag =>
        {
            var name = remoteTag.Name.Trim();
            var exists = idsByRemoteId.TryGetValue(remoteTag.Id, out var localId);
            if (!exists && matchedByName.TryGetValue(name, out var nameMatch))
            {
                exists = true;
                localId = nameMatch.Id;
            }
            return new MetadataServerEntityCandidateDto(remoteTag.Id, name, exists, exists ? localId : null);
        }).ToList();
    }

    private static List<List<object>> BuildFingerprintQueries(Video video)
    {
        var query = video.Files
            .SelectMany(file => file.Fingerprints)
            .Select(CreateFingerprintQuery)
            .Where(item => item != null)
            .Select(item => item!)
            .DistinctBy(item => $"{item.Algorithm}:{item.Hash}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => GetFingerprintQueryPriority(item.Algorithm))
            .ToList();

        return query.Count == 0
            ? []
            :
            [
                query.Select(item => (object)new { algorithm = item.Algorithm, hash = item.Hash }).ToList()
            ];
    }

    private static FingerprintQueryEntry? CreateFingerprintQuery(FileFingerprint fingerprint)
    {
        var algorithm = fingerprint.Type.ToLowerInvariant() switch
        {
            "md5" => "MD5",
            "oshash" => "OSHASH",
            "phash" => "PHASH",
            _ => null,
        };

        if (algorithm == null || string.IsNullOrWhiteSpace(fingerprint.Value))
            return null;

        var hash = algorithm == "OSHASH" ? NormalizeOshash(fingerprint.Value) : fingerprint.Value;
        return new FingerprintQueryEntry(algorithm, hash);
    }

    private static int GetFingerprintQueryPriority(string algorithm) => algorithm switch
    {
        "MD5" => 0,
        "OSHASH" => 1,
        "PHASH" => 2,
        _ => 3,
    };

    private sealed record FingerprintQueryEntry(string Algorithm, string Hash);

    /// <summary>
    /// Normalize oshash to zero-padded 16-char hex to match Go's fmt.Sprintf("%016x") format.
    /// Go always produces 16-character zero-padded hex strings for oshash values.
    /// </summary>
    private static string NormalizeOshash(string value) => value.PadLeft(16, '0');

    private static IReadOnlyList<string> BuildVideoSearchTerms(string? term)
    {
        if (string.IsNullOrWhiteSpace(term))
            return [];

        var terms = new List<string>();

        static string NormalizeWhitespace(string value)
            => WhitespaceRegex.Replace(value, " ").Trim();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return;

            var normalized = NormalizeWhitespace(candidate);
            if (normalized.Length == 0)
                return;

            if (!terms.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                terms.Add(normalized);
        }

        var trimmed = NormalizeWhitespace(term);
        Add(trimmed);

        var withoutIndex = NormalizeWhitespace(LeadingVideoIndexRegex.Replace(trimmed, string.Empty));
        Add(withoutIndex);

        var dashedParts = trimmed.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (dashedParts.Length == 2 && dashedParts[0].All(char.IsDigit))
            Add(dashedParts[1]);

        return terms;
    }

    private static int? GetVideoDurationSeconds(Video video)
    {
        var maxDuration = video.Files.Select(file => file.Duration).DefaultIfEmpty().Max();
        return maxDuration > 0 ? (int?)Math.Round(maxDuration) : null;
    }

    /// <summary>
    /// Computes the hamming distance between two phash hex strings.
    /// Returns int.MaxValue if either string is invalid.
    /// </summary>
    internal static int ComputePhashHammingDistance(string? hex1, string? hex2)
    {
        if (string.IsNullOrWhiteSpace(hex1) || string.IsNullOrWhiteSpace(hex2))
            return int.MaxValue;

        if (!ulong.TryParse(hex1, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash1) ||
            !ulong.TryParse(hex2, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash2))
            return int.MaxValue;

        return BitOperations.PopCount(hash1 ^ hash2);
    }

    private static int GetDurationDifference(int? localDuration, int? remoteDuration)
    {
        if (!localDuration.HasValue && !remoteDuration.HasValue) return 0;
        if (!localDuration.HasValue || !remoteDuration.HasValue) return int.MaxValue;
        return Math.Abs(localDuration.Value - remoteDuration.Value);
    }

    private static string? Coalesce(string? currentValue, string? nextValue)
    {
        return string.IsNullOrWhiteSpace(nextValue) ? currentValue : nextValue.Trim();
    }

    private static string BuildMetadataSourceKey(string? endpoint)
        => string.IsNullOrWhiteSpace(endpoint) ? "metadata" : $"metadata:{endpoint.Trim()}";

    private enum MetadataFieldStrategy
    {
        Ignore,
        Merge,
        Overwrite,
    }

    private static MetadataFieldStrategy GetMetadataFieldStrategy(IReadOnlyDictionary<string, string>? strategies, string field, MetadataFieldStrategy fallback)
    {
        if (strategies == null || !strategies.TryGetValue(field, out var value))
            return fallback;

        return value?.Trim().ToLowerInvariant() switch
        {
            "ignore" or "skip" => MetadataFieldStrategy.Ignore,
            "overwrite" or "replace" => MetadataFieldStrategy.Overwrite,
            _ => MetadataFieldStrategy.Merge,
        };
    }

    private static void ApplyMetadataStringField(
        Dictionary<string, object?> fieldProvenance,
        string fieldKey,
        string? remoteValue,
        MetadataFieldStrategy strategy,
        Action<string?> apply,
        string? currentValue)
    {
        var mergedValue = MergeStringField(currentValue, remoteValue, strategy);
        if (mergedValue != null)
            apply(mergedValue);

        if (strategy != MetadataFieldStrategy.Ignore && !string.IsNullOrWhiteSpace(remoteValue))
            fieldProvenance[fieldKey] = remoteValue.Trim();
    }

    private static string? MergeStringField(string? currentValue, string? nextValue, MetadataFieldStrategy strategy)
    {
        if (strategy == MetadataFieldStrategy.Ignore || string.IsNullOrWhiteSpace(nextValue))
            return currentValue;

        if (strategy == MetadataFieldStrategy.Merge && !string.IsNullOrWhiteSpace(currentValue))
            return currentValue;

        return nextValue.Trim();
    }

    private static DateOnly? MergeDateField(DateOnly? currentValue, DateOnly? nextValue, MetadataFieldStrategy strategy)
    {
        if (strategy == MetadataFieldStrategy.Ignore || !nextValue.HasValue)
            return currentValue;

        if (strategy == MetadataFieldStrategy.Merge && currentValue.HasValue)
            return currentValue;

        return nextValue;
    }

    private static HashSet<string>? BuildAllowedPerformerGenderSet(IReadOnlyCollection<string>? values)
    {
        if (values == null || values.Count == 0)
            return null;

        return values
            .Select(NormalizeGenderKey)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsPerformerGenderAllowed(string? gender, HashSet<string>? allowedGenders)
    {
        if (allowedGenders == null)
            return true;

        var key = string.IsNullOrWhiteSpace(gender) ? "UNKNOWN" : NormalizeGenderKey(gender);
        return allowedGenders.Contains(key);
    }

    private static string NormalizeGenderKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return Regex.Replace(value, "[^A-Za-z0-9]", string.Empty).ToUpperInvariant();
    }

    private static bool IsSingleNamePerformer(string? value)
    {
        var name = value?.Trim();
        return !string.IsNullOrWhiteSpace(name) && !WhitespaceRegex.IsMatch(name);
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static string? FormatMeasurements(MetadataServerRemoteMeasurements? measurements)
    {
        if (measurements == null || measurements.BandSize is null or <= 0 || string.IsNullOrWhiteSpace(measurements.CupSize) || measurements.Waist is null or <= 0 || measurements.Hip is null or <= 0)
            return null;

        return $"{measurements.BandSize}{measurements.CupSize}-{measurements.Waist}-{measurements.Hip}";
    }

    private static string? FormatBodyModifications(List<MetadataServerBodyModification>? items)
    {
        if (items == null || items.Count == 0)
            return null;

        return string.Join("; ", items.Select(item => string.IsNullOrWhiteSpace(item.Description) ? item.Location : $"{item.Location}, {item.Description}"));
    }

    private static GenderEnum? MapGender(string? value)
    {
        return value?.ToUpperInvariant() switch
        {
            "MALE" => GenderEnum.Male,
            "FEMALE" => GenderEnum.Female,
            "TRANSGENDER_MALE" => GenderEnum.TransgenderMale,
            "TRANSGENDER_FEMALE" => GenderEnum.TransgenderFemale,
            "INTERSEX" => GenderEnum.Intersex,
            "NON_BINARY" => GenderEnum.NonBinary,
            _ => null,
        };
    }

    private static string? HumanizeGraphQlEnum(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join(' ', parts.Select(part => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(part.ToLowerInvariant())));
    }

    private static string MapValidationError(Exception ex)
    {
        var message = ex.Message.ToLowerInvariant();
        return message switch
        {
            _ when message.Contains("doctype") || message.Contains("<html") => "Invalid endpoint",
            _ when message.Contains("connection refused") || message.Contains("no such host") || message.Contains("name or service not known") => "No response from server",
            _ when message.Contains("signature is invalid") || message.Contains("unauthorized") || message.Contains("forbidden") => "Invalid or expired API key.",
            _ when message.Contains("illegal base64 data") || message.Contains("token contains an invalid number of segments") || message.Contains("malformed") => "Malformed API key.",
            _ => $"Unknown error: {ex.Message}",
        };
    }

    private sealed record MetadataServerGraphQlRequest(string Query, object? Variables);

    private sealed record MetadataServerGraphQlResponse<T>
    {
        public T? Data { get; init; }
        public List<MetadataServerGraphQlError> Errors { get; init; } = [];
    }

    private sealed record MetadataServerGraphQlError(string Message);

    private sealed record MetadataServerMeQueryResponse(MetadataServerMeUser? Me);

    private sealed record MetadataServerMeUser(string Name);

    private sealed record MetadataServerSearchPerformerResponse(List<MetadataServerRemotePerformer> SearchPerformer);

    private sealed record MetadataServerFindPerformerResponse(MetadataServerRemotePerformer? FindPerformer);

    private sealed record MetadataServerSearchVideoResponse(List<MetadataServerRemoteVideo> SearchVideo);

    private sealed record MetadataServerFindVideoResponse(MetadataServerRemoteVideo? FindVideo);

    private sealed record MetadataServerSearchStudioResponse(List<MetadataServerRemoteStudio> SearchStudio);

    private sealed record MetadataServerFindStudioResponse(MetadataServerRemoteStudio? FindStudio);

    private sealed record MetadataServerFindTagResponse(MetadataServerRemoteTag? FindTag);

    private sealed record MetadataServerFindVideosByFingerprintsResponse(List<List<MetadataServerRemoteVideo>> FindVideosByVideoFingerprints);

    private sealed record MetadataServerRemotePerformer(
        string Id,
        string Name,
        string? Disambiguation,
        List<string> Aliases,
        string? Gender,
        bool Deleted,
        [property: JsonPropertyName("merged_into_id")] string? MergedIntoId,
        List<MetadataServerRemoteUrl> Urls,
        List<MetadataServerRemoteImage> Images,
        [property: JsonPropertyName("birth_date")] string? BirthDate,
        [property: JsonPropertyName("death_date")] string? DeathDate,
        string? Ethnicity,
        string? Country,
        [property: JsonPropertyName("eye_color")] string? EyeColor,
        [property: JsonPropertyName("hair_color")] string? HairColor,
        int? Height,
        MetadataServerRemoteMeasurements? Measurements,
        [property: JsonPropertyName("breast_type")] string? BreastType,
        [property: JsonPropertyName("career_start_year")] int? CareerStartYear,
        [property: JsonPropertyName("career_end_year")] int? CareerEndYear,
        List<MetadataServerBodyModification>? Tattoos,
        List<MetadataServerBodyModification>? Piercings
    );

    private sealed record MetadataServerRemoteUrl(string Url);

    private sealed record MetadataServerRemoteImage(string Url);

    private sealed record MetadataServerRemoteVideo(
        string Id,
        string? Title,
        string? Code,
        string? Details,
        string? Director,
        int? Duration,
        string? Date,
        List<MetadataServerRemoteUrl> Urls,
        List<MetadataServerRemoteImage> Images,
        MetadataServerRemoteStudio? Studio,
        List<MetadataServerRemoteTag> Tags,
        List<MetadataServerRemotePerformerAppearance> Performers,
        List<MetadataServerRemoteFingerprint> Fingerprints
    );

    private sealed record MetadataServerRemotePerformerAppearance(MetadataServerRemotePerformer? Performer);

    private sealed record MetadataServerRemoteStudio(string Id, string Name, List<string> Aliases, List<MetadataServerRemoteUrl> Urls, List<MetadataServerRemoteImage> Images, MetadataServerRemoteStudioParent? Parent);
    private sealed record MetadataServerRemoteStudioParent(string Id, string Name);

    private sealed record MetadataServerRemoteTag(string Id, string Name, string? Description, List<string> Aliases);

    private sealed record MetadataServerRemoteFingerprint(string Algorithm, string Hash, int? Duration);

    private sealed record MetadataServerRemoteMeasurements(
        [property: JsonPropertyName("band_size")] int? BandSize,
        [property: JsonPropertyName("cup_size")] string? CupSize,
        int? Waist,
        int? Hip
    );

    private sealed record MetadataServerBodyModification(string Location, string? Description);
}
