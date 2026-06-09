namespace Cove.Core.Interfaces;

/// <summary>
/// Maintains the materialized per-face "top suggestion" projection (the <c>Face.TopSuggestion*</c>
/// columns). Callers use these methods to mark faces as needing recompute; the actual computation is
/// done off the request path by the background materializer. Implemented by the host and resolvable
/// from extension scopes, so extensions (e.g. AI.Faces) can invalidate the projection when their own
/// inputs change — for example after importing or removing a reference pack.
/// </summary>
public interface IFaceTopSuggestionMaintenance
{
    /// <summary>Marks the given faces as needing recompute. Unknown ids are ignored.</summary>
    Task InvalidateAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every currently-unlinked face as needing recompute. Use when a change affects all faces
    /// at once (reference pack imported/removed). This is a cheap bulk UPDATE; the background
    /// materializer then recomputes the affected faces over time.
    /// </summary>
    Task InvalidateAllUnlinkedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a face's link state changing. Clears the (now-linked) face's own projection and
    /// invalidates its nearest-neighbour faces, whose local-match suggestions may now surface this
    /// performer. Bounded to the neighbourhood, so it stays cheap at scale.
    /// </summary>
    Task InvalidateForLinkChangeAsync(int faceId, CancellationToken cancellationToken = default);
}
