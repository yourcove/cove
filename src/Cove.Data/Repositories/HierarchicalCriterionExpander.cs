using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public sealed record ExpandedHierarchyCriterion(
    MultiIdCriterion Criterion,
    IReadOnlyList<int[]> ValueGroups);

public static class HierarchicalCriterionExpander
{
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

        var valueGroups = criterion.Value
            .Distinct()
            .Select(rootId => ExpandRoot(rootId, childrenByParent))
            .ToList();
        var excludes = criterion.Excludes?
            .Distinct()
            .SelectMany(rootId => ExpandRoot(rootId, childrenByParent))
            .Distinct()
            .ToList();

        return new ExpandedHierarchyCriterion(
            new MultiIdCriterion
            {
                Value = valueGroups.SelectMany(group => group).Distinct().ToList(),
                Modifier = criterion.Modifier,
                Excludes = excludes is { Count: > 0 } ? excludes : null,
                RequiredIds = criterion.RequiredIds,
                Depth = criterion.Depth,
            },
            valueGroups);
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
