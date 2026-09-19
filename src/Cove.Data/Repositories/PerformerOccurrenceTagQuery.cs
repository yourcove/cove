using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

/// <summary>
/// Applies a host filter's <c>PerformerTagsCriterion</c>: tags applied to a performer's occurrence on the host
/// (tag applications with a performer context), optionally scoped to the performers the filter already selects.
/// </summary>
public static class PerformerOccurrenceTagQuery
{
    /// <param name="valueGroups">
    /// One group per selected tag when the criterion was expanded with sub-tags; each group matches the tag or any of
    /// its descendants. Without groups every selected tag is its own group.
    /// </param>
    public static IQueryable<THost> Apply<THost>(
        CoveContext db,
        IQueryable<THost> query,
        AffinityHostType hostType,
        MultiIdCriterion? criterion,
        IReadOnlyCollection<int> performerIds,
        IReadOnlyList<int[]>? valueGroups = null)
        where THost : BaseEntity
    {
        if (criterion == null)
            return query;

        var groups = (valueGroups ?? criterion.Value.Select(tagId => new[] { tagId }).ToArray())
            .Select(group => group.Where(tagId => tagId > 0).Distinct().ToArray())
            .Where(group => group.Length > 0)
            .ToArray();
        var tagIds = groups.SelectMany(group => group).Distinct().ToArray();
        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (tagIds.Length == 0 && excludedTagIds.Length == 0)
            return query;

        var applications = db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == hostType
                && application.ContextType == "performer"
                && application.ContextId != null);

        if (performerIds.Count > 0)
        {
            var performerIdArray = performerIds.ToArray();
            applications = applications.Where(application => application.ContextId != null && performerIdArray.Contains(application.ContextId.Value));
        }

        if (tagIds.Length > 0)
        {
            query = criterion.Modifier switch
            {
                CriterionModifier.Excludes => query.Where(host => !applications.Any(application => application.HostId == host.Id && tagIds.Contains(application.TagId))),
                CriterionModifier.ExcludesAll => ApplyExcludesAll(query, applications, groups),
                CriterionModifier.IncludesAll => ApplyIncludesAll(query, applications, groups),
                _ => query.Where(host => applications.Any(application => application.HostId == host.Id && tagIds.Contains(application.TagId))),
            };
        }

        if (excludedTagIds.Length > 0)
            query = query.Where(host => !applications.Any(application => application.HostId == host.Id && excludedTagIds.Contains(application.TagId)));

        return query;
    }

    private static IQueryable<THost> ApplyIncludesAll<THost>(IQueryable<THost> query, IQueryable<TagApplication> applications, IReadOnlyList<int[]> groups)
        where THost : BaseEntity
    {
        foreach (var group in groups)
            query = query.Where(host => applications.Any(application => application.HostId == host.Id && group.Contains(application.TagId)));

        return query;
    }

    private static IQueryable<THost> ApplyExcludesAll<THost>(IQueryable<THost> query, IQueryable<TagApplication> applications, IReadOnlyList<int[]> groups)
        where THost : BaseEntity
    {
        var matchingAll = ApplyIncludesAll(query, applications, groups);
        return query.Where(host => !matchingAll.Select(match => match.Id).Contains(host.Id));
    }
}
