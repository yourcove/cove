using Cove.Api.Controllers;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;

using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

internal static class EffectiveTagDtoLoader
{
    public static async Task<IReadOnlyDictionary<int, List<TagDto>>> LoadAsync(
        CoveContext db,
        AffinityHostType hostType,
        IEnumerable<int> hostIds,
        CancellationToken cancellationToken)
    {
        var ids = hostIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0)
        {
            return new Dictionary<int, List<TagDto>>();
        }

        var rows = await EffectiveHostTagQuery.ForHostType(db, hostType)
            .AsNoTracking()
            .Where(row => ids.Contains(row.HostId))
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return ids.ToDictionary(id => id, _ => new List<TagDto>());
        }

        var tagIds = rows.Select(row => row.TagId).Where(tagId => tagId > 0).Distinct().ToArray();
        var tags = await db.Tags
            .AsNoTracking()
            .Include(tag => tag.Aliases)
            .Include(tag => tag.TagGroup)
            .Where(tag => tagIds.Contains(tag.Id))
            .ToDictionaryAsync(tag => tag.Id, cancellationToken);

        var provenanceRows = await db.TagApplications
            .AsNoTracking()
            .Where(application => application.HostType == hostType
                && ids.Contains(application.HostId)
                && tagIds.Contains(application.TagId))
            .OrderBy(application => application.SourceKey)
            .ThenBy(application => application.CreatedAt)
            .ToListAsync(cancellationToken);

        var provenanceLookup = provenanceRows
            .GroupBy(application => (application.HostId, application.TagId))
            .ToDictionary(
                group => group.Key,
                group => group.Select(MapProvenance).ToList());

        var result = ids.ToDictionary(id => id, _ => new List<TagDto>());
        foreach (var hostGroup in rows.GroupBy(row => row.HostId))
        {
            var tagDtos = hostGroup
                .GroupBy(row => row.TagId)
                .Select(group =>
                {
                    if (!tags.TryGetValue(group.Key, out var tag))
                    {
                        return null;
                    }

                    var isManual = group.Any(row => row.IsManual);
                    var isDerived = group.Any(row => row.IsDerived);
                    var provenance = provenanceLookup.GetValueOrDefault((hostGroup.Key, group.Key)) ?? [];
                    var canRemove = isManual && HasEditableDirectSource(provenance);
                    var durationSec = provenance
                        .Where(row => row.TotalDurationSec.HasValue)
                        .Select(row => row.TotalDurationSec!.Value)
                        .DefaultIfEmpty()
                        .Max();
                    var durationPercent = provenance
                        .Where(row => row.TotalDurationSec.HasValue && row.HostDurationSec.HasValue && row.HostDurationSec.Value > 0d)
                        .Select(row => row.TotalDurationSec!.Value * 100d / row.HostDurationSec!.Value)
                        .DefaultIfEmpty()
                        .Max();

                    return TagDtoMapping.MapTagDto(tag, provenance) with
                    {
                        IsDerived = isDerived && !canRemove,
                        CanRemove = canRemove,
                        // A locked, AI-derived chip is the only thing a user can "report as wrong":
                        // it has no editable manual source, so the global threshold is their only
                        // other lever. This flag tells the UI to surface the per-video correction.
                        CanReportIncorrect = isDerived && !canRemove,
                        EffectiveDurationSec = durationSec > 0d ? durationSec : null,
                        EffectiveDurationPercent = durationPercent > 0d ? durationPercent : null,
                    };
                })
                .Where(tag => tag != null)
                .Select(tag => tag!)
                .OrderBy(tag => tag.Name)
                .ThenBy(tag => tag.Id)
                .ToList();

            result[hostGroup.Key] = tagDtos;
        }

        return result;
    }

    private static TagProvenanceDto MapProvenance(TagApplication application)
        => new(
            application.SourceKey,
            string.IsNullOrWhiteSpace(application.SourceRunId) ? null : application.SourceRunId,
            string.IsNullOrWhiteSpace(application.ModelKey) ? null : application.ModelKey,
            application.Confidence,
            application.CreatedAt.ToString("o"),
            application.ContextType,
            application.ContextId,
            application.TotalDurationSec,
            application.HostDurationSec);

    private static bool HasEditableDirectSource(IReadOnlyCollection<TagProvenanceDto> provenance)
    {
        var hostLevelSources = provenance.Where(source => source.ContextType == null).ToArray();
        return hostLevelSources.Length == 0 || hostLevelSources.Any(source => !IsExtensionSource(source.SourceKey));
    }

    private static bool IsExtensionSource(string sourceKey)
        => SourceKeyConventions.IsExtensionSource(sourceKey);
}
