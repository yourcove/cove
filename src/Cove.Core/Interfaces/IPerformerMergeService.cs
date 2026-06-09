using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>
/// Merges one or more source performers into a target performer. The target is the "primary": its
/// single-value fields (height, measurements, ethnicity, …) win, and are only filled from a source when
/// the target leaves them empty. List-style data is unioned across all performers: scene/image/gallery
/// links, tags, URLs, remote ids, and aliases (each source's name is also added to the target as an
/// alias). Source performers are deleted once their data has been folded in.
/// </summary>
public interface IPerformerMergeService
{
    /// <summary>
    /// Folds <paramref name="sourceIds"/> into <paramref name="targetId"/> and deletes the sources.
    /// Source ids equal to the target are ignored. Returns the merged target, or null if the target
    /// does not exist.
    /// </summary>
    Task<Performer?> MergeAsync(int targetId, IReadOnlyCollection<int> sourceIds, CancellationToken ct = default);
}
