using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class FaceRepository : IFaceRepository
{
    private readonly CoveContext _db;
    public FaceRepository(CoveContext db) => _db = db;

    public async Task<IReadOnlyList<Face>> FindFacesAsync(FaceFilter filter, bool tracking = false, CancellationToken ct = default)
    {
        IQueryable<Face> query = filter.IncludePerformer
            ? _db.Faces.Include(f => f.Performer!).ThenInclude(p => p.RemoteIds)
            : _db.Faces.AsQueryable();

        if (filter.PrimarySourceKeys != null && filter.PrimarySourceKeys.Count > 0)
            query = query.Where(f => f.PrimarySourceKey != null && filter.PrimarySourceKeys.Contains(f.PrimarySourceKey));
        if (filter.Ids != null && filter.Ids.Count > 0)
            query = query.Where(f => filter.Ids.Contains(f.Id));
        if (filter.HasPerformer.HasValue)
            query = filter.HasPerformer.Value
                ? query.Where(f => f.PerformerId != null)
                : query.Where(f => f.PerformerId == null);
        if (filter.Ignored.HasValue)
            query = query.Where(f => f.Ignored == filter.Ignored.Value);
        if (filter.IsMerged.HasValue)
            query = filter.IsMerged.Value
                ? query.Where(f => f.MergedIntoFaceId != null)
                : query.Where(f => f.MergedIntoFaceId == null);

        if (!tracking)
            query = query.AsNoTracking();

        return await query.ToListAsync(ct);
    }

    public async Task<Face?> GetFaceAsync(int faceId, bool tracking = true, CancellationToken ct = default)
    {
        var query = _db.Faces.AsQueryable();
        if (!tracking)
            query = query.AsNoTracking();
        return await query.FirstOrDefaultAsync(f => f.Id == faceId, ct);
    }

    public async Task<bool> FaceExistsAsync(int faceId, CancellationToken ct = default)
        => await _db.Faces.AnyAsync(f => f.Id == faceId, ct);

    public void AddFace(Face face) => _db.Faces.Add(face);

    public async Task<IReadOnlyList<FaceAppearance>> FindAppearancesAsync(FaceAppearanceFilter filter, CancellationToken ct = default)
    {
        var query = _db.FaceAppearances.AsQueryable();

        if (filter.HostType.HasValue)
            query = query.Where(a => a.HostType == filter.HostType.Value);
        if (filter.HostId.HasValue)
            query = query.Where(a => a.HostId == filter.HostId.Value);
        if (filter.SourceKey != null)
            query = query.Where(a => a.SourceKey == filter.SourceKey);
        if (filter.FaceIds != null && filter.FaceIds.Count > 0)
            query = query.Where(a => filter.FaceIds.Contains(a.FaceId));

        return await query.AsNoTracking().ToListAsync(ct);
    }

    public void AddAppearance(FaceAppearance appearance) => _db.FaceAppearances.Add(appearance);

    public void RemoveAppearances(IEnumerable<FaceAppearance> appearances) => _db.FaceAppearances.RemoveRange(appearances);

    public async Task UpdateAppearanceFaceIdAsync(string sourceKey, IReadOnlyList<int> oldFaceIds, int newFaceId, CancellationToken ct = default)
    {
        // Tracked update (not ExecuteUpdate) so it works on any provider and commits with the
        // caller's SaveChangesAsync alongside the rest of a face merge.
        var appearances = await _db.FaceAppearances
            .Where(a => a.SourceKey == sourceKey && oldFaceIds.Contains(a.FaceId))
            .ToListAsync(ct);
        foreach (var appearance in appearances)
            appearance.FaceId = newFaceId;
    }

    public async Task<int> ReassignAppearancesByRunAsync(string sourceKey, int oldFaceId, IReadOnlyCollection<string> runIds, int newFaceId, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
            return 0;

        var appearances = await _db.FaceAppearances
            .Where(a => a.SourceKey == sourceKey && a.FaceId == oldFaceId && a.SourceRunId != null && runIds.Contains(a.SourceRunId))
            .ToListAsync(ct);
        foreach (var appearance in appearances)
            appearance.FaceId = newFaceId;
        return appearances.Count;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
