using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>Filter criteria for querying detections.</summary>
public sealed class DetectionFilter
{
    public DetectionHostType? HostType { get; init; }
    public int? HostId { get; init; }
    public string? SourceKey { get; init; }
    public string? RefKind { get; init; }
    /// <summary>When set, only detections whose RefId is in this list are returned.</summary>
    public IReadOnlyList<long>? RefIds { get; init; }
}

/// <summary>
/// Generic repository for spatial detection CRUD (face bounding boxes, object detections, etc.).
/// Available to any extension that writes or reads frame-level detections.
/// </summary>
public interface IDetectionRepository
{
    Task<IReadOnlyList<Detection>> FindAsync(DetectionFilter filter, CancellationToken ct = default);
    void Add(Detection detection);
    void RemoveRange(IEnumerable<Detection> detections);

    /// <summary>Re-points detections with RefKind matching <paramref name="refKind"/> and RefId in
    /// <paramref name="oldRefIds"/> to <paramref name="newRefId"/>.</summary>
    Task UpdateRefIdAsync(string sourceKey, string refKind,
        IReadOnlyList<long> oldRefIds, long newRefId, CancellationToken ct = default);

    /// <summary>Re-points the <paramref name="refKind"/> detections of <paramref name="oldRefId"/> that
    /// came from the given runs to <paramref name="newRefId"/>.</summary>
    Task ReassignRefByRunAsync(string sourceKey, string refKind, long oldRefId,
        IReadOnlyCollection<string> runIds, long newRefId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
