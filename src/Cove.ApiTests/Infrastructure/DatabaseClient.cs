using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector.EntityFrameworkCore;

namespace Cove.ApiTests.Infrastructure;

public sealed class DatabaseClient
{
    private readonly string _connectionString;

    internal DatabaseClient(string connectionString)
        => _connectionString = connectionString;

    public async Task<IReadOnlyDictionary<string, string>> GetFileFingerprintsAsync(
        int fileId,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Non-video API DTOs do not expose fingerprints, so this read-only assertion helper is the
        // narrow verification escape hatch for public generate jobs.
        return await db.Set<FileFingerprint>()
            .AsNoTracking()
            .Where(fingerprint => fingerprint.FileId == fileId)
            .ToDictionaryAsync(
                fingerprint => fingerprint.Type,
                fingerprint => fingerprint.Value,
                StringComparer.OrdinalIgnoreCase,
                cancellationToken);
    }

    public async Task<int> CreateFaceAppearanceAsync(
        int faceId,
        FaceAppearanceHostType hostType,
        int hostId,
        int sampleCount,
        int retainedSpatialSampleCount,
        int segmentCount,
        double? firstSeenAtSec,
        double? lastSeenAtSec,
        float? topConfidence,
        string sourceKey = "api-test",
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var appearance = new FaceAppearance
        {
            FaceId = faceId,
            HostType = hostType,
            HostId = hostId,
            SampleCount = sampleCount,
            RetainedSpatialSampleCount = retainedSpatialSampleCount,
            SegmentCount = segmentCount,
            FirstSeenAtSec = firstSeenAtSec,
            LastSeenAtSec = lastSeenAtSec,
            TopConfidence = topConfidence,
            SourceKey = sourceKey,
        };
        db.FaceAppearances.Add(appearance);
        await db.SaveChangesAsync(cancellationToken);
        return appearance.Id;
    }

}
