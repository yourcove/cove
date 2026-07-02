using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class EmbeddingRepository : IEmbeddingRepository
{
    private readonly CoveContext _db;
    public EmbeddingRepository(CoveContext db) => _db = db;

    public async Task<IReadOnlyList<Embedding>> FindAsync(EmbeddingFilter filter, CancellationToken ct = default)
        => await BuildQuery(filter).AsNoTracking().ToListAsync(ct);

    public async Task<bool> ExistsAsync(EmbeddingFilter filter, CancellationToken ct = default)
        => await BuildQuery(filter).AnyAsync(ct);

    private IQueryable<Embedding> BuildQuery(EmbeddingFilter filter)
    {
        var query = _db.Embeddings.AsQueryable();

        if (filter.HostType.HasValue)
            query = query.Where(e => e.HostType == filter.HostType.Value);
        if (filter.HostId.HasValue)
            query = query.Where(e => e.HostId == filter.HostId.Value);
        if (filter.HostIds != null && filter.HostIds.Count > 0)
            query = query.Where(e => filter.HostIds.Contains(e.HostId));
        if (filter.SourceKey != null)
            query = query.Where(e => e.SourceKey == filter.SourceKey);
        if (filter.Kind != null)
            query = query.Where(e => e.Kind == filter.Kind);
        if (filter.KindFamily != null)
            query = query.Where(e => e.KindFamily == filter.KindFamily);
        if (filter.Modality.HasValue)
            query = query.Where(e => e.Modality == filter.Modality.Value);
        if (filter.IsSemantic.HasValue)
            query = query.Where(e => e.IsSemantic == filter.IsSemantic.Value);
        if (filter.SectionIndexGreaterThan.HasValue)
            query = query.Where(e => e.SectionIndex > filter.SectionIndexGreaterThan.Value);
        if (filter.SectionIndex.HasValue)
            query = query.Where(e => e.SectionIndex == filter.SectionIndex.Value);

        return query;
    }

    public void Add(Embedding embedding) => _db.Embeddings.Add(embedding);

    public void RemoveRange(IEnumerable<Embedding> embeddings) => _db.Embeddings.RemoveRange(embeddings);

    public async Task UpdateHostIdAsync(EmbeddingHostType hostType, string sourceKey,
        IReadOnlyList<int> oldHostIds, int newHostId, CancellationToken ct = default)
    {
        // Tracked update (not ExecuteUpdate) so it works on any provider and commits with the
        // caller's SaveChangesAsync alongside the rest of a face merge.
        var embeddings = await _db.Embeddings
            .Where(e => e.HostType == hostType && e.SourceKey == sourceKey && oldHostIds.Contains(e.HostId))
            .ToListAsync(ct);
        foreach (var embedding in embeddings)
            embedding.HostId = newHostId;
    }

    public async Task ReassignHostByRunAsync(EmbeddingHostType hostType, string sourceKey, int oldHostId,
        IReadOnlyCollection<string> runIds, int newHostId, CancellationToken ct = default)
    {
        if (runIds.Count == 0)
            return;

        var embeddings = await _db.Embeddings
            .Where(e => e.HostType == hostType && e.SourceKey == sourceKey && e.HostId == oldHostId && e.SourceRunId != null && runIds.Contains(e.SourceRunId))
            .ToListAsync(ct);
        foreach (var embedding in embeddings)
            embedding.HostId = newHostId;
    }

    public async Task<int> SaveChangesAsync(CancellationToken ct = default)
        => await _db.SaveChangesAsync(ct);
}
