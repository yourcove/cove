using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Pgvector;
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

    public async Task AttachVideoFileAsync(
        int videoId,
        double duration,
        long size,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public video creation cannot supply deterministic probe metrics. Seed only the file row
        // needed to verify the aggregate endpoint's derived duration and file-size totals.
        var now = DateTime.UtcNow;
        var folder = new Folder
        {
            Path = $"/api-tests/video-aggregate/{Guid.NewGuid():N}",
            ModTime = now,
        };
        db.VideoFiles.Add(new VideoFile
        {
            VideoId = videoId,
            Basename = "aggregate-source.mp4",
            ParentFolder = folder,
            Size = size,
            ModTime = now,
            Format = "mp4",
            Duration = duration,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AttachAudioFileAsync(
        int audioId,
        double duration,
        long size,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public audio creation cannot supply deterministic probe metrics. Seed the file row and
        // the summary columns maintained by ScanAudioProcessor so aggregate API assertions stay exact.
        var audio = await db.Audios.SingleAsync(item => item.Id == audioId, cancellationToken);
        var now = DateTime.UtcNow;
        var folder = new Folder
        {
            Path = $"/api-tests/audio-aggregate/{Guid.NewGuid():N}",
            ModTime = now,
        };
        const string basename = "aggregate-source.mp3";
        db.AudioFiles.Add(new AudioFile
        {
            AudioId = audioId,
            Basename = basename,
            ParentFolder = folder,
            Size = size,
            ModTime = now,
            Format = "mp3",
            Duration = duration,
            AudioCodec = "mp3",
        });
        audio.FileCount = 1;
        audio.MaxDuration = duration;
        audio.MaxFileSize = size;
        audio.MaxFileModTime = now;
        audio.MinPath = BaseFileEntity.ComputePath(folder.Path, basename);
        audio.MaxPath = audio.MinPath;
        audio.FileSearchText = audio.MinPath;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AttachGalleryFileAsync(
        int galleryId,
        long size,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public gallery creation cannot provide deterministic archive metrics. Seed only the
        // file row required to verify the aggregate endpoint's filtered file-size total.
        var now = DateTime.UtcNow;
        var folder = new Folder
        {
            Path = $"/api-tests/gallery-aggregate/{Guid.NewGuid():N}",
            ModTime = now,
        };
        db.GalleryFiles.Add(new GalleryFile
        {
            GalleryId = galleryId,
            Basename = "aggregate-source.zip",
            ParentFolder = folder,
            Size = size,
            ModTime = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetStoredStudioVideoCountsAsync(
        int studioWithVideoId,
        int studioWithoutVideoId,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public studio DTOs calculate their visible counts from source relationships, but list
        // ordering intentionally uses this stored rollup. Bypass SaveChanges maintenance to seed
        // only the stale state that the maintenance endpoint is intended to repair.
        var cleared = await db.Studios
            .Where(studio => studio.Id == studioWithVideoId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(studio => studio.VideoCount, 0),
                cancellationToken);
        var inflated = await db.Studios
            .Where(studio => studio.Id == studioWithoutVideoId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(studio => studio.VideoCount, 2),
                cancellationToken);
        if (cleared != 1 || inflated != 1)
            throw new InvalidOperationException("The API test could not seed the expected stale studio rollups.");
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

    public async Task<int> CreateFaceEmbeddingAsync(
        int faceId,
        IReadOnlyCollection<float> values,
        string kindFamily,
        CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
            throw new ArgumentException("A face embedding must contain at least one value.", nameof(values));

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var vector = values.ToArray();
        var embedding = new Embedding
        {
            HostType = EmbeddingHostType.Face,
            HostId = faceId,
            Kind = kindFamily,
            KindFamily = kindFamily,
            Modality = EmbeddingModality.Face,
            IsSemantic = true,
            Dim = vector.Length,
            Vector = new Vector(vector),
            SourceKey = "api-test",
        };
        db.Embeddings.Add(embedding);
        await db.SaveChangesAsync(cancellationToken);
        return embedding.Id;
    }

}
