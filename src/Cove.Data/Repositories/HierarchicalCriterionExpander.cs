using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public sealed record ExpandedHierarchyCriterion(
    MultiIdCriterion Criterion,
    IReadOnlyList<int[]> ValueGroups,
    IReadOnlyList<int[]> RequiredIdGroups);

public static class HierarchicalCriterionExpander
{
    public static bool RequiresExpansion(MultiIdCriterion? criterion)
        => criterion is { Depth: -1 } or { RequiredIdsDepth: -1 };

    public static async Task<ExpandedHierarchyCriterion> ExpandTagsAsync(
        CoveContext db,
        MultiIdCriterion criterion,
        CancellationToken ct)
    {
        var edges = await db.Set<TagParent>()
            .AsNoTracking()
            .Select(link => new HierarchyEdge(link.ParentId, link.ChildId))
            .ToListAsync(ct);

        return Expand(criterion, edges);
    }

    public static async Task<ExpandedHierarchyCriterion> ExpandStudiosAsync(
        CoveContext db,
        MultiIdCriterion criterion,
        CancellationToken ct)
    {
        var edges = await db.Studios
            .AsNoTracking()
            .Where(studio => studio.ParentId.HasValue)
            .Select(studio => new HierarchyEdge(studio.ParentId!.Value, studio.Id))
            .ToListAsync(ct);

        return Expand(criterion, edges);
    }

    private static ExpandedHierarchyCriterion Expand(
        MultiIdCriterion criterion,
        IReadOnlyCollection<HierarchyEdge> edges)
    {
        var childrenByParent = edges
            .GroupBy(edge => edge.ParentId)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.ChildId).ToArray());

        var expandValues = criterion.Depth == -1;
        var valueGroups = criterion.Value
            .Distinct()
            .Select(rootId => expandValues ? ExpandRoot(rootId, childrenByParent) : [rootId])
            .ToList();
        var excludes = criterion.Excludes?
            .Distinct()
            .SelectMany(rootId => expandValues ? ExpandRoot(rootId, childrenByParent) : [rootId])
            .Distinct()
            .ToList();
        var expandRequiredIds = criterion.RequiredIdsDepth == -1;
        var requiredIdGroups = expandRequiredIds ? criterion.RequiredIds?
            .Where(rootId => rootId > 0)
            .Distinct()
            .Select(rootId => ExpandRoot(rootId, childrenByParent))
            .ToList() ?? [] : [];

        return new ExpandedHierarchyCriterion(
            new MultiIdCriterion
            {
                Value = valueGroups.SelectMany(group => group).Distinct().ToList(),
                Modifier = criterion.Modifier,
                Excludes = excludes is { Count: > 0 } ? excludes : null,
                RequiredIds = requiredIdGroups.Count > 0 ? null : criterion.RequiredIds,
                RequiredIdsDepth = criterion.RequiredIdsDepth,
                Depth = criterion.Depth,
            },
            valueGroups,
            requiredIdGroups);
    }

    private static int[] ExpandRoot(
        int rootId,
        IReadOnlyDictionary<int, int[]> childrenByParent)
    {
        var expanded = new HashSet<int> { rootId };
        var queue = new Queue<int>();
        queue.Enqueue(rootId);

        while (queue.TryDequeue(out var parentId))
        {
            if (!childrenByParent.TryGetValue(parentId, out var children)) continue;

            foreach (var childId in children)
            {
                if (expanded.Add(childId)) queue.Enqueue(childId);
            }
        }

        return expanded.ToArray();
    }

    private sealed record HierarchyEdge(int ParentId, int ChildId);
}
