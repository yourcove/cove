using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// A merge marks the source face as merged into its target and copies its appearances, but the source's
/// detections and segments keep referring to the source. Whatever reads a face's detections or segments
/// therefore has to read those of the faces merged into it as well.
/// </summary>
public static class FaceMergedIds
{
    /// <summary>The face's id followed by the ids of every face merged into it, directly or through another merged face.</summary>
    public static async Task<long[]> LoadWithMergedAsync(CoveContext db, int faceId, CancellationToken cancellationToken)
    {
        var ids = new List<int> { faceId };
        var frontier = new[] { faceId };
        while (frontier.Length > 0)
        {
            var parents = frontier;
            // A merged-away face is part of its target, so the target's visibility is the one that counts.
            frontier = await db.Faces
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(face => face.MergedIntoFaceId != null && parents.Contains(face.MergedIntoFaceId.Value))
                .Select(face => face.Id)
                .ToArrayAsync(cancellationToken);
            frontier = frontier.Except(ids).ToArray();
            ids.AddRange(frontier);
        }

        return ids.Select(static id => (long)id).ToArray();
    }

    /// <summary>
    /// For several faces at once: every face merged into one of them, directly or through another merged face,
    /// mapped to the requested face it ends up in.
    /// </summary>
    public static async Task<Dictionary<int, int>> LoadMergedIntoAsync(CoveContext db, IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken)
    {
        var targetByMergedFace = new Dictionary<int, int>();
        var frontier = faceIds.Distinct().ToDictionary(static id => id, static id => id);
        while (frontier.Count > 0)
        {
            var parents = frontier.Keys.ToArray();
            var merged = await db.Faces
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(face => face.MergedIntoFaceId != null && parents.Contains(face.MergedIntoFaceId.Value))
                .Select(face => new { face.Id, Parent = face.MergedIntoFaceId!.Value })
                .ToListAsync(cancellationToken);
            var next = new Dictionary<int, int>();
            foreach (var face in merged)
            {
                if (faceIds.Contains(face.Id) || !targetByMergedFace.TryAdd(face.Id, frontier[face.Parent]))
                    continue;
                next[face.Id] = frontier[face.Parent];
            }

            frontier = next;
        }

        return targetByMergedFace;
    }
}
