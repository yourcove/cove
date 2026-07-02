using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public class AiRunRepository : IAiRunRepository
{
    private readonly CoveContext _db;
    public AiRunRepository(CoveContext db) => _db = db;

    public async Task<AiRun> FindOrCreateAsync(string runKey, string sourceKey,
        AiRunTargetType targetType, int targetId,
        AiRunStatus initialStatus = AiRunStatus.Pending,
        CancellationToken ct = default)
    {
        var existing = await _db.AiRuns
            .FirstOrDefaultAsync(r => r.RunKey == runKey && r.SourceKey == sourceKey, ct);

        if (existing != null)
            return existing;

        var run = new AiRun
        {
            RunKey = runKey,
            SourceKey = sourceKey,
            TargetType = targetType,
            TargetId = targetId,
            Status = initialStatus,
        };
        _db.AiRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run;
    }

    public async Task<IReadOnlyDictionary<string, AiRun>> FindOrCreateManyAsync(
        IReadOnlyList<(string RunKey, AiRunTargetType TargetType, int TargetId)> keys,
        string sourceKey,
        AiRunStatus initialStatus,
        CancellationToken ct = default)
    {
        var runKeys = keys.Select(k => k.RunKey).Distinct().ToList();
        var existing = await _db.AiRuns
            .Where(r => r.SourceKey == sourceKey && runKeys.Contains(r.RunKey))
            .ToListAsync(ct);

        var byKey = new Dictionary<string, AiRun>(StringComparer.Ordinal);
        foreach (var run in existing)
            byKey[run.RunKey] = run;

        var toAdd = new List<AiRun>();
        foreach (var key in keys)
        {
            if (byKey.ContainsKey(key.RunKey))
                continue;

            var run = new AiRun
            {
                RunKey = key.RunKey,
                SourceKey = sourceKey,
                TargetType = key.TargetType,
                TargetId = key.TargetId,
                Status = initialStatus,
            };
            byKey[key.RunKey] = run;
            toAdd.Add(run);
        }

        if (toAdd.Count > 0)
        {
            _db.AiRuns.AddRange(toAdd);
            await _db.SaveChangesAsync(ct);
        }

        return byKey;
    }

    public async Task UpdateAsync(AiRun run, CancellationToken ct = default)
    {
        _db.AiRuns.Update(run);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateManyAsync(IReadOnlyList<AiRun> runs, CancellationToken ct = default)
    {
        if (runs.Count == 0)
            return;

        _db.AiRuns.UpdateRange(runs);
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AiRun>> GetCompletedAsync(AiRunTargetType targetType, int targetId,
        string sourceKey, CancellationToken ct = default)
    {
        return await _db.AiRuns
            .Where(r => r.TargetType == targetType
                && r.TargetId == targetId
                && r.SourceKey == sourceKey
                && r.Status == AiRunStatus.Completed)
            .OrderByDescending(r => r.CompletedAt)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
