using System.Text.Json;
using System.Text;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Data.Services;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Cove.ApiTests.Infrastructure;

public sealed class DatabaseClient
{
    private const long CustomFieldJsonIndexAdvisoryLockKey = 0x434F56454A534F4E;
    private readonly string _connectionString;

    internal DatabaseClient(string connectionString)
        => _connectionString = connectionString;

    public async Task<StringCollectionOperatorFixture> SeedStringCollectionOperatorFixtureAsync(
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var now = DateTime.UtcNow;
        var folder = new Folder { Path = $"/api-tests/string-operators/{Guid.NewGuid():N}", ModTime = now };
        var matchingAudio = new Audio
        {
            Title = "Matching collection audio",
            Files = [new AudioFile { Basename = "match.flac", ParentFolder = folder, Format = "flac", AudioCodec = "flac", ModTime = now }],
            Tracks = [new AudioTrack { OrderIndex = 1, Title = "Opening" }, new AudioTrack { OrderIndex = 2, Title = "Needle track" }],
        };
        var otherAudio = new Audio
        {
            Title = "Other collection audio",
            Files = [new AudioFile { Basename = "other.mp3", ParentFolder = folder, Format = "mp3", AudioCodec = "mp3", ModTime = now }],
        };
        var matchingText = new TextDocument
        {
            Title = "Matching collection text",
            Files = [new TextFile { Basename = "match.epub", ParentFolder = folder, Format = "epub", ModTime = now }],
        };
        var otherText = new TextDocument
        {
            Title = "Other collection text",
            Files = [new TextFile { Basename = "other.pdf", ParentFolder = folder, Format = "pdf", ModTime = now }],
        };
        var aliasStudio = new Studio { Name = "Alias collection studio", Aliases = [new StudioAlias { Alias = "Needle alias" }] };
        var otherStudio = new Studio { Name = "Alias collection studio control", Aliases = [new StudioAlias { Alias = "Other alias" }] };
        var hostTypeGroup = new Group { Name = "Host type collection group", AllowedHostTypes = ["video", "gallery"] };
        var otherHostTypeGroup = new Group { Name = "Host type collection group control", AllowedHostTypes = ["video"] };
        var video = new Video { Title = "Segment collection host" };
        db.AddRange(folder, matchingAudio, otherAudio, matchingText, otherText, aliasStudio, otherStudio, hostTypeGroup, otherHostTypeGroup, video);
        await db.SaveChangesAsync(cancellationToken);
        var matchingSegment = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 1, EndSec = 2, SourceKey = "user", Title = "Needle segment" };
        var emptySegment = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 3, EndSec = 4, SourceKey = "user", Title = "" };
        db.Segments.AddRange(matchingSegment, emptySegment);
        await db.SaveChangesAsync(cancellationToken);
        return new StringCollectionOperatorFixture(
            matchingAudio.Id, otherAudio.Id, matchingText.Id, otherText.Id,
            aliasStudio.Id, hostTypeGroup.Id, matchingSegment.Id, emptySegment.Id);
    }

    public async Task<int> CreateOwnedFileAsync(
        string ownerKind,
        int? ownerId,
        string path,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists || info.DirectoryName is null)
            throw new FileNotFoundException("The API test owned-file source does not exist.", path);

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var folder = await db.Folders.FirstOrDefaultAsync(
            candidate => candidate.Path == info.DirectoryName,
            cancellationToken) ?? new Folder { Path = info.DirectoryName, ModTime = info.LastWriteTimeUtc };
        BaseFileEntity file = ownerKind.ToLowerInvariant() switch
        {
            EntityKinds.Video => new VideoFile { VideoId = ownerId },
            EntityKinds.Image => new ImageFile { ImageId = ownerId },
            EntityKinds.Gallery => new GalleryFile { GalleryId = ownerId },
            EntityKinds.Audio => new AudioFile { AudioId = ownerId },
            EntityKinds.Text => new TextFile { TextDocumentId = ownerId },
            _ => throw new ArgumentOutOfRangeException(nameof(ownerKind), ownerKind, "Unsupported owned-file kind."),
        };
        file.Basename = info.Name;
        file.ParentFolder = folder;
        file.Size = info.Length;
        file.ModTime = info.LastWriteTimeUtc;
        db.Set<BaseFileEntity>().Add(file);
        await db.SaveChangesAsync(cancellationToken);
        return file.Id;
    }

    public async Task CreateAuditEventAsync(
        string action,
        string detail,
        string? targetId = null,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        db.AuditEvents.Add(new AuditEvent
        {
            ActorKind = "system",
            Action = action,
            Outcome = "success",
            TargetId = targetId,
            Detail = detail,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveCustomFieldJsonValueAsync(
        int definitionId,
        string entityType,
        int entityId,
        JsonElement value,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        db.CustomFieldValues.Add(new CustomFieldValue
        {
            DefinitionId = definitionId,
            EntityType = entityType,
            EntityId = entityId,
            Position = 0,
            JsonValue = value.Clone(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<CustomFieldTextStorage> GetCustomFieldTextStorageAsync(
        int definitionId,
        string entityType,
        int entityId,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        return await db.CustomFieldValues
            .AsNoTracking()
            .Where(value => value.DefinitionId == definitionId
                && value.EntityType == entityType
                && value.EntityId == entityId)
            .Select(value => new CustomFieldTextStorage(value.TextValue, value.LongTextValue))
            .SingleAsync(cancellationToken);
    }

    public async Task SetCustomFieldDefinitionShapeAsync(
        int definitionId,
        string type,
        bool filterable,
        bool sortable,
        bool isMultiValue,
        CancellationToken cancellationToken = default)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var updated = await db.CustomFieldDefinitions
            .Where(definition => definition.Id == definitionId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(definition => definition.Type, type)
                    .SetProperty(definition => definition.Filterable, filterable)
                    .SetProperty(definition => definition.Sortable, sortable)
                    .SetProperty(definition => definition.IsMultiValue, isMultiValue),
                cancellationToken);
        if (updated != 1)
            throw new InvalidOperationException("The API test could not update the expected custom field definition.");
    }

    public async Task<IReadOnlyList<string>> GetCustomFieldValueIndexDefinitionsAsync(
        CancellationToken cancellationToken = default)
    {
        var definitions = new List<string>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'custom_field_values'
            ORDER BY indexname;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            definitions.Add(reader.GetString(0));
        return definitions;
    }

    public async Task<IReadOnlyList<ManagedCustomFieldJsonIndex>> GetManagedCustomFieldJsonIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        var indexes = new List<ManagedCustomFieldJsonIndex>();
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT index_class.relname,
                   index_metadata.indisvalid,
                   index_metadata.indisready,
                   pg_get_indexdef(index_class.oid)
            FROM pg_class AS index_class
            JOIN pg_index AS index_metadata ON index_metadata.indexrelid = index_class.oid
            JOIN pg_class AS table_class ON table_class.oid = index_metadata.indrelid
            JOIN pg_namespace AS table_namespace ON table_namespace.oid = table_class.relnamespace
            WHERE table_namespace.nspname = 'public'
              AND table_class.relname = 'custom_field_values'
              AND index_class.relname LIKE 'ix_cfv_json_v%'
            ORDER BY index_class.relname;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            indexes.Add(new ManagedCustomFieldJsonIndex(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetString(3)));
        }

        return indexes;
    }

    public async Task<string> ExplainCustomFieldJsonNumberQueryAsync(
        int definitionId,
        string entityType,
        string path,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string pathLiteral;
        await using (var quote = connection.CreateCommand())
        {
            quote.CommandText = "SELECT quote_literal(@path);";
            quote.Parameters.AddWithValue("path", path);
            pathLiteral = (string)(await quote.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not quote the JSON path."));
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = "SET LOCAL enable_seqscan = off;";
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var explain = connection.CreateCommand();
        explain.Transaction = transaction;
        explain.CommandText = $"""
            EXPLAIN (FORMAT JSON)
            SELECT video."Id"
            FROM public.videos AS video
            WHERE EXISTS (
                SELECT 1
                FROM public.custom_field_values AS field_value
                WHERE field_value."DefinitionId" = @definitionId
                  AND field_value."EntityType" = @entityType
                  AND field_value."EntityId" = video."Id"
                  AND field_value."Position" = 0
                  AND field_value."JsonValue" IS NOT NULL
                  AND public.cove_json_pointer_number(field_value."JsonValue", {pathLiteral}) IS NOT NULL
                  AND public.cove_json_pointer_number(field_value."JsonValue", {pathLiteral}) > 0);
            """;
        explain.Parameters.AddWithValue("definitionId", definitionId);
        explain.Parameters.AddWithValue("entityType", entityType);
        var plan = (string)(await explain.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an EXPLAIN plan."));
        await transaction.RollbackAsync(cancellationToken);
        return plan;
    }

    public async Task<string> ExplainCustomFieldJsonTextEqualsQueryAsync(
        int definitionId,
        string entityType,
        string path,
        string value,
        CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        string pathLiteral;
        await using (var quote = connection.CreateCommand())
        {
            quote.CommandText = "SELECT quote_literal(@path);";
            quote.Parameters.AddWithValue("path", path);
            pathLiteral = (string)(await quote.ExecuteScalarAsync(cancellationToken)
                ?? throw new InvalidOperationException("PostgreSQL did not quote the JSON path."));
        }

        var utf8Value = Encoding.UTF8.GetBytes(value);
        var indexKey = utf8Value[..Math.Min(utf8Value.Length, CustomFieldJsonDbFunctions.TextIndexKeyByteLength)];
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var settings = connection.CreateCommand())
        {
            settings.Transaction = transaction;
            settings.CommandText = "SET LOCAL enable_seqscan = off;";
            await settings.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var explain = connection.CreateCommand();
        explain.Transaction = transaction;
        explain.CommandText = $"""
            EXPLAIN (FORMAT JSON)
            SELECT video."Id"
            FROM public.videos AS video
            WHERE EXISTS (
                SELECT 1
                FROM public.custom_field_values AS field_value
                WHERE field_value."DefinitionId" = @definitionId
                  AND field_value."EntityType" = @entityType
                  AND field_value."EntityId" = video."Id"
                  AND field_value."Position" = 0
                  AND field_value."JsonValue" IS NOT NULL
                  AND public.cove_json_pointer_text_index_key(field_value."JsonValue", {pathLiteral}) IS NOT NULL
                  AND public.cove_json_pointer_text_index_key(field_value."JsonValue", {pathLiteral}) = @indexKey
                  AND public.cove_json_pointer_text(field_value."JsonValue", {pathLiteral}) = @value);
            """;
        explain.Parameters.AddWithValue("definitionId", definitionId);
        explain.Parameters.AddWithValue("entityType", entityType);
        explain.Parameters.AddWithValue("indexKey", indexKey);
        explain.Parameters.AddWithValue("value", value);
        var plan = (string)(await explain.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("PostgreSQL did not return an EXPLAIN plan."));
        await transaction.RollbackAsync(cancellationToken);
        return plan;
    }

    public async Task<IAsyncDisposable> HoldCustomFieldJsonIndexReconcileLockAsync(
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(@key);";
            command.Parameters.AddWithValue("key", CustomFieldJsonIndexAdvisoryLockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new AdvisoryLockLease(connection, CustomFieldJsonIndexAdvisoryLockKey);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

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

    private sealed class AdvisoryLockLease(NpgsqlConnection connection, long key) : IAsyncDisposable
    {
        private NpgsqlConnection? _connection = connection;

        public async ValueTask DisposeAsync()
        {
            var ownedConnection = Interlocked.Exchange(ref _connection, null);
            if (ownedConnection == null)
                return;

            try
            {
                if (ownedConnection.State == System.Data.ConnectionState.Open)
                {
                    await using var command = ownedConnection.CreateCommand();
                    command.CommandText = "SELECT pg_advisory_unlock(@key);";
                    command.Parameters.AddWithValue("key", key);
                    await command.ExecuteNonQueryAsync();
                }
            }
            finally
            {
                await ownedConnection.DisposeAsync();
            }
        }
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
        // the file row needed for API assertions that depend on persisted video-file metadata.
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
        => await CreateEmbeddingAsync(
            EmbeddingHostType.Face, faceId, values, kindFamily, EmbeddingModality.Face,
            cancellationToken, sourceKey, sourceRunId, sectionIndex, startSec, endSec, metaJson);

    public async Task<int> CreateEmbeddingAsync(
        EmbeddingHostType hostType,
        int hostId,
        IReadOnlyCollection<float> values,
        string kindFamily,
        EmbeddingModality modality = EmbeddingModality.Visual,
        CancellationToken cancellationToken = default,
        string sourceKey = "api-test",
        string? sourceRunId = null,
        int sectionIndex = 0,
        double? startSec = null,
        double? endSec = null,
        string? metaJson = null)
    {
        if (values.Count == 0)
            throw new ArgumentException("An embedding must contain at least one value.", nameof(values));

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(_connectionString, npgsql => npgsql.UseVector())
            .Options;
        await using var db = new CoveContext(options);
        var vector = values.ToArray();
        var embedding = new Embedding
        {
            HostType = hostType,
            HostId = hostId,
            Kind = kindFamily,
            KindFamily = kindFamily,
            Modality = modality,
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

public sealed record ManagedCustomFieldJsonIndex(
    string Name,
    bool IsValid,
    bool IsReady,
    string Definition);

public sealed record CustomFieldTextStorage(
    string? TextValue,
    string? LongTextValue);

public sealed record StringCollectionOperatorFixture(
    int MatchingAudioId,
    int OtherAudioId,
    int MatchingTextId,
    int OtherTextId,
    int AliasStudioId,
    int HostTypeGroupId,
    int MatchingSegmentId,
    int EmptySegmentId);
