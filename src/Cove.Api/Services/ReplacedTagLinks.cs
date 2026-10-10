using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// A scrape or import that replaces an item's tags clears its tag links and links again only the tags it
/// applies. A video, audio or text shows its links plus every tag a source recorded on it, so the dropped
/// tags' records go too, as a removal in the video edit form drops them; otherwise a record from the
/// person or another source keeps a dropped tag on the item as a locked, derived tag. Extension records
/// stay.
/// </summary>
internal static class ReplacedTagLinks
{
    public static Task ForgetDroppedAsync(
        ITagProvenanceService tagProvenanceService,
        AffinityHostType hostType,
        int hostId,
        IReadOnlyCollection<int> previousTagIds,
        IEnumerable<int> currentTagIds,
        CancellationToken ct)
    {
        var current = currentTagIds.ToHashSet();
        // Only removals: passing what stayed as the new set records nothing for the tags the replace added.
        return tagProvenanceService.SyncTagSetAsync(
            hostType,
            hostId,
            previousTagIds,
            previousTagIds.Where(current.Contains).ToArray(),
            cancellationToken: ct);
    }
}
