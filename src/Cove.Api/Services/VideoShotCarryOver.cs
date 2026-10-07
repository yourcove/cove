using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Keeps a video file's shot boundaries when Cove deletes the file in favour of another copy of the
/// same footage on the same timeline. Every path that deletes a file for that reason calls this first:
/// file review and conversions deleting a file that is not the primary one, the Files tab, and a merge
/// that removes the copies. Deleting a whole video and the clean job's removal of files that are gone
/// from disk do not, because nothing replaces those files; their sets go with them.
/// </summary>
internal static class VideoShotCarryOver
{
    // Beyond matching fingerprints, the running times must agree to within two frames or 0.1 s,
    // whichever is longer, for every cut to stay on its frame.
    private const int SameTimelineFrames = 2;
    private const double SameTimelineMinSec = 0.1;

    /// <summary>
    /// Moves the set of each of <paramref name="removedFileIds"/> onto <paramref name="replacementFileId"/>,
    /// or, when that is null, onto the primary file of the file's own video, wherever the two files show
    /// the same footage on the same timeline. Runs in the caller's transaction, before the files are deleted.
    /// </summary>
    public static async Task KeepOnReplacementAsync(CoveContext db, IReadOnlyCollection<int> removedFileIds, int? replacementFileId, CancellationToken ct)
    {
        var removed = removedFileIds.ToArray();
        var withSets = await db.VideoShotSets
            .AsNoTracking()
            .Where(set => removed.Contains(set.FileId))
            .Select(set => set.FileId)
            .ToArrayAsync(ct);
        if (withSets.Length == 0)
            return;

        var sources = await db.VideoFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(file => withSets.Contains(file.Id))
            .Select(file => new { file.Id, file.Duration, file.FrameRate, PrimaryFileId = file.Video == null ? null : file.Video.PrimaryFileId })
            .ToListAsync(ct);
        var moves = sources
            .Select(source => (Source: source, TargetId: replacementFileId ?? source.PrimaryFileId))
            .Where(move => move.TargetId is int target && target != move.Source.Id && !removed.Contains(target))
            .ToList();
        if (moves.Count == 0)
            return;

        var targetIds = moves.Select(move => move.TargetId!.Value).Distinct().ToArray();
        var targetDurations = await db.VideoFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(file => targetIds.Contains(file.Id))
            .ToDictionaryAsync(file => file.Id, file => file.Duration, ct);
        var phashes = await VideoFileEquivalence.LoadStoredPhashesAsync(db, [.. withSets, .. targetIds], ct);
        foreach (var (source, targetId) in moves)
        {
            var target = targetId!.Value;
            if (targetDurations.TryGetValue(target, out var targetDuration)
                && VideoFileEquivalence.AreEquivalent(source.Duration, phashes.GetValueOrDefault(source.Id), targetDuration, phashes.GetValueOrDefault(target))
                && Math.Abs(source.Duration - targetDuration) <= Math.Max(SameTimelineMinSec, source.FrameRate > 0 ? SameTimelineFrames / source.FrameRate : 0))
                await VideoShotService.MoveSetToFileAsync(db, source.Id, target, ct);
        }
    }
}
