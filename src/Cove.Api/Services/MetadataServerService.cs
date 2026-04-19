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
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Services;

public class MetadataServerService
{
    private static readonly Regex LeadingSceneIndexRegex = new(@"^\s*(?:scene\s+)?(?:\[\s*\d+\s*\]|\(\s*\d+\s*\)|\d+)\s*(?:[-â€“â€”:._)\]]\s*)+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
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

        private const string FindStudioByIdQuery = """
query FindStudioByID($id: ID!) {
  findStudio(id: $id) {
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

        private const string FingerprintFragment = """
fragment FingerprintFields on Fingerprint {
    algorithm
    hash
    duration
}
""";

        private const string SceneFragment = """
fragment SceneFields on Scene {
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

        private const string SearchSceneQuery = """
query SearchScene($term: String!) {
    searchScene(term: $term) {
        ... SceneFields
    }
}
""" + SceneFragment;

        private const string FindSceneByIdQuery = """
query FindSceneByID($id: ID!) {
    findScene(id: $id) {
        ... SceneFields
    }
}
""" + SceneFragment;

        private const string FindScenesByFingerprintsQuery = """
query FindScenesBySceneFingerprints($fingerprints: [[FingerprintQueryInput!]!]!) {
    findScenesBySceneFingerprints(fingerprints: $fingerprints) {
        ... SceneFields
    }
}
""" + SceneFragment;

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
    private readonly ILogger<MetadataServerService> _logger;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public MetadataServerService(HttpClient httpClient, CoveConfiguration config, CoveContext db, IBlobService blobService, ILogger<MetadataServerService> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _db = db;
        _blobService = blobService;
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

        foreach (var box in boxes)
        {
            try
            {
                var response = await SendQueryAsync<MetadataServerSearchPerformerResponse>(box, SearchPerformerQuery, new { term }, ct);
                results.AddRange(response.SearchPerformer.Select(remote => ToMatchDto(box, remote)));
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                _logger.LogWarning(ex, "Skipping metadata-server performer search for {Endpoint}", box.Endpoint);
            }
        }

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

    public async Task<bool> MergePerformerAsync(Performer performer, string endpoint, string performerId, CancellationToken ct)
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

        ApplyRemotePerformer(performer, box.Endpoint, remote);
        await DownloadPerformerImageAsync(performer, remote, ct);
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

        foreach (var box in boxes)
        {
            try
            {
                var response = await SendQueryAsync<MetadataServerSearchStudioResponse>(box, SearchStudioQuery, new { term }, ct);
                results.AddRange(response.SearchStudio.Select(remote => ToStudioMatchDto(box, remote)));
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                _logger.LogWarning(ex, "Skipping metadata-server studio search for {Endpoint}", box.Endpoint);
            }
        }

        return results
            .OrderByDescending(m => string.Equals(m.Name, term, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<bool> MergeStudioAsync(Studio studio, string endpoint, string studioId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemoteStudioAsync(box, studioId, ct);
        if (remote == null)
            return false;

        studio.Name = remote.Name.Trim();
        MergeAliases(studio, remote.Aliases);
        MergeUrls(studio, remote.Urls.Select(u => u.Url));
        UpsertRemoteId(studio.RemoteIds, box.Endpoint, remote.Id, id => id.Endpoint, id => id.RemoteId, (id, value) => id.RemoteId = value, value => new StudioRemoteId { Endpoint = box.Endpoint, RemoteId = value });
        await DownloadStudioImageAsync(studio, remote, ct);

        // Resolve parent studio
        if (remote.Parent != null && studio.ParentId == null)
        {
            var parent = await _db.Studios
                .Include(s => s.RemoteIds)
                .FirstOrDefaultAsync(s => s.RemoteIds.Any(id => id.Endpoint == box.Endpoint && id.RemoteId == remote.Parent.Id), ct)
                ?? await _db.Studios
                    .Include(s => s.RemoteIds)
                    .FirstOrDefaultAsync(s => s.Name == remote.Parent.Name, ct);

            if (parent == null)
            {
                parent = new Studio { Name = remote.Parent.Name };
                parent.RemoteIds.Add(new StudioRemoteId { Endpoint = box.Endpoint, RemoteId = remote.Parent.Id });
                _db.Studios.Add(parent);
            }
            studio.Parent = parent;
        }

        return true;
    }

    private async Task<MetadataServerRemoteStudio?> GetRemoteStudioAsync(MetadataServerInstance box, string studioId, CancellationToken ct)
    {
        try
        {
            var response = await SendQueryAsync<MetadataServerFindStudioResponse>(box, FindStudioByIdQuery, new { id = studioId }, ct);
            return response.FindStudio;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch studio {StudioId} from {Endpoint}", studioId, box.Endpoint);
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

    public async Task<IReadOnlyList<MetadataServerSceneMatchDto>> SearchScenesAsync(Scene scene, string? term, string? endpoint, CancellationToken ct)
    {
        var boxes = ResolveBoxes(endpoint);
        var strictEndpoint = !string.IsNullOrWhiteSpace(endpoint);
        var results = new List<MetadataServerSceneMatchDto>();
        var sceneTitle = term ?? scene.Title;
        var sceneDuration = GetSceneDurationSeconds(scene);
        var localFingerprints = scene.Files.SelectMany(f => f.Fingerprints).ToList();
        var searchTerms = BuildSceneSearchTerms(string.IsNullOrWhiteSpace(term) ? scene.Title : term);

        foreach (var box in boxes)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(term))
                {
                    var existingRemoteId = scene.RemoteIds.FirstOrDefault(remoteId => string.Equals(remoteId.Endpoint, box.Endpoint, StringComparison.OrdinalIgnoreCase));
                    if (existingRemoteId != null)
                    {
                        var existing = await GetSceneMatchAsync(box.Endpoint, existingRemoteId.RemoteId, ct);
                        if (existing != null)
                        {
                            results.Add(existing);
                            continue;
                        }
                    }

                    var fingerprintQuery = BuildFingerprintQuery(scene);
                    if (fingerprintQuery.Count > 0)
                    {
                        _logger.LogDebug("Querying metadata-server {Endpoint} with {Count} fingerprints for scene {SceneId}",
                            box.Endpoint, fingerprintQuery.Count, scene.Id);

                        var fingerprintResponse = await SendQueryAsync<MetadataServerFindScenesByFingerprintsResponse>(
                            box,
                            FindScenesByFingerprintsQuery,
                            new { fingerprints = new[] { fingerprintQuery } },
                            ct);

                        var matchCount = fingerprintResponse.FindScenesBySceneFingerprints.Sum(batch => batch.Count);
                        _logger.LogDebug("Metadata server returned {Count} fingerprint matches for scene {SceneId}", matchCount, scene.Id);

                        foreach (var remote in fingerprintResponse.FindScenesBySceneFingerprints.SelectMany(batch => batch))
                        {
                            results.Add(await ToSceneMatchDtoAsync(box, remote, localFingerprints, ct));
                        }
                        if (fingerprintResponse.FindScenesBySceneFingerprints.Any(batch => batch.Count > 0))
                            continue;
                    }
                }

                if (searchTerms.Count == 0)
                    continue;

                foreach (var searchTerm in searchTerms)
                {
                    var searchResponse = await SendQueryAsync<MetadataServerSearchSceneResponse>(box, SearchSceneQuery, new { term = searchTerm }, ct);
                    foreach (var remote in searchResponse.SearchScene)
                    {
                        results.Add(await ToSceneMatchDtoAsync(box, remote, localFingerprints, ct));
                    }

                    if (searchResponse.SearchScene.Count > 0)
                        break;
                }
            }
            catch (Exception ex) when (!strictEndpoint)
            {
                _logger.LogWarning(ex, "Skipping metadata-server scene search for {Endpoint}", box.Endpoint);
            }
        }

        return results
            .GroupBy(match => $"{match.Endpoint}::{match.Id}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderByDescending(match => string.Equals(match.Title, sceneTitle, StringComparison.OrdinalIgnoreCase))
            .ThenBy(match => GetDurationDifference(sceneDuration, match.Duration))
            .ThenBy(match => match.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.MetadataServerName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<MetadataServerSceneMatchDto?> GetSceneMatchAsync(string endpoint, string sceneId, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var scene = await GetRemoteSceneAsync(box, sceneId, ct);
        return scene == null ? null : await ToSceneMatchDtoAsync(box, scene, null, ct);
    }

    public async Task<bool> MergeSceneAsync(Scene scene, string endpoint, string sceneId, MetadataServerSceneImportRequestDto? importConfig, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);
        var remote = await GetRemoteSceneAsync(box, sceneId, ct);
        if (remote == null)
            return false;

        await ApplyRemoteSceneAsync(scene, box.Endpoint, remote, importConfig, ct);
        return true;
    }

    private async Task<MetadataServerRemotePerformer?> GetRemotePerformerAsync(MetadataServerInstance box, string performerId, CancellationToken ct)
    {
        var response = await SendQueryAsync<MetadataServerFindPerformerResponse>(box, FindPerformerByIdQuery, new { id = performerId }, ct);
        return response.FindPerformer;
    }

    private async Task<MetadataServerRemoteScene?> GetRemoteSceneAsync(MetadataServerInstance box, string sceneId, CancellationToken ct)
    {
        var response = await SendQueryAsync<MetadataServerFindSceneResponse>(box, FindSceneByIdQuery, new { id = sceneId }, ct);
        return response.FindScene;
    }

    private async Task ApplyRemoteSceneAsync(Scene scene, string endpoint, MetadataServerRemoteScene remote, MetadataServerSceneImportRequestDto? importConfig, CancellationToken ct)
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
        var studioOverride = MatchSceneEntityOverride(importConfig?.StudioOverride, remote.Studio?.Id, remote.Studio?.Name);
        var performerOverrides = importConfig?.PerformerOverrides;
        var tagOverrides = importConfig?.TagOverrides;

        scene.Title = Coalesce(scene.Title, remote.Title) ?? scene.Title;
        scene.Code = Coalesce(scene.Code, remote.Code) ?? scene.Code;
        scene.Details = Coalesce(scene.Details, remote.Details) ?? scene.Details;
        scene.Director = Coalesce(scene.Director, remote.Director) ?? scene.Director;
        scene.Date = ParseDate(remote.Date) ?? scene.Date;
        if (markOrganized) scene.Organized = true;

        MergeSceneUrls(scene, remote.Urls.Select(url => url.Url));

        if (setStudio && remote.Studio != null)
        {
            var studio = await ResolveSceneStudioAsync(remote.Studio, endpoint, studioOverride, ct, allowCreate: !onlyExistingStudio);
            if (studio != null)
            {
                scene.Studio = studio;
                scene.StudioId = studio.Id == 0 ? null : studio.Id;
            }
        }

        if (setTags)
        {
            foreach (var remoteTag in remote.Tags)
            {
                var tagOverride = MatchSceneEntityOverride(tagOverrides, remoteTag.Id, remoteTag.Name);
                if (GetSceneEntityOverrideAction(tagOverride) == SceneEntityOverrideAction.Skip)
                    continue;
                if (tagOverride == null && excludedTagNames != null && excludedTagNames.Contains(remoteTag.Name))
                    continue;
                var tag = await ResolveSceneTagAsync(remoteTag, endpoint, tagOverride, ct, allowCreate: !onlyExistingTags);
                if (tag == null)
                    continue;
                var alreadyLinkedTag = tag.Id == 0
                    ? scene.SceneTags.Any(link => ReferenceEquals(link.Tag, tag))
                    : scene.SceneTags.Any(link => link.TagId == tag.Id);
                if (!alreadyLinkedTag)
                {
                    scene.SceneTags.Add(new SceneTag { SceneId = scene.Id, Tag = tag });
                }
            }
        }

        if (setPerformers)
        {
            foreach (var remotePerformer in remote.Performers.Select(appearance => appearance.Performer).OfType<MetadataServerRemotePerformer>())
            {
                var performerOverride = MatchSceneEntityOverride(performerOverrides, remotePerformer.Id, remotePerformer.Name);
                if (GetSceneEntityOverrideAction(performerOverride) == SceneEntityOverrideAction.Skip)
                    continue;
                if (performerOverride == null && excludedPerformerNames != null && remotePerformer.Name != null && excludedPerformerNames.Contains(remotePerformer.Name))
                    continue;
                var performer = await ResolveScenePerformerAsync(remotePerformer, endpoint, performerOverride, ct, allowCreate: !onlyExistingPerformers);
                if (performer == null)
                    continue;
                var alreadyLinkedPerformer = performer.Id == 0
                    ? scene.ScenePerformers.Any(link => ReferenceEquals(link.Performer, performer))
                    : scene.ScenePerformers.Any(link => link.PerformerId == performer.Id);
                if (!alreadyLinkedPerformer)
                {
                    scene.ScenePerformers.Add(new ScenePerformer { SceneId = scene.Id, Performer = performer });
                }
            }
        }

        // Download scene cover image
        if (setCoverImage && remote.Images.Count > 0)
        {
            await DownloadSceneCoverAsync(scene.Id, remote.Images[0].Url, ct);
        }

        var remoteId = scene.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
        if (remoteId == null)
        {
            scene.RemoteIds.Add(new SceneRemoteId { Endpoint = endpoint, RemoteId = remote.Id, SceneId = scene.Id });
        }
        else
        {
            remoteId.RemoteId = remote.Id;
        }
    }

    // ===== Submissions =====

    private const string SubmitFingerprintMutation = """
        mutation SubmitFingerprint($input: FingerprintSubmission!) {
          submitFingerprint(input: $input)
        }
        """;

    private const string SubmitSceneDraftMutation = """
        mutation SubmitSceneDraft($input: SceneDraftInput!) {
          submitSceneDraft(input: $input) { id }
        }
        """;

    private const string SubmitPerformerDraftMutation = """
        mutation SubmitPerformerDraft($input: PerformerDraftInput!) {
          submitPerformerDraft(input: $input) { id }
        }
        """;

    public async Task SubmitFingerprintsAsync(Scene scene, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var sceneRemoteId = scene.RemoteIds.FirstOrDefault(id =>
            string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
        if (sceneRemoteId == null)
            throw new InvalidOperationException("Scene does not have a remote ID for this endpoint");

        foreach (var file in scene.Files)
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
                    scene_id = sceneRemoteId.RemoteId,
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

    public async Task<string?> SubmitSceneDraftAsync(Scene scene, string endpoint, CancellationToken ct)
    {
        var box = ResolveBox(endpoint);

        var sceneRemoteId = scene.RemoteIds.FirstOrDefault(id =>
            string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

        var fingerprints = scene.Files
            .SelectMany(f => f.Fingerprints.Select(fp => new { fp, file = f }))
            .Where(x => x.fp.Type is "md5" or "oshash" or "phash")
            .Select(x => new
            {
                hash = x.fp.Type.Equals("oshash", StringComparison.OrdinalIgnoreCase) ? NormalizeOshash(x.fp.Value) : x.fp.Value,
                algorithm = x.fp.Type.ToUpperInvariant(),
                duration = (int)(x.file is VideoFile vf ? vf.Duration : 0),
            })
            .ToList();

        var performers = scene.ScenePerformers
            .Where(sp => sp.Performer != null)
            .Select(sp =>
            {
                var perfRemoteId = sp.Performer!.RemoteIds
                    .FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
                return new { name = sp.Performer.Name, id = perfRemoteId?.RemoteId };
            })
            .ToList();

        var tags = scene.SceneTags
            .Where(st => st.Tag != null)
            .Select(st =>
            {
                var tagRemoteId = st.Tag!.RemoteIds
                    .FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
                return new { name = st.Tag.Name, id = tagRemoteId?.RemoteId };
            })
            .ToList();

        object? studio = null;
        if (scene.Studio != null)
        {
            var studioRemoteId = scene.Studio.RemoteIds
                .FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
            studio = new { name = scene.Studio.Name, id = studioRemoteId?.RemoteId };
        }

        var input = new
        {
            id = sceneRemoteId?.RemoteId,
            title = scene.Title,
            code = scene.Code,
            details = scene.Details,
            director = scene.Director,
            urls = scene.Urls.Select(u => u.Url).ToList(),
            date = scene.Date?.ToString("yyyy-MM-dd"),
            studio,
            performers,
            tags,
            fingerprints,
        };

        var response = await SendQueryAsync<MetadataServerDraftSubmissionResponse>(box, SubmitSceneDraftMutation, new { input }, ct);
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

    private sealed record MetadataServerDraftSubmissionResponse(
        MetadataServerDraftIdResult? SubmitSceneDraft = null,
        MetadataServerDraftIdResult? SubmitPerformerDraft = null
    );
    private sealed record MetadataServerDraftIdResult(string? Id);

    private async Task<Performer?> ResolveScenePerformerAsync(
        MetadataServerRemotePerformer remote,
        string endpoint,
        MetadataServerSceneEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetSceneEntityOverrideAction(entityOverride) switch
        {
            SceneEntityOverrideAction.Skip => null,
            SceneEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Performers.FirstOrDefaultAsync(performer => performer.Id == localId, ct),
            SceneEntityOverrideAction.Create => await FindOrCreatePerformerAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreatePerformerAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private async Task<Studio?> ResolveSceneStudioAsync(
        MetadataServerRemoteStudio remote,
        string endpoint,
        MetadataServerSceneEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetSceneEntityOverrideAction(entityOverride) switch
        {
            SceneEntityOverrideAction.Skip => null,
            SceneEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Studios.FirstOrDefaultAsync(studio => studio.Id == localId, ct),
            SceneEntityOverrideAction.Create => await FindOrCreateStudioAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreateStudioAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private async Task<Tag?> ResolveSceneTagAsync(
        MetadataServerRemoteTag remote,
        string endpoint,
        MetadataServerSceneEntityOverrideDto? entityOverride,
        CancellationToken ct,
        bool allowCreate)
    {
        return GetSceneEntityOverrideAction(entityOverride) switch
        {
            SceneEntityOverrideAction.Skip => null,
            SceneEntityOverrideAction.Existing when entityOverride?.LocalId is int localId => await _db.Tags.FirstOrDefaultAsync(tag => tag.Id == localId, ct),
            SceneEntityOverrideAction.Create => await FindOrCreateTagAsync(remote, endpoint, ct, allowCreate: true),
            _ => await FindOrCreateTagAsync(remote, endpoint, ct, allowCreate: allowCreate),
        };
    }

    private static MetadataServerSceneEntityOverrideDto? MatchSceneEntityOverride(
        IEnumerable<MetadataServerSceneEntityOverrideDto>? overrides,
        string? remoteId,
        string? name)
    {
        if (overrides == null)
            return null;

        return overrides.FirstOrDefault(entityOverride =>
            (!string.IsNullOrWhiteSpace(remoteId) && string.Equals(entityOverride.RemoteId, remoteId, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrWhiteSpace(name) && string.Equals(entityOverride.Name, name, StringComparison.OrdinalIgnoreCase)));
    }

    private static MetadataServerSceneEntityOverrideDto? MatchSceneEntityOverride(
        MetadataServerSceneEntityOverrideDto? entityOverride,
        string? remoteId,
        string? name)
    {
        if (entityOverride == null)
            return null;

        return MatchSceneEntityOverride(new[] { entityOverride }, remoteId, name);
    }

    private static SceneEntityOverrideAction GetSceneEntityOverrideAction(MetadataServerSceneEntityOverrideDto? entityOverride)
    {
        return entityOverride?.Action.Trim().ToLowerInvariant() switch
        {
            "skip" => SceneEntityOverrideAction.Skip,
            "create" => SceneEntityOverrideAction.Create,
            "existing" => SceneEntityOverrideAction.Existing,
            _ => SceneEntityOverrideAction.Auto,
        };
    }

    private enum SceneEntityOverrideAction
    {
        Auto,
        Skip,
        Create,
        Existing,
    }

    private void ApplyRemotePerformer(Performer performer, string endpoint, MetadataServerRemotePerformer remote)
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

        var aliases = remote.Aliases
            .Where(alias => !string.IsNullOrWhiteSpace(alias))
            .Select(alias => alias.Trim())
            .Where(alias => !string.Equals(alias, remote.Name, StringComparison.OrdinalIgnoreCase));
        MergeAliases(performer, aliases);
        MergeUrls(performer, remote.Urls.Select(url => url.Url));

        var remoteId = performer.RemoteIds.FirstOrDefault(id => string.Equals(id.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));
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

    private async Task DownloadPerformerImageAsync(Performer performer, MetadataServerRemotePerformer remote, CancellationToken ct)
    {
        if (remote.Images.Count == 0)
            return;

        // If blob exists on disk already, skip download
        if (performer.ImageBlobId != null)
        {
            var existing = await _blobService.GetBlobAsync(performer.ImageBlobId, ct);
            if (existing.HasValue)
            {
                existing.Value.Stream.Dispose();
                return;
            }
            _logger.LogWarning("Performer {Name} has ImageBlobId {BlobId} but file is missing â€” re-downloading", performer.Name, performer.ImageBlobId);
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

    private async Task DownloadStudioImageAsync(Studio studio, MetadataServerRemoteStudio remote, CancellationToken ct)
    {
        if (remote.Images.Count == 0)
            return;

        // If blob exists on disk already, skip download
        if (studio.ImageBlobId != null)
        {
            var existing = await _blobService.GetBlobAsync(studio.ImageBlobId, ct);
            if (existing.HasValue)
            {
                existing.Value.Stream.Dispose();
                return;
            }
            // Blob ID set but file missing on disk â€” clear it and re-download
            _logger.LogWarning("Studio {Name} has ImageBlobId {BlobId} but file is missing â€” re-downloading", studio.Name, studio.ImageBlobId);
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

        // SVG: starts with < (XML)
        if (data[0] == 0x3C)
        {
            var head = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256));
            if (head.Contains("<svg", StringComparison.OrdinalIgnoreCase))
                return "image/svg+xml";
        }

        // JPEG XL: FF 0A or 00 00 00 0C 4A 58 4C 20
        if (data[0] == 0xFF && data[1] == 0x0A)
            return "image/jxl";
        if (data.Length >= 8 && data[0] == 0x00 && data[1] == 0x00 && data[2] == 0x00 && data[3] == 0x0C
            && data[4] == 0x4A && data[5] == 0x58 && data[6] == 0x4C && data[7] == 0x20)
            return "image/jxl";

        return null;
    }

    private async Task DownloadSceneCoverAsync(int sceneId, string imageUrl, CancellationToken ct)
    {
        try
        {
            var generatedPath = _config.GeneratedPath;
            if (string.IsNullOrEmpty(generatedPath)) return;

            var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(BitConverter.GetBytes(sceneId)));
            var thumbPath = Path.Combine(generatedPath, "screenshots", hash[..2], $"{sceneId}.jpg");

            using var response = await _httpClient.GetAsync(imageUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return;

            var dir = Path.GetDirectoryName(thumbPath)!;
            Directory.CreateDirectory(dir);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            await using var fileStream = new FileStream(thumbPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await stream.CopyToAsync(fileStream, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download scene cover for scene {SceneId}", sceneId);
        }
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

    private static void MergeSceneUrls(Scene scene, IEnumerable<string> urls)
    {
        var existing = scene.Urls
            .Select(url => url.Url)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Select(url => url.Trim()))
        {
            if (existing.Add(url))
            {
                scene.Urls.Add(new SceneUrl { Url = url, SceneId = scene.Id });
            }
        }
    }

    private async Task<Performer?> FindOrCreatePerformerAsync(MetadataServerRemotePerformer remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var performer = await _db.Performers
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct)
            ?? await _db.Performers
                .Include(entity => entity.RemoteIds)
                .Include(entity => entity.Aliases)
                .Include(entity => entity.Urls)
                .FirstOrDefaultAsync(entity => entity.Name == remote.Name, ct);

        if (performer == null && !allowCreate)
        {
            return null;
        }

        if (performer == null)
        {
            performer = new Performer { Name = remote.Name };
            _db.Performers.Add(performer);
        }

        ApplyRemotePerformer(performer, endpoint, remote);
        await DownloadPerformerImageAsync(performer, remote, ct);
        return performer;
    }

    private async Task<Studio?> FindOrCreateStudioAsync(MetadataServerRemoteStudio remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var studio = await _db.Studios
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .Include(entity => entity.Urls)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct)
            ?? await _db.Studios
                .Include(entity => entity.RemoteIds)
                .Include(entity => entity.Aliases)
                .Include(entity => entity.Urls)
                .FirstOrDefaultAsync(entity => entity.Name == remote.Name, ct);

        if (studio == null && !allowCreate)
        {
            return null;
        }

        if (studio == null)
        {
            studio = new Studio { Name = remote.Name };
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
                ?? await _db.Studios
                    .Include(s => s.RemoteIds)
                    .FirstOrDefaultAsync(s => s.Name == remote.Parent.Name, ct);

            if (parent == null)
            {
                parent = new Studio { Name = remote.Parent.Name };
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
                    var parentRemote = await GetRemoteStudioAsync(box, remote.Parent.Id, ct);
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

    private async Task<Tag?> FindOrCreateTagAsync(MetadataServerRemoteTag remote, string endpoint, CancellationToken ct, bool allowCreate = true)
    {
        var tag = await _db.Tags
            .Include(entity => entity.RemoteIds)
            .Include(entity => entity.Aliases)
            .FirstOrDefaultAsync(entity => entity.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remote.Id), ct)
            ?? await _db.Tags
                .Include(entity => entity.RemoteIds)
                .Include(entity => entity.Aliases)
                .FirstOrDefaultAsync(entity => entity.Name == remote.Name, ct);

        if (tag == null && !allowCreate)
        {
            return null;
        }

        if (tag == null)
        {
            tag = new Tag { Name = remote.Name };
            _db.Tags.Add(tag);
        }

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

    private static void MergeAliases(Tag tag, IEnumerable<string> aliases)
    {
        var existing = tag.Aliases.Select(alias => alias.Alias).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in aliases.Where(alias => !string.IsNullOrWhiteSpace(alias)).Select(alias => alias.Trim()).Where(alias => !string.Equals(alias, tag.Name, StringComparison.OrdinalIgnoreCase)))
        {
            if (existing.Add(alias))
                tag.Aliases.Add(new TagAlias { Alias = alias, TagId = tag.Id });
        }
    }

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
        return _config.Scraping.MetadataServers.FirstOrDefault(box => string.Equals(box.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Configured metadata-server endpoint not found: {endpoint}");
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

    private async Task<MetadataServerSceneMatchDto> ToSceneMatchDtoAsync(MetadataServerInstance box, MetadataServerRemoteScene scene, IReadOnlyCollection<FileFingerprint>? localFingerprints, CancellationToken ct)
    {
        var studioCandidate = await BuildStudioCandidateAsync(box.Endpoint, scene.Studio, ct);
        var performerCandidates = await BuildPerformerCandidatesAsync(box.Endpoint, scene, ct);
        var tagCandidates = await BuildTagCandidatesAsync(box.Endpoint, scene, ct);

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
                foreach (var fp in scene.Fingerprints)
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
                    bool anyMatch = scene.Fingerprints.Any(fp =>
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

        return new MetadataServerSceneMatchDto(
            Endpoint: box.Endpoint,
            MetadataServerName: string.IsNullOrWhiteSpace(box.Name) ? box.Endpoint : box.Name,
            Id: scene.Id,
            Title: scene.Title,
            Code: scene.Code,
            Date: scene.Date,
            Director: scene.Director,
            Details: scene.Details,
            StudioName: scene.Studio?.Name,
            ImageUrl: scene.Images.FirstOrDefault()?.Url,
            Duration: scene.Duration,
            PerformerNames: performerCandidates.Select(candidate => candidate.Name).ToList(),
            TagNames: tagCandidates.Select(candidate => candidate.Name).ToList(),
            Urls: scene.Urls.Select(url => url.Url).Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            FingerprintAlgorithms: matchedAlgorithms,
            MatchCount: matchCount,
            Fingerprints: scene.Fingerprints.Select(fp => new MetadataServerFingerprintDto(fp.Algorithm, fp.Hash, fp.Duration)).ToList(),
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
            .Where(studio => studio.Name == remoteStudio.Name || studio.RemoteIds.Any(remoteId => remoteId.Endpoint == endpoint && remoteId.RemoteId == remoteStudio.Id))
            .Select(studio => (int?)studio.Id)
            .FirstOrDefaultAsync(ct);

        return new MetadataServerEntityCandidateDto(remoteStudio.Id, remoteStudio.Name.Trim(), localId.HasValue, localId);
    }

    private async Task<List<MetadataServerEntityCandidateDto>> BuildPerformerCandidatesAsync(string endpoint, MetadataServerRemoteScene scene, CancellationToken ct)
    {
        var remotePerformers = scene.Performers
            .Select(appearance => appearance.Performer)
            .OfType<MetadataServerRemotePerformer>()
            .Where(performer => !string.IsNullOrWhiteSpace(performer.Name))
            .GroupBy(performer => performer.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (remotePerformers.Count == 0)
            return [];

        var remoteIds = remotePerformers.Select(performer => performer.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remoteNames = remotePerformers.Select(performer => performer.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var matchedByRemoteId = remoteIds.Count == 0
            ? []
            : await _db.Performers
                .SelectMany(performer => performer.RemoteIds
                    .Where(remoteId => remoteId.Endpoint == endpoint && remoteIds.Contains(remoteId.RemoteId))
                    .Select(remoteId => new { remoteId.RemoteId, PerformerId = performer.Id }))
                .ToListAsync(ct);

        var matchedByName = remoteNames.Count == 0
            ? []
            : await _db.Performers
                .Where(performer => remoteNames.Contains(performer.Name))
                .Select(performer => new { performer.Name, performer.Id })
                .ToListAsync(ct);

        var idsByRemoteId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByRemoteId)
        {
            idsByRemoteId.TryAdd(match.RemoteId, match.PerformerId);
        }

        var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByName)
        {
            idsByName.TryAdd(match.Name, match.Id);
        }

        return remotePerformers.Select(remotePerformer =>
        {
            var name = remotePerformer.Name.Trim();
            var exists = idsByRemoteId.TryGetValue(remotePerformer.Id, out var localId) || idsByName.TryGetValue(name, out localId);
            return new MetadataServerEntityCandidateDto(remotePerformer.Id, name, exists, exists ? localId : null);
        }).ToList();
    }

    private async Task<List<MetadataServerEntityCandidateDto>> BuildTagCandidatesAsync(string endpoint, MetadataServerRemoteScene scene, CancellationToken ct)
    {
        var remoteTags = scene.Tags
            .Where(tag => !string.IsNullOrWhiteSpace(tag.Name))
            .GroupBy(tag => tag.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (remoteTags.Count == 0)
            return [];

        var remoteIds = remoteTags.Select(tag => tag.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var remoteNames = remoteTags.Select(tag => tag.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var matchedByRemoteId = remoteIds.Count == 0
            ? []
            : await _db.Tags
                .SelectMany(tag => tag.RemoteIds
                    .Where(remoteId => remoteId.Endpoint == endpoint && remoteIds.Contains(remoteId.RemoteId))
                    .Select(remoteId => new { remoteId.RemoteId, TagId = tag.Id }))
                .ToListAsync(ct);

        var matchedByName = remoteNames.Count == 0
            ? []
            : await _db.Tags
                .Where(tag => remoteNames.Contains(tag.Name))
                .Select(tag => new { tag.Name, tag.Id })
                .ToListAsync(ct);

        var idsByRemoteId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByRemoteId)
        {
            idsByRemoteId.TryAdd(match.RemoteId, match.TagId);
        }

        var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var match in matchedByName)
        {
            idsByName.TryAdd(match.Name, match.Id);
        }

        return remoteTags.Select(remoteTag =>
        {
            var name = remoteTag.Name.Trim();
            var exists = idsByRemoteId.TryGetValue(remoteTag.Id, out var localId) || idsByName.TryGetValue(name, out localId);
            return new MetadataServerEntityCandidateDto(remoteTag.Id, name, exists, exists ? localId : null);
        }).ToList();
    }

    private static List<object> BuildFingerprintQuery(Scene scene)
    {
        return scene.Files
            .SelectMany(file => file.Fingerprints)
            .Select(fp =>
            {
                var algorithm = fp.Type.ToLowerInvariant() switch
                {
                    "md5" => "MD5",
                    "oshash" => "OSHASH",
                    "phash" => "PHASH",
                    _ => null,
                };

                if (algorithm == null || string.IsNullOrWhiteSpace(fp.Value))
                    return null;

                // Normalize oshash to zero-padded 16-char hex to match Go's fmt.Sprintf("%016x")
                var hash = algorithm == "OSHASH" ? NormalizeOshash(fp.Value) : fp.Value;

                return new { algorithm, hash } as object;
            })
            .Where(item => item != null)
            .Cast<object>()
            .ToList();
    }

    /// <summary>
    /// Normalize oshash to zero-padded 16-char hex to match Go's fmt.Sprintf("%016x") format.
    /// Go always produces 16-character zero-padded hex strings for oshash values.
    /// </summary>
    private static string NormalizeOshash(string value) => value.PadLeft(16, '0');

    private static IReadOnlyList<string> BuildSceneSearchTerms(string? term)
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

        var withoutIndex = NormalizeWhitespace(LeadingSceneIndexRegex.Replace(trimmed, string.Empty));
        Add(withoutIndex);

        var dashedParts = trimmed.Split(" - ", 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (dashedParts.Length == 2 && dashedParts[0].All(char.IsDigit))
            Add(dashedParts[1]);

        return terms;
    }

    private static int? GetSceneDurationSeconds(Scene scene)
    {
        var maxDuration = scene.Files.Select(file => file.Duration).DefaultIfEmpty().Max();
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

    private sealed record MetadataServerSearchSceneResponse(List<MetadataServerRemoteScene> SearchScene);

    private sealed record MetadataServerFindSceneResponse(MetadataServerRemoteScene? FindScene);

    private sealed record MetadataServerSearchStudioResponse(List<MetadataServerRemoteStudio> SearchStudio);

    private sealed record MetadataServerFindStudioResponse(MetadataServerRemoteStudio? FindStudio);

    private sealed record MetadataServerFindScenesByFingerprintsResponse(List<List<MetadataServerRemoteScene>> FindScenesBySceneFingerprints);

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

    private sealed record MetadataServerRemoteScene(
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