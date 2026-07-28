namespace Cove.Core.Interfaces;

/// <summary>
/// Invalidates Cove's cached segment-span projections after segment data changes outside Cove's
/// built-in mutation paths.
/// </summary>
/// <remarks>
/// Extensions that create, update, or remove video segments should resolve this host-owned service
/// and invalidate the affected video after their database transaction commits. Use
/// <see cref="InvalidateAll"/> only when the affected videos cannot be identified. Invalidating a
/// cache does not authorize or persist a segment mutation.
/// </remarks>
public interface ISegmentSpanCacheInvalidator
{
    /// <summary>Invalidate raw, resolved, and derived-query segment caches for one video.</summary>
    void InvalidateVideo(int videoId);

    /// <summary>Invalidate every cached segment span and display-rule projection.</summary>
    void InvalidateAll();
}
