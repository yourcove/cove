using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class FileFingerprintWriter(IServiceScopeFactory scopeFactory)
{
    public async Task UpsertAsync(
        int fileId,
        string type,
        string value,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var existing = await db.FileFingerprints
            .FirstOrDefaultAsync(fingerprint => fingerprint.FileId == fileId && fingerprint.Type == type, ct);

        if (existing is not null)
            existing.Value = value;
        else
            db.FileFingerprints.Add(new FileFingerprint { FileId = fileId, Type = type, Value = value });

        await db.SaveChangesAsync(ct);
    }
}
