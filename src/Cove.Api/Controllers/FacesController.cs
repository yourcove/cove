using System.Diagnostics;
using System.Data;
using System.Linq.Expressions;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Data.Services;
using Cove.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.FacesRead)]
public class FacesController(
    CoveContext db,
    IEmbeddingService embeddingService,
    IBlobService blobService,
    FacePerformerPropagationService facePerformerPropagationService,
    IEnumerable<IFaceLifecycleParticipant> faceLifecycleParticipants,
    ILogger<FacesController> logger,
    IEnumerable<IFaceSuggester>? faceSuggesters = null,
    ICurrentPrincipalAccessor? principalAccessor = null,
    IFieldProvenanceService? fieldProvenanceService = null,
    IEnumerable<IFaceSuggestionDecisionHandler>? faceSuggestionDecisionHandlers = null,
    IExtensionServiceExchange? serviceExchange = null,
    IFaceTopSuggestionMaintenance? suggestionMaintenance = null,
    IReferencePerformerImporter? referencePerformerImporter = null,
    BulkDeletionJobService? bulkDeletionJobService = null,
    BulkEntityDeletionService? bulkEntityDeletionService = null) : ControllerBase
{
    private const int TopSuggestionCandidateCount = 3;

    // Extensions live in isolated DI containers since the extensions-runtime redesign and surface
    // their face contributions through the cross-extension service exchange. The host-injected
    // enumerables only carry host registrations (e.g. the EmptyFaceSuggester stub), so merge in the
    // exchange-published implementations before using them. Without this the face list/detail show
    // no suggestions because the real AI.Faces suggester never runs.
    private IReadOnlyList<IFaceSuggester> ActiveSuggesters()
        => (faceSuggesters ?? Enumerable.Empty<IFaceSuggester>())
            .Concat(serviceExchange?.GetAll<IFaceSuggester>() ?? [])
            .Where(suggester => suggester is not EmptyFaceSuggester)
            .Distinct()
            .ToArray();

    private IReadOnlyList<IFaceSuggestionDecisionHandler> ActiveSuggestionDecisionHandlers()
        => (faceSuggestionDecisionHandlers ?? Enumerable.Empty<IFaceSuggestionDecisionHandler>())
            .Concat(serviceExchange?.GetAll<IFaceSuggestionDecisionHandler>() ?? [])
            .Distinct()
            .ToArray();

    private IReadOnlyList<IFaceLifecycleParticipant> ActiveLifecycleParticipants()
        => faceLifecycleParticipants
            .Concat(serviceExchange?.GetAll<IFaceLifecycleParticipant>() ?? [])
            .Distinct()
            .ToArray();

    // The provider that owns per-track face evidence, if one is installed. Occurrence editing is
    // meaningless without it, so the endpoints below report it as unimplemented rather than guessing.
    private IFaceOccurrenceEditor? ActiveOccurrenceEditor()
        => (serviceExchange?.GetAll<IFaceOccurrenceEditor>() ?? []).FirstOrDefault();

    private static string? NormalizeOccurrenceHostType(string? hostType)
        => hostType?.Trim().ToLowerInvariant() switch
        {
            "video" or "videos" => "video",
            "image" or "images" => "image",
            _ => null,
        };

    [HttpGet("capabilities")]
    public ActionResult<FaceCapabilitiesDto> GetCapabilities()
        => Ok(new FaceCapabilitiesDto(
            CanEditOccurrences: ActiveOccurrenceEditor() is not null,
            CanSuggest: ActiveSuggesters().Count > 0));

    /// <summary>The face's separate tracked appearances on one host, for the occurrence-split UI.</summary>
    [HttpGet("{id:int}/host-tracks")]
    public async Task<ActionResult<IReadOnlyList<FaceHostTrackDto>>> GetHostTracks(
        int id,
        [FromQuery] string? hostType,
        [FromQuery] int hostId,
        CancellationToken cancellationToken)
    {
        var editor = ActiveOccurrenceEditor();
        if (editor is null)
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "No installed extension provides face occurrence editing." });

        var normalizedHostType = NormalizeOccurrenceHostType(hostType);
        if (normalizedHostType is null || hostId <= 0)
            return BadRequest(new { error = "A hostType of 'video' or 'image' and a positive hostId are required." });

        if (!await db.Faces.AnyAsync(face => face.Id == id, cancellationToken))
            return NotFound();

        return Ok(await editor.GetHostTracksAsync(id, normalizedHostType, hostId, cancellationToken));
    }

    /// <summary>
    /// Moves selected appearances of a face on one host onto another face. The finer-grained counterpart
    /// to <see cref="MarkNotPresent"/>, which can only reject a face from a whole host.
    /// </summary>
    [HttpPost("{id:int}/split")]
    [RequiresPermission(Permissions.FacesWrite)]
    public async Task<ActionResult<FaceOccurrenceSplitResultDto>> Split(
        int id,
        [FromBody] FaceSplitDto request,
        CancellationToken cancellationToken)
    {
        var editor = ActiveOccurrenceEditor();
        if (editor is null)
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "No installed extension provides face occurrence editing." });

        var normalizedHostType = NormalizeOccurrenceHostType(request?.HostType);
        if (normalizedHostType is null || request is null || request.HostId <= 0 || request.GroupKeys is not { Count: > 0 })
            return BadRequest(new { error = "A hostType of 'video' or 'image', a positive hostId, and at least one groupKey are required." });

        var result = await editor.SplitAsync(id, normalizedHostType, request.HostId, request.GroupKeys, cancellationToken);
        if (!result.FaceFound)
            return NotFound(new { error = "Face was not found." });
        if (!result.HostHadFace)
            return BadRequest(new { error = "That face is not present on the specified host." });
        if (!result.GroupKeysMatched)
            return BadRequest(new { error = "None of the supplied appearances belong to that face on that host." });
        if (result.WouldEmptyFace)
            return BadRequest(new { error = "Separating every appearance would leave the face empty here — mark it not present instead." });

        return Ok(result);
    }

    /// <summary>Records that a face is not really present on a host and re-homes its occurrences there.</summary>
    [HttpPost("{id:int}/not-present")]
    [RequiresPermission(Permissions.FacesWrite)]
    public async Task<ActionResult<FaceNotPresentResultDto>> MarkNotPresent(
        int id,
        [FromBody] FaceOccurrenceHostDto request,
        CancellationToken cancellationToken)
    {
        var editor = ActiveOccurrenceEditor();
        if (editor is null)
            return StatusCode(StatusCodes.Status501NotImplemented, new { error = "No installed extension provides face occurrence editing." });

        var normalizedHostType = NormalizeOccurrenceHostType(request?.HostType);
        if (normalizedHostType is null || request is null || request.HostId <= 0)
            return BadRequest(new { error = "A hostType of 'video' or 'image' and a positive hostId are required." });

        var result = await editor.MarkNotPresentAsync(id, normalizedHostType, request.HostId, cancellationToken);
        if (!result.FaceFound)
            return NotFound(new { error = "Face was not found." });
        if (!result.HostHadFace)
            return BadRequest(new { error = "That face is not present on the specified host." });

        return Ok(result);
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<FaceDto>>> List(
        [FromQuery] string? q = null,
        [FromQuery] int? performerId = null,
        [FromQuery] string? performerIds = null,
        [FromQuery] bool? linked = null,
        [FromQuery] bool? ignored = null,
        [FromQuery] bool? merged = null,
        [FromQuery] int? mergedIntoFaceId = null,
        [FromQuery] string? label = null,
        [FromQuery] string? labelModifier = null,
        [FromQuery] string? primarySourceKey = null,
        [FromQuery] string? primarySourceKeyModifier = null,
        [FromQuery] bool? hasCover = null,
        [FromQuery] int? detectionCount = null,
        [FromQuery] int? detectionCount2 = null,
        [FromQuery] string? detectionCountModifier = null,
        [FromQuery] int? appearanceCount = null,
        [FromQuery] int? appearanceCount2 = null,
        [FromQuery] string? appearanceCountModifier = null,
        [FromQuery] int? frameSampleCount = null,
        [FromQuery] int? frameSampleCount2 = null,
        [FromQuery] string? frameSampleCountModifier = null,
        [FromQuery] int? videoCount = null,
        [FromQuery] int? videoCount2 = null,
        [FromQuery] string? videoCountModifier = null,
        [FromQuery] int? imageCount = null,
        [FromQuery] int? imageCount2 = null,
        [FromQuery] string? imageCountModifier = null,
        [FromQuery] float? minSuggestionConfidence = null,
        [FromQuery] float? suggestionConfidence = null,
        [FromQuery] float? suggestionConfidence2 = null,
        [FromQuery] string? suggestionConfidenceModifier = null,
        [FromQuery] string? topSuggestionPerformerIds = null,
        [FromQuery] string? sort = null,
        [FromQuery] SortDirection direction = SortDirection.Asc,
        [FromQuery] int? seed = null,
        [FromQuery] string? customFieldCriteria = null,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        perPage = Math.Clamp(perPage, 1, 250);

        var totalSw = Stopwatch.StartNew();
        var phaseSw = new Stopwatch();

        var baseQuery = db.Faces
            .AsNoTracking()
            .Include(face => face.Performer)
            .AsQueryable();

        var query = FullTextSearchHelpers.Apply(db, baseQuery, q,
            face => face.Label,
            face => face.PrimarySourceKey,
            face => face.SearchText,
            face => face.Performer != null ? face.Performer.Name : null);

        // Postgres full-text matches only the face's own SearchVector, which does not include a *linked*
        // performer's name — so a search by performer name wouldn't surface that performer's faces (and
        // the merge dialog, which searches faces the same way, couldn't find a face by who it's linked
        // to). Union linked performer name/alias matches, mirroring how video search includes performers.
        var faceSearchTerm = q?.Trim();
        if (!string.IsNullOrWhiteSpace(faceSearchTerm))
        {
            var performerTerm = faceSearchTerm.ToLowerInvariant();
            query = query.Concat(baseQuery.Where(face => face.Performer != null && (
                    face.Performer.Name.ToLower().Contains(performerTerm)
                    || face.Performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(performerTerm)))))
                .Distinct();
        }

        if (performerId.HasValue)
            query = query.Where(face => face.PerformerId == performerId.Value);

        var parsedPerformerIds = ParseIntList(performerIds);
        if (parsedPerformerIds.Count > 0)
            query = query.Where(face => face.PerformerId.HasValue && parsedPerformerIds.Contains(face.PerformerId.Value));

        if (linked.HasValue)
            query = linked.Value
                ? query.Where(face => face.PerformerId != null)
                : query.Where(face => face.PerformerId == null);

        if (ignored.HasValue)
            query = query.Where(face => face.Ignored == ignored.Value);

        // A merged face is absorbed into its surviving target, so it must never surface in the faces list
        // — regardless of the merged / mergedIntoFaceId params. Otherwise a cluster appears twice (the
        // merged "loser" plus its target) and per-performer counts/numbering look wrong. The tombstone row
        // is retained only for AI re-scan redirect and un-merge; it is intentionally invisible to the list.
        query = query.Where(face => face.MergedIntoFaceId == null);

        query = FilterHelpers.ApplyString(query, BuildStringCriterion(label, labelModifier), face => face.Label);
        query = FilterHelpers.ApplyString(query, BuildStringCriterion(primarySourceKey, primarySourceKeyModifier), face => face.PrimarySourceKey);
        query = FilterHelpers.ApplyInt(query, BuildIntCriterion(detectionCount, detectionCount2, detectionCountModifier), face => face.DetectionCount);
        query = FilterHelpers.ApplyInt(query, BuildIntCriterion(appearanceCount, appearanceCount2, appearanceCountModifier), face => face.AppearanceCount);
        query = FilterHelpers.ApplyInt(query, BuildIntCriterion(frameSampleCount, frameSampleCount2, frameSampleCountModifier), face => face.FrameSampleCount);
        query = FilterHelpers.ApplyInt(query, BuildIntCriterion(videoCount, videoCount2, videoCountModifier), face => face.VideoCount);
        query = FilterHelpers.ApplyInt(query, BuildIntCriterion(imageCount, imageCount2, imageCountModifier), face => face.ImageCount);
        query = query.ApplyCustomFieldCriteria(db, CustomFieldEntityTypes.Face, null, ParseCustomFieldCriteria(customFieldCriteria));

        if (hasCover.HasValue)
            query = hasCover.Value
                ? query.Where(face => face.CoverBlobId != null && face.CoverBlobId != "")
                : query.Where(face => face.CoverBlobId == null || face.CoverBlobId == "");

        var sortedQuery = FullTextSearchHelpers.ShouldOrderByRelevance(db, q, sort)
            ? FullTextSearchHelpers.OrderByRelevance(db, query, q)
            : ApplyFaceSort(db, query, sort, direction == SortDirection.Desc, seed);

        var parsedTopSuggestionPerformerIds = ParseIntList(topSuggestionPerformerIds);
        var hasTopSuggestionFilter = minSuggestionConfidence.HasValue || suggestionConfidence.HasValue || parsedTopSuggestionPerformerIds.Count > 0;
        if (hasTopSuggestionFilter)
        {
            // Suggestion confidence and suggested-performer are filtered and sorted directly on the
            // materialized Face.TopSuggestion* columns, so this stays an indexed, paginated SQL query
            // no matter how many unlinked faces exist. (This branch previously loaded every matching
            // face and computed suggestions for all of them on the request thread — the O(N) cost.)
            phaseSw.Restart();
            var min = minSuggestionConfidence.HasValue ? NormalizeConfidenceThreshold(minSuggestionConfidence.Value) : (float?)null;
            var val = suggestionConfidence.HasValue ? NormalizeConfidenceThreshold(suggestionConfidence.Value) : (float?)null;
            var val2 = suggestionConfidence2.HasValue ? NormalizeConfidenceThreshold(suggestionConfidence2.Value) : (float?)null;
            var modifier = NormalizeCriterionModifier(suggestionConfidenceModifier)
                ?? (minSuggestionConfidence.HasValue ? "GREATER_THAN" : null);

            // Only unlinked faces carry a materialized suggestion.
            var suggestionQuery = query.Where(face => face.PerformerId == null && face.TopSuggestionPerformerId != null);

            if (parsedTopSuggestionPerformerIds.Count > 0)
                suggestionQuery = suggestionQuery.Where(face => face.TopSuggestionLocalPerformerId != null && parsedTopSuggestionPerformerIds.Contains(face.TopSuggestionLocalPerformerId.Value));

            if (min.HasValue)
                suggestionQuery = suggestionQuery.Where(face => face.TopSuggestionConfidence >= min.Value);

            suggestionQuery = ApplyStoredConfidenceCriterion(suggestionQuery, modifier, val, val2);

            var sortedFiltered = IsSuggestionConfidenceSort(sort)
                ? suggestionQuery
                    .OrderByDescending(face => face.TopSuggestionConfidence ?? -1f)
                    .ThenByDescending(face => face.UpdatedAt)
                    .ThenBy(face => face.Id)
                : ApplyFaceSort(db, suggestionQuery, sort, direction == SortDirection.Desc, seed);

            var totalFilteredCount = await sortedFiltered.CountAsync(cancellationToken);
            var filteredPage = await sortedFiltered
                .Skip((page - 1) * perPage)
                .Take(perPage)
                .ToListAsync(cancellationToken);
            logger.LogDebug("Faces.List (filtered) DB query: {Ms}ms (page={Count}, total={TotalMs}ms)", phaseSw.ElapsedMilliseconds, filteredPage.Count, totalSw.ElapsedMilliseconds);
            var filteredComputedCounts = await LoadComputedCountsAsync(filteredPage.Select(face => face.Id).ToArray(), cancellationToken);
            var filteredOrdinals = await LoadPerformerFaceOrdinalsAsync(filteredPage, cancellationToken);
            var filteredCoverFallbacks = await LoadFaceCoverFallbackUrlsAsync(filteredPage, cancellationToken);

            return Ok(new PaginatedResponse<FaceDto>(
                filteredPage.Select(face => MapToDto(
                    face,
                    filteredComputedCounts.TryGetValue(face.Id, out var counts) ? counts : null,
                    MapStoredTopSuggestion(face),
                    performerFaceOrdinal: filteredOrdinals.TryGetValue(face.Id, out var ord) ? ord : null,
                    coverFallbackUrl: filteredCoverFallbacks.GetValueOrDefault(face.Id))).ToList(),
                totalFilteredCount,
                page,
                perPage));
        }

        var totalCount = await query.CountAsync(cancellationToken);

        phaseSw.Restart();
        // sortedQuery already orders by the stored TopSuggestionConfidence when sort=suggestion_confidence
        // (see ApplyFaceSort), so the page is paginated in SQL and the top suggestion is read straight
        // off the materialized columns — no per-request suggestion compute.
        var items = await sortedQuery
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(cancellationToken);
        logger.LogDebug("Faces.List DB page query: {Ms}ms (items={Count})", phaseSw.ElapsedMilliseconds, items.Count);

        var computedCounts = await LoadComputedCountsAsync(items.Select(face => face.Id).ToArray(), cancellationToken);
        var ordinals = await LoadPerformerFaceOrdinalsAsync(items, cancellationToken);
        var coverFallbacks = await LoadFaceCoverFallbackUrlsAsync(items, cancellationToken);
        logger.LogDebug("Faces.List total: {Ms}ms", totalSw.ElapsedMilliseconds);

        return Ok(new PaginatedResponse<FaceDto>(
            items.Select(face => MapToDto(
                face,
                computedCounts.TryGetValue(face.Id, out var counts) ? counts : null,
                MapStoredTopSuggestion(face),
                performerFaceOrdinal: ordinals.TryGetValue(face.Id, out var ord) ? ord : null,
                coverFallbackUrl: coverFallbacks.GetValueOrDefault(face.Id))).ToList(),
            totalCount,
            page,
            perPage));
    }

    private static List<int> ParseIntList(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(static part => int.TryParse(part, out var parsed) ? parsed : 0)
                .Where(static id => id > 0)
                .Distinct()
                .ToList();

    private static string? NormalizeCriterionModifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "EQUALS" or "NOT_EQUALS" or "GREATER_THAN" or "LESS_THAN" or "BETWEEN" or "NOT_BETWEEN" or "IS_NULL" or "NOT_NULL" or "INCLUDES" or "EXCLUDES" or "MATCHES_REGEX" or "NOT_MATCHES_REGEX"
            ? normalized
            : null;
    }

    private static CriterionModifier? ParseCriterionModifier(string? value)
    {
        var normalized = NormalizeCriterionModifier(value);
        return normalized switch
        {
            "EQUALS" => CriterionModifier.Equals,
            "NOT_EQUALS" => CriterionModifier.NotEquals,
            "GREATER_THAN" => CriterionModifier.GreaterThan,
            "LESS_THAN" => CriterionModifier.LessThan,
            "BETWEEN" => CriterionModifier.Between,
            "NOT_BETWEEN" => CriterionModifier.NotBetween,
            "IS_NULL" => CriterionModifier.IsNull,
            "NOT_NULL" => CriterionModifier.NotNull,
            "INCLUDES" => CriterionModifier.Includes,
            "EXCLUDES" => CriterionModifier.Excludes,
            "MATCHES_REGEX" => CriterionModifier.MatchesRegex,
            "NOT_MATCHES_REGEX" => CriterionModifier.NotMatchesRegex,
            _ => null,
        };
    }

    private static StringCriterion? BuildStringCriterion(string? value, string? modifier)
    {
        var parsedModifier = ParseCriterionModifier(modifier) ?? CriterionModifier.Includes;
        if ((parsedModifier == CriterionModifier.IsNull || parsedModifier == CriterionModifier.NotNull) || !string.IsNullOrWhiteSpace(value))
            return new StringCriterion { Value = value?.Trim() ?? string.Empty, Modifier = parsedModifier };

        return null;
    }

    private static IntCriterion? BuildIntCriterion(int? value, int? value2, string? modifier)
    {
        var parsedModifier = ParseCriterionModifier(modifier) ?? CriterionModifier.Equals;
        if (parsedModifier is CriterionModifier.IsNull or CriterionModifier.NotNull || value.HasValue)
            return new IntCriterion { Value = value ?? 0, Value2 = value2, Modifier = parsedModifier };

        return null;
    }

    private static List<CustomFieldCriterion> ParseCustomFieldCriteria(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            var criteria = new List<CustomFieldCriterion>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var key = GetString(element, "key");
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                criteria.Add(new CustomFieldCriterion
                {
                    Key = key.Trim(),
                    Type = GetString(element, "type") ?? CustomFieldTypes.Text,
                    Value = GetString(element, "value") ?? string.Empty,
                    Value2 = GetString(element, "value2"),
                    Modifier = ParseCriterionModifier(GetString(element, "modifier")) ?? CriterionModifier.Equals,
                });
            }

            return criteria;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
            return null;

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<FaceDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var face = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        var computedCounts = await LoadComputedCountsAsync(new[] { id }, cancellationToken);
        var topSuggestion = MapStoredTopSuggestion(face);
        var fieldProvenance = await LoadFaceFieldProvenanceAsync(face.Id, cancellationToken);
        var ordinals = await LoadPerformerFaceOrdinalsAsync([face], cancellationToken);
        return Ok(MapToDto(
            face,
            computedCounts.TryGetValue(face.Id, out var counts) ? counts : null,
            topSuggestion,
            fieldProvenance,
            ordinals.TryGetValue(face.Id, out var ord) ? ord : null));
    }

    [HttpGet("{id:int}/appearances")]
    public async Task<ActionResult<PaginatedResponse<FaceAppearanceDto>>> GetAppearances(
        int id,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] int? seed,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 24,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        perPage = Math.Clamp(perPage, 1, 250);

        var faceExists = await db.Faces.AsNoTracking().AnyAsync(face => face.Id == id, cancellationToken);
        if (!faceExists)
            return NotFound();

        var items = await LoadFaceAppearanceItemsAsync(id, cancellationToken);
        if (!string.IsNullOrWhiteSpace(q))
        {
            items = items
                .Where(item =>
                    item.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || item.HostType.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        items = ApplyAppearanceSort(items, sort, direction, seed);
        var totalCount = items.Count;
        var pageItems = items
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToList();

        return Ok(new PaginatedResponse<FaceAppearanceDto>(pageItems, totalCount, page, perPage));
    }

    [HttpGet("{id:int}/detections")]
    public async Task<ActionResult<IReadOnlyList<DetectionDto>>> GetDetections(int id, CancellationToken cancellationToken)
    {
        var faceExists = await db.Faces.AsNoTracking().AnyAsync(face => face.Id == id, cancellationToken);
        if (!faceExists)
            return NotFound();

        var detections = await db.Detections
            .AsNoTracking()
            .Where(detection => detection.RefId == id && detection.RefKind != null && detection.RefKind.ToLower() == "face")
            .OrderByDescending(detection => detection.UpdatedAt)
            .ThenBy(detection => detection.Id)
            .ToListAsync(cancellationToken);

        return Ok(detections.Select(MapDetectionToDto).ToList());
    }

    [HttpGet("/api/videos/{videoId:int}/faces")]
    public async Task<ActionResult<IReadOnlyList<FaceHostFaceDto>>> GetVideoFaces(int videoId, CancellationToken cancellationToken)
        => Ok(await LoadHostFacesAsync(FaceAppearanceHostType.Video, videoId, cancellationToken));

    [HttpGet("/api/images/{imageId:int}/faces")]
    public async Task<ActionResult<IReadOnlyList<FaceHostFaceDto>>> GetImageFaces(int imageId, CancellationToken cancellationToken)
        => Ok(await LoadHostFacesAsync(FaceAppearanceHostType.Image, imageId, cancellationToken));

    [HttpGet("/api/performers/{performerId:int}/faces")]
    public async Task<ActionResult<IReadOnlyList<FaceDto>>> GetPerformerFaces(int performerId, CancellationToken cancellationToken)
    {
        var faces = await db.Faces
            .AsNoTracking()
            .Include(face => face.Performer)
            .Where(face => face.PerformerId == performerId && face.MergedIntoFaceId == null)
            .OrderByDescending(face => face.AppearanceCount)
            .ThenBy(face => face.Label)
            .ThenBy(face => face.Id)
            .ToListAsync(cancellationToken);

        var computedCounts = await LoadComputedCountsAsync(faces.Select(face => face.Id).ToArray(), cancellationToken);
        var ordinals = await LoadPerformerFaceOrdinalsAsync(faces, cancellationToken);
        var coverFallbacks = await LoadFaceCoverFallbackUrlsAsync(faces, cancellationToken);
        return Ok(faces.Select(face => MapToDto(
            face,
            computedCounts.GetValueOrDefault(face.Id),
            performerFaceOrdinal: ordinals.TryGetValue(face.Id, out var ord) ? ord : null,
            coverFallbackUrl: coverFallbacks.GetValueOrDefault(face.Id))).ToList());
    }

    [HttpGet("review/unlinked")]
    public async Task<ActionResult<IReadOnlyList<FaceDto>>> GetUnlinkedReviewFaces(
        [FromQuery] int take = 24,
        CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);
        var faces = await db.Faces
            .AsNoTracking()
            .Include(face => face.Performer)
            .Where(face => face.PerformerId == null && face.MergedIntoFaceId == null && !face.Ignored)
            .OrderByDescending(face => face.AppearanceCount)
            .ThenByDescending(face => face.FrameSampleCount)
            .ThenBy(face => face.Id)
            .Take(take)
            .ToListAsync(cancellationToken);

        var computedCounts = await LoadComputedCountsAsync(faces.Select(face => face.Id).ToArray(), cancellationToken);
        var topSuggestions = await BuildTopSuggestionsAsync(faces, cancellationToken);
        var coverFallbacks = await LoadFaceCoverFallbackUrlsAsync(faces, cancellationToken);
        return Ok(faces.Select(face => MapToDto(
            face,
            computedCounts.GetValueOrDefault(face.Id),
            topSuggestions.GetValueOrDefault(face.Id),
            coverFallbackUrl: coverFallbacks.GetValueOrDefault(face.Id))).ToList());
    }

    [HttpGet("review/ai-run")]
    public async Task<ActionResult<IReadOnlyList<FaceDto>>> GetAiRunReviewFaces(
        [FromQuery] DateTime? startedAt,
        [FromQuery] DateTime? completedAt,
        [FromQuery] int take = 12,
        CancellationToken cancellationToken = default)
    {
        if (!startedAt.HasValue || !completedAt.HasValue)
            return Ok(Array.Empty<FaceDto>());

        take = Math.Clamp(take, 1, 100);
        var windowStart = startedAt.Value.ToUniversalTime().AddMinutes(-1);
        var windowEnd = completedAt.Value.ToUniversalTime().AddMinutes(1);
        // Run provenance is internal correlation data for this face-reading endpoint. The
        // FaceAppearance query below still applies face and media-host visibility filters.
        var runKeys = await db.AiRuns
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(run => run.Status == AiRunStatus.Completed
                && run.StartedAt >= windowStart
                && (run.CompletedAt ?? run.StartedAt) <= windowEnd
                && (run.TargetType == AiRunTargetType.Video || run.TargetType == AiRunTargetType.Image))
            .OrderByDescending(run => run.CompletedAt ?? run.StartedAt)
            .Select(run => run.RunKey)
            .ToArrayAsync(cancellationToken);

        if (runKeys.Length == 0)
            return Ok(Array.Empty<FaceDto>());

        var targets = await db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.SourceRunId != null && runKeys.Contains(appearance.SourceRunId))
            .Select(appearance => new
            {
                TargetType = appearance.HostType == FaceAppearanceHostType.Video ? AiRunTargetType.Video : AiRunTargetType.Image,
                TargetId = appearance.HostId,
            })
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (targets.Length != 1)
            return Ok(Array.Empty<FaceDto>());

        var target = targets[0];
        var hostType = target.TargetType == AiRunTargetType.Video ? FaceAppearanceHostType.Video : FaceAppearanceHostType.Image;
        return Ok(await LoadReviewFacesForHostAsync(hostType, target.TargetId, take, cancellationToken));
    }

    [HttpGet("{id:int}/delete-impact")]
    [RequiresPermission(Permissions.FacesDelete)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesDelete)]
    public async Task<ActionResult<FaceDeleteImpactDto>> GetDeleteImpact(int id, CancellationToken cancellationToken)
    {
        var face = await db.Faces
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);

        return face is null
            ? NotFound()
            : Ok(await BuildDeleteImpactAsync(id, face.CoverBlobId is not null, cancellationToken));
    }

    [HttpGet("{id:int}/suggestions")]
    [RequiresPermission(Permissions.FacesRead)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesRead)]
    public async Task<ActionResult<IReadOnlyList<FaceSuggestionDto>>> GetSuggestions(
        int id,
        [FromQuery] int maxResults = 5,
        CancellationToken cancellationToken = default)
    {
        var face = await db.Faces
            .AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        if (face.PerformerId.HasValue)
            return Ok(Array.Empty<FaceSuggestionDto>());

        return Ok(await BuildRankedSuggestionsAsync(id, maxResults, cancellationToken));
    }

    [HttpPost]
    [RequiresPermission(Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> Create([FromBody] FaceCreateDto dto, CancellationToken cancellationToken)
    {
        if (dto.PerformerId.HasValue)
        {
            var performerExists = await db.Performers.AnyAsync(performer => performer.Id == dto.PerformerId.Value, cancellationToken);
            if (!performerExists)
                return ValidationProblem($"Performer {dto.PerformerId.Value} was not found.");
        }

        var face = new Face
        {
            Label = Clean(dto.Label),
            PerformerId = dto.PerformerId,
            Ignored = dto.Ignored,
            PrimarySourceKey = Clean(dto.PrimarySourceKey),
        };

        db.Faces.Add(face);
        await db.SaveChangesAsync(cancellationToken);

        var created = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == face.Id, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { id = face.Id }, MapToDto(created));
    }

    [HttpPost("batch/link-top-suggestion")]
    [RequiresPermission(Permissions.FacesWrite)]
    public async Task<ActionResult<FaceBatchOperationResultDto>> BatchLinkTopSuggestion([FromBody] FaceBatchLinkTopSuggestionDto dto, CancellationToken cancellationToken)
    {
        var succeeded = new List<int>();
        var skipped = new List<FaceBatchSkippedDto>();
        var failed = new List<FaceBatchFailedDto>();
        var requestedFaceIds = dto.FaceIds.Distinct().ToArray();
        var facesById = requestedFaceIds.Length == 0
            ? new Dictionary<int, Face>()
            : await db.Faces
                .Where(face => requestedFaceIds.Contains(face.Id))
                .ToDictionaryAsync(face => face.Id, cancellationToken);
        var eligibleFaceIds = facesById.Values
            .Where(face => !face.PerformerId.HasValue)
            .Select(face => face.Id)
            .ToArray();
        var blockedByFaceId = await LoadBlockedSuggestionIdsAsync(eligibleFaceIds, cancellationToken);
        var suggestionsByFaceId = await BuildRankedSuggestionsByFaceAsync(
            eligibleFaceIds,
            blockedByFaceId,
            TopSuggestionCandidateCount,
            cancellationToken,
            includeReferenceMatches: true);

        foreach (var faceId in requestedFaceIds)
        {
            try
            {
                if (!facesById.TryGetValue(faceId, out var face))
                {
                    skipped.Add(new FaceBatchSkippedDto(faceId, "Face was not found."));
                    continue;
                }

                if (face.PerformerId.HasValue)
                {
                    skipped.Add(new FaceBatchSkippedDto(faceId, "Face is already linked."));
                    continue;
                }

                suggestionsByFaceId.TryGetValue(faceId, out var suggestions);
                var ordered = suggestions ?? [];

                // A face whose highest-ranked match belongs to a conflict group (the same face matched
                // more than one performer) needs an explicit choice: skip it, link the top directly, or
                // merge the competing matches into the top one.
                var top = ordered.Count > 0 ? ordered[0] : null;
                var conflictGroupId = top?.ConflictGroupId;
                var conflictCandidates = string.IsNullOrEmpty(conflictGroupId)
                    ? []
                    : ordered.Where(item => item.ConflictGroupId == conflictGroupId).ToList();
                if (top is not null && conflictCandidates.Count >= 2)
                {
                    if (!dto.LinkConflicting)
                    {
                        skipped.Add(new FaceBatchSkippedDto(faceId, "Face has conflicting matches."));
                        continue;
                    }

                    if (dto.MergeConflicting)
                    {
                        var secondaryIds = conflictCandidates
                            .Where(item => item.PerformerId != top.PerformerId)
                            .Select(item => item.PerformerId)
                            .ToList();
                        var mergeDecision = new FaceSuggestionDecisionDto(top.PerformerId, FaceSuggestionDecisionValues.Merge, SecondaryPerformerIds: secondaryIds);
                        var mergeOutcome = await TryHandleProviderSuggestionDecisionAsync(faceId, mergeDecision, FaceSuggestionDecisionValues.Merge, cancellationToken);
                        if (mergeOutcome is { Succeeded: true }) { succeeded.Add(faceId); continue; }
                        if (mergeOutcome is { Succeeded: false }) { failed.Add(new FaceBatchFailedDto(faceId, mergeOutcome.Error ?? "Merge was rejected by the provider.")); continue; }
                        skipped.Add(new FaceBatchSkippedDto(faceId, "No provider was available to merge the conflicting matches."));
                        continue;
                    }

                    // Take the top match directly.
                    var topLocalId = ResolveLocalPerformerId(top);
                    if (topLocalId.HasValue)
                    {
                        await facePerformerPropagationService.ApplyLinkChangeAsync(faceId, face.PerformerId, topLocalId, cancellationToken);
                        face.PerformerId = topLocalId;
                        await RecordReferencePerformerLinkAsync(topLocalId.Value, top.ReferenceEndpoint, top.ReferenceExternalId, top.ReferenceWillRefreshFromMetadata, cancellationToken);
                        succeeded.Add(faceId);
                        continue;
                    }

                    if (top.PerformerId < 0)
                    {
                        var topDecision = new FaceSuggestionDecisionDto(top.PerformerId, FaceSuggestionDecisionValues.Accept);
                        var topOutcome = await TryHandleProviderSuggestionDecisionAsync(faceId, topDecision, FaceSuggestionDecisionValues.Accept, cancellationToken);
                        if (topOutcome is { Succeeded: true }) { succeeded.Add(faceId); continue; }
                        if (topOutcome is { Succeeded: false }) { failed.Add(new FaceBatchFailedDto(faceId, topOutcome.Error ?? "Reference performer creation was rejected by the provider.")); continue; }
                    }

                    skipped.Add(new FaceBatchSkippedDto(faceId, "No linkable top match was available."));
                    continue;
                }

                var suggestion = ordered
                    .FirstOrDefault(item => ResolveLocalPerformerId(item).HasValue);
                var performerId = suggestion is null ? null : ResolveLocalPerformerId(suggestion);
                if (performerId.HasValue)
                {
                    await facePerformerPropagationService.ApplyLinkChangeAsync(faceId, face.PerformerId, performerId, cancellationToken);
                    face.PerformerId = performerId;
                    if (suggestion is not null)
                        await RecordReferencePerformerLinkAsync(performerId.Value, suggestion.ReferenceEndpoint, suggestion.ReferenceExternalId, suggestion.ReferenceWillRefreshFromMetadata, cancellationToken);
                    succeeded.Add(faceId);
                    continue;
                }

                // No local performer to link. If the caller opted in, create-and-link from a reference
                // (SAIE) match via its provider, which may scrape a configured metadata server.
                if (dto.CreateFromReference)
                {
                    var referenceSuggestion = ordered
                        .FirstOrDefault(item => !ResolveLocalPerformerId(item).HasValue
                            && item.PerformerId < 0);
                    if (referenceSuggestion is not null)
                    {
                        var decision = new FaceSuggestionDecisionDto(referenceSuggestion.PerformerId, FaceSuggestionDecisionValues.Accept);
                        var outcome = await TryHandleProviderSuggestionDecisionAsync(faceId, decision, FaceSuggestionDecisionValues.Accept, cancellationToken);
                        if (outcome is { Succeeded: true })
                        {
                            succeeded.Add(faceId);
                            continue;
                        }

                        if (outcome is { Succeeded: false })
                        {
                            failed.Add(new FaceBatchFailedDto(faceId, outcome.Error ?? "Reference performer creation was rejected by the provider."));
                            continue;
                        }
                    }
                }

                skipped.Add(new FaceBatchSkippedDto(faceId, "No linkable top suggestion was available."));
                continue;
            }
            catch (Exception ex)
            {
                failed.Add(new FaceBatchFailedDto(faceId, ex.Message));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var faceId in succeeded)
            await InvalidateSuggestionForLinkChangeAsync(faceId, cancellationToken);
        return Ok(new FaceBatchOperationResultDto(succeeded, skipped, failed));
    }

    [HttpPost("batch/delete")]
    [RequiresPermission(Permissions.FacesDelete)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesDelete, ActionArgumentName = "dto", PropertyName = "FaceIds")]
    public IActionResult BatchDelete([FromBody] FaceBatchDeleteDto dto, CancellationToken cancellationToken)
    {
        var ids = dto.FaceIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
            return BadRequest("Select at least one face to delete.");

        return Accepted(bulkDeletionJobService!.Start(
            principalAccessor?.Current,
            BulkDeletionEntityKind.Face,
            ids));
    }

    [HttpPost("{id:int}/create-performer")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> CreatePerformerFromFace(int id, [FromBody] FaceCreatePerformerDto dto, CancellationToken cancellationToken)
    {
        var name = Clean(dto.Name);
        if (string.IsNullOrWhiteSpace(name))
            return ValidationProblem("A performer name is required.");

        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();
        if (face.PerformerId.HasValue)
            return ValidationProblem("This face is already linked to a performer.");

        var performer = new Performer { Name = name };
        await TrySetLocalPerformerImageFromFaceAsync(face, performer, dto.SetPerformerImage, cancellationToken);

        db.Performers.Add(performer);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (EntityNameConflictException exception)
        {
            return Conflict(new { code = "PERFORMER_NAME_CONFLICT", message = exception.Message });
        }

        await facePerformerPropagationService.ApplyLinkChangeAsync(id, face.PerformerId, performer.Id, cancellationToken);
        face.PerformerId = performer.Id;
        await RecordManualFaceFieldProvenanceAsync(face.Id, new Dictionary<string, object?> { ["performer_id"] = performer.Id }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateSuggestionForLinkChangeAsync(id, cancellationToken);

        var updated = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return Ok(MapToDto(updated));
    }

    [HttpPut("{id:int}")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> Update(int id, [FromBody] FaceUpdateDto dto, CancellationToken cancellationToken)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        var originalLabel = face.Label;
        var originalPerformerId = face.PerformerId;
        var originalIgnored = face.Ignored;
        var originalPrimarySourceKey = face.PrimarySourceKey;

        if (dto.PerformerId.HasValue)
        {
            var performerExists = await db.Performers.AnyAsync(performer => performer.Id == dto.PerformerId.Value, cancellationToken);
            if (!performerExists)
                return ValidationProblem($"Performer {dto.PerformerId.Value} was not found.");
        }

        face.Label = Clean(dto.Label);
        await facePerformerPropagationService.ApplyLinkChangeAsync(id, face.PerformerId, dto.PerformerId, cancellationToken);
        face.PerformerId = dto.PerformerId;
        face.Ignored = dto.Ignored;
        face.PrimarySourceKey = Clean(dto.PrimarySourceKey);

        var manualFields = new Dictionary<string, object?>();
        if (!string.Equals(originalLabel, face.Label, StringComparison.Ordinal))
            manualFields["label"] = face.Label;
        if (originalPerformerId != face.PerformerId)
            manualFields["performer_id"] = face.PerformerId;
        if (originalIgnored != face.Ignored)
            manualFields["ignored"] = face.Ignored;
        if (!string.Equals(originalPrimarySourceKey, face.PrimarySourceKey, StringComparison.Ordinal))
            manualFields["primary_source_key"] = face.PrimarySourceKey;
        await RecordManualFaceFieldProvenanceAsync(face.Id, manualFields, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        if (originalPerformerId != face.PerformerId)
            await InvalidateSuggestionForLinkChangeAsync(id, cancellationToken);

        var updated = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return Ok(MapToDto(updated, fieldProvenance: await LoadFaceFieldProvenanceAsync(updated.Id, cancellationToken)));
    }

    [HttpDelete("{id:int}")]
    [RequiresPermission(Permissions.FacesDelete)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesDelete)]
    public async Task<IActionResult> Delete(int id, CancellationToken cancellationToken)
    {
        if (bulkEntityDeletionService is not null)
        {
            var deletedBySharedService = await bulkEntityDeletionService.DeleteAsync(
                BulkDeletionEntityKind.Face,
                id,
                new BulkDeletionExecutionContext(),
                deleteFiles: false,
                deleteGenerated: true,
                cancellationToken,
                publishEvent: false);
            return deletedBySharedService ? NoContent() : NotFound();
        }

        var clearedEvidence = new List<ClearedFaceRunEvidence>();
        var deleted = false;
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            clearedEvidence.Clear();
            var propagationHosts = new HashSet<(FaceAppearanceHostType HostType, int HostId)>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            deleted = await DeleteFaceAsync(id, cancellationToken, clearedEvidence, propagationHosts);
            if (!deleted)
                return;

            await db.SaveChangesAsync(cancellationToken);
            foreach (var (hostType, hostId) in propagationHosts)
                await facePerformerPropagationService.ReconcileHostUnscopedAsync(hostType, hostId, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        if (!deleted)
            return NotFound();
        await NotifyHostFacesClearedAsync(clearedEvidence, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:int}/link")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> Link(int id, [FromBody] FaceLinkDto dto, CancellationToken cancellationToken)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        Performer? performer = null;
        if (dto.PerformerId.HasValue)
        {
            performer = await db.Performers
                .Include(item => item.RemoteIds)
                .FirstOrDefaultAsync(item => item.Id == dto.PerformerId.Value, cancellationToken);
            if (performer is null)
                return ValidationProblem($"Performer {dto.PerformerId.Value} was not found.");
        }

        await facePerformerPropagationService.ApplyLinkChangeAsync(id, face.PerformerId, dto.PerformerId, cancellationToken);
        face.PerformerId = dto.PerformerId;
        if (performer is not null)
            await TrySetLocalPerformerImageFromFaceAsync(face, performer, dto.SetPerformerImage, cancellationToken);
        await RecordManualFaceFieldProvenanceAsync(face.Id, new Dictionary<string, object?> { ["performer_id"] = face.PerformerId }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateSuggestionForLinkChangeAsync(id, cancellationToken);

        var linked = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return Ok(MapToDto(linked, fieldProvenance: await LoadFaceFieldProvenanceAsync(linked.Id, cancellationToken)));
    }

    [HttpPost("{id:int}/suggestions/decision")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> RecordSuggestionDecision(int id, [FromBody] FaceSuggestionDecisionDto dto, CancellationToken cancellationToken)
    {
        if (principalAccessor?.Current?.UserId is not int userId)
            return Unauthorized();

        var normalizedDecision = dto.Decision.Trim().ToLowerInvariant();
        if (normalizedDecision is not FaceSuggestionDecisionValues.Accept and not FaceSuggestionDecisionValues.Reject and not FaceSuggestionDecisionValues.Merge)
            return ValidationProblem("Decision must be 'accept', 'reject', or 'merge'.");

        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        // Merging competing reference matches is always provider business (the ids may be reference-encoded
        // and the fold-in logic lives in the reference suggester), so route it straight to the handler.
        if (normalizedDecision == FaceSuggestionDecisionValues.Merge)
        {
            var mergeOutcome = await TryHandleProviderSuggestionDecisionAsync(id, dto, normalizedDecision, cancellationToken);
            if (mergeOutcome is null)
                return ValidationProblem("No provider is available to merge these matches.");
            if (mergeOutcome.Succeeded)
            {
                await InvalidateSuggestionForLinkChangeAsync(id, cancellationToken);
                return Ok(await LoadFaceDtoAsync(id, cancellationToken));
            }
            return StatusCode(mergeOutcome.StatusCode ?? StatusCodes.Status400BadRequest, new { error = mergeOutcome.Error ?? "Merge was not accepted by the provider." });
        }

        var performer = await db.Performers
            .Include(item => item.RemoteIds)
            .FirstOrDefaultAsync(item => item.Id == dto.PerformerId, cancellationToken);
        if (performer is null)
        {
            var providerOutcome = await TryHandleProviderSuggestionDecisionAsync(id, dto, normalizedDecision, cancellationToken);
            if (providerOutcome is not null)
            {
                if (providerOutcome.Succeeded)
                    return Ok(await LoadFaceDtoAsync(id, cancellationToken));

                return StatusCode(providerOutcome.StatusCode ?? StatusCodes.Status400BadRequest, new { error = providerOutcome.Error ?? "Suggestion decision was not accepted by the provider." });
            }

            return ValidationProblem($"Performer {dto.PerformerId} was not found.");
        }

        var decision = await db.FaceSuggestionDecisions
            .FirstOrDefaultAsync(item => item.FaceId == id && item.PerformerId == dto.PerformerId && item.UserId == userId, cancellationToken);

        if (decision is null)
        {
            db.FaceSuggestionDecisions.Add(new FaceSuggestionDecision
            {
                FaceId = id,
                PerformerId = dto.PerformerId,
                UserId = userId,
                Decision = normalizedDecision,
            });
        }
        else
        {
            decision.Decision = normalizedDecision;
        }

        if (normalizedDecision == FaceSuggestionDecisionValues.Accept)
        {
            await facePerformerPropagationService.ApplyLinkChangeAsync(id, face.PerformerId, dto.PerformerId, cancellationToken);
            face.PerformerId = dto.PerformerId;
            await TrySetLocalPerformerImageFromFaceAsync(face, performer, dto.SetPerformerImage, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (normalizedDecision == FaceSuggestionDecisionValues.Accept)
        {
            // A reference (metadata-server) match that resolved to an existing local performer arrives
            // here as a plain positive-id accept, so the reference enrichment was skipped. Record this
            // server's remote id on the performer (and scrape it when enabled).
            await RecordReferencePerformerLinkAsync(dto.PerformerId, dto.ReferenceEndpoint, dto.ReferenceExternalId, dto.ReferenceUpdateMetadata, cancellationToken);
            await InvalidateSuggestionForLinkChangeAsync(id, cancellationToken);
        }
        else
            await InvalidateSuggestionAsync(new[] { id }, cancellationToken);
        return Ok(await LoadFaceDtoAsync(id, cancellationToken));
    }

    // When a reference (metadata-server) match resolved to an existing local performer, the accept comes
    // in as a normal positive-id link, so the reference enrichment never ran. Record this server's remote
    // id on the linked performer (and scrape it to refresh image/bio/aliases when the user enabled
    // "Update existing performers from metadata servers"). No-op for non-reference links, when the
    // performer already carries the id, or when no importer is registered.
    private async Task RecordReferencePerformerLinkAsync(int performerId, string? endpoint, string? externalId, bool updateMetadata, CancellationToken cancellationToken)
    {
        if (referencePerformerImporter is null || performerId <= 0
            || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(externalId))
            return;

        await referencePerformerImporter.TryImportAsync(performerId, endpoint!, externalId!, importMetadata: updateMetadata, cancellationToken);
    }

    private async Task<FaceDto> LoadFaceDtoAsync(int id, CancellationToken cancellationToken)
    {
        var updated = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return MapToDto(updated, fieldProvenance: await LoadFaceFieldProvenanceAsync(updated.Id, cancellationToken));
    }

    private async Task<FaceSuggestionDecisionOutcome?> TryHandleProviderSuggestionDecisionAsync(int faceId, FaceSuggestionDecisionDto dto, string normalizedDecision, CancellationToken cancellationToken)
    {
        var handlers = ActiveSuggestionDecisionHandlers();
        if (handlers.Count == 0)
            return null;

        var request = new FaceSuggestionDecisionRequest(faceId, dto.PerformerId, normalizedDecision, dto.SetPerformerImage == true, dto.SecondaryPerformerIds);
        foreach (var handler in handlers)
        {
            var outcome = await handler.TryHandleAsync(request, cancellationToken);
            if (outcome.Handled)
                return outcome;
        }

        return null;
    }

    [HttpPost("{id:int}/merge-into")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> MergeInto(int id, [FromBody] FaceMergeDto dto, CancellationToken cancellationToken)
    {
        if (id == dto.TargetFaceId)
            return ValidationProblem("A face cannot be merged into itself.");

        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();
        if (face.MergedIntoFaceId.HasValue)
            return ValidationProblem("Cannot merge a face that has already been merged.");

        var target = await db.Faces.AsNoTracking().FirstOrDefaultAsync(item => item.Id == dto.TargetFaceId, cancellationToken);
        if (target is null)
            return ValidationProblem($"Target face {dto.TargetFaceId} was not found.");

        if (target.MergedIntoFaceId.HasValue)
            return ValidationProblem("Cannot merge into a face that has already been merged.");

        var strategy = db.Database.CreateExecutionStrategy();
        var targetGainedPerformer = false;
        await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

            var workingFace = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken)
                ?? throw new InvalidOperationException("The source face changed while the merge was being applied.");
            var workingTarget = await db.Faces.FirstOrDefaultAsync(item => item.Id == dto.TargetFaceId, cancellationToken)
                ?? throw new InvalidOperationException("The target face changed while the merge was being applied.");
            if (workingFace.MergedIntoFaceId.HasValue || workingTarget.MergedIntoFaceId.HasValue)
                throw new InvalidOperationException("A face changed merge state while the merge was being applied.");

            var sourceAppearances = await db.FaceAppearances
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(appearance => appearance.FaceId == workingFace.Id)
                .ToListAsync(cancellationToken);
            var targetHosts = await db.FaceAppearances
                .AsNoTracking()
                .IgnoreQueryFilters()
                .Where(appearance => appearance.FaceId == workingTarget.Id)
                .Select(appearance => new { appearance.HostType, appearance.HostId })
                .ToListAsync(cancellationToken);
            var affectedHosts = sourceAppearances
                .Select(appearance => (appearance.HostType, appearance.HostId))
                .Concat(targetHosts.Select(host => (host.HostType, host.HostId)))
                .Distinct()
                .ToArray();

            db.FaceAppearances.AddRange(sourceAppearances.Select(appearance => new FaceAppearance
            {
                FaceId = workingTarget.Id,
                HostType = appearance.HostType,
                HostId = appearance.HostId,
                FirstSeenAtSec = appearance.FirstSeenAtSec,
                LastSeenAtSec = appearance.LastSeenAtSec,
                SampleCount = appearance.SampleCount,
                RetainedSpatialSampleCount = appearance.RetainedSpatialSampleCount,
                SegmentCount = appearance.SegmentCount,
                RepresentativeFrameSec = appearance.RepresentativeFrameSec,
                TopConfidence = appearance.TopConfidence,
                GroupKey = appearance.GroupKey,
                Payload = appearance.Payload is null
                    ? null
                    : JsonDocument.Parse(appearance.Payload.RootElement.GetRawText()),
                SourceKey = appearance.SourceKey,
                SourceRunId = appearance.SourceRunId,
            }));

            workingFace.MergedIntoFaceId = workingTarget.Id;
            targetGainedPerformer = false;
            if (workingFace.PerformerId.HasValue && workingTarget.PerformerId == null)
            {
                workingTarget.PerformerId = workingFace.PerformerId;
                targetGainedPerformer = true;
            }
            if (string.IsNullOrWhiteSpace(workingTarget.Label) && !string.IsNullOrWhiteSpace(workingFace.Label))
                workingTarget.Label = workingFace.Label;

            await db.SaveChangesAsync(cancellationToken);

            foreach (var (hostType, hostId) in affectedHosts)
                await facePerformerPropagationService.ReconcileHostUnscopedAsync(hostType, hostId, cancellationToken);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
        if (targetGainedPerformer)
            await InvalidateSuggestionForLinkChangeAsync(dto.TargetFaceId, cancellationToken);

        var merged = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return Ok(MapToDto(merged));
    }

    [HttpPost("{id:int}/ignore")]
    [RequiresPermission(Permissions.FacesWrite)]
    [RequiresEntityAccess(EntityKinds.Face, Permissions.FacesWrite)]
    public async Task<ActionResult<FaceDto>> SetIgnored(int id, [FromBody] FaceIgnoreDto dto, CancellationToken cancellationToken)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return NotFound();

        face.Ignored = dto.Ignored;
        await db.SaveChangesAsync(cancellationToken);

        var updated = await db.Faces
            .AsNoTracking()
            .Include(item => item.Performer)
            .FirstAsync(item => item.Id == id, cancellationToken);

        return Ok(MapToDto(updated));
    }

    [HttpGet("{id:int}/similar")]
    public async Task<ActionResult<PaginatedResponse<FaceSimilarDto>>> GetSimilar(
        int id,
        [FromQuery] string? kindFamily,
        [FromQuery] string? q,
        [FromQuery] string? sort,
        [FromQuery] string? direction,
        [FromQuery] int? seed,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 18,
        [FromQuery] int k = 80,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        perPage = Math.Clamp(perPage, 1, 250);
        var candidateCount = Math.Clamp(k, 1, 250);

        // Face similarity is a face-reading feature, so callers do not need broad access to the raw
        // embeddings API. Establish source-face visibility under the normal face filter first, then
        // bypass only the embedding permission filter while resolving and ranking internal vectors.
        var sourceFaceVisible = await db.Faces
            .AsNoTracking()
            .AnyAsync(face => face.Id == id && face.MergedIntoFaceId == null, cancellationToken);
        if (!sourceFaceVisible)
            return Ok(new PaginatedResponse<FaceSimilarDto>(Array.Empty<FaceSimilarDto>(), 0, page, perPage));

        Embedding? sourceEmbedding;
        IReadOnlyList<EmbeddingSearchResult> results;
        using (db.SuppressEmbeddingReadAuthorizationFilter())
        {
            sourceEmbedding = await db.Embeddings
                .AsNoTracking()
                .Where(embedding =>
                    embedding.HostType == EmbeddingHostType.Face &&
                    embedding.HostId == id &&
                    embedding.Modality == EmbeddingModality.Face &&
                    (kindFamily == null || embedding.KindFamily == kindFamily))
                .OrderByDescending(embedding => embedding.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (sourceEmbedding is null)
                return Ok(new PaginatedResponse<FaceSimilarDto>(Array.Empty<FaceSimilarDto>(), 0, page, perPage));

            results = await embeddingService.KnnAsync(
                sourceEmbedding.Vector,
                candidateCount + 1,
                new EmbeddingSearchOptions
                {
                    HostType = EmbeddingHostType.Face,
                    KindFamily = sourceEmbedding.KindFamily,
                    Modality = EmbeddingModality.Face,
                },
                cancellationToken);
        }

        var faceIds = results
            .Where(result => result.Embedding.HostId != id)
            .Select(result => result.Embedding.HostId)
            .Distinct()
            .Take(candidateCount)
            .ToArray();

        if (faceIds.Length == 0)
            return Ok(new PaginatedResponse<FaceSimilarDto>(Array.Empty<FaceSimilarDto>(), 0, page, perPage));

        var faces = await db.Faces
            .AsNoTracking()
            .Include(face => face.Performer)
            // Merged faces are absorbed into their target, so they must not appear as similar-face results.
            .Where(face => faceIds.Contains(face.Id) && face.MergedIntoFaceId == null)
            .ToDictionaryAsync(face => face.Id, cancellationToken);

        var computedCounts = await LoadComputedCountsAsync(faceIds, cancellationToken);

        var response = results
            .Where(result => result.Embedding.HostId != id)
            .GroupBy(result => result.Embedding.HostId)
            .Select(group => group.OrderBy(result => result.Distance).First())
            .OrderBy(result => result.Distance)
            .Take(candidateCount)
            .Where(result => faces.ContainsKey(result.Embedding.HostId))
            .Select(result => MapToSimilarDto(
                faces[result.Embedding.HostId],
                computedCounts.GetValueOrDefault(result.Embedding.HostId),
                result.Distance))
            .ToList();

        if (!string.IsNullOrWhiteSpace(q))
        {
            response = response
                .Where(face =>
                    (!string.IsNullOrWhiteSpace(face.Label) && face.Label.Contains(q, StringComparison.OrdinalIgnoreCase))
                    || (!string.IsNullOrWhiteSpace(face.PerformerName) && face.PerformerName.Contains(q, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        response = ApplySimilarSort(response, sort, direction, seed);
        var totalCount = response.Count;
        var pageItems = response
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToList();
        var coverFallbacks = await LoadFaceCoverFallbackUrlsAsync(
            pageItems.Select(item => faces[item.Id]).ToArray(),
            cancellationToken);
        pageItems = pageItems
            .Select(item => item.CoverImageUrl is null
                ? item with { CoverImageUrl = coverFallbacks.GetValueOrDefault(item.Id) }
                : item)
            .ToList();

        return Ok(new PaginatedResponse<FaceSimilarDto>(pageItems, totalCount, page, perPage));
    }

    private async Task<IReadOnlyList<FaceHostFaceDto>> LoadHostFacesAsync(FaceAppearanceHostType hostType, int hostId, CancellationToken cancellationToken)
    {
        var appearances = await db.FaceAppearances
            .AsNoTracking()
            .Include(appearance => appearance.Face)
                .ThenInclude(face => face!.Performer)
            .Where(appearance => appearance.HostType == hostType && appearance.HostId == hostId && appearance.Face != null && appearance.Face.MergedIntoFaceId == null)
            .OrderByDescending(appearance => appearance.TopConfidence ?? 0)
            .ThenBy(appearance => appearance.FaceId)
            .ToListAsync(cancellationToken);

        var computedCounts = await LoadComputedCountsAsync(appearances.Select(appearance => appearance.FaceId).Distinct().ToArray(), cancellationToken);
        return appearances
            .GroupBy(appearance => appearance.FaceId)
            .Select(group =>
            {
                var primaryAppearance = group
                    .OrderByDescending(appearance => appearance.TopConfidence ?? 0)
                    .ThenBy(appearance => appearance.Id)
                    .First();
                var face = primaryAppearance.Face!;
                var hasCounts = computedCounts.TryGetValue(face.Id, out var counts);
                return new FaceHostFaceDto(
                    face.Id,
                    face.Label,
                    face.PerformerId,
                    face.Performer?.Name,
                    face.CoverBlobId is null ? null : EntityImageUrls.Face(ControllerContext.HttpContext, face.Id, face.UpdatedAt),
                    hasCounts ? counts.AppearanceCount : face.AppearanceCount,
                    hasCounts ? counts.FrameSampleCount : face.FrameSampleCount,
                    hasCounts ? counts.VideoCount : face.VideoCount,
                    hasCounts ? counts.ImageCount : face.ImageCount,
                    MinOrNull(group.Select(appearance => appearance.FirstSeenAtSec)),
                    MaxOrNull(group.Select(appearance => appearance.LastSeenAtSec)),
                    MaxFloatOrNull(group.Select(appearance => appearance.TopConfidence)),
                    group.Count());
            })
            .OrderByDescending(face => face.TopConfidence ?? 0)
            .ThenBy(face => face.Id)
            .ToList();

        static double? MinOrNull(IEnumerable<double?> values)
        {
            var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
            return resolved.Length == 0 ? null : resolved.Min();
        }

        static double? MaxOrNull(IEnumerable<double?> values)
        {
            var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
            return resolved.Length == 0 ? null : resolved.Max();
        }

        static float? MaxFloatOrNull(IEnumerable<float?> values)
        {
            var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
            return resolved.Length == 0 ? null : resolved.Max();
        }
    }

    private static IQueryable<Face> ApplyFaceSort(CoveContext db, IQueryable<Face> query, string? sort, bool descending, int? seed = null)
    {
        var normalized = (sort ?? string.Empty).Trim().ToLowerInvariant();
        if (FilterHelpers.TryParseCustomFieldSort(normalized, out _, out _))
            return query.ApplyCustomFieldSort(db, CustomFieldEntityTypes.Face, normalized, descending);

        // Back-compat: older clients baked the direction into the sort key (e.g. "video_count_desc",
        // "label_asc"). Strip the trailing direction suffix and let it drive the descending flag so the
        // single-key sorts + the shared asc/desc toggle behave the same as every other list in the app.
        if (normalized.EndsWith("_asc", StringComparison.Ordinal))
        {
            normalized = normalized[..^"_asc".Length];
            descending = false;
        }
        else if (normalized.EndsWith("_desc", StringComparison.Ordinal))
        {
            normalized = normalized[..^"_desc".Length];
            descending = true;
        }

        return normalized switch
        {
            "label" => OrderFacesBy(query, face => face.Label ?? (face.Performer != null ? face.Performer.Name : string.Empty), descending),
            "performer_name" => descending
                ? query.OrderByDescending(face => face.Performer != null ? face.Performer.Name : string.Empty).ThenByDescending(face => face.Label).ThenByDescending(face => face.Id)
                : query.OrderBy(face => face.Performer != null ? face.Performer.Name : string.Empty).ThenBy(face => face.Label).ThenBy(face => face.Id),
            "primary_source_key" => OrderFacesBy(query, face => face.PrimarySourceKey ?? string.Empty, descending),
            "ignored" => OrderFacesBy(query, face => face.Ignored, descending),
            "merged" => OrderFacesBy(query, face => face.MergedIntoFaceId != null, descending),
            "cover_present" => OrderFacesBy(query, face => face.CoverBlobId != null && face.CoverBlobId != string.Empty, descending),
            "detection_count" => OrderFacesBy(query, face => face.DetectionCount, descending),
            "appearance_count" => OrderFacesBy(query, face => face.AppearanceCount, descending),
            "frame_sample_count" => OrderFacesBy(query, face => face.FrameSampleCount, descending),
            "video_count" => OrderFacesBy(query, face => face.VideoCount, descending),
            "image_count" => OrderFacesBy(query, face => face.ImageCount, descending),
            "random" => SeededRandomOrdering.OrderBy(query, seed, face => face.Id, descending),
            "created" or "created_at" => OrderFacesBy(query, face => face.CreatedAt, descending),
            "updated" or "updated_at" => OrderFacesBy(query, face => face.UpdatedAt, descending),
            // Composite "best ordering" sort used as the default surface; intentionally direction-agnostic.
            "appearance" => query.OrderBy(face => face.MergedIntoFaceId != null).ThenByDescending(face => face.AppearanceCount).ThenByDescending(face => face.FrameSampleCount).ThenBy(face => face.Label).ThenBy(face => face.Id),
            // Suggestion review ordering: unlinked faces with the strongest suggestions first; direction-agnostic.
            "suggestion_confidence" => query.OrderBy(face => face.PerformerId != null).ThenByDescending(face => face.TopSuggestionConfidence ?? -1f).ThenByDescending(face => face.UpdatedAt).ThenBy(face => face.Id),
            _ => query.OrderBy(face => face.MergedIntoFaceId != null).ThenByDescending(face => face.AppearanceCount).ThenByDescending(face => face.FrameSampleCount).ThenBy(face => face.Label).ThenBy(face => face.Id),
        };
    }

    private static IQueryable<Face> OrderFacesBy<TKey>(IQueryable<Face> query, Expression<Func<Face, TKey>> keySelector, bool descending)
        => descending
            ? query.OrderByDescending(keySelector).ThenByDescending(face => face.Id)
            : query.OrderBy(keySelector).ThenBy(face => face.Id);

    private static bool IsSuggestionConfidenceSort(string? sort)
        => string.Equals(sort?.Trim(), "suggestion_confidence", StringComparison.OrdinalIgnoreCase);

    private async Task<List<FaceAppearanceDto>> LoadFaceAppearanceItemsAsync(int faceId, CancellationToken cancellationToken)
    {
        var appearances = await db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.FaceId == faceId)
            .OrderBy(appearance => appearance.HostType)
            .ThenByDescending(appearance => appearance.LastSeenAtSec ?? appearance.FirstSeenAtSec ?? double.MinValue)
            .ThenByDescending(appearance => appearance.UpdatedAt)
            .ToListAsync(cancellationToken);

        if (appearances.Count == 0)
            return await BuildFallbackAppearanceItemsAsync(faceId, cancellationToken);

        Dictionary<int, string?> videoTitles = [];
        var videoIds = appearances
            .Where(appearance => appearance.HostType == FaceAppearanceHostType.Video)
            .Select(appearance => appearance.HostId)
            .Distinct()
            .ToArray();
        if (videoIds.Length > 0)
        {
            videoTitles = await db.Videos
                .AsNoTracking()
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionaryAsync(video => video.Id, video => video.Title, cancellationToken);
        }

        Dictionary<int, string?> imageTitles = [];
        var imageIds = appearances
            .Where(appearance => appearance.HostType == FaceAppearanceHostType.Image)
            .Select(appearance => appearance.HostId)
            .Distinct()
            .ToArray();
        if (imageIds.Length > 0)
        {
            imageTitles = await db.Images
                .AsNoTracking()
                .Where(image => imageIds.Contains(image.Id))
                .ToDictionaryAsync(image => image.Id, image => image.Title, cancellationToken);
        }

        return appearances
            .GroupBy(appearance => new { appearance.HostType, appearance.HostId })
            .Select(group =>
            {
                var primaryAppearance = group
                    .OrderByDescending(appearance => appearance.TopConfidence ?? 0)
                    .ThenBy(appearance => appearance.Id)
                    .First();
                return new FaceAppearanceDto(
                    primaryAppearance.Id,
                    primaryAppearance.HostType == FaceAppearanceHostType.Video ? "video" : "image",
                    primaryAppearance.HostId,
                    ResolveAppearanceTitle(primaryAppearance, videoTitles, imageTitles),
                    ResolveAppearanceThumbnailUrl(primaryAppearance.HostType, primaryAppearance.HostId),
                    group.Sum(appearance => appearance.SampleCount),
                    group.Sum(appearance => appearance.RetainedSpatialSampleCount),
                    group.Sum(appearance => appearance.SegmentCount),
                    MinOrNull(group.Select(appearance => appearance.FirstSeenAtSec)),
                    MaxOrNull(group.Select(appearance => appearance.LastSeenAtSec)),
                    MaxFloatOrNull(group.Select(appearance => appearance.TopConfidence)));
            })
            .ToList();
    }

    private static List<FaceAppearanceDto> ApplyAppearanceSort(IEnumerable<FaceAppearanceDto> items, string? sort, string? direction, int? seed)
    {
        var normalized = (sort ?? string.Empty).Trim().ToLowerInvariant();
        var ascending = ResolveSortDirection(direction, normalized is "title" or "host_type");

        return normalized switch
        {
            "random" => OrderSeededRandom(items, item => item.AppearanceId, seed, ascending).ToList(),
            "title" => OrderBy(items, item => item.Title, ascending).ThenBy(item => item.HostId).ToList(),
            "host_type" => OrderBy(items, item => item.HostType, ascending).ThenBy(item => item.Title).ToList(),
            "sample_count" => OrderBy(items, item => item.FrameSampleCount, ascending).ThenBy(item => item.Title).ToList(),
            "confidence" => OrderBy(items, item => item.TopConfidence ?? float.MinValue, ascending).ThenBy(item => item.Title).ToList(),
            "first_seen" => OrderBy(items, item => item.FirstSeenAtSec ?? double.MinValue, ascending).ThenBy(item => item.Title).ToList(),
            _ => OrderBy(items, item => item.LastSeenAtSec ?? item.FirstSeenAtSec ?? double.MinValue, ascending).ThenBy(item => item.Title).ToList(),
        };
    }

    private static List<FaceSimilarDto> ApplySimilarSort(IEnumerable<FaceSimilarDto> items, string? sort, string? direction, int? seed)
    {
        var normalized = (sort ?? string.Empty).Trim().ToLowerInvariant();
        var ascending = ResolveSortDirection(direction, normalized is "" or "distance" or "label");

        return normalized switch
        {
            "random" => OrderSeededRandom(items, item => item.Id, seed, ascending).ToList(),
            "label" => OrderBy(items, item => item.Label ?? item.PerformerName ?? string.Empty, ascending).ThenBy(item => item.Id).ToList(),
            "updated_at" => OrderBy(items, item => item.UpdatedAt, ascending).ThenBy(item => item.Id).ToList(),
            "appearance_count" => OrderBy(items, item => item.AppearanceCount, ascending).ThenBy(item => item.Distance).ToList(),
            "video_count" => OrderBy(items, item => item.VideoCount, ascending).ThenBy(item => item.Distance).ToList(),
            "image_count" => OrderBy(items, item => item.ImageCount, ascending).ThenBy(item => item.Distance).ToList(),
            _ => OrderBy(items, item => item.Distance, ascending).ThenBy(item => item.Id).ToList(),
        };
    }

    private static IOrderedEnumerable<TItem> OrderBy<TItem, TKey>(IEnumerable<TItem> items, Func<TItem, TKey> keySelector, bool ascending)
        where TKey : IComparable<TKey>
        => ascending ? items.OrderBy(keySelector) : items.OrderByDescending(keySelector);

    private static IOrderedEnumerable<TItem> OrderSeededRandom<TItem>(IEnumerable<TItem> items, Func<TItem, int> idSelector, int? seed, bool ascending)
    {
        var normalizedSeed = Math.Abs((long)(seed ?? 1));
        if (normalizedSeed == 0) normalizedSeed = 1;
        long Primary(TItem item) => ((long)idSelector(item) * 17L + normalizedSeed * 31L) % 13L;
        long Secondary(TItem item) => ((long)idSelector(item) * 101L + normalizedSeed * 131L) % 97L;
        long Tertiary(TItem item) => ((long)idSelector(item) * 1103515245L + normalizedSeed * 12345L) % 2147483647L;

        return ascending
            ? items.OrderBy(Primary).ThenBy(Secondary).ThenBy(Tertiary).ThenBy(idSelector)
            : items.OrderByDescending(Primary).ThenByDescending(Secondary).ThenByDescending(Tertiary).ThenByDescending(idSelector);
    }

    private static bool ResolveSortDirection(string? direction, bool defaultAscending)
        => string.Equals(direction, "asc", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(direction, "desc", StringComparison.OrdinalIgnoreCase) && defaultAscending);

    private async Task<IReadOnlyList<FaceDto>> LoadReviewFacesForHostAsync(FaceAppearanceHostType hostType, int hostId, int take, CancellationToken cancellationToken)
    {
        var faceIds = await db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.HostType == hostType && appearance.HostId == hostId)
            .Select(appearance => appearance.FaceId)
            .Distinct()
            .ToArrayAsync(cancellationToken);
        if (faceIds.Length == 0)
            return [];

        var candidateTake = Math.Clamp(take * 4, take, 100);
        var faces = await db.Faces
            .AsNoTracking()
            .Include(face => face.Performer)
            .Where(face => faceIds.Contains(face.Id) && face.PerformerId == null && face.MergedIntoFaceId == null && !face.Ignored)
            .OrderByDescending(face => face.AppearanceCount)
            .ThenByDescending(face => face.FrameSampleCount)
            .ThenBy(face => face.Id)
            .Take(candidateTake)
            .ToListAsync(cancellationToken);
        if (faces.Count == 0)
            return [];

        var computedCounts = await LoadComputedCountsAsync(faces.Select(face => face.Id).ToArray(), cancellationToken);
        var topSuggestions = await BuildTopSuggestionsAsync(faces, cancellationToken);
        var coverFallbacks = await LoadFaceCoverFallbackUrlsAsync(faces, cancellationToken);
        return faces
            .Where(face => topSuggestions.ContainsKey(face.Id))
            .OrderByDescending(face => topSuggestions[face.Id].Confidence)
            .ThenByDescending(face => face.AppearanceCount)
            .Take(take)
            .Select(face => MapToDto(face, computedCounts.GetValueOrDefault(face.Id), topSuggestions.GetValueOrDefault(face.Id), coverFallbackUrl: coverFallbacks.GetValueOrDefault(face.Id)))
            .ToList();
    }

    private async Task<bool> DeleteFaceAsync(
        int id,
        CancellationToken cancellationToken,
        ICollection<ClearedFaceRunEvidence>? clearedEvidence = null,
        ISet<(FaceAppearanceHostType HostType, int HostId)>? propagationHosts = null)
    {
        var face = await db.Faces.FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (face is null)
            return false;

        var mergedFaces = await db.Faces
            .Where(item => item.MergedIntoFaceId == id)
            .ToListAsync(cancellationToken);
        var detections = await db.Detections
            .Where(detection => detection.RefId == id && detection.RefKind != null && detection.RefKind.ToLower() == "face")
            .ToListAsync(cancellationToken);
        var appearances = await db.FaceAppearances
            .IgnoreQueryFilters()
            .Where(appearance => appearance.FaceId == id)
            .ToListAsync(cancellationToken);
        if (propagationHosts is not null)
        {
            foreach (var appearance in appearances)
                propagationHosts.Add((appearance.HostType, appearance.HostId));

            var mergedFaceIds = mergedFaces.Select(item => item.Id).ToArray();
            if (mergedFaceIds.Length > 0)
            {
                var restoredHosts = await db.FaceAppearances
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(appearance => mergedFaceIds.Contains(appearance.FaceId))
                    .Select(appearance => new { appearance.HostType, appearance.HostId })
                    .Distinct()
                    .ToListAsync(cancellationToken);
                foreach (var host in restoredHosts)
                    propagationHosts.Add((host.HostType, host.HostId));
            }
        }
        var embeddings = await db.Embeddings
            .Where(embedding => embedding.HostType == EmbeddingHostType.Face && embedding.HostId == id)
            .ToListAsync(cancellationToken);
        var segments = await db.Segments
            .Where(segment => segment.RefId == id && segment.Kind != null && segment.Kind.ToLower() == "face")
            .ToListAsync(cancellationToken);
        var coverBlobId = face.CoverBlobId;

        foreach (var participant in ActiveLifecycleParticipants())
        {
            await participant.OnDeletingAsync(face, cancellationToken);
        }

        foreach (var mergedFace in mergedFaces)
        {
            mergedFace.MergedIntoFaceId = face.MergedIntoFaceId;
        }

        if (clearedEvidence is not null)
        {
            // Capture the (host, model-key) of the face detections AND embeddings being removed so that, once a
            // host has no faces left, the matching face models can be pruned from its AI run records. The keys
            // are the categories the AI server reported the work under (e.g. "face_detections"/"face_embeddings").
            // Embeddings are hosted on the Face, so report them against the asset hosts the face was detected on.
            var faceModelKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var detection in detections)
            {
                if (TryReadModelKey(detection.Extra, out var detectionKey))
                    faceModelKeys.Add(detectionKey);
            }

            foreach (var embedding in embeddings)
            {
                if (TryReadModelKey(embedding.Meta, out var embeddingKey))
                    faceModelKeys.Add(embeddingKey);
            }

            if (faceModelKeys.Count > 0)
            {
                foreach (var host in detections.Select(detection => (detection.HostType, detection.HostId)).Distinct())
                {
                    foreach (var modelKey in faceModelKeys)
                        clearedEvidence.Add(new ClearedFaceRunEvidence(host.HostType, host.HostId, modelKey));
                }
            }
        }

        if (detections.Count > 0)
            db.Detections.RemoveRange(detections);

        if (appearances.Count > 0)
            db.FaceAppearances.RemoveRange(appearances);

        if (embeddings.Count > 0)
            db.Embeddings.RemoveRange(embeddings);

        if (segments.Count > 0)
            db.Segments.RemoveRange(segments);

        db.Faces.Remove(face);

        if (!string.IsNullOrWhiteSpace(coverBlobId))
        {
            try
            {
                await blobService.DeleteBlobAsync(coverBlobId, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete face cover blob {BlobId} after deleting face {FaceId}.", coverBlobId, id);
            }
        }

        return true;
    }

    // (host, model) of a removed face detection, recorded during deletion so its run evidence can be
    // pruned once the host has no faces left.
    private readonly record struct ClearedFaceRunEvidence(DetectionHostType HostType, int HostId, string ModelKey);

    // After a host's faces are deleted, notify lifecycle participants that the host's face run evidence was
    // cleared — but only when no faces remain on the host. A host that genuinely has no faces never reaches
    // here (it never had detections to delete), so it is never needlessly reported. Participants that record
    // run/processing history (e.g. an AI extension) prune their own evidence for the reported model keys so a
    // re-run redoes the work; the host stays agnostic of any extension's run/source layout.
    private async Task NotifyHostFacesClearedAsync(IReadOnlyCollection<ClearedFaceRunEvidence> cleared, CancellationToken cancellationToken)
    {
        var participants = ActiveLifecycleParticipants();
        if (cleared.Count == 0 || participants.Count == 0)
            return;

        foreach (var hostGroup in cleared.GroupBy(item => (item.HostType, item.HostId)))
        {
            var (hostType, hostId) = hostGroup.Key;

            var stillHasFaces = await db.Detections.AnyAsync(
                detection => detection.HostType == hostType
                    && detection.HostId == hostId
                    && detection.RefKind != null
                    && detection.RefKind.ToLower() == "face",
                cancellationToken);
            if (stillHasFaces)
                continue;

            var modelKeys = hostGroup
                .Select(item => item.ModelKey)
                .Where(key => !string.IsNullOrWhiteSpace(key))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (modelKeys.Count == 0)
                continue;

            var evidence = new FaceRunEvidenceCleared(hostType, hostId, modelKeys);
            foreach (var participant in participants)
                await participant.OnHostFacesClearedAsync(evidence, cancellationToken);
        }
    }

    private static bool TryReadModelKey(JsonDocument? document, out string modelKey)
    {
        modelKey = string.Empty;
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("modelKey", out var element))
            return false;

        var raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        modelKey = raw.Trim();
        return true;
    }

    private static int? ResolveLocalPerformerId(FaceSuggestionDto suggestion)
        => suggestion.LocalPerformerId ?? (suggestion.PerformerId > 0 ? suggestion.PerformerId : null);

    private static float NormalizeConfidenceThreshold(float confidence)
        => confidence <= 1f ? confidence * 100f : confidence;

    // Reads the materialized top suggestion straight off the Face row. Linked faces never surface a
    // suggestion; an unmaterialized face (no stored top yet) returns null until the background
    // materializer computes it.
    private static FaceTopSuggestionDto? MapStoredTopSuggestion(Face face)
    {
        if (face.PerformerId.HasValue || face.TopSuggestionPerformerId is not int performerId)
            return null;

        return new FaceTopSuggestionDto(
            performerId,
            face.TopSuggestionPerformerName ?? string.Empty,
            face.TopSuggestionCoverImageUrl,
            face.TopSuggestionConfidence ?? 0f,
            face.TopSuggestionLocalPerformerId,
            face.TopSuggestionExternalUrl,
            face.TopSuggestionLocalPerformerHasImage,
            face.TopSuggestionLocalPerformerIsLocalOnly);
    }

    // Translates the suggestion-confidence criterion onto the stored Face.TopSuggestionConfidence column
    // so filtering happens in SQL. Mirrors MatchesConfidenceCriterion's modifier semantics. Values are
    // already normalized to the 0..100 scale by the caller.
    private static IQueryable<Face> ApplyStoredConfidenceCriterion(IQueryable<Face> query, string? modifier, float? value, float? value2)
    {
        if (modifier is null && !value.HasValue)
            return query;

        switch (modifier)
        {
            case "IS_NULL":
                // A materialized suggestion is required upstream, so "is null" matches nothing.
                return query.Where(face => face.TopSuggestionPerformerId == null);
            case "NOT_NULL":
                return query;
            case "NOT_EQUALS":
                return value.HasValue
                    ? query.Where(face => face.TopSuggestionConfidence < value.Value - 0.0001f || face.TopSuggestionConfidence > value.Value + 0.0001f)
                    : query;
            case "LESS_THAN":
                return value.HasValue ? query.Where(face => face.TopSuggestionConfidence < value.Value) : query;
            case "BETWEEN":
                if (value.HasValue && value2.HasValue)
                {
                    var lo = Math.Min(value.Value, value2.Value);
                    var hi = Math.Max(value.Value, value2.Value);
                    return query.Where(face => face.TopSuggestionConfidence >= lo && face.TopSuggestionConfidence <= hi);
                }
                return query;
            case "NOT_BETWEEN":
                if (value.HasValue && value2.HasValue)
                {
                    var lo = Math.Min(value.Value, value2.Value);
                    var hi = Math.Max(value.Value, value2.Value);
                    return query.Where(face => face.TopSuggestionConfidence < lo || face.TopSuggestionConfidence > hi);
                }
                return query;
            case "EQUALS":
                return value.HasValue
                    ? query.Where(face => face.TopSuggestionConfidence >= value.Value - 0.0001f && face.TopSuggestionConfidence <= value.Value + 0.0001f)
                    : query;
            default:
                return value.HasValue ? query.Where(face => face.TopSuggestionConfidence >= value.Value) : query;
        }
    }

    private Task InvalidateSuggestionForLinkChangeAsync(int faceId, CancellationToken cancellationToken)
        => suggestionMaintenance?.InvalidateForLinkChangeAsync(faceId, cancellationToken) ?? Task.CompletedTask;

    private Task InvalidateSuggestionAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken)
        => suggestionMaintenance?.InvalidateAsync(faceIds, cancellationToken) ?? Task.CompletedTask;

    private async Task<Dictionary<int, FaceTopSuggestionDto>> BuildTopSuggestionsAsync(IReadOnlyCollection<Face> faces, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var activeSuggesters = ActiveSuggesters();
        if (activeSuggesters.Count == 0)
        {
            return [];
        }

        var eligibleFaceIds = faces
            .Where(face => !face.PerformerId.HasValue)
            .Select(face => face.Id)
            .ToArray();
        if (eligibleFaceIds.Length == 0)
        {
            return [];
        }

        var blockedByFaceId = await LoadBlockedSuggestionIdsAsync(eligibleFaceIds, cancellationToken);
        logger.LogDebug("BuildTopSuggestions.LoadBlocked: {Ms}ms", sw.ElapsedMilliseconds);
        sw.Restart();

        var rankedSuggestionsByFaceId = await BuildRankedSuggestionsByFaceAsync(
            eligibleFaceIds,
            blockedByFaceId,
            TopSuggestionCandidateCount,
            cancellationToken,
            includeReferenceMatches: true);
        logger.LogDebug("BuildTopSuggestions.BuildRanked: {Ms}ms (eligibleFaces={Count})", sw.ElapsedMilliseconds, eligibleFaceIds.Length);

        var topSuggestions = new Dictionary<int, FaceTopSuggestionDto>(rankedSuggestionsByFaceId.Count);
        foreach (var (faceId, suggestions) in rankedSuggestionsByFaceId)
        {
            var top = suggestions.FirstOrDefault();
            if (top is not null)
            {
                topSuggestions[faceId] = MapTopSuggestion(top);
            }
        }
        return topSuggestions;
    }

    private async Task<Dictionary<int, IReadOnlyList<FaceSuggestionDto>>> BuildRankedSuggestionsByFaceAsync(
        IReadOnlyCollection<int> faceIds,
        IReadOnlyDictionary<int, HashSet<int>> blockedByFaceId,
        int maxResults,
        CancellationToken cancellationToken,
        bool includeReferenceMatches = true)
    {
        maxResults = Math.Clamp(maxResults, 1, 20);
        var distinctFaceIds = faceIds.Where(static faceId => faceId > 0).Distinct().ToArray();
        if (distinctFaceIds.Length == 0)
        {
            return [];
        }

        var activeSuggesters = ActiveSuggesters();
        if (activeSuggesters.Count == 0)
        {
            return [];
        }

        var suggestionOptions = new FaceSuggestionOptions(IncludeReferenceMatches: includeReferenceMatches);
        var suggestionsByFaceId = new Dictionary<int, List<FaceSuggestionDto>>();
        foreach (var suggester in activeSuggesters)
        {
            var suggesterSw = Stopwatch.StartNew();
            var batch = await suggester.SuggestForBatchAsync(distinctFaceIds, maxResults, suggestionOptions, cancellationToken);
            logger.LogDebug("Suggester {Name}.SuggestForBatch: {Ms}ms (results={Count})", suggester.GetType().Name, suggesterSw.ElapsedMilliseconds, batch.Count);
            foreach (var (faceId, suggestions) in batch)
            {
                if (!suggestionsByFaceId.TryGetValue(faceId, out var faceSuggestions))
                {
                    faceSuggestions = [];
                    suggestionsByFaceId[faceId] = faceSuggestions;
                }

                faceSuggestions.AddRange(suggestions);
            }
        }

        return suggestionsByFaceId.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<FaceSuggestionDto>)pair.Value
                .Where(item => !blockedByFaceId.TryGetValue(pair.Key, out var blockedPerformerIds) || !blockedPerformerIds.Contains(item.PerformerId))
                .GroupBy(item => item.PerformerId)
                .Select(group => group
                    .OrderByDescending(item => item.Confidence)
                    .ThenByDescending(item => item.Evidence.Count)
                    .ThenBy(item => item.PerformerName)
                    .First())
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.PerformerName)
                .Take(maxResults)
                .ToList());
    }

    private async Task<IReadOnlyList<FaceSuggestionDto>> BuildRankedSuggestionsAsync(
        int faceId,
        int maxResults,
        CancellationToken cancellationToken,
        bool includeReferenceMatches = true)
    {
        var blockedByFaceId = await LoadBlockedSuggestionIdsAsync(new[] { faceId }, cancellationToken);
        blockedByFaceId.TryGetValue(faceId, out var blockedIds);
        return await BuildRankedSuggestionsAsync(faceId, blockedIds, maxResults, cancellationToken, includeReferenceMatches);
    }

    private async Task<IReadOnlyList<FaceSuggestionDto>> BuildRankedSuggestionsAsync(
        int faceId,
        IReadOnlySet<int>? blockedPerformerIds,
        int maxResults,
        CancellationToken cancellationToken,
        bool includeReferenceMatches = true)
    {
        maxResults = Math.Clamp(maxResults, 1, 20);

        var activeSuggesters = ActiveSuggesters();
        if (activeSuggesters.Count == 0)
        {
            return [];
        }

        var suggestionOptions = new FaceSuggestionOptions(IncludeReferenceMatches: includeReferenceMatches);
        var suggestions = await Task.WhenAll(activeSuggesters.Select(suggester => suggester.SuggestForAsync(faceId, maxResults, suggestionOptions, cancellationToken)));
        return suggestions
            .SelectMany(items => items)
            .Where(item => blockedPerformerIds is null || !blockedPerformerIds.Contains(item.PerformerId))
            .GroupBy(item => item.PerformerId)
            .Select(group => group
                .OrderByDescending(item => item.Confidence)
                .ThenByDescending(item => item.Evidence.Count)
                .ThenBy(item => item.PerformerName)
                .First())
            .OrderByDescending(item => item.Confidence)
            .ThenBy(item => item.PerformerName)
            .Take(maxResults)
            .ToList();
    }

    private async Task<Dictionary<int, HashSet<int>>> LoadBlockedSuggestionIdsAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken)
    {
        if (faceIds.Count == 0 || principalAccessor?.Current?.UserId is not int userId)
        {
            return [];
        }

        var blockedRows = await db.FaceSuggestionDecisions
            .AsNoTracking()
            .Where(decision => faceIds.Contains(decision.FaceId) && decision.UserId == userId)
            .Select(decision => new { decision.FaceId, decision.PerformerId })
            .ToListAsync(cancellationToken);

        return blockedRows
            .GroupBy(item => item.FaceId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.PerformerId).ToHashSet());
    }

    private static FaceTopSuggestionDto MapTopSuggestion(FaceSuggestionDto suggestion) => new(
        suggestion.PerformerId,
        suggestion.PerformerName,
        suggestion.CoverImageUrl,
        suggestion.Confidence,
        suggestion.LocalPerformerId ?? (suggestion.PerformerId > 0 ? suggestion.PerformerId : null),
        suggestion.ExternalUrl,
        suggestion.LocalPerformerHasImage,
        suggestion.LocalPerformerIsLocalOnly);

    private async Task TrySetLocalPerformerImageFromFaceAsync(
        Face face,
        Performer performer,
        bool setPerformerImage,
        CancellationToken cancellationToken)
    {
        if (!setPerformerImage
            || string.IsNullOrWhiteSpace(face.CoverBlobId)
            || !string.IsNullOrWhiteSpace(performer.ImageBlobId)
            || performer.RemoteIds.Count > 0)
        {
            return;
        }

        var blob = await blobService.GetBlobAsync(face.CoverBlobId, cancellationToken);
        if (blob is null)
        {
            return;
        }

        await using var stream = blob.Value.Stream;
        performer.ImageBlobId = await blobService.StoreBlobAsync(stream, blob.Value.ContentType, cancellationToken);
    }

    private FaceDto MapToDto(Face face, FaceComputedCounts? computedCounts = null, FaceTopSuggestionDto? topSuggestion = null, IReadOnlyList<FieldProvenanceDto>? fieldProvenance = null, (int Index, int Count)? performerFaceOrdinal = null, string? coverFallbackUrl = null) => new(
        face.Id,
        face.Label,
        face.PerformerId,
        face.Performer?.Name,
        face.CoverBlobId is null
            ? coverFallbackUrl
            : EntityImageUrls.Face(ControllerContext.HttpContext, face.Id, face.UpdatedAt),
        face.Ignored,
        face.MergedIntoFaceId,
        computedCounts?.DetectionCount ?? face.DetectionCount,
        computedCounts?.VideoCount ?? face.VideoCount,
        computedCounts?.ImageCount ?? face.ImageCount,
        face.PrimarySourceKey,
        face.CreatedAt,
        face.UpdatedAt,
        computedCounts?.AppearanceCount ?? face.AppearanceCount,
        computedCounts?.FrameSampleCount ?? face.FrameSampleCount,
        topSuggestion,
        fieldProvenance?.ToList(),
        performerFaceOrdinal?.Index ?? 0,
        performerFaceOrdinal?.Count ?? 0);

    /// <summary>
    /// For the linked faces in <paramref name="faces"/>, returns each face id's 1-based position among
    /// all non-merged faces of the same performer (ordered by face id) plus that performer's total face
    /// count. Used to disambiguate "&lt;performer&gt; 1/2/3…" in lists.
    /// </summary>
    private async Task<Dictionary<int, (int Index, int Count)>> LoadPerformerFaceOrdinalsAsync(IEnumerable<Face> faces, CancellationToken cancellationToken)
    {
        var performerIds = faces
            .Where(face => face.PerformerId.HasValue)
            .Select(face => face.PerformerId!.Value)
            .Distinct()
            .ToArray();
        if (performerIds.Length == 0)
            return [];

        var rows = await db.Faces
            .AsNoTracking()
            .Where(face => face.PerformerId != null && performerIds.Contains(face.PerformerId.Value) && face.MergedIntoFaceId == null)
            .Select(face => new { face.Id, PerformerId = face.PerformerId!.Value })
            .ToListAsync(cancellationToken);

        var ordinals = new Dictionary<int, (int Index, int Count)>();
        foreach (var group in rows.GroupBy(row => row.PerformerId))
        {
            var orderedIds = group.Select(row => row.Id).OrderBy(id => id).ToList();
            for (var index = 0; index < orderedIds.Count; index++)
                ordinals[orderedIds[index]] = (index + 1, orderedIds.Count);
        }

        return ordinals;
    }

    private async Task<IReadOnlyList<FieldProvenanceDto>?> LoadFaceFieldProvenanceAsync(int faceId, CancellationToken cancellationToken)
        => fieldProvenanceService == null
            ? null
            : await fieldProvenanceService.GetForHostAsync(AffinityHostType.Face, faceId, cancellationToken);

    private Task RecordManualFaceFieldProvenanceAsync(int faceId, IReadOnlyDictionary<string, object?> fields, CancellationToken cancellationToken)
        => fieldProvenanceService == null || fields.Count == 0
            ? Task.CompletedTask
            : fieldProvenanceService.RecordManyAsync(AffinityHostType.Face, faceId, fields, "user", cancellationToken: cancellationToken);

    private FaceSimilarDto MapToSimilarDto(Face face, FaceComputedCounts? computedCounts, float distance) => new(
        face.Id,
        face.Label,
        face.PerformerId,
        face.Performer?.Name,
        face.CoverBlobId is null ? null : EntityImageUrls.Face(ControllerContext.HttpContext, face.Id, face.UpdatedAt),
        face.Ignored,
        face.MergedIntoFaceId,
        computedCounts?.DetectionCount ?? face.DetectionCount,
        computedCounts?.VideoCount ?? face.VideoCount,
        computedCounts?.ImageCount ?? face.ImageCount,
        face.PrimarySourceKey,
        face.CreatedAt,
        face.UpdatedAt,
        computedCounts?.AppearanceCount ?? face.AppearanceCount,
        computedCounts?.FrameSampleCount ?? face.FrameSampleCount,
        distance);

    private async Task<Dictionary<int, FaceComputedCounts>> LoadComputedCountsAsync(
        IReadOnlyCollection<int> faceIds,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        if (faceIds.Count == 0)
            return [];

        var distinctFaceIds = faceIds.Distinct().ToArray();
        var faceIdLongs = distinctFaceIds.Select(static id => (long)id).ToArray();

        // Aggregate at the database tier so we don't materialize every detection row
        // for every face on the page (a face can have tens of thousands of detections).
        var detectionAggregates = await db.Detections
            .AsNoTracking()
            .Where(detection =>
                detection.RefId.HasValue &&
                faceIdLongs.Contains(detection.RefId.Value) &&
                detection.RefKind != null &&
                detection.RefKind.ToLower() == "face")
            .GroupBy(detection => new
            {
                FaceId = (int)detection.RefId!.Value,
                detection.HostType,
                detection.HostId,
            })
            .Select(group => new
            {
                group.Key.FaceId,
                group.Key.HostType,
                group.Key.HostId,
                Count = group.Count(),
            })
            .ToListAsync(cancellationToken);
        logger.LogDebug("LoadComputedCounts.Detections: {Ms}ms (faces={Count})", sw.ElapsedMilliseconds, distinctFaceIds.Length);
        sw.Restart();

        var detectionCounts = detectionAggregates
            .GroupBy(row => row.FaceId)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var rows = group.ToList();
                    var totalDetections = rows.Sum(row => row.Count);
                    var videoCount = rows.Where(row => row.HostType == DetectionHostType.Video).Select(row => row.HostId).Distinct().Count();
                    var imageCount = rows.Where(row => row.HostType == DetectionHostType.Image).Select(row => row.HostId).Distinct().Count();
                    var hostCount = rows.Select(row => (row.HostType, row.HostId)).Distinct().Count();
                    return new FaceComputedCounts(
                        totalDetections,
                        videoCount,
                        imageCount,
                        hostCount,
                        totalDetections);
                });

        var storedCounts = await db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => distinctFaceIds.Contains(appearance.FaceId))
            .GroupBy(appearance => appearance.FaceId)
            .Select(group => new
            {
                FaceId = group.Key,
                AppearanceCount = group.Count(),
                FrameSampleCount = group.Sum(item => item.SampleCount),
                VideoCount = group.Where(item => item.HostType == FaceAppearanceHostType.Video).Select(item => item.HostId).Distinct().Count(),
                ImageCount = group.Where(item => item.HostType == FaceAppearanceHostType.Image).Select(item => item.HostId).Distinct().Count(),
            })
            .ToDictionaryAsync(
                item => item.FaceId,
                item => new FaceStoredCounts(item.AppearanceCount, item.FrameSampleCount, item.VideoCount, item.ImageCount),
                cancellationToken);
        logger.LogDebug("LoadComputedCounts.Appearances: {Ms}ms", sw.ElapsedMilliseconds);

        var computedCounts = new Dictionary<int, FaceComputedCounts>(distinctFaceIds.Length);
        foreach (var faceId in distinctFaceIds)
        {
            var detectionCount = detectionCounts.GetValueOrDefault(faceId);
            var storedCount = storedCounts.GetValueOrDefault(faceId);
            var videoCount = detectionCount.VideoCount > 0 ? detectionCount.VideoCount : storedCount.VideoCount;
            var imageCount = detectionCount.ImageCount > 0 ? detectionCount.ImageCount : storedCount.ImageCount;

            computedCounts[faceId] = new FaceComputedCounts(
                detectionCount.DetectionCount,
                videoCount,
                imageCount,
                // "Appearances" is the number of distinct hosts the face appears in (= videos + images),
                // matching the "Appears In" list which groups by host. A face can have multiple
                // appearance rows per host (one per track), so the raw row count would overstate it.
                videoCount + imageCount,
                storedCount.FrameSampleCount > 0 ? storedCount.FrameSampleCount : detectionCount.FrameSampleCount);
        }

        return computedCounts;
    }

    // A face with no stored CoverBlobId still shows an image on its detail page, because the detail hero
    // falls back to a crop of the face's best detection (see ui buildFaceHeroImageUrls). The faces LIST
    // only had the cover blob, so those same faces showed the fingerprint placeholder. Mirror the detail
    // fallback here: for each cover-less face, pick the representative detection (best role, then highest
    // cover-quality, then score) and expose its crop URL so the list and detail agree. One batched query.
    private async Task<Dictionary<int, string>> LoadFaceCoverFallbackUrlsAsync(IReadOnlyCollection<Face> faces, CancellationToken cancellationToken)
    {
        var coverlessFaceIds = faces
            .Where(face => string.IsNullOrEmpty(face.CoverBlobId))
            .Select(face => face.Id)
            .Distinct()
            .ToArray();
        if (coverlessFaceIds.Length == 0)
            return [];

        var coverlessFaceIdLongs = coverlessFaceIds.Select(static id => (long)id).ToArray();
        var detections = await db.Detections
            .AsNoTracking()
            .Where(detection =>
                detection.RefId.HasValue &&
                coverlessFaceIdLongs.Contains(detection.RefId.Value) &&
                detection.RefKind != null &&
                detection.RefKind.ToLower() == "face" &&
                detection.W > 0 &&
                detection.H > 0)
            .Select(detection => new
            {
                FaceId = (int)detection.RefId!.Value,
                detection.Id,
                detection.Score,
                detection.W,
                detection.H,
                detection.FrameWidth,
                detection.FrameHeight,
                detection.Extra,
            })
            .ToListAsync(cancellationToken);

        var result = new Dictionary<int, string>(coverlessFaceIds.Length);
        foreach (var group in detections.GroupBy(detection => detection.FaceId))
        {
            // Prefer detections that pass the quality/aspect gate (good, roughly-frontal crops).
            var plausible = group.Where(detection =>
            {
                var aspect = detection.H == 0 ? 0f : detection.W / detection.H;
                if (aspect < 0.45f || aspect > 1.8f)
                    return false;
                if (detection.FrameWidth <= 0 || detection.FrameHeight <= 0)
                    return true;
                var area = (detection.W * detection.H) / (float)(detection.FrameWidth * detection.FrameHeight);
                return area >= 0.005f;
            });

            // But if a face only ever appears in side-view/low-quality shots, still show its best available
            // detection rather than nothing — a face with no cover at all is useless. When a better, more
            // frontal image is later matched, AI.Extensions promotes it to a real CoverBlobId which
            // supersedes this fallback, so this never blocks a future upgrade.
            var candidates = plausible.Any() ? plausible : group;
            var best = candidates
                .OrderByDescending(detection => ReadDetectionRoleIsBest(detection.Extra) ? 1 : 0)
                .ThenByDescending(detection => ReadDetectionCoverQualityScore(detection.Extra))
                .ThenByDescending(detection => detection.Score)
                .ThenBy(detection => detection.Id)
                .FirstOrDefault();

            if (best is not null)
                result[group.Key] = EntityImageUrls.DetectionCrop(ControllerContext.HttpContext, best.Id);
        }

        return result;
    }

    private static bool ReadDetectionRoleIsBest(JsonDocument? extra)
        => extra is not null
            && extra.RootElement.ValueKind == JsonValueKind.Object
            && extra.RootElement.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            && string.Equals(role.GetString(), "best", StringComparison.OrdinalIgnoreCase);

    private static double ReadDetectionCoverQualityScore(JsonDocument? extra)
    {
        if (extra is null || extra.RootElement.ValueKind != JsonValueKind.Object || !extra.RootElement.TryGetProperty("coverQualityScore", out var value))
            return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String => double.TryParse(value.GetString(), out var parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private async Task<List<FaceAppearanceDto>> BuildFallbackAppearanceItemsAsync(int faceId, CancellationToken cancellationToken)
    {
        var detections = await db.Detections
            .AsNoTracking()
            .Where(detection => detection.RefId == faceId && detection.RefKind != null && detection.RefKind.ToLower() == "face")
            .Select(detection => new
            {
                detection.HostType,
                detection.HostId,
                detection.ObservedAtSec,
                detection.Score,
            })
            .ToListAsync(cancellationToken);

        var groupedDetections = detections
            .GroupBy(detection => (detection.HostType, detection.HostId))
            .OrderBy(group => group.Key.HostType)
            .ThenByDescending(group => group.Max(item => item.ObservedAtSec ?? double.MinValue))
            .ThenBy(group => group.Key.HostId)
            .ToList();

        Dictionary<int, string?> videoTitles = [];
        var videoIds = groupedDetections
            .Where(group => group.Key.HostType == DetectionHostType.Video)
            .Select(group => group.Key.HostId)
            .ToArray();
        if (videoIds.Length > 0)
        {
            videoTitles = await db.Videos
                .AsNoTracking()
                .Where(video => videoIds.Contains(video.Id))
                .ToDictionaryAsync(video => video.Id, video => video.Title, cancellationToken);
        }

        Dictionary<int, string?> imageTitles = [];
        var imageIds = groupedDetections
            .Where(group => group.Key.HostType == DetectionHostType.Image)
            .Select(group => group.Key.HostId)
            .ToArray();
        if (imageIds.Length > 0)
        {
            imageTitles = await db.Images
                .AsNoTracking()
                .Where(image => imageIds.Contains(image.Id))
                .ToDictionaryAsync(image => image.Id, image => image.Title, cancellationToken);
        }

        var items = groupedDetections
            .Select((group, index) =>
            {
                var hostType = group.Key.HostType == DetectionHostType.Video
                    ? FaceAppearanceHostType.Video
                    : FaceAppearanceHostType.Image;

                return new FaceAppearanceDto(
                    -(index + 1),
                    hostType == FaceAppearanceHostType.Video ? "video" : "image",
                    group.Key.HostId,
                    ResolveAppearanceTitle(hostType, group.Key.HostId, videoTitles, imageTitles),
                    ResolveAppearanceThumbnailUrl(hostType, group.Key.HostId),
                    group.Count(),
                    group.Count(),
                    0,
                    group.Min(item => item.ObservedAtSec),
                    group.Max(item => item.ObservedAtSec),
                    group.Max(item => (float?)item.Score));
            })
            .ToList();

        return items;
    }

    private static double? MinOrNull(IEnumerable<double?> values)
    {
        var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return resolved.Length == 0 ? null : resolved.Min();
    }

    private static double? MaxOrNull(IEnumerable<double?> values)
    {
        var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return resolved.Length == 0 ? null : resolved.Max();
    }

    private static float? MaxFloatOrNull(IEnumerable<float?> values)
    {
        var resolved = values.Where(static value => value.HasValue).Select(static value => value!.Value).ToArray();
        return resolved.Length == 0 ? null : resolved.Max();
    }

    private static string ResolveAppearanceTitle(
        FaceAppearance appearance,
        IReadOnlyDictionary<int, string?> videoTitles,
        IReadOnlyDictionary<int, string?> imageTitles)
        => ResolveAppearanceTitle(appearance.HostType, appearance.HostId, videoTitles, imageTitles);

    private static string ResolveAppearanceTitle(
        FaceAppearanceHostType hostType,
        int hostId,
        IReadOnlyDictionary<int, string?> videoTitles,
        IReadOnlyDictionary<int, string?> imageTitles) => hostType switch
    {
        FaceAppearanceHostType.Video => Clean(videoTitles.GetValueOrDefault(hostId)) ?? $"Video {hostId}",
        FaceAppearanceHostType.Image => Clean(imageTitles.GetValueOrDefault(hostId)) ?? $"Image {hostId}",
        _ => $"Host {hostId}",
    };

    private static string ResolveAppearanceThumbnailUrl(FaceAppearanceHostType hostType, int hostId) => hostType switch
    {
        FaceAppearanceHostType.Video => $"/api/stream/video/{hostId}/screenshot",
        FaceAppearanceHostType.Image => $"/api/stream/image/{hostId}/thumbnail?max=320",
        _ => string.Empty,
    };

    private static DetectionDto MapDetectionToDto(Detection detection) => new(
        detection.Id,
        detection.HostType,
        detection.HostId,
        detection.ObservedAtSec,
        detection.FrameWidth,
        detection.FrameHeight,
        detection.Class,
        detection.Score,
        detection.X,
        detection.Y,
        detection.W,
        detection.H,
        detection.Extra?.RootElement.Clone(),
        detection.RefKind,
        detection.RefId,
        detection.GroupKey,
        detection.SourceKey,
        detection.SourceRunId,
        detection.CreatedAt.ToString("o"),
        detection.UpdatedAt.ToString("o"));

    private async Task<FaceDeleteImpactDto> BuildDeleteImpactAsync(int faceId, bool hasCoverImage, CancellationToken cancellationToken)
    {
        var detectionCount = await db.Detections.CountAsync(
            detection => detection.RefId == faceId && detection.RefKind != null && detection.RefKind.ToLower() == "face",
            cancellationToken);
        var embeddingCount = await db.Embeddings.CountAsync(
            embedding => embedding.HostType == EmbeddingHostType.Face && embedding.HostId == faceId,
            cancellationToken);
        var segmentCount = await db.Segments.CountAsync(
            segment => segment.RefId == faceId && segment.Kind != null && segment.Kind.ToLower() == "face",
            cancellationToken);
        var releasedMergedFaceCount = await db.Faces.CountAsync(
            face => face.MergedIntoFaceId == faceId,
            cancellationToken);

        return new FaceDeleteImpactDto(
            detectionCount,
            embeddingCount,
            segmentCount,
            hasCoverImage,
            releasedMergedFaceCount);
    }

    private readonly record struct FaceComputedCounts(
        int DetectionCount,
        int VideoCount,
        int ImageCount,
        int AppearanceCount,
        int FrameSampleCount);

    private readonly record struct FaceStoredCounts(
        int AppearanceCount,
        int FrameSampleCount,
        int VideoCount,
        int ImageCount);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
