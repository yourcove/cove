using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>Filter criteria for querying segments.</summary>
public sealed class SegmentFilter
{
    public SegmentHostType? HostType { get; init; }
    public int? HostId { get; init; }
    public string? SourceKey { get; init; }
    /// <summary>When set, only segments whose RefId matches one of these values are returned.</summary>
    public IReadOnlyList<long>? RefIds { get; init; }
}

/// <summary>
/// Generic repository for timeline segment CRUD (audio, face, tag, and other segment types).
/// Available to any extension that writes or reads timeline segments.
/// </summary>
public interface ISegmentRepository
{
    Task<IReadOnlyList<Segment>> FindAsync(SegmentFilter filter, CancellationToken ct = default);
    void Add(Segment segment);
    void RemoveRange(IEnumerable<Segment> segments);

    /// <summary>Re-points face-ref segments from <paramref name="oldRefIds"/> to <paramref name="newRefId"/>.</summary>
    Task UpdateRefIdAsync(string sourceKey, IReadOnlyList<long> oldRefIds, long newRefId, CancellationToken ct = default);

    /// <summary>Re-points the segments of <paramref name="oldRefId"/> that came from the given runs to
    /// <paramref name="newRefId"/>.</summary>
    Task ReassignRefByRunAsync(string sourceKey, long oldRefId, IReadOnlyCollection<string> runIds, long newRefId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
