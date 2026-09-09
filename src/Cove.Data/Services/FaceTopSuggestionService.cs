using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

/// <summary>
/// Computes and persists the per-face "top suggestion" projection (<c>Face.TopSuggestion*</c>), and
/// services invalidations. This is the write side of the materialized suggestions: the faces list
/// reads the stored columns, while this service (driven by the background materializer and by
/// invalidation triggers) keeps them current — off the request path.
///
/// The stored value is the <em>global</em> top suggestion and deliberately ignores per-user reject
/// decisions; a single shared projection cannot be per-user. Per-user filtering still applies on the
/// single-face detail/suggestions endpoints, which remain compute-on-read — and on the list read path,
/// which overlays the caller's decisions onto the stored columns (see
/// <c>FacesController.ResolveDecidedTopSuggestionsAsync</c>). Never treat the stored columns as
/// user-facing without that overlay, or a rejected performer reappears the next time this runs.
/// </summary>
public sealed class FaceTopSuggestionService(
    CoveContext db,
    IEnumerable<IFaceSuggester> faceSuggesters,
    IExtensionServiceExchange? serviceExchange = null) : IFaceTopSuggestionMaintenance
{
    // How many candidate suggestions to request per face before taking the single best. Mirrors the
    // controller's TopSuggestionCandidateCount so list and detail agree on ranking.
    private const int CandidateCount = 3;

    private IReadOnlyList<IFaceSuggester> ActiveSuggesters()
        => faceSuggesters
            .Concat(serviceExchange?.GetAll<IFaceSuggester>() ?? [])
            .Where(suggester => suggester is not EmptyFaceSuggester)
            .Distinct()
            .ToArray();

    /// <summary>
    /// Recomputes and upserts the top-suggestion projection for the given faces. Linked faces are
    /// cleared (they never carry a suggestion); unlinked faces are scored against the active
    /// suggesters and stamped with <see cref="Cove.Core.Entities.Face.TopSuggestionComputedAt"/> even
    /// when no suggestion is found, so they are not reselected for recompute. Returns the number of
    /// faces written.
    /// </summary>
    public async Task<int> MaterializeAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default)
    {
        var distinctIds = faceIds.Where(static id => id > 0).Distinct().ToArray();
        if (distinctIds.Length == 0)
            return 0;

        var faces = await db.Faces
            .Where(face => distinctIds.Contains(face.Id))
            .ToListAsync(cancellationToken);
        if (faces.Count == 0)
            return 0;

        var eligibleFaceIds = faces
            .Where(face => !face.PerformerId.HasValue)
            .Select(face => face.Id)
            .ToArray();

        // No suggester is available yet (e.g. extensions are still publishing their contributions at
        // startup). Leave these faces unstamped so they are retried, rather than marking them
        // "computed, no suggestion" prematurely.
        if (eligibleFaceIds.Length > 0 && ActiveSuggesters().Count == 0)
            return 0;

        var topByFaceId = eligibleFaceIds.Length == 0
            ? new Dictionary<int, FaceSuggestionDto>()
            : await ComputeTopSuggestionsAsync(eligibleFaceIds, cancellationToken);

        var computedAt = DateTime.UtcNow;
        foreach (var face in faces)
        {
            if (face.PerformerId.HasValue)
            {
                // Linked faces don't carry a suggestion. Clear any stale projection.
                ClearProjection(face, computedAt);
                continue;
            }

            if (topByFaceId.TryGetValue(face.Id, out var top))
                ApplyProjection(face, top, computedAt);
            else
                ClearProjection(face, computedAt);
        }

        await db.SaveChangesAsync(cancellationToken);
        return faces.Count;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default)
    {
        var distinctIds = faceIds.Where(static id => id > 0).Distinct().ToArray();
        if (distinctIds.Length == 0)
            return;

        await db.Faces
            .Where(face => distinctIds.Contains(face.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(face => face.TopSuggestionPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionPerformerName, (string?)null)
                .SetProperty(face => face.TopSuggestionConfidence, (float?)null)
                .SetProperty(face => face.TopSuggestionCoverImageUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionExternalUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerHasImage, false)
                .SetProperty(face => face.TopSuggestionLocalPerformerIsLocalOnly, false)
                .SetProperty(face => face.TopSuggestionComputedAt, (DateTime?)null),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task InvalidateAllUnlinkedAsync(CancellationToken cancellationToken = default)
    {
        await db.Faces
            .Where(face => face.PerformerId == null && face.MergedIntoFaceId == null)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(face => face.TopSuggestionPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionPerformerName, (string?)null)
                .SetProperty(face => face.TopSuggestionConfidence, (float?)null)
                .SetProperty(face => face.TopSuggestionCoverImageUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionExternalUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerHasImage, false)
                .SetProperty(face => face.TopSuggestionLocalPerformerIsLocalOnly, false)
                .SetProperty(face => face.TopSuggestionComputedAt, (DateTime?)null),
                cancellationToken);
    }

    /// <inheritdoc />
    public async Task InvalidateForLinkChangeAsync(int faceId, CancellationToken cancellationToken = default)
    {
        if (faceId <= 0)
            return;

        // The (now-linked) face's own projection is no longer meaningful; clear it. PerformerId-bearing
        // faces are excluded from the materializer scan, so this won't be recomputed.
        await db.Faces
            .Where(face => face.Id == faceId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(face => face.TopSuggestionPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerId, (int?)null)
                .SetProperty(face => face.TopSuggestionPerformerName, (string?)null)
                .SetProperty(face => face.TopSuggestionConfidence, (float?)null)
                .SetProperty(face => face.TopSuggestionCoverImageUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionExternalUrl, (string?)null)
                .SetProperty(face => face.TopSuggestionLocalPerformerHasImage, false)
                .SetProperty(face => face.TopSuggestionLocalPerformerIsLocalOnly, false)
                .SetProperty(face => face.TopSuggestionComputedAt, (DateTime?)DateTime.UtcNow)
                .SetProperty(face => face.UpdatedAt, DateTime.UtcNow),
                cancellationToken);

        // NOTE: we intentionally do NOT invalidate visually-similar ("neighbour") faces here.
        // The newly-linked performer does become a usable local-match reference, but eagerly
        // recomputing neighbours on every link churned their suggestions and — because the materializer
        // runs off the request path — left them showing "no top suggestion yet" until it caught up.
        // Users found it surprising that accepting one face changed/blanked the suggestions for other
        // faces. The new reference is still picked up whenever a face's suggestion is next (re)computed
        // (new/uncomputed faces, or a bulk recompute via InvalidateAllUnlinkedAsync), so suggestions
        // stay stable during a linking session instead of shifting underfoot.
    }

    private async Task<Dictionary<int, FaceSuggestionDto>> ComputeTopSuggestionsAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken)
    {
        var suggesters = ActiveSuggesters();
        if (suggesters.Count == 0)
            return [];

        var options = new FaceSuggestionOptions(IncludeReferenceMatches: true);
        var suggestionsByFaceId = new Dictionary<int, List<FaceSuggestionDto>>();
        foreach (var suggester in suggesters)
        {
            var batch = await suggester.SuggestForBatchAsync(faceIds, CandidateCount, options, cancellationToken);
            foreach (var (faceId, suggestions) in batch)
            {
                if (!suggestionsByFaceId.TryGetValue(faceId, out var merged))
                {
                    merged = [];
                    suggestionsByFaceId[faceId] = merged;
                }

                merged.AddRange(suggestions);
            }
        }

        var topByFaceId = new Dictionary<int, FaceSuggestionDto>(suggestionsByFaceId.Count);
        foreach (var (faceId, suggestions) in suggestionsByFaceId)
        {
            var top = suggestions
                .GroupBy(item => item.PerformerId)
                .Select(group => group
                    .OrderByDescending(item => item.Confidence)
                    .ThenByDescending(item => item.Evidence.Count)
                    .ThenBy(item => item.PerformerName)
                    .First())
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.PerformerName)
                .FirstOrDefault();

            if (top is not null)
                topByFaceId[faceId] = top;
        }

        return topByFaceId;
    }

    private static void ApplyProjection(Cove.Core.Entities.Face face, FaceSuggestionDto suggestion, DateTime computedAt)
    {
        face.TopSuggestionPerformerId = suggestion.PerformerId;
        face.TopSuggestionLocalPerformerId = suggestion.LocalPerformerId ?? (suggestion.PerformerId > 0 ? suggestion.PerformerId : null);
        face.TopSuggestionPerformerName = suggestion.PerformerName;
        face.TopSuggestionConfidence = suggestion.Confidence;
        face.TopSuggestionCoverImageUrl = suggestion.CoverImageUrl;
        face.TopSuggestionExternalUrl = suggestion.ExternalUrl;
        face.TopSuggestionLocalPerformerHasImage = suggestion.LocalPerformerHasImage;
        face.TopSuggestionLocalPerformerIsLocalOnly = suggestion.LocalPerformerIsLocalOnly;
        face.TopSuggestionComputedAt = computedAt;
    }

    private static void ClearProjection(Cove.Core.Entities.Face face, DateTime computedAt)
    {
        face.TopSuggestionPerformerId = null;
        face.TopSuggestionLocalPerformerId = null;
        face.TopSuggestionPerformerName = null;
        face.TopSuggestionConfidence = null;
        face.TopSuggestionCoverImageUrl = null;
        face.TopSuggestionExternalUrl = null;
        face.TopSuggestionLocalPerformerHasImage = false;
        face.TopSuggestionLocalPerformerIsLocalOnly = false;
        face.TopSuggestionComputedAt = computedAt;
    }
}
