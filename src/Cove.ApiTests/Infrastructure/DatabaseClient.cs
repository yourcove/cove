using System.Text.Json;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Data.Services;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Cove.ApiTests.Infrastructure;

public sealed class DatabaseClient
{
    private readonly string _connectionString;

    internal DatabaseClient(string connectionString)
        => _connectionString = connectionString;

    internal async Task<string> CreateSetupTokenAsync(
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Setup tokens are normally issued outside the public API before the first owner exists.
        // Seed only that deployment-provisioning input so the anonymous redemption route can be
        // exercised against an otherwise untouched, pre-owner API host.
        var (token, tokenHash) = TokenService.NewOpaqueToken();
        var now = DateTime.UtcNow;
        db.UserInviteTokens.Add(new UserInviteToken
        {
            TokenHash = tokenHash,
            Purpose = "setup",
            ExpiresAt = now.AddHours(1),
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);
        return token;
    }

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

    public async Task<int> GetFileParentFolderIdAsync(
        int fileId,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        return await db.Set<BaseFileEntity>()
            .AsNoTracking()
            .Where(file => file.Id == fileId)
            .Select(file => file.ParentFolderId)
            .SingleAsync(cancellationToken);
    }

    public async Task AttachVideoFileAsync(
        int videoId,
        double duration,
        long size,
        IReadOnlyDictionary<string, string>? fingerprints = null,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public video creation cannot supply deterministic file metrics or fingerprints. Seed only
        // the file row needed to probe aggregate and duplicate-discovery behavior.
        var now = DateTime.UtcNow;
        var folder = new Folder
        {
            Path = $"/api-tests/video-aggregate/{Guid.NewGuid():N}",
            ModTime = now,
        };
        var file = new VideoFile
        {
            VideoId = videoId,
            Basename = "aggregate-source.mp4",
            ParentFolder = folder,
            Size = size,
            ModTime = now,
            Format = "mp4",
            Duration = duration,
        };
        if (fingerprints != null)
        {
            foreach (var (type, value) in fingerprints)
                file.Fingerprints.Add(new FileFingerprint { Type = type, Value = value });
        }
        db.VideoFiles.Add(file);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetVideoParentAsync(
        int videoId,
        int parentVideoId,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public clip creation flattens nested requests to the file-backed root. Seed a deeper
        // legacy hierarchy only to verify that merge validation cannot create a parent cycle.
        await db.Videos
            .Where(video => video.Id == videoId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(video => video.ParentVideoId, parentVideoId),
                cancellationToken);
    }

    public async Task AttachStreamVideoFileAsync(
        int videoId,
        string path,
        int width,
        int height,
        double duration,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        var file = new FileInfo(path);
        if (!file.Exists || file.DirectoryName is null)
            throw new FileNotFoundException("The API test video source does not exist.", path);

        db.VideoFiles.Add(new VideoFile
        {
            VideoId = videoId,
            Basename = file.Name,
            ParentFolder = new Folder { Path = file.DirectoryName, ModTime = file.LastWriteTimeUtc },
            Size = file.Length,
            ModTime = file.LastWriteTimeUtc,
            Format = file.Extension.TrimStart('.'),
            Width = width,
            Height = height,
            Duration = duration,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> AttachStreamVideoCaptionAsync(
        int videoId,
        string filename,
        string languageCode,
        string captionType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filename);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(captionType);
        if (!string.Equals(Path.GetFileName(filename), filename, StringComparison.Ordinal))
            throw new ArgumentOutOfRangeException(nameof(filename), "API test caption sidecars must use a leaf filename beside their video source.");

        var normalizedCaptionType = captionType.Trim().ToLowerInvariant();
        if (normalizedCaptionType is not ("vtt" or "srt"))
            throw new ArgumentOutOfRangeException(nameof(captionType), "API test caption sidecars must be VTT or SRT files.");

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Caption discovery is scanner-owned in production. Seed only a caption row for the single
        // fixture video file after proving that the sidecar is colocated in the disposable library.
        var file = await db.VideoFiles
            .Include(candidate => candidate.ParentFolder)
            .SingleOrDefaultAsync(candidate => candidate.VideoId == videoId, cancellationToken)
            ?? throw new InvalidOperationException($"The API test video {videoId} has no stream file to attach a caption to.");
        var directory = file.ParentFolder?.Path;
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Path.Combine(directory, filename)))
            throw new FileNotFoundException("The API test caption sidecar does not exist beside the stream video source.", filename);

        var caption = new VideoCaption
        {
            FileId = file.Id,
            Filename = filename,
            LanguageCode = languageCode.Trim().ToLowerInvariant(),
            CaptionType = normalizedCaptionType,
        };
        db.VideoCaptions.Add(caption);
        await db.SaveChangesAsync(cancellationToken);
        return caption.Id;
    }

    public async Task AttachStreamImageFileAsync(
        int imageId,
        string path,
        int width,
        int height,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        var file = new FileInfo(path);
        if (!file.Exists || file.DirectoryName is null)
            throw new FileNotFoundException("The API test image source does not exist.", path);

        db.ImageFiles.Add(new ImageFile
        {
            ImageId = imageId,
            Basename = file.Name,
            ParentFolder = new Folder { Path = file.DirectoryName, ModTime = file.LastWriteTimeUtc },
            Size = file.Length,
            ModTime = file.LastWriteTimeUtc,
            Format = file.Extension.TrimStart('.'),
            Width = width,
            Height = height,
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

    public async Task AttachGalleryArchiveAsync(
        int galleryId,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var fullPath = Path.GetFullPath(archivePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("The gallery archive fixture does not exist.", fullPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentOutOfRangeException(nameof(archivePath), "Gallery archive fixtures must use the .zip extension.");

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        if (!await db.Galleries.AnyAsync(gallery => gallery.Id == galleryId, cancellationToken))
            throw new InvalidOperationException($"Gallery {galleryId} does not exist.");

        var folderPath = Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("The gallery archive fixture has no parent directory.");
        var folder = await db.Folders.FirstOrDefaultAsync(item => item.Path == folderPath, cancellationToken);
        if (folder == null)
        {
            folder = new Folder
            {
                Path = folderPath,
                ModTime = Directory.GetLastWriteTimeUtc(folderPath),
            };
        }

        var file = new FileInfo(fullPath);
        db.GalleryFiles.Add(new GalleryFile
        {
            GalleryId = galleryId,
            Basename = file.Name,
            ParentFolder = folder,
            Size = file.Length,
            ModTime = file.LastWriteTimeUtc,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AttachImageFileAsync(
        int imageId,
        long size,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);

        // Public image creation cannot supply deterministic probe metrics. Seed only the file row
        // needed to verify the aggregate endpoint's filtered file-size total.
        var now = DateTime.UtcNow;
        var folder = new Folder
        {
            Path = $"/api-tests/image-aggregate/{Guid.NewGuid():N}",
            ModTime = now,
        };
        db.ImageFiles.Add(new ImageFile
        {
            ImageId = imageId,
            Basename = "aggregate-source.png",
            ParentFolder = folder,
            Size = size,
            ModTime = now,
            Format = "png",
            Width = 20,
            Height = 10,
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
        string? sourceRunId = null,
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
            SourceRunId = sourceRunId,
        };
        db.FaceAppearances.Add(appearance);
        await db.SaveChangesAsync(cancellationToken);
        return appearance.Id;
    }

    public async Task<int> CreateCompletedAiRunAsync(
        string runKey,
        AiRunTargetType targetType,
        int targetId,
        DateTime startedAt,
        DateTime completedAt,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runKey))
            throw new ArgumentException("An AI run key is required.", nameof(runKey));

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var run = new AiRun
        {
            RunKey = runKey,
            SourceKey = "api-test",
            TargetType = targetType,
            TargetId = targetId,
            Status = AiRunStatus.Completed,
            StartedAt = startedAt.ToUniversalTime(),
            CompletedAt = completedAt.ToUniversalTime(),
        };
        db.AiRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run.Id;
    }

    public async Task<int> CreateFaceEmbeddingAsync(
        int faceId,
        IReadOnlyCollection<float> values,
        string kindFamily,
        CancellationToken cancellationToken = default,
        string sourceKey = "api-test",
        string? sourceRunId = null,
        int sectionIndex = 0,
        double? startSec = null,
        double? endSec = null,
        string? metaJson = null)
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
            SectionIndex = sectionIndex,
            StartSec = startSec,
            EndSec = endSec,
            SourceKey = sourceKey,
            SourceRunId = sourceRunId,
            Meta = metaJson is null ? null : JsonDocument.Parse(metaJson),
        };
        db.Embeddings.Add(embedding);
        await db.SaveChangesAsync(cancellationToken);
        return embedding.Id;
    }

}
