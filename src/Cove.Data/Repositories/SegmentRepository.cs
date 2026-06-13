using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class SegmentRepository : ISegmentRepository
{
    private readonly CoveContext _db;
    public SegmentRepository(CoveContext db) => _db = db;

    public async Task<IReadOnlyList<Segment>> FindAsync(SegmentFilter filter, CancellationToken ct = default)
    {
        var query = _db.Segments.AsQueryable();

        if (filter.HostType.HasValue)
            query = query.Where(s => s.HostType == filter.HostType.Value);
        if (filter.HostId.HasValue)
            query = query.Where(s => s.HostId == filter.HostId.Value);
        if (filter.SourceKey != null)
            query = query.Where(s => s.SourceKey == filter.SourceKey);
        if (filter.RefIds != null && filter.RefIds.Count > 0)
            query = query.Where(s => s.RefId.HasValue && filter.RefIds.Contains(s.RefId.Value));

        return await query.AsNoTracking().ToListAsync(ct);
    }

    public void Add(Segment segment) => _db.Segments.Add(segment);

    public void RemoveRange(IEnumerable<Segment> segments) => _db.Segments.RemoveRange(segments);

    public async Task UpdateRefIdAsync(string sourceKey, IReadOnlyList<long> oldRefIds, long newRefId, CancellationToken ct = default)
    {
        // Tracked update (not ExecuteUpdate) so it works on any provider and commits with the
        // caller's SaveChangesAsync alongside the rest of a face merge.
        var segments = await _db.Segments
            .Where(s => s.SourceKey == sourceKey && s.RefId.HasValue && oldRefIds.Contains(s.RefId!.Value))
            .ToListAsync(ct);
        foreach (var segment in segments)
            segment.RefId = newRefId;
    }

    public async Task ReassignRefByRunAsync(string sourceKey, long oldRefId, IReadOnlyCollection<string> runIds, long newRefId, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
            return;

        var segments = await _db.Segments
            .Where(s => s.SourceKey == sourceKey && s.RefId == oldRefId && s.SourceRunId != null && runIds.Contains(s.SourceRunId))
            .ToListAsync(ct);
        foreach (var segment in segments)
            segment.RefId = newRefId;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
