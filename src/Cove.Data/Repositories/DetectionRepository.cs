using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class DetectionRepository : IDetectionRepository
{
    private readonly CoveContext _db;
    public DetectionRepository(CoveContext db) => _db = db;

    public async Task<IReadOnlyList<Detection>> FindAsync(DetectionFilter filter, CancellationToken ct = default)
    {
        var query = _db.Detections.AsQueryable();

        if (filter.HostType.HasValue)
            query = query.Where(d => d.HostType == filter.HostType.Value);
        if (filter.HostId.HasValue)
            query = query.Where(d => d.HostId == filter.HostId.Value);
        if (filter.SourceKey != null)
            query = query.Where(d => d.SourceKey == filter.SourceKey);
        if (filter.RefKind != null)
            query = query.Where(d => d.RefKind == filter.RefKind);
        if (filter.RefIds != null && filter.RefIds.Count > 0)
            query = query.Where(d => d.RefId.HasValue && filter.RefIds.Contains(d.RefId.Value));

        return await query.AsNoTracking().ToListAsync(ct);
    }

    public void Add(Detection detection) => _db.Detections.Add(detection);

    public void RemoveRange(IEnumerable<Detection> detections) => _db.Detections.RemoveRange(detections);

    public async Task UpdateRefIdAsync(string sourceKey, string refKind,
        IReadOnlyList<long> oldRefIds, long newRefId, CancellationToken ct = default)
    {
        // Tracked update (not ExecuteUpdate) so it works on any provider and commits with the
        // caller's SaveChangesAsync alongside the rest of a face merge.
        var detections = await _db.Detections
            .Where(d => d.SourceKey == sourceKey && d.RefKind == refKind && d.RefId.HasValue && oldRefIds.Contains(d.RefId!.Value))
            .ToListAsync(ct);
        foreach (var detection in detections)
            detection.RefId = newRefId;
    }

    public async Task ReassignRefByRunAsync(string sourceKey, string refKind, long oldRefId,
        IReadOnlyCollection<string> runIds, long newRefId, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
            return;

        var detections = await _db.Detections
            .Where(d => d.SourceKey == sourceKey && d.RefKind == refKind && d.RefId == oldRefId && d.SourceRunId != null && runIds.Contains(d.SourceRunId))
            .ToListAsync(ct);
        foreach (var detection in detections)
            detection.RefId = newRefId;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
