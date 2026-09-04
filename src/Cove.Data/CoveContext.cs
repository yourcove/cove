using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data.Services;
using Cove.Plugins;
using System.Text.Json;
using System.Linq.Expressions;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using System.Data;

namespace Cove.Data;

public partial class CoveContext : DbContext
{
    private static IReadOnlyList<IDataExtension> _dataExtensions = [];
    private static int _modelGeneration;
    private readonly bool _includeDataExtensionsInModel;
    private const int DerivedArrayAdvisoryLockNamespace = 0x434F5600;
    private const int TagNamespaceAdvisoryLockKey = 0x434F564E;
    private static readonly SemaphoreSlim[] DerivedArrayWriteStripes =
        Enumerable.Range(0, 257).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly SemaphoreSlim TagNamespaceWriteLock = new(1, 1);
    private bool _persistingDerivedCounts;
    private int _tagNameValidationSuppressionDepth;
    private int _authorizationFilterSuppressionDepth;
    private int _embeddingReadAuthorizationFilterSuppressionDepth;

    /// <summary>
    /// Monotonic token that changes whenever the set of loaded data extensions changes. Consumed by
    /// <see cref="CoveModelCacheKeyFactory"/> so EF Core rebuilds the model — picking up or dropping an
    /// extension's entity types — when a data extension is installed or uninstalled at runtime, without
    /// an app restart. The rebuilt model is a superset for installs, so other extensions and core code
    /// keep working against it unchanged.
    /// </summary>
    public static int ModelGeneration => Volatile.Read(ref _modelGeneration);

    public static void SetDataExtensions(IEnumerable<IDataExtension> extensions)
    {
        var next = extensions.ToList();
        // Compare by instance reference, not id: a reloaded extension keeps its id but is a new object from
        // a new AssemblyLoadContext, and the model must be rebuilt so its entity types match the running
        // code. SetEquals over a reference-keyed set also ignores ordering (which is irrelevant here).
        var changed = !next.ToHashSet().SetEquals(_dataExtensions);
        _dataExtensions = next;
        if (changed)
            Interlocked.Increment(ref _modelGeneration);
    }

    public CoveContext(DbContextOptions<CoveContext> options, ICurrentPrincipalAccessor? principalAccessor = null)
        : this(options, principalAccessor, includeDataExtensionsInModel: true)
    {
    }

    internal CoveContext(
        DbContextOptions<CoveContext> options,
        ICurrentPrincipalAccessor? principalAccessor,
        bool includeDataExtensionsInModel) : base(options)
    {
        _principalAccessor = principalAccessor;
        _includeDataExtensionsInModel = includeDataExtensionsInModel;
    }

    protected CoveContext(DbContextOptions options) : base(options)
    {
        _includeDataExtensionsInModel = true;
    }

    internal bool IncludeDataExtensionsInModel => _includeDataExtensionsInModel;

    // Core entities
    public DbSet<Video> Videos => Set<Video>();
    public DbSet<Performer> Performers => Set<Performer>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagGroup> TagGroups => Set<TagGroup>();
    public DbSet<Studio> Studios => Set<Studio>();
    public DbSet<Gallery> Galleries => Set<Gallery>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<Audio> Audios => Set<Audio>();
    public DbSet<TextDocument> TextDocuments => Set<TextDocument>();
    public DbSet<Group> Groups => Set<Group>();
    public DbSet<DuplicateSearch> DuplicateSearches => Set<DuplicateSearch>();
    public DbSet<DuplicateSearchGroup> DuplicateSearchGroups => Set<DuplicateSearchGroup>();
    public DbSet<DuplicateSearchItem> DuplicateSearchItems => Set<DuplicateSearchItem>();
    public DbSet<DuplicateDeletionKeeperReservation> DuplicateDeletionKeeperReservations => Set<DuplicateDeletionKeeperReservation>();
    public DbSet<PendingPhysicalFileDeletion> PendingPhysicalFileDeletions => Set<PendingPhysicalFileDeletion>();
    public DbSet<VideoDeletionCommitMarker> VideoDeletionCommitMarkers => Set<VideoDeletionCommitMarker>();
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldJsonPathDefinition> CustomFieldJsonPathDefinitions => Set<CustomFieldJsonPathDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();
    public DbSet<TagApplication> TagApplications => Set<TagApplication>();
    public DbSet<FieldProvenance> FieldProvenance => Set<FieldProvenance>();
    public DbSet<Segment> Segments => Set<Segment>();
    public DbSet<SegmentDisplayProfile> SegmentDisplayProfiles => Set<SegmentDisplayProfile>();
    public DbSet<SegmentDisplayRule> SegmentDisplayRules => Set<SegmentDisplayRule>();
    public DbSet<Detection> Detections => Set<Detection>();
    public DbSet<Face> Faces => Set<Face>();
    public DbSet<FaceAppearance> FaceAppearances => Set<FaceAppearance>();
    public DbSet<FaceSuggestionDecision> FaceSuggestionDecisions => Set<FaceSuggestionDecision>();
    public DbSet<Embedding> Embeddings => Set<Embedding>();
    public DbSet<AiRun> AiRuns => Set<AiRun>();
    public DbSet<UserEntityAffinity> UserEntityAffinities => Set<UserEntityAffinity>();
    public DbSet<Interaction> Interactions => Set<Interaction>();
    public DbSet<PlaybackSession> PlaybackSessions => Set<PlaybackSession>();
    public DbSet<UserSession> UserSessions => Set<UserSession>();
    public DbSet<PlaybackInterval> PlaybackIntervals => Set<PlaybackInterval>();
    public DbSet<Rating> Ratings => Set<Rating>();
    public DbSet<UserBookmark> UserBookmarks => Set<UserBookmark>();
    public DbSet<SavedFilter> SavedFilters => Set<SavedFilter>();
    public DbSet<GalleryChapter> GalleryChapters => Set<GalleryChapter>();
    public DbSet<ScrapeAttempt> ScrapeAttempts => Set<ScrapeAttempt>();

    // Users / Auth / Permissions / Audit
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<UserRoleAssignment> UserRoleAssignments => Set<UserRoleAssignment>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<RoleContentRule> RoleContentRules => Set<RoleContentRule>();
    public DbSet<RoleEntityOverride> RoleEntityOverrides => Set<RoleEntityOverride>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<UserInviteToken> UserInviteTokens => Set<UserInviteToken>();
    public DbSet<ExternalIdentityLink> ExternalIdentityLinks => Set<ExternalIdentityLink>();
    public DbSet<ShareLink> ShareLinks => Set<ShareLink>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<Dashboard> Dashboards => Set<Dashboard>();

    // Extensions
    public DbSet<ExtensionData> ExtensionData => Set<ExtensionData>();

    // Files & Folders
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<VideoFile> VideoFiles => Set<VideoFile>();
    public DbSet<ImageFile> ImageFiles => Set<ImageFile>();
    public DbSet<GalleryFile> GalleryFiles => Set<GalleryFile>();
    public DbSet<AudioFile> AudioFiles => Set<AudioFile>();
    public DbSet<TextFile> TextFiles => Set<TextFile>();
    public DbSet<FileFingerprint> FileFingerprints => Set<FileFingerprint>();
    public DbSet<VideoCaption> VideoCaptions => Set<VideoCaption>();
    public DbSet<GroupItem> GroupItems => Set<GroupItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CoveContext).Assembly);

        // TPH for file hierarchy
        modelBuilder.Entity<BaseFileEntity>()
            .HasDiscriminator<string>("FileType")
            .HasValue<VideoFile>("Video")
            .HasValue<ImageFile>("Image")
            .HasValue<GalleryFile>("Gallery")
            .HasValue<AudioFile>("Audio")
            .HasValue<TextFile>("Text");

        modelBuilder.Entity<BaseFileEntity>()
            .HasMany(f => f.Fingerprints)
            .WithOne(fp => fp.File)
            .HasForeignKey(fp => fp.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<FileFingerprint>(entity =>
        {
            entity.ToTable("file_fingerprints");
            entity.HasIndex(fp => new { fp.Type, fp.Value });
            entity.HasIndex(fp => fp.FileId);
        });

        modelBuilder.Entity<VideoCaption>()
            .ToTable("video_captions");

        // Secondary tables that historically defaulted to their CLR type name (PascalCase). Map them to
        // snake_case so every table follows one convention. (The 20260707 migration renames them in the DB.)
        modelBuilder.Entity<VideoUrl>().ToTable("video_urls");
        modelBuilder.Entity<GalleryUrl>().ToTable("gallery_urls");
        modelBuilder.Entity<GroupUrl>().ToTable("group_urls");
        modelBuilder.Entity<ImageUrl>().ToTable("image_urls");
        modelBuilder.Entity<PerformerUrl>().ToTable("performer_urls");
        modelBuilder.Entity<StudioUrl>().ToTable("studio_urls");
        modelBuilder.Entity<PerformerAlias>().ToTable("performer_aliases");
        modelBuilder.Entity<StudioAlias>().ToTable("studio_aliases");
        modelBuilder.Entity<TagAlias>(entity =>
        {
            entity.ToTable("tag_aliases");
            entity.Property(alias => alias.NamespaceKey).IsRequired();
        });
        modelBuilder.Entity<VideoPlayHistory>().ToTable("video_play_history");

        modelBuilder.Entity<VideoFile>()
            .HasMany(v => v.Captions)
            .WithOne(c => c.File)
            .HasForeignKey(c => c.FileId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.ToTable("user_sessions");
            entity.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(session => new { session.UserId, session.LastSeenAt });
        });

        modelBuilder.Entity<PlaybackSession>(entity =>
        {
            entity.ToTable("playback_sessions");
            entity.HasOne<User>().WithMany().HasForeignKey(session => session.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(session => new { session.UserId, session.HostType, session.HostId, session.StartedAt });
            // The per-entity session within a user-global session. This is the lookup + concurrency key now;
            // a reload reuses the same UserSession (within the idle timeout) so it merges into one row.
            entity.HasIndex(session => new { session.UserId, session.HostType, session.HostId, session.UserSessionId }).IsUnique();
            entity.HasIndex(session => new { session.UserId, session.Surface, session.LastSeenAt });
            entity.Property(session => session.Surface).HasMaxLength(64);
            entity.Property(session => session.ScopeKey).HasMaxLength(256);
            entity.Property(session => session.Route).HasMaxLength(512);
            entity.Property(session => session.Referrer).HasMaxLength(512);
            entity.Property(session => session.RecommendationSource).HasMaxLength(128);
            entity.Property(session => session.Context).HasColumnType("jsonb");
            entity.HasMany(session => session.Intervals)
                .WithOne(interval => interval.Session)
                .HasForeignKey(interval => interval.PlaybackSessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlaybackInterval>(entity =>
        {
            entity.ToTable("playback_intervals");
            entity.HasOne<User>().WithMany().HasForeignKey(interval => interval.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(interval => new { interval.UserId, interval.HostType, interval.HostId });
            entity.HasIndex(interval => new { interval.PlaybackSessionId, interval.StartSec });
            entity.HasIndex(interval => new { interval.UserId, interval.Surface, interval.RecordedAt });
            entity.Property(interval => interval.Surface).HasMaxLength(64);
            entity.Property(interval => interval.ScopeKey).HasMaxLength(256);
            entity.Property(interval => interval.Context).HasColumnType("jsonb");
        });

        modelBuilder.Entity<Face>(entity =>
        {
            // Sort/filter the unlinked-faces list by suggestion confidence in SQL.
            entity.HasIndex(face => new { face.PerformerId, face.TopSuggestionConfidence });
            // Filter the list by suggested (local) performer.
            entity.HasIndex(face => face.TopSuggestionLocalPerformerId);
            // Background materializer scan: unlinked faces awaiting (re)compute.
            entity.HasIndex(face => new { face.PerformerId, face.TopSuggestionComputedAt });
        });

        foreach (var ext in _includeDataExtensionsInModel ? _dataExtensions : [])
        {
            ext.ConfigureModel(modelBuilder);
        }

        var isNpgsql = Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;
        ConfigureVectorStorage(modelBuilder, isNpgsql);

        if (isNpgsql)
        {
            ConfigureCustomFieldJsonFunctions(modelBuilder);
            ConfigureSearchVectors(modelBuilder);
            ConfigureAuthorizationFilters(modelBuilder);
        }
        else
            ConfigureProviderFallbacks(modelBuilder);
    }

    private static void ConfigureCustomFieldJsonFunctions(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDbFunction(typeof(CustomFieldJsonDbFunctions).GetMethod(nameof(CustomFieldJsonDbFunctions.Text))!)
            .HasName("cove_json_pointer_text")
            .HasSchema("public");
        modelBuilder.HasDbFunction(typeof(CustomFieldJsonDbFunctions).GetMethod(nameof(CustomFieldJsonDbFunctions.TextIndexKey))!)
            .HasName("cove_json_pointer_text_index_key")
            .HasSchema("public");
        modelBuilder.HasDbFunction(typeof(CustomFieldJsonDbFunctions).GetMethod(nameof(CustomFieldJsonDbFunctions.Number))!)
            .HasName("cove_json_pointer_number")
            .HasSchema("public");
        modelBuilder.HasDbFunction(typeof(CustomFieldJsonDbFunctions).GetMethod(nameof(CustomFieldJsonDbFunctions.Boolean))!)
            .HasName("cove_json_pointer_boolean")
            .HasSchema("public");
    }

    private static void ConfigureSearchVectors(ModelBuilder modelBuilder)
    {
        ConfigureSearchVector<Video>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Title", '') || ' ' || coalesce("Code", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '') || ' ' || coalesce("Director", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("Captions", '') || ' ' || coalesce("FileSearchText", '') || ' ' || coalesce("SearchText", '')), 'C')
            """);

        ConfigureSearchVector<Image>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Title", '') || ' ' || coalesce("Code", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '') || ' ' || coalesce("Photographer", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("FileSearchText", '') || ' ' || coalesce("SearchText", '')), 'C')
            """);

        ConfigureSearchVector<Audio>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Title", '') || ' ' || coalesce("Code", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("FileSearchText", '') || ' ' || coalesce("SearchText", '')), 'C')
            """);

        ConfigureSearchVector<TextDocument>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Title", '') || ' ' || coalesce("Code", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("FileSearchText", '') || ' ' || coalesce("SearchText", '')), 'C')
            """);

        ConfigureSearchVector<Performer>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Name", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Disambiguation", '') || ' ' || coalesce("Details", '') || ' ' || coalesce("SearchText", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("Country", '') || ' ' || coalesce("Ethnicity", '') || ' ' || coalesce("Tattoos", '') || ' ' || coalesce("Piercings", '')), 'C')
            """);

        ConfigureSearchVector<Tag>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Name", '') || ' ' || coalesce("SortName", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Description", '') || ' ' || coalesce("SearchText", '')), 'B')
            """);

        ConfigureSearchVector<Studio>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Name", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '') || ' ' || coalesce("SearchText", '')), 'B')
            """);

        ConfigureSearchVector<Gallery>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Title", '') || ' ' || coalesce("Code", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Details", '') || ' ' || coalesce("Photographer", '')), 'B') ||
            setweight(to_tsvector('simple', coalesce("SearchText", '')), 'C')
            """);

        ConfigureSearchVector<Group>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Name", '') || ' ' || coalesce("Aliases", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("Synopsis", '') || ' ' || coalesce("Director", '') || ' ' || coalesce("SearchText", '')), 'B')
            """);

        ConfigureSearchVector<Face>(modelBuilder, """
            setweight(to_tsvector('simple', coalesce("Label", '')), 'A') ||
            setweight(to_tsvector('simple', coalesce("PrimarySourceKey", '') || ' ' || coalesce("SearchText", '')), 'B')
            """);
    }

    private static void ConfigureSearchVector<TEntity>(ModelBuilder modelBuilder, string computedColumnSql)
        where TEntity : class
    {
        modelBuilder.Entity<TEntity>()
            .Property<NpgsqlTsVector>("SearchVector")
            .HasColumnType("tsvector")
            .HasComputedColumnSql(computedColumnSql, stored: true);

        modelBuilder.Entity<TEntity>()
            .HasIndex("SearchVector")
            .HasMethod("gin");
    }

    private static void ConfigureVectorStorage(ModelBuilder modelBuilder, bool usePgvector)
    {
        var vectorConverter = new ValueConverter<Vector?, string?>(
            vector => vector == null ? null : SerializeVector(vector),
            json => string.IsNullOrWhiteSpace(json) ? null : DeserializeVector(json));

        var vectorComparer = new ValueComparer<Vector?>(
            (left, right) => left == null ? right == null : right != null && VectorsEqual(left, right),
            vector => vector == null ? 0 : GetVectorHash(vector),
            vector => vector == null ? null : CloneVector(vector));

        if (usePgvector)
        {
            modelBuilder.HasPostgresExtension("vector");
        }

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entityType => entityType.GetProperties()))
        {
            if (property.ClrType != typeof(Vector))
                continue;

            property.SetValueComparer(vectorComparer);
            if (usePgvector)
            {
                property.SetColumnType("vector");
            }
            else
            {
                property.SetValueConverter(vectorConverter);
                property.SetColumnType("text");
            }
        }
    }

    private static void ConfigureProviderFallbacks(ModelBuilder modelBuilder)
    {
        var jsonConverter = new ValueConverter<JsonDocument?, string?>(
            document => SerializeJsonDocument(document),
            json => DeserializeJsonDocument(json));

        var jsonComparer = new ValueComparer<JsonDocument?>(
            (left, right) => JsonDocumentsEqual(left, right),
            document => GetJsonDocumentHash(document),
            document => CloneJsonDocument(document));

        var jsonElementConverter = new ValueConverter<JsonElement?, string?>(
            element => SerializeJsonElement(element),
            json => DeserializeJsonElement(json));

        var jsonElementComparer = new ValueComparer<JsonElement?>(
            (left, right) => JsonElementsEqual(left, right),
            element => GetJsonElementHash(element),
            element => CloneJsonElement(element));

        var objectDictionaryConverter = new ValueConverter<Dictionary<string, object>?, string?>(
            dictionary => SerializeObjectDictionary(dictionary),
            json => DeserializeObjectDictionary(json));

        var objectDictionaryComparer = new ValueComparer<Dictionary<string, object>?>(
            (left, right) => string.Equals(GetObjectDictionaryText(left), GetObjectDictionaryText(right), StringComparison.Ordinal),
            dictionary => GetObjectDictionaryHash(dictionary),
            dictionary => CloneObjectDictionary(dictionary));

        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(entityType => entityType.GetProperties()))
        {
            if (property.ClrType == typeof(JsonDocument))
            {
                property.SetValueConverter(jsonConverter);
                property.SetValueComparer(jsonComparer);
            }

            if (property.ClrType == typeof(JsonElement?))
            {
                property.SetValueConverter(jsonElementConverter);
                property.SetValueComparer(jsonElementComparer);
                property.SetColumnType("text");
            }

            if (property.ClrType == typeof(Dictionary<string, object>))
            {
                property.SetValueConverter(objectDictionaryConverter);
                property.SetValueComparer(objectDictionaryComparer);
            }
        }
    }

    private static string? SerializeJsonDocument(JsonDocument? document) =>
        document is null ? null : document.RootElement.GetRawText();

    private static JsonDocument? DeserializeJsonDocument(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);

    private static bool JsonDocumentsEqual(JsonDocument? left, JsonDocument? right) =>
        string.Equals(GetJsonText(left), GetJsonText(right), StringComparison.Ordinal);

    private static int GetJsonDocumentHash(JsonDocument? document) =>
        GetJsonText(document)?.GetHashCode(StringComparison.Ordinal) ?? 0;

    private static JsonDocument? CloneJsonDocument(JsonDocument? document) =>
        document is null ? null : JsonDocument.Parse(document.RootElement.GetRawText());

    private static string? GetJsonText(JsonDocument? document) =>
        document is null ? null : document.RootElement.GetRawText();

    private static string? SerializeJsonElement(JsonElement? element) =>
        element?.GetRawText();

    private static JsonElement? DeserializeJsonElement(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static bool JsonElementsEqual(JsonElement? left, JsonElement? right) =>
        string.Equals(SerializeJsonElement(left), SerializeJsonElement(right), StringComparison.Ordinal);

    private static int GetJsonElementHash(JsonElement? element) =>
        element?.GetRawText().GetHashCode(StringComparison.Ordinal) ?? 0;

    private static JsonElement? CloneJsonElement(JsonElement? element) =>
        element?.Clone();

    private static string? SerializeObjectDictionary(Dictionary<string, object>? dictionary)
    {
        if (dictionary is null)
        {
            return null;
        }

        var normalized = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in dictionary)
        {
            normalized[key] = value;
        }

        return JsonSerializer.Serialize(normalized);
    }

    private static Dictionary<string, object>? DeserializeObjectDictionary(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<Dictionary<string, object>>(json);

    private static string? GetObjectDictionaryText(Dictionary<string, object>? dictionary) =>
        SerializeObjectDictionary(dictionary);

    private static int GetObjectDictionaryHash(Dictionary<string, object>? dictionary) =>
        GetObjectDictionaryText(dictionary) is { } json ? json.GetHashCode(StringComparison.Ordinal) : 0;

    private static Dictionary<string, object>? CloneObjectDictionary(Dictionary<string, object>? dictionary) =>
        DeserializeObjectDictionary(SerializeObjectDictionary(dictionary));

    private static string SerializeVector(Vector vector) =>
        JsonSerializer.Serialize(vector.ToArray());

    private static Vector DeserializeVector(string json)
    {
        var values = JsonSerializer.Deserialize<float[]>(json) ?? [];
        return new Vector(values);
    }

    private static bool VectorsEqual(Vector left, Vector right)
    {
        var leftValues = left.ToArray();
        var rightValues = right.ToArray();

        if (leftValues.Length != rightValues.Length)
            return false;

        for (var index = 0; index < leftValues.Length; index++)
        {
            if (!leftValues[index].Equals(rightValues[index]))
                return false;
        }

        return true;
    }

    private static int GetVectorHash(Vector vector)
    {
        var hash = new HashCode();
        foreach (var value in vector.ToArray())
            hash.Add(value);
        return hash.ToHashCode();
    }

    private static Vector CloneVector(Vector vector) =>
        new(vector.ToArray());

    public override int SaveChanges()
    {
        if (_persistingDerivedCounts)
            return base.SaveChanges();

        var tagNamespaceLock = AcquireTagNamespaceWriteLock();
        try
        {
            NormalizeAndValidateTagNames();
            NormalizeAndValidateEntityNames();
            ProtectDenormalizedIdArraysFromDirectWrites();
            var arrayTargets = CollectDerivedArrayTargets();
            var localLockIndexes = UsesDatabaseDerivedArrayWriteLocks()
                ? []
                : AcquireLocalDerivedArrayWriteLocks(arrayTargets);
            var databaseLock = default(DatabaseDerivedArrayWriteLock);
            try
            {
                databaseLock = AcquireDatabaseDerivedArrayWriteLocks(arrayTargets);
                UpdateTimestamps();
                ComputeFilePaths();
                MaintainDenormalizedIdArrays(arrayTargets);
                CleanupEngagementRowsForDeletedEntities();
                var derivedCountTargets = CollectDerivedCountTargets();
                var postSaveDerivedCountTargets = CollectPostSaveDerivedCountTargets();
                var result = SaveChangesWithNameConstraintTranslation();
                AddPostSaveDerivedCountTargets(derivedCountTargets, postSaveDerivedCountTargets);
                MaintainPostSaveDenormalizedIdArrays(derivedCountTargets, postSaveDerivedCountTargets);
                PersistDerivedCounts(derivedCountTargets);
                return result;
            }
            finally
            {
                try
                {
                    ReleaseDatabaseDerivedArrayWriteLocks(databaseLock);
                }
                finally
                {
                    ReleaseLocalDerivedArrayWriteLocks(localLockIndexes);
                }
            }
        }
        finally
        {
            ReleaseTagNamespaceWriteLock(tagNamespaceLock);
        }
    }

    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_persistingDerivedCounts)
            return await base.SaveChangesAsync(cancellationToken);

        var tagNamespaceLock = await AcquireTagNamespaceWriteLockAsync(cancellationToken);
        try
        {
            await NormalizeAndValidateTagNamesAsync(cancellationToken);
            await NormalizeAndValidateEntityNamesAsync(cancellationToken);
            ProtectDenormalizedIdArraysFromDirectWrites();
            var arrayTargets = CollectDerivedArrayTargets();
            var localLockIndexes = UsesDatabaseDerivedArrayWriteLocks()
                ? []
                : await AcquireLocalDerivedArrayWriteLocksAsync(arrayTargets, cancellationToken);
            var databaseLock = default(DatabaseDerivedArrayWriteLock);
            try
            {
                databaseLock = await AcquireDatabaseDerivedArrayWriteLocksAsync(arrayTargets, cancellationToken);
                UpdateTimestamps();
                ComputeFilePaths();
                MaintainDenormalizedIdArrays(arrayTargets);
                await CleanupEngagementRowsForDeletedEntitiesAsync(cancellationToken);
                var derivedCountTargets = CollectDerivedCountTargets();
                var postSaveDerivedCountTargets = CollectPostSaveDerivedCountTargets();
                return await SaveChangesWithDerivedCountsAsync(derivedCountTargets, postSaveDerivedCountTargets, cancellationToken);
            }
            finally
            {
                try
                {
                    await ReleaseDatabaseDerivedArrayWriteLocksAsync(databaseLock);
                }
                finally
                {
                    ReleaseLocalDerivedArrayWriteLocks(localLockIndexes);
                }
            }
        }
        finally
        {
            await ReleaseTagNamespaceWriteLockAsync(tagNamespaceLock);
        }
    }

    /// <summary>
    /// Temporarily disables shared tag-namespace validation for the internal merge implementation,
    /// which must persist intermediate states before deleting source tags. Callers must leave the
    /// database in a scanner-clean state before committing their transaction.
    /// </summary>
    public IDisposable SuppressTagNameValidation()
    {
        _tagNameValidationSuppressionDepth++;
        return new TagNameValidationScope(this);
    }

    /// <summary>
    /// Bypasses row-level read filters for a narrowly scoped internal data-maintenance operation.
    /// The caller remains responsible for API authorization. This is required for merges because
    /// references belonging to other users or hidden content must be transferred before a source
    /// entity is deleted.
    /// </summary>
    internal IDisposable SuppressAuthorizationFilters()
    {
        _authorizationFilterSuppressionDepth++;
        return new AuthorizationFilterScope(this);
    }

    /// <summary>
    /// Temporarily bypasses only the embedding read-permission filter for an internal feature that
    /// separately authorizes the visible host entities it returns. Face and other entity visibility
    /// filters remain active while this scope is held.
    /// </summary>
    internal IDisposable SuppressEmbeddingReadAuthorizationFilter()
    {
        _embeddingReadAuthorizationFilterSuppressionDepth++;
        return new EmbeddingReadAuthorizationFilterScope(this);
    }

    private void NormalizeAndValidateTagNames()
    {
        var candidates = NormalizeChangedTagNames(normalizeValues: _tagNameValidationSuppressionDepth == 0);
        if (_tagNameValidationSuppressionDepth > 0 || candidates.Count == 0)
            return;

        ValidateTrackedTagNames(candidates);
        var excludedTagIds = CandidateAndDeletedTagIds(candidates);
        var excludedAliasIds = CandidateAndDeletedAliasIds(candidates);
        var deletedTagIds = DeletedIds<Tag>();
        var existingTags = Tags.IgnoreQueryFilters().AsNoTracking()
            .Where(tag => !excludedTagIds.Contains(tag.Id))
            .Select(tag => new { tag.Id, tag.Name })
            .ToList();
        var existingAliases = Set<TagAlias>().IgnoreQueryFilters().AsNoTracking()
            .Where(alias => !excludedAliasIds.Contains(alias.Id) && !deletedTagIds.Contains(alias.TagId))
            .Select(alias => new { alias.Id, alias.TagId, alias.Alias })
            .ToList();

        ThrowForPersistedConflicts(candidates,
            existingTags.Select(tag => new TagNameConflictTarget(
                TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(tag.Name)),
                tag.Name,
                false,
                tag.Id))
            .Concat(existingAliases
                .Select(alias => new { Alias = alias, Normalized = TagNameRules.NormalizeAlias(alias.Alias) })
                .Where(row => row.Normalized != null)
                .Select(row => new TagNameConflictTarget(
                    TagNameRules.NamespaceKey(row.Normalized!),
                    row.Alias.Alias,
                    true,
                    row.Alias.TagId))));
    }

    private async Task NormalizeAndValidateTagNamesAsync(CancellationToken cancellationToken)
    {
        var candidates = NormalizeChangedTagNames(normalizeValues: _tagNameValidationSuppressionDepth == 0);
        if (_tagNameValidationSuppressionDepth > 0 || candidates.Count == 0)
            return;

        ValidateTrackedTagNames(candidates);
        var excludedTagIds = CandidateAndDeletedTagIds(candidates);
        var excludedAliasIds = CandidateAndDeletedAliasIds(candidates);
        var deletedTagIds = DeletedIds<Tag>();
        var existingTags = await Tags.IgnoreQueryFilters().AsNoTracking()
            .Where(tag => !excludedTagIds.Contains(tag.Id))
            .Select(tag => new { tag.Id, tag.Name })
            .ToListAsync(cancellationToken);
        var existingAliases = await Set<TagAlias>().IgnoreQueryFilters().AsNoTracking()
            .Where(alias => !excludedAliasIds.Contains(alias.Id) && !deletedTagIds.Contains(alias.TagId))
            .Select(alias => new { alias.Id, alias.TagId, alias.Alias })
            .ToListAsync(cancellationToken);

        ThrowForPersistedConflicts(candidates,
            existingTags.Select(tag => new TagNameConflictTarget(
                TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(tag.Name)),
                tag.Name,
                false,
                tag.Id))
            .Concat(existingAliases
                .Select(alias => new { Alias = alias, Normalized = TagNameRules.NormalizeAlias(alias.Alias) })
                .Where(row => row.Normalized != null)
                .Select(row => new TagNameConflictTarget(
                    TagNameRules.NamespaceKey(row.Normalized!),
                    row.Alias.Alias,
                    true,
                    row.Alias.TagId))));
    }

    private List<TagNameCandidate> NormalizeChangedTagNames(bool normalizeValues)
    {
        var candidates = new List<TagNameCandidate>();
        foreach (var entry in ChangeTracker.Entries<Tag>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified))
        {
            var originalNamespaceKey = entry.State == EntityState.Modified
                ? TagNameRules.NamespaceKey(TagNameRules.NormalizeCanonicalName(
                    entry.Property(tag => tag.Name).OriginalValue))
                : null;
            var normalized = TagNameRules.NormalizeCanonicalName(entry.Entity.Name);
            if (normalizeValues)
                entry.Entity.Name = normalized;
            entry.Entity.NamespaceKey = TagNameRules.NamespaceKey(normalized);
            if (string.Equals(entry.Entity.NamespaceKey, originalNamespaceKey, StringComparison.Ordinal))
                continue;
            candidates.Add(new TagNameCandidate(
                entry.Entity.NamespaceKey,
                normalized,
                false,
                entry.Entity.Id > 0 ? entry.Entity.Id : null,
                null));
        }

        foreach (var entry in ChangeTracker.Entries<TagAlias>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified)
            .ToArray())
        {
            var originalNormalized = entry.State == EntityState.Modified
                ? TagNameRules.NormalizeAlias(entry.Property(alias => alias.Alias).OriginalValue)
                : null;
            var originalNamespaceKey = originalNormalized == null
                ? null
                : TagNameRules.NamespaceKey(originalNormalized);
            var normalized = TagNameRules.NormalizeAlias(entry.Entity.Alias);
            if (normalized == null)
            {
                if (normalizeValues)
                    entry.State = entry.State == EntityState.Added ? EntityState.Detached : EntityState.Deleted;
                else
                    entry.Entity.NamespaceKey = string.Empty;
                continue;
            }

            if (normalizeValues)
                entry.Entity.Alias = normalized;
            entry.Entity.NamespaceKey = TagNameRules.NamespaceKey(normalized);
            if (string.Equals(entry.Entity.NamespaceKey, originalNamespaceKey, StringComparison.Ordinal))
                continue;
            var owningTag = entry.Entity.Tag
                ?? ChangeTracker.Entries<Tag>().FirstOrDefault(tag => tag.Entity.Id == entry.Entity.TagId)?.Entity;
            candidates.Add(new TagNameCandidate(
                entry.Entity.NamespaceKey,
                normalized,
                true,
                entry.Entity.TagId > 0 ? entry.Entity.TagId : owningTag?.Id > 0 ? owningTag.Id : null,
                entry.Entity.Id > 0 ? entry.Entity.Id : null));
        }

        return candidates;
    }

    private static void ValidateTrackedTagNames(IReadOnlyList<TagNameCandidate> candidates)
    {
        var duplicate = candidates
            .GroupBy(candidate => candidate.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate == null)
            return;

        var ordered = duplicate.OrderBy(candidate => candidate.IsAlias).ToArray();
        ThrowTagNameConflict(ordered[0], ordered[1].DisplayName);
    }

    private static void ThrowForPersistedConflicts(
        IReadOnlyList<TagNameCandidate> candidates,
        IEnumerable<TagNameConflictTarget> existingNames)
    {
        var targets = existingNames
            .GroupBy(target => target.Key, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(target => target.IsAlias).ThenBy(target => target.TagId).First(), StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (targets.TryGetValue(candidate.Key, out var existing))
                ThrowTagNameConflict(existing, candidate.DisplayName);
        }
    }

    private static void ThrowTagNameConflict(TagNameConflictTarget conflict, string conflictingName)
        => throw (conflict.IsAlias
            ? TagNameConflictException.ForExistingAlias(conflict.DisplayName, conflictingName)
            : TagNameConflictException.ForExistingTagName(conflict.DisplayName, conflictingName));

    private int[] CandidateAndDeletedTagIds(IReadOnlyCollection<TagNameCandidate> candidates)
        => candidates.Where(candidate => !candidate.IsAlias && candidate.TagId != null)
            .Select(candidate => candidate.TagId!.Value)
            .Concat(DeletedIds<Tag>())
            .Distinct()
            .ToArray();

    private int[] CandidateAndDeletedAliasIds(IReadOnlyCollection<TagNameCandidate> candidates)
        => candidates.Where(candidate => candidate.IsAlias && candidate.AliasId != null)
            .Select(candidate => candidate.AliasId!.Value)
            .Concat(DeletedIds<TagAlias>())
            .Distinct()
            .ToArray();

    private int[] DeletedIds<TEntity>() where TEntity : class
        => ChangeTracker.Entries<TEntity>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Property<int>("Id").CurrentValue)
            .Where(id => id > 0)
            .ToArray();

    private record TagNameConflictTarget(
        string Key,
        string DisplayName,
        bool IsAlias,
        int? TagId);

    private sealed record TagNameCandidate(
        string Key,
        string DisplayName,
        bool IsAlias,
        int? TagId,
        int? AliasId)
        : TagNameConflictTarget(Key, DisplayName, IsAlias, TagId);

    private sealed class TagNameValidationScope(CoveContext context) : IDisposable
    {
        private CoveContext? _context = context;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _context, null);
            if (owner != null)
                owner._tagNameValidationSuppressionDepth--;
        }
    }

    private sealed class AuthorizationFilterScope(CoveContext context) : IDisposable
    {
        private CoveContext? _context = context;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _context, null);
            if (owner != null)
                owner._authorizationFilterSuppressionDepth--;
        }
    }

    private sealed class EmbeddingReadAuthorizationFilterScope(CoveContext context) : IDisposable
    {
        private CoveContext? _context = context;

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _context, null);
            if (owner != null)
                owner._embeddingReadAuthorizationFilterSuppressionDepth--;
        }
    }

    private DerivedArrayTargets CollectDerivedArrayTargets() =>
        new(
            CollectChangedParentIds<VideoTag>(link => link.VideoId),
            CollectChangedParentIds<VideoPerformer>(link => link.VideoId),
            CollectChangedParentIds<ImageTag>(link => link.ImageId),
            CollectChangedParentIds<ImagePerformer>(link => link.ImageId),
            CollectChangedParentIds<GalleryTag>(link => link.GalleryId),
            CollectChangedParentIds<GalleryPerformer>(link => link.GalleryId));

    private static int[] AcquireLocalDerivedArrayWriteLocks(DerivedArrayTargets targets)
    {
        var indexes = GetDerivedArrayWriteStripeIndexes(targets);
        foreach (var index in indexes)
            DerivedArrayWriteStripes[index].Wait();
        return indexes;
    }

    private static async Task<int[]> AcquireLocalDerivedArrayWriteLocksAsync(
        DerivedArrayTargets targets,
        CancellationToken cancellationToken)
    {
        var indexes = GetDerivedArrayWriteStripeIndexes(targets);
        var acquiredCount = 0;
        try
        {
            foreach (var index in indexes)
            {
                await DerivedArrayWriteStripes[index].WaitAsync(cancellationToken);
                acquiredCount++;
            }
            return indexes;
        }
        catch
        {
            for (var index = acquiredCount - 1; index >= 0; index--)
                DerivedArrayWriteStripes[indexes[index]].Release();
            throw;
        }
    }

    private static void ReleaseLocalDerivedArrayWriteLocks(int[] indexes)
    {
        for (var index = indexes.Length - 1; index >= 0; index--)
            DerivedArrayWriteStripes[indexes[index]].Release();
    }

    private bool HasPendingTagNamespaceWrite() =>
        ChangeTracker.Entries<Tag>().Any(entry => entry.State is EntityState.Added or EntityState.Deleted
            || entry.State == EntityState.Modified && entry.Property(tag => tag.Name).IsModified)
        || ChangeTracker.Entries<TagAlias>().Any(entry => entry.State is EntityState.Added or EntityState.Deleted
            || entry.State == EntityState.Modified && entry.Property(alias => alias.Alias).IsModified);

    private TagNamespaceLock AcquireTagNamespaceWriteLock()
    {
        if (!HasPendingTagNamespaceWrite())
            return default;

        if (!UsesDatabaseDerivedArrayWriteLocks())
        {
            TagNamespaceWriteLock.Wait();
            return new TagNamespaceLock(Acquired: true, Local: true, TransactionScoped: false, OpenedConnection: false);
        }

        var openedConnection = Database.GetDbConnection().State != ConnectionState.Open;
        if (openedConnection)
            Database.OpenConnection();
        var transactionScoped = Database.CurrentTransaction != null;
        try
        {
            if (transactionScoped)
                Database.ExecuteSqlInterpolated($"SELECT pg_advisory_xact_lock({TagNamespaceAdvisoryLockKey})");
            else
                Database.ExecuteSqlInterpolated($"SELECT pg_advisory_lock({TagNamespaceAdvisoryLockKey})");
            return new TagNamespaceLock(Acquired: true, Local: false, transactionScoped, openedConnection);
        }
        catch
        {
            if (openedConnection)
                Database.CloseConnection();
            throw;
        }
    }

    private async Task<TagNamespaceLock> AcquireTagNamespaceWriteLockAsync(CancellationToken cancellationToken)
    {
        if (!HasPendingTagNamespaceWrite())
            return default;

        if (!UsesDatabaseDerivedArrayWriteLocks())
        {
            await TagNamespaceWriteLock.WaitAsync(cancellationToken);
            return new TagNamespaceLock(Acquired: true, Local: true, TransactionScoped: false, OpenedConnection: false);
        }

        var openedConnection = Database.GetDbConnection().State != ConnectionState.Open;
        if (openedConnection)
            await Database.OpenConnectionAsync(cancellationToken);
        var transactionScoped = Database.CurrentTransaction != null;
        try
        {
            if (transactionScoped)
                await Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_xact_lock({TagNamespaceAdvisoryLockKey})",
                    cancellationToken);
            else
                await Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_lock({TagNamespaceAdvisoryLockKey})",
                    cancellationToken);
            return new TagNamespaceLock(Acquired: true, Local: false, transactionScoped, openedConnection);
        }
        catch
        {
            if (openedConnection)
                await Database.CloseConnectionAsync();
            throw;
        }
    }

    private void ReleaseTagNamespaceWriteLock(TagNamespaceLock tagNamespaceLock)
    {
        try
        {
            if (!tagNamespaceLock.Local && !tagNamespaceLock.TransactionScoped && tagNamespaceLock.Acquired)
                Database.ExecuteSqlInterpolated($"SELECT pg_advisory_unlock({TagNamespaceAdvisoryLockKey})");
        }
        finally
        {
            if (tagNamespaceLock.OpenedConnection)
                Database.CloseConnection();
            if (tagNamespaceLock.Local)
                TagNamespaceWriteLock.Release();
        }
    }

    private async Task ReleaseTagNamespaceWriteLockAsync(TagNamespaceLock tagNamespaceLock)
    {
        try
        {
            if (!tagNamespaceLock.Local && !tagNamespaceLock.TransactionScoped && tagNamespaceLock.Acquired)
                await Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT pg_advisory_unlock({TagNamespaceAdvisoryLockKey})",
                    CancellationToken.None);
        }
        finally
        {
            if (tagNamespaceLock.OpenedConnection)
                await Database.CloseConnectionAsync();
            if (tagNamespaceLock.Local)
                TagNamespaceWriteLock.Release();
        }
    }

    private static int[] GetDerivedArrayWriteStripeIndexes(DerivedArrayTargets targets) =>
        targets.GetLockKeys()
            .Select(key => (int)((uint)HashCode.Combine(key.Namespace, key.ParentId) % DerivedArrayWriteStripes.Length))
            .Distinct()
            .Order()
            .ToArray();

    private DatabaseDerivedArrayWriteLock AcquireDatabaseDerivedArrayWriteLocks(DerivedArrayTargets targets)
    {
        var keys = targets.GetLockKeys();
        if (keys.Length == 0 || !UsesDatabaseDerivedArrayWriteLocks())
            return default;

        var openedConnection = Database.GetDbConnection().State != ConnectionState.Open;
        if (openedConnection)
            Database.OpenConnection();

        var transactionScoped = Database.CurrentTransaction != null;
        var acquiredCount = 0;
        try
        {
            foreach (var key in keys)
            {
                if (transactionScoped)
                    Database.ExecuteSqlInterpolated($"SELECT pg_advisory_xact_lock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})");
                else
                    Database.ExecuteSqlInterpolated($"SELECT pg_advisory_lock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})");
                acquiredCount++;
            }
            return new DatabaseDerivedArrayWriteLock(keys, transactionScoped, openedConnection);
        }
        catch
        {
            try
            {
                if (!transactionScoped)
                {
                    for (var index = acquiredCount - 1; index >= 0; index--)
                    {
                        var key = keys[index];
                        Database.ExecuteSqlInterpolated($"SELECT pg_advisory_unlock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})");
                    }
                }
            }
            finally
            {
                if (openedConnection)
                    Database.CloseConnection();
            }
            throw;
        }
    }

    private async Task<DatabaseDerivedArrayWriteLock> AcquireDatabaseDerivedArrayWriteLocksAsync(
        DerivedArrayTargets targets,
        CancellationToken cancellationToken)
    {
        var keys = targets.GetLockKeys();
        if (keys.Length == 0 || !UsesDatabaseDerivedArrayWriteLocks())
            return default;

        var openedConnection = Database.GetDbConnection().State != ConnectionState.Open;
        if (openedConnection)
            await Database.OpenConnectionAsync(cancellationToken);

        var transactionScoped = Database.CurrentTransaction != null;
        var acquiredCount = 0;
        try
        {
            foreach (var key in keys)
            {
                if (transactionScoped)
                    await Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_xact_lock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})",
                        cancellationToken);
                else
                    await Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_lock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})",
                        cancellationToken);
                acquiredCount++;
            }
            return new DatabaseDerivedArrayWriteLock(keys, transactionScoped, openedConnection);
        }
        catch
        {
            try
            {
                if (!transactionScoped)
                {
                    for (var index = acquiredCount - 1; index >= 0; index--)
                    {
                        var key = keys[index];
                        await Database.ExecuteSqlInterpolatedAsync(
                            $"SELECT pg_advisory_unlock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})",
                            CancellationToken.None);
                    }
                }
            }
            finally
            {
                if (openedConnection)
                    await Database.CloseConnectionAsync();
            }
            throw;
        }
    }

    private bool UsesDatabaseDerivedArrayWriteLocks() =>
        Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

    private void ReleaseDatabaseDerivedArrayWriteLocks(DatabaseDerivedArrayWriteLock databaseLock)
    {
        var keys = databaseLock.Keys ?? [];
        try
        {
            if (!databaseLock.TransactionScoped)
            {
                for (var index = keys.Length - 1; index >= 0; index--)
                {
                    var key = keys[index];
                    Database.ExecuteSqlInterpolated($"SELECT pg_advisory_unlock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})");
                }
            }
        }
        finally
        {
            if (databaseLock.OpenedConnection)
                Database.CloseConnection();
        }
    }

    private async Task ReleaseDatabaseDerivedArrayWriteLocksAsync(DatabaseDerivedArrayWriteLock databaseLock)
    {
        var keys = databaseLock.Keys ?? [];
        try
        {
            if (!databaseLock.TransactionScoped)
            {
                for (var index = keys.Length - 1; index >= 0; index--)
                {
                    var key = keys[index];
                    await Database.ExecuteSqlInterpolatedAsync(
                        $"SELECT pg_advisory_unlock({DerivedArrayAdvisoryLockNamespace + key.Namespace}, {key.ParentId})",
                        CancellationToken.None);
                }
            }
        }
        finally
        {
            if (databaseLock.OpenedConnection)
                await Database.CloseConnectionAsync();
        }
    }

    private readonly record struct DerivedArrayLockKey(int Namespace, int ParentId);

    private readonly record struct DatabaseDerivedArrayWriteLock(
        DerivedArrayLockKey[] Keys,
        bool TransactionScoped,
        bool OpenedConnection);

    private readonly record struct TagNamespaceLock(
        bool Acquired,
        bool Local,
        bool TransactionScoped,
        bool OpenedConnection);

    private readonly record struct DerivedArrayTargets(
        HashSet<int> VideoTagParents,
        HashSet<int> VideoPerformerParents,
        HashSet<int> ImageTagParents,
        HashSet<int> ImagePerformerParents,
        HashSet<int> GalleryTagParents,
        HashSet<int> GalleryPerformerParents)
    {
        public DerivedArrayLockKey[] GetLockKeys()
        {
            var keys = new List<DerivedArrayLockKey>(
                VideoTagParents.Count
                + VideoPerformerParents.Count
                + ImageTagParents.Count
                + ImagePerformerParents.Count
                + GalleryTagParents.Count
                + GalleryPerformerParents.Count);
            Add(keys, 1, VideoTagParents.Concat(VideoPerformerParents).Distinct());
            Add(keys, 2, ImageTagParents.Concat(ImagePerformerParents).Distinct());
            Add(keys, 3, GalleryTagParents.Concat(GalleryPerformerParents).Distinct());
            return keys.OrderBy(key => key.Namespace).ThenBy(key => key.ParentId).ToArray();
        }

        private static void Add(ICollection<DerivedArrayLockKey> keys, int lockNamespace, IEnumerable<int> parentIds)
        {
            foreach (var parentId in parentIds)
                keys.Add(new DerivedArrayLockKey(lockNamespace, parentId));
        }
    }

    private void ProtectDenormalizedIdArraysFromDirectWrites()
    {
        var changedVideoTagIds = CollectChangedParentIds<VideoTag>(link => link.VideoId);
        var changedVideoPerformerIds = CollectChangedParentIds<VideoPerformer>(link => link.VideoId);
        var changedImageTagIds = CollectChangedParentIds<ImageTag>(link => link.ImageId);
        var changedImagePerformerIds = CollectChangedParentIds<ImagePerformer>(link => link.ImageId);
        var changedGalleryTagIds = CollectChangedParentIds<GalleryTag>(link => link.GalleryId);
        var changedGalleryPerformerIds = CollectChangedParentIds<GalleryPerformer>(link => link.GalleryId);

        ProtectDerivedArray(ChangeTracker.Entries<Video>(), video => video.TagIds, changedVideoTagIds);
        ProtectDerivedArray(ChangeTracker.Entries<Video>(), video => video.PerformerIds, changedVideoPerformerIds);
        ProtectDerivedArray(ChangeTracker.Entries<Image>(), image => image.TagIds, changedImageTagIds);
        ProtectDerivedArray(ChangeTracker.Entries<Image>(), image => image.PerformerIds, changedImagePerformerIds);
        ProtectDerivedArray(ChangeTracker.Entries<Gallery>(), gallery => gallery.TagIds, changedGalleryTagIds);
        ProtectDerivedArray(ChangeTracker.Entries<Gallery>(), gallery => gallery.PerformerIds, changedGalleryPerformerIds);
    }

    private static void ProtectDerivedArray<TEntity>(
        IEnumerable<EntityEntry<TEntity>> entries,
        Expression<Func<TEntity, int[]>> property,
        IReadOnlySet<int> parentsWithChangedLinks)
        where TEntity : BaseEntity
    {
        foreach (var entry in entries)
        {
            if (entry.State != EntityState.Modified
                || entry.Entity.Id <= 0
                || parentsWithChangedLinks.Contains(entry.Entity.Id))
            {
                continue;
            }

            entry.Property(property).IsModified = false;
        }
    }

    private void CleanupEngagementRowsForDeletedEntities()
    {
        foreach (var target in CollectDeletedEngagementTargets())
        {
            UserEntityAffinities.RemoveRange(UserEntityAffinities.IgnoreQueryFilters().Where(row => row.HostType == target.AffinityHostType && row.HostId == target.HostId).ToList());
            UserBookmarks.RemoveRange(UserBookmarks.IgnoreQueryFilters().Where(row => row.HostType == target.AffinityHostType && row.HostId == target.HostId).ToList());
            Interactions.RemoveRange(Interactions.IgnoreQueryFilters().Where(row => row.HostType == target.InteractionHostType && row.HostId == target.HostId).ToList());
            PlaybackSessions.RemoveRange(PlaybackSessions.IgnoreQueryFilters().Where(row => row.HostType == target.InteractionHostType && row.HostId == target.HostId).ToList());
            Ratings.RemoveRange(Ratings.IgnoreQueryFilters().Where(row => row.HostType == target.RatingHostType && row.HostId == target.HostId).ToList());
        }
    }

    private async Task CleanupEngagementRowsForDeletedEntitiesAsync(CancellationToken cancellationToken)
    {
        foreach (var target in CollectDeletedEngagementTargets())
        {
            UserEntityAffinities.RemoveRange(await UserEntityAffinities.IgnoreQueryFilters().Where(row => row.HostType == target.AffinityHostType && row.HostId == target.HostId).ToListAsync(cancellationToken));
            UserBookmarks.RemoveRange(await UserBookmarks.IgnoreQueryFilters().Where(row => row.HostType == target.AffinityHostType && row.HostId == target.HostId).ToListAsync(cancellationToken));
            Interactions.RemoveRange(await Interactions.IgnoreQueryFilters().Where(row => row.HostType == target.InteractionHostType && row.HostId == target.HostId).ToListAsync(cancellationToken));
            PlaybackSessions.RemoveRange(await PlaybackSessions.IgnoreQueryFilters().Where(row => row.HostType == target.InteractionHostType && row.HostId == target.HostId).ToListAsync(cancellationToken));
            Ratings.RemoveRange(await Ratings.IgnoreQueryFilters().Where(row => row.HostType == target.RatingHostType && row.HostId == target.HostId).ToListAsync(cancellationToken));
        }
    }

    private IReadOnlyList<EngagementCleanupTarget> CollectDeletedEngagementTargets()
    {
        var targets = new List<EngagementCleanupTarget>();
        AddDeletedTargets(targets, ChangeTracker.Entries<Video>(), entry => entry.Entity.Id, AffinityHostType.Video, InteractionHostType.Video, RatingHostType.Video);
        AddDeletedTargets(targets, ChangeTracker.Entries<Image>(), entry => entry.Entity.Id, AffinityHostType.Image, InteractionHostType.Image, RatingHostType.Image);
        AddDeletedTargets(targets, ChangeTracker.Entries<Audio>(), entry => entry.Entity.Id, AffinityHostType.Audio, InteractionHostType.Audio, RatingHostType.Audio);
        AddDeletedTargets(targets, ChangeTracker.Entries<TextDocument>(), entry => entry.Entity.Id, AffinityHostType.Text, InteractionHostType.Text, RatingHostType.Text);
        AddDeletedTargets(targets, ChangeTracker.Entries<Segment>(), entry => entry.Entity.Id, AffinityHostType.Segment, InteractionHostType.Segment, RatingHostType.Segment);
        AddDeletedTargets(targets, ChangeTracker.Entries<Performer>(), entry => entry.Entity.Id, AffinityHostType.Performer, InteractionHostType.Performer, RatingHostType.Performer);
        AddDeletedTargets(targets, ChangeTracker.Entries<Face>(), entry => entry.Entity.Id, AffinityHostType.Face, InteractionHostType.Face, RatingHostType.Face);
        AddDeletedTargets(targets, ChangeTracker.Entries<Tag>(), entry => entry.Entity.Id, AffinityHostType.Tag, InteractionHostType.Tag, RatingHostType.Tag);
        AddDeletedTargets(targets, ChangeTracker.Entries<Studio>(), entry => entry.Entity.Id, AffinityHostType.Studio, InteractionHostType.Studio, RatingHostType.Studio);
        AddDeletedTargets(targets, ChangeTracker.Entries<Gallery>(), entry => entry.Entity.Id, AffinityHostType.Gallery, InteractionHostType.Gallery, RatingHostType.Gallery);
        AddDeletedTargets(targets, ChangeTracker.Entries<Group>(), entry => entry.Entity.Id, AffinityHostType.Group, InteractionHostType.Group, RatingHostType.Group);
        return targets
            .GroupBy(target => (target.AffinityHostType, target.HostId))
            .Select(group => group.First())
            .ToList();
    }

    private static void AddDeletedTargets<TEntity>(
        ICollection<EngagementCleanupTarget> targets,
        IEnumerable<EntityEntry<TEntity>> entries,
        Func<EntityEntry<TEntity>, int> getId,
        AffinityHostType affinityHostType,
        InteractionHostType interactionHostType,
        RatingHostType ratingHostType)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            var id = getId(entry);
            if (entry.State == EntityState.Deleted && id > 0)
                targets.Add(new EngagementCleanupTarget(affinityHostType, interactionHostType, ratingHostType, id));
        }
    }

    private sealed record EngagementCleanupTarget(
        AffinityHostType AffinityHostType,
        InteractionHostType InteractionHostType,
        RatingHostType RatingHostType,
        int HostId);

    private async Task<int> SaveChangesWithDerivedCountsAsync(DerivedCountTargets derivedCountTargets, PostSaveDerivedCountTargets postSaveDerivedCountTargets, CancellationToken cancellationToken)
    {
        var result = await SaveChangesWithNameConstraintTranslationAsync(cancellationToken);
        AddPostSaveDerivedCountTargets(derivedCountTargets, postSaveDerivedCountTargets);
        MaintainPostSaveDenormalizedIdArrays(derivedCountTargets, postSaveDerivedCountTargets);
        await PersistDerivedCountsAsync(derivedCountTargets, cancellationToken);
        return result;
    }

    private int SaveChangesWithNameConstraintTranslation()
    {
        try
        {
            return base.SaveChanges();
        }
        catch (DbUpdateException exception)
        {
            if (IsTagNameConstraint(exception))
                throw TagNameConflictException.ForConcurrentWrite();
            var entityType = EntityNameConstraintType(exception);
            if (entityType != null)
                throw new EntityNameConflictException(entityType, exception);
            throw;
        }
    }

    private async Task<int> SaveChangesWithNameConstraintTranslationAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            if (IsTagNameConstraint(exception))
                throw TagNameConflictException.ForConcurrentWrite();
            var entityType = EntityNameConstraintType(exception);
            if (entityType != null)
                throw new EntityNameConflictException(entityType, exception);
            throw;
        }
    }

    private static string? EntityNameConstraintType(DbUpdateException exception)
    {
        if (exception.InnerException is not PostgresException postgres
            || postgres.SqlState is not PostgresErrorCodes.ExclusionViolation and not PostgresErrorCodes.UniqueViolation)
        {
            return null;
        }

        return postgres.ConstraintName switch
        {
            "UQ_performers_identity" => NameConflictEntityTypes.Performer,
            "UQ_studios_name" => NameConflictEntityTypes.Studio,
            _ => null,
        };
    }

    private static bool IsTagNameConstraint(DbUpdateException exception)
        => exception.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.ExclusionViolation,
            ConstraintName: "UQ_tag_name_claims_namespace",
        };

    private void MaintainDenormalizedIdArrays(DerivedArrayTargets targets)
    {
        // Refresh GIN-indexed Video/Image/Gallery TagIds/PerformerIds arrays whenever
        // the corresponding join tables change. The arrays let combo filters like
        // "videos with tags A AND B AND performer C" run as a single index-only
        // array-containment scan instead of N joins per filter term.
        //
        // Strategy: collect parent ids whose link rows changed in this unit of work,
        // then for each parent rebuild the array from the join table in one query per
        // (parent type, link type). This is O(changed parents) round-trips, not O(rows).

        InitializeAddedParentIdArrays();

        // Also handle Added Video/Image/Gallery rows whose join collections were set
        // through the navigation property: in that case the link entries are Added too
        // and will already be picked up above. But a freshly-Added parent with no links
        // still needs its arrays initialized to an empty array (the default), so nothing
        // extra is needed here.

        if (targets.VideoTagParents.Count > 0)
            RebuildArray<Video, VideoTag>(targets.VideoTagParents, s => s.TagIds, e => e.VideoId, e => e.TagId);
        if (targets.VideoPerformerParents.Count > 0)
            RebuildArray<Video, VideoPerformer>(targets.VideoPerformerParents, s => s.PerformerIds, e => e.VideoId, e => e.PerformerId);
        if (targets.ImageTagParents.Count > 0)
            RebuildArray<Image, ImageTag>(targets.ImageTagParents, i => i.TagIds, e => e.ImageId, e => e.TagId);
        if (targets.ImagePerformerParents.Count > 0)
            RebuildArray<Image, ImagePerformer>(targets.ImagePerformerParents, i => i.PerformerIds, e => e.ImageId, e => e.PerformerId);
        if (targets.GalleryTagParents.Count > 0)
            RebuildArray<Gallery, GalleryTag>(targets.GalleryTagParents, g => g.TagIds, e => e.GalleryId, e => e.TagId);
        if (targets.GalleryPerformerParents.Count > 0)
            RebuildArray<Gallery, GalleryPerformer>(targets.GalleryPerformerParents, g => g.PerformerIds, e => e.GalleryId, e => e.PerformerId);
    }

    private readonly record struct DerivedCountTargets(
        HashSet<int> TagIds,
        HashSet<int> StudioIds,
        HashSet<int> PerformerIds,
        HashSet<int> GalleryIds,
        HashSet<int> VideoIds,
        HashSet<int> ImageIds)
    {
        public bool HasAny => TagIds.Count > 0
            || StudioIds.Count > 0
            || PerformerIds.Count > 0
            || GalleryIds.Count > 0
            || VideoIds.Count > 0
            || ImageIds.Count > 0;
    }

    private readonly record struct PostSaveDerivedCountTargets(
        IReadOnlyList<VideoFile> VideoFilesWithDeferredVideoIds,
        IReadOnlyList<ImageFile> ImageFilesWithDeferredImageIds,
        IReadOnlyList<VideoTag> VideoTagsWithDeferredIds,
        IReadOnlyList<VideoPerformer> VideoPerformersWithDeferredIds,
        IReadOnlyList<ImageTag> ImageTagsWithDeferredIds,
        IReadOnlyList<ImagePerformer> ImagePerformersWithDeferredIds,
        IReadOnlyList<GalleryTag> GalleryTagsWithDeferredIds,
        IReadOnlyList<GalleryPerformer> GalleryPerformersWithDeferredIds,
        IReadOnlyList<Studio> StudiosWithDeferredIds);

    private PostSaveDerivedCountTargets CollectPostSaveDerivedCountTargets()
    {
        var videoFiles = ChangeTracker.Entries<VideoFile>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.VideoId is null or <= 0)
            .Select(entry => entry.Entity)
            .ToList();

        var imageFiles = ChangeTracker.Entries<ImageFile>()
            .Where(entry => entry.State == EntityState.Added && entry.Entity.ImageId is null or <= 0)
            .Select(entry => entry.Entity)
            .ToList();

        return new PostSaveDerivedCountTargets(
            videoFiles,
            imageFiles,
            CollectAddedLinksWithDeferredIds<VideoTag>(link => link.VideoId, link => link.TagId),
            CollectAddedLinksWithDeferredIds<VideoPerformer>(link => link.VideoId, link => link.PerformerId),
            CollectAddedLinksWithDeferredIds<ImageTag>(link => link.ImageId, link => link.TagId),
            CollectAddedLinksWithDeferredIds<ImagePerformer>(link => link.ImageId, link => link.PerformerId),
            CollectAddedLinksWithDeferredIds<GalleryTag>(link => link.GalleryId, link => link.TagId),
            CollectAddedLinksWithDeferredIds<GalleryPerformer>(link => link.GalleryId, link => link.PerformerId),
            ChangeTracker.Entries<Studio>()
                .Where(entry => entry.State == EntityState.Added && entry.Entity.Id <= 0)
                .Select(entry => entry.Entity)
                .ToList());
    }

    private IReadOnlyList<TLink> CollectAddedLinksWithDeferredIds<TLink>(Func<TLink, int> parentId, Func<TLink, int> childId)
        where TLink : class =>
        ChangeTracker.Entries<TLink>()
            .Where(entry => entry.State == EntityState.Added
                && (parentId(entry.Entity) <= 0 || childId(entry.Entity) <= 0))
            .Select(entry => entry.Entity)
            .ToList();

    private static void AddPostSaveDerivedCountTargets(DerivedCountTargets targets, PostSaveDerivedCountTargets postSaveTargets)
    {
        foreach (var videoFile in postSaveTargets.VideoFilesWithDeferredVideoIds)
        {
            AddIfPositive(targets.VideoIds, videoFile.VideoId);
            AddIfPositive(targets.VideoIds, videoFile.Video?.Id);
        }

        foreach (var imageFile in postSaveTargets.ImageFilesWithDeferredImageIds)
        {
            AddIfPositive(targets.ImageIds, imageFile.ImageId);
            AddIfPositive(targets.ImageIds, imageFile.Image?.Id);
        }

        foreach (var studio in postSaveTargets.StudiosWithDeferredIds)
            AddIfPositive(targets.StudioIds, studio.Id);
    }

    private void MaintainPostSaveDenormalizedIdArrays(DerivedCountTargets targets, PostSaveDerivedCountTargets postSaveTargets)
    {
        RebuildPostSaveArray<Video, VideoTag>(postSaveTargets.VideoTagsWithDeferredIds, link => link.VideoId, video => video.TagIds, link => link.TagId);
        RebuildPostSaveArray<Video, VideoPerformer>(postSaveTargets.VideoPerformersWithDeferredIds, link => link.VideoId, video => video.PerformerIds, link => link.PerformerId);
        RebuildPostSaveArray<Image, ImageTag>(postSaveTargets.ImageTagsWithDeferredIds, link => link.ImageId, image => image.TagIds, link => link.TagId);
        RebuildPostSaveArray<Image, ImagePerformer>(postSaveTargets.ImagePerformersWithDeferredIds, link => link.ImageId, image => image.PerformerIds, link => link.PerformerId);
        RebuildPostSaveArray<Gallery, GalleryTag>(postSaveTargets.GalleryTagsWithDeferredIds, link => link.GalleryId, gallery => gallery.TagIds, link => link.TagId);
        RebuildPostSaveArray<Gallery, GalleryPerformer>(postSaveTargets.GalleryPerformersWithDeferredIds, link => link.GalleryId, gallery => gallery.PerformerIds, link => link.PerformerId);

        AddPostSaveChildIds(targets.TagIds, postSaveTargets.VideoTagsWithDeferredIds, link => link.TagId);
        AddPostSaveChildIds(targets.PerformerIds, postSaveTargets.VideoPerformersWithDeferredIds, link => link.PerformerId);
        AddPostSaveChildIds(targets.TagIds, postSaveTargets.ImageTagsWithDeferredIds, link => link.TagId);
        AddPostSaveChildIds(targets.PerformerIds, postSaveTargets.ImagePerformersWithDeferredIds, link => link.PerformerId);
        AddPostSaveChildIds(targets.TagIds, postSaveTargets.GalleryTagsWithDeferredIds, link => link.TagId);
        AddPostSaveChildIds(targets.PerformerIds, postSaveTargets.GalleryPerformersWithDeferredIds, link => link.PerformerId);
    }

    private void RebuildPostSaveArray<TParent, TLink>(
        IReadOnlyList<TLink> links,
        Expression<Func<TLink, int>> parentId,
        Expression<Func<TParent, int[]>> arrayProperty,
        Expression<Func<TLink, int>> childId)
        where TParent : class
        where TLink : class
    {
        var parentIdSelector = parentId.Compile();
        var parentIds = links.Select(parentIdSelector).Where(id => id > 0).ToHashSet();
        if (parentIds.Count > 0)
            RebuildArray<TParent, TLink>(parentIds, arrayProperty, parentId, childId);
    }

    private static void AddPostSaveChildIds<TLink>(HashSet<int> ids, IEnumerable<TLink> links, Func<TLink, int> childId)
    {
        foreach (var link in links)
            AddIfPositive(ids, childId(link));
    }

    private DerivedCountTargets CollectDerivedCountTargets()
    {
        return new DerivedCountTargets(
            CollectAffectedTagCountIds(),
            CollectAffectedStudioCountIds(),
            CollectAffectedPerformerCountIds(),
            CollectAffectedGalleryCountIds(),
            CollectAffectedVideoMetricIds(),
            CollectAffectedImageIds());
    }

    private HashSet<int> CollectAffectedVideoMetricIds()
    {
        var ids = new HashSet<int>();
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<VideoFile>(), entry => entry.VideoId, nameof(VideoFile.VideoId));

        foreach (var entry in ChangeTracker.Entries<Video>())
        {
            if (entry.State is EntityState.Modified or EntityState.Added && entry.Entity.ParentVideoId.HasValue)
                AddIfPositive(ids, entry.Entity.Id);
        }

        var sourceVideoIds = ids.ToArray();
        if (sourceVideoIds.Length > 0)
        {
            foreach (var childVideoId in Videos.AsNoTracking()
                .Where(video => video.ParentVideoId.HasValue && sourceVideoIds.Contains(video.ParentVideoId.Value))
                .Select(video => video.Id)
                .ToList())
            {
                AddIfPositive(ids, childVideoId);
            }
        }

        return ids;
    }

    private HashSet<int> CollectAffectedImageIds()
    {
        var ids = new HashSet<int>();
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<ImageFile>(), entry => entry.ImageId, nameof(ImageFile.ImageId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<ImageTag>(), entry => entry.ImageId, nameof(ImageTag.ImageId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<ImagePerformer>(), entry => entry.ImageId, nameof(ImagePerformer.ImageId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<ImageGallery>(), entry => entry.ImageId, nameof(ImageGallery.ImageId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Tag>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            tagIds => Set<ImageTag>().AsNoTracking()
                .Where(imageTag => tagIds.Contains(imageTag.TagId))
                .Select(imageTag => imageTag.ImageId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Performer>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            performerIds => Set<ImagePerformer>().AsNoTracking()
                .Where(imagePerformer => performerIds.Contains(imagePerformer.PerformerId))
                .Select(imagePerformer => imagePerformer.ImageId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Gallery>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            galleryIds => Set<ImageGallery>().AsNoTracking()
                .Where(imageGallery => galleryIds.Contains(imageGallery.GalleryId))
                .Select(imageGallery => imageGallery.ImageId));

        return ids;
    }

    private HashSet<int> CollectAffectedPerformerCountIds()
    {
        var ids = new HashSet<int>();

        CollectChangedIntKey(ids, ChangeTracker.Entries<VideoPerformer>(), entry => entry.PerformerId, nameof(VideoPerformer.PerformerId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<ImagePerformer>(), entry => entry.PerformerId, nameof(ImagePerformer.PerformerId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<GalleryPerformer>(), entry => entry.PerformerId, nameof(GalleryPerformer.PerformerId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<PerformerTag>(), entry => entry.PerformerId, nameof(PerformerTag.PerformerId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Video>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            videoIds => Set<VideoPerformer>().AsNoTracking()
                .Where(videoPerformer => videoIds.Contains(videoPerformer.VideoId))
                .Select(videoPerformer => videoPerformer.PerformerId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Image>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            imageIds => Set<ImagePerformer>().AsNoTracking()
                .Where(imagePerformer => imageIds.Contains(imagePerformer.ImageId))
                .Select(imagePerformer => imagePerformer.PerformerId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Gallery>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            galleryIds => Set<GalleryPerformer>().AsNoTracking()
                .Where(galleryPerformer => galleryIds.Contains(galleryPerformer.GalleryId))
                .Select(galleryPerformer => galleryPerformer.PerformerId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Tag>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            tagIds => Set<PerformerTag>().AsNoTracking()
                .Where(performerTag => tagIds.Contains(performerTag.TagId))
                .Select(performerTag => performerTag.PerformerId));

        return ids;
    }

    private HashSet<int> CollectAffectedGalleryCountIds()
    {
        var ids = new HashSet<int>();

        CollectChangedIntKey(ids, ChangeTracker.Entries<ImageGallery>(), entry => entry.GalleryId, nameof(ImageGallery.GalleryId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<VideoGallery>(), entry => entry.GalleryId, nameof(VideoGallery.GalleryId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<GalleryPerformer>(), entry => entry.GalleryId, nameof(GalleryPerformer.GalleryId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<GalleryTag>(), entry => entry.GalleryId, nameof(GalleryTag.GalleryId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Image>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            imageIds => Set<ImageGallery>().AsNoTracking()
                .Where(imageGallery => imageIds.Contains(imageGallery.ImageId))
                .Select(imageGallery => imageGallery.GalleryId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Video>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            videoIds => Set<VideoGallery>().AsNoTracking()
                .Where(videoGallery => videoIds.Contains(videoGallery.VideoId))
                .Select(videoGallery => videoGallery.GalleryId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Performer>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            performerIds => Set<GalleryPerformer>().AsNoTracking()
                .Where(galleryPerformer => performerIds.Contains(galleryPerformer.PerformerId))
                .Select(galleryPerformer => galleryPerformer.GalleryId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Tag>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            tagIds => Set<GalleryTag>().AsNoTracking()
                .Where(galleryTag => tagIds.Contains(galleryTag.TagId))
                .Select(galleryTag => galleryTag.GalleryId));

        return ids;
    }

    private HashSet<int> CollectAffectedTagCountIds()
    {
        var ids = new HashSet<int>();

        CollectChangedIntKey(ids, ChangeTracker.Entries<VideoTag>(), entry => entry.TagId, nameof(VideoTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<PerformerTag>(), entry => entry.TagId, nameof(PerformerTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<ImageTag>(), entry => entry.TagId, nameof(ImageTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<GalleryTag>(), entry => entry.TagId, nameof(GalleryTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<StudioTag>(), entry => entry.TagId, nameof(StudioTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<GroupTag>(), entry => entry.TagId, nameof(GroupTag.TagId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<TagApplication>(), entry => entry.TagId, nameof(TagApplication.TagId));
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Segment>(), entry => entry.TagId, nameof(Segment.TagId));

        foreach (var entry in ChangeTracker.Entries<Tag>())
        {
            if (entry.State != EntityState.Modified)
                continue;

            if (entry.Property<double?>(nameof(Tag.MinOccurrenceSec)).IsModified
                || entry.Property<double?>(nameof(Tag.MinOccurrencePercent)).IsModified)
            {
                AddIfPositive(ids, entry.Entity.Id);
            }
        }

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Video>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            videoIds => Set<VideoTag>().AsNoTracking()
                .Where(videoTag => videoIds.Contains(videoTag.VideoId))
                .Select(videoTag => videoTag.TagId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Performer>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            performerIds => Set<PerformerTag>().AsNoTracking()
                .Where(performerTag => performerIds.Contains(performerTag.PerformerId))
                .Select(performerTag => performerTag.TagId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Image>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            imageIds => Set<ImageTag>().AsNoTracking()
                .Where(imageTag => imageIds.Contains(imageTag.ImageId))
                .Select(imageTag => imageTag.TagId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Gallery>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            galleryIds => Set<GalleryTag>().AsNoTracking()
                .Where(galleryTag => galleryIds.Contains(galleryTag.GalleryId))
                .Select(galleryTag => galleryTag.TagId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Studio>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            studioIds => Set<StudioTag>().AsNoTracking()
                .Where(studioTag => studioIds.Contains(studioTag.StudioId))
                .Select(studioTag => studioTag.TagId));

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Group>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            groupIds => Set<GroupTag>().AsNoTracking()
                .Where(groupTag => groupIds.Contains(groupTag.GroupId))
                .Select(groupTag => groupTag.TagId));

        return ids;
    }

    private HashSet<int> CollectAffectedStudioCountIds()
    {
        var ids = new HashSet<int>();

        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Video>(), entry => entry.StudioId, nameof(Video.StudioId));
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Image>(), entry => entry.StudioId, nameof(Image.StudioId));
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Gallery>(), entry => entry.StudioId, nameof(Gallery.StudioId));
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Group>(), entry => entry.StudioId, nameof(Group.StudioId));
        CollectChangedNullableIntKey(ids, ChangeTracker.Entries<Studio>(), entry => entry.ParentId, nameof(Studio.ParentId));
        CollectChangedIntKey(ids, ChangeTracker.Entries<StudioTag>(), entry => entry.StudioId, nameof(StudioTag.StudioId));

        var videoIds = new HashSet<int>();
        foreach (var entry in ChangeTracker.Entries<VideoPerformer>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            AddIfPositive(videoIds, entry.Entity.VideoId);
            AddIfPositive(videoIds, entry.Property<int>(nameof(VideoPerformer.VideoId)).OriginalValue);
        }

        if (videoIds.Count > 0)
        {
            var trackedVideos = ChangeTracker.Entries<Video>()
                .Where(entry => videoIds.Contains(entry.Entity.Id))
                .ToDictionary(entry => entry.Entity.Id);

            foreach (var videoId in videoIds)
            {
                if (!trackedVideos.TryGetValue(videoId, out var trackedVideo))
                    continue;

                AddIfPositive(ids, trackedVideo.Entity.StudioId);
                AddIfPositive(ids, trackedVideo.Property<int?>(nameof(Video.StudioId)).OriginalValue);
            }

            var missingVideoIds = videoIds.Where(videoId => !trackedVideos.ContainsKey(videoId)).ToArray();
            if (missingVideoIds.Length > 0)
            {
                foreach (var studioId in Videos.AsNoTracking()
                    .Where(video => missingVideoIds.Contains(video.Id) && video.StudioId.HasValue)
                    .Select(video => video.StudioId)
                    .ToList())
                {
                    AddIfPositive(ids, studioId);
                }
            }
        }

        AddRelatedIdsFromDeletedParents(ids,
            ChangeTracker.Entries<Performer>()
                .Where(entry => entry.State == EntityState.Deleted)
                .Select(entry => entry.Entity.Id)
                .ToArray(),
            performerIds => Set<VideoPerformer>().AsNoTracking()
                .Where(videoPerformer => performerIds.Contains(videoPerformer.PerformerId) && videoPerformer.Video!.StudioId.HasValue)
                .Select(videoPerformer => videoPerformer.Video!.StudioId!.Value));

        return ids;
    }

    private void PersistDerivedCounts(DerivedCountTargets derivedCountTargets)
    {
        if (!derivedCountTargets.HasAny)
            return;

        _persistingDerivedCounts = true;
        try
        {
            if (derivedCountTargets.TagIds.Count > 0)
                RefreshTagCounts(derivedCountTargets.TagIds);
            if (derivedCountTargets.StudioIds.Count > 0)
                RefreshStudioCounts(derivedCountTargets.StudioIds);
            if (derivedCountTargets.PerformerIds.Count > 0)
                RefreshPerformerCounts(derivedCountTargets.PerformerIds);
            if (derivedCountTargets.GalleryIds.Count > 0)
                RefreshGalleryCounts(derivedCountTargets.GalleryIds);
            if (derivedCountTargets.VideoIds.Count > 0)
                RefreshVideoMetrics(derivedCountTargets.VideoIds);
            if (derivedCountTargets.ImageIds.Count > 0)
                RefreshImageMetrics(derivedCountTargets.ImageIds);

            if (ChangeTracker.HasChanges())
                base.SaveChanges();
        }
        finally
        {
            _persistingDerivedCounts = false;
        }
    }

    private async Task PersistDerivedCountsAsync(DerivedCountTargets derivedCountTargets, CancellationToken cancellationToken)
    {
        if (!derivedCountTargets.HasAny)
            return;

        _persistingDerivedCounts = true;
        try
        {
            if (derivedCountTargets.TagIds.Count > 0)
                await RefreshTagCountsAsync(derivedCountTargets.TagIds, cancellationToken);
            if (derivedCountTargets.StudioIds.Count > 0)
                await RefreshStudioCountsAsync(derivedCountTargets.StudioIds, cancellationToken);
            if (derivedCountTargets.PerformerIds.Count > 0)
                await RefreshPerformerCountsAsync(derivedCountTargets.PerformerIds, cancellationToken);
            if (derivedCountTargets.GalleryIds.Count > 0)
                await RefreshGalleryCountsAsync(derivedCountTargets.GalleryIds, cancellationToken);
            if (derivedCountTargets.VideoIds.Count > 0)
                await RefreshVideoMetricsAsync(derivedCountTargets.VideoIds, cancellationToken);
            if (derivedCountTargets.ImageIds.Count > 0)
                await RefreshImageMetricsAsync(derivedCountTargets.ImageIds, cancellationToken);

            if (ChangeTracker.HasChanges())
                await base.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _persistingDerivedCounts = false;
        }
    }

    /// <summary>
    /// Recomputes denormalized state from source data for the whole library:
    /// video/image/gallery tag and performer ids, video/image file summaries (duration, resolution, file size, …),
    /// gallery image/video counts, and studio/performer/tag rollup counts. The per-SaveChanges maintenance only touches entities
    /// changed in a unit of work, so this is the catch-all used to repair data that predates a fix or
    /// was loaded through a path that didn't trigger maintenance (e.g. a bulk import). Idempotent.
    /// Returns the number of entities recomputed.
    /// </summary>
    public async Task<int> RecomputeAllDerivedCountsAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        const int batchSize = 500;
        var total = 0;
        var alreadyPersisting = _persistingDerivedCounts;
        _persistingDerivedCounts = true;
        try
        {
            total += await RecomputeAllAsync<Video>("videos", ids => RefreshAllVideoDerivedStateAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
            total += await RecomputeAllAsync<Image>("images", ids => RefreshAllImageDerivedStateAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
            total += await RecomputeAllAsync<Gallery>("galleries", ids => RefreshAllGalleryDerivedStateAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
            total += await RecomputeAllAsync<Studio>("studios", ids => RefreshStudioCountsAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
            total += await RecomputeAllAsync<Performer>("performers", ids => RefreshPerformerCountsAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
            total += await RecomputeAllAsync<Tag>("tags", ids => RefreshTagCountsAsync(ids, cancellationToken), batchSize, progress, cancellationToken);
        }
        finally
        {
            _persistingDerivedCounts = alreadyPersisting;
        }

        return total;
    }

    private async Task RefreshAllVideoDerivedStateAsync(HashSet<int> videoIds, CancellationToken cancellationToken)
    {
        RebuildArray<Video, VideoTag>(videoIds, video => video.TagIds, link => link.VideoId, link => link.TagId);
        RebuildArray<Video, VideoPerformer>(videoIds, video => video.PerformerIds, link => link.VideoId, link => link.PerformerId);
        await RefreshVideoMetricsAsync(videoIds, cancellationToken);
    }

    private async Task RefreshAllImageDerivedStateAsync(HashSet<int> imageIds, CancellationToken cancellationToken)
    {
        RebuildArray<Image, ImageTag>(imageIds, image => image.TagIds, link => link.ImageId, link => link.TagId);
        RebuildArray<Image, ImagePerformer>(imageIds, image => image.PerformerIds, link => link.ImageId, link => link.PerformerId);
        await RefreshImageMetricsAsync(imageIds, cancellationToken);
    }

    private async Task RefreshAllGalleryDerivedStateAsync(HashSet<int> galleryIds, CancellationToken cancellationToken)
    {
        RebuildArray<Gallery, GalleryTag>(galleryIds, gallery => gallery.TagIds, link => link.GalleryId, link => link.TagId);
        RebuildArray<Gallery, GalleryPerformer>(galleryIds, gallery => gallery.PerformerIds, link => link.GalleryId, link => link.PerformerId);
        await RefreshGalleryCountsAsync(galleryIds, cancellationToken);
    }

    private async Task<int> RecomputeAllAsync<TEntity>(
        string label,
        Func<HashSet<int>, Task> refresh,
        int batchSize,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
        where TEntity : BaseEntity
    {
        var allIds = await Set<TEntity>().AsNoTracking().Select(entity => entity.Id).ToListAsync(cancellationToken);
        var processed = 0;
        foreach (var batch in allIds.Chunk(batchSize))
        {
            await refresh([.. batch]);
            if (ChangeTracker.HasChanges())
                await base.SaveChangesAsync(cancellationToken);
            ChangeTracker.Clear();
            processed += batch.Length;
            progress?.Report($"Recomputed {processed}/{allIds.Count} {label}");
        }

        return allIds.Count;
    }

    private void RefreshTagCounts(HashSet<int> affectedTagIds)
    {
        var tags = Tags.Where(BuildIdContainsPredicate<Tag>(affectedTagIds.ToArray())).ToDictionary(tag => tag.Id);
        if (tags.Count == 0)
            return;

        var ids = tags.Keys.ToArray();
        var videoCounts = EffectiveHostTagQuery.ForHostType(this, AffinityHostType.Video)
            .AsNoTracking()
            .Where(tag => ids.Contains(tag.TagId))
            .Select(tag => new { tag.TagId, tag.HostId })
            .Distinct()
            .GroupBy(tag => tag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var videoSegmentCounts = Segments.AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.TagId.HasValue && ids.Contains(segment.TagId.Value))
            .GroupBy(segment => segment.TagId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var imageCounts = Set<ImageTag>().AsNoTracking().Where(imageTag => ids.Contains(imageTag.TagId))
            .GroupBy(imageTag => imageTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var galleryCounts = Set<GalleryTag>().AsNoTracking().Where(galleryTag => ids.Contains(galleryTag.TagId))
            .GroupBy(galleryTag => galleryTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var groupCounts = Set<GroupTag>().AsNoTracking().Where(groupTag => ids.Contains(groupTag.TagId))
            .GroupBy(groupTag => groupTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var performerCounts = Set<PerformerTag>().AsNoTracking().Where(performerTag => ids.Contains(performerTag.TagId))
            .GroupBy(performerTag => performerTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var studioCounts = Set<StudioTag>().AsNoTracking().Where(studioTag => ids.Contains(studioTag.TagId))
            .GroupBy(studioTag => studioTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);

        foreach (var tag in tags.Values)
        {
            tag.VideoCount = videoCounts.GetValueOrDefault(tag.Id, 0);
            tag.SegmentCount = videoSegmentCounts.GetValueOrDefault(tag.Id, 0);
            tag.ImageCount = imageCounts.GetValueOrDefault(tag.Id, 0);
            tag.GalleryCount = galleryCounts.GetValueOrDefault(tag.Id, 0);
            tag.GroupCount = groupCounts.GetValueOrDefault(tag.Id, 0);
            tag.PerformerCount = performerCounts.GetValueOrDefault(tag.Id, 0);
            tag.StudioCount = studioCounts.GetValueOrDefault(tag.Id, 0);
        }
    }

    private async Task RefreshTagCountsAsync(HashSet<int> affectedTagIds, CancellationToken cancellationToken)
    {
        var tags = await Tags.Where(BuildIdContainsPredicate<Tag>(affectedTagIds.ToArray())).ToDictionaryAsync(tag => tag.Id, cancellationToken);
        if (tags.Count == 0)
            return;

        var ids = tags.Keys.ToArray();
        var videoCounts = await EffectiveHostTagQuery.ForHostType(this, AffinityHostType.Video)
            .AsNoTracking()
            .Where(tag => ids.Contains(tag.TagId))
            .Select(tag => new { tag.TagId, tag.HostId })
            .Distinct()
            .GroupBy(tag => tag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var videoSegmentCounts = await Segments.AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video && segment.TagId.HasValue && ids.Contains(segment.TagId.Value))
            .GroupBy(segment => segment.TagId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var imageCounts = await Set<ImageTag>().AsNoTracking().Where(imageTag => ids.Contains(imageTag.TagId))
            .GroupBy(imageTag => imageTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var galleryCounts = await Set<GalleryTag>().AsNoTracking().Where(galleryTag => ids.Contains(galleryTag.TagId))
            .GroupBy(galleryTag => galleryTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var groupCounts = await Set<GroupTag>().AsNoTracking().Where(groupTag => ids.Contains(groupTag.TagId))
            .GroupBy(groupTag => groupTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var performerCounts = await Set<PerformerTag>().AsNoTracking().Where(performerTag => ids.Contains(performerTag.TagId))
            .GroupBy(performerTag => performerTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var studioCounts = await Set<StudioTag>().AsNoTracking().Where(studioTag => ids.Contains(studioTag.TagId))
            .GroupBy(studioTag => studioTag.TagId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        foreach (var tag in tags.Values)
        {
            tag.VideoCount = videoCounts.GetValueOrDefault(tag.Id, 0);
            tag.SegmentCount = videoSegmentCounts.GetValueOrDefault(tag.Id, 0);
            tag.ImageCount = imageCounts.GetValueOrDefault(tag.Id, 0);
            tag.GalleryCount = galleryCounts.GetValueOrDefault(tag.Id, 0);
            tag.GroupCount = groupCounts.GetValueOrDefault(tag.Id, 0);
            tag.PerformerCount = performerCounts.GetValueOrDefault(tag.Id, 0);
            tag.StudioCount = studioCounts.GetValueOrDefault(tag.Id, 0);
        }
    }

    private void RefreshVideoMetrics(HashSet<int> affectedVideoIds)
    {
        var videos = Videos.Where(BuildIdContainsPredicate<Video>(affectedVideoIds.ToArray())).ToDictionary(video => video.Id);
        if (videos.Count == 0)
            return;

        var ids = videos.Keys.ToArray();
        var sourceIds = ids.Concat(videos.Values.Select(video => video.ParentVideoId).Where(parentId => parentId.HasValue).Select(parentId => parentId!.Value)).Distinct().ToArray();
        var fileRows = VideoFiles.AsNoTracking()
            .Where(file => file.VideoId.HasValue && sourceIds.Contains(file.VideoId.Value))
            .Select(file => new
            {
                VideoId = file.VideoId!.Value,
                file.Path,
                file.Duration,
                file.Width,
                file.Height,
                file.FrameRate,
                file.BitRate,
                file.Size,
                file.ModTime,
            })
            .ToList();
        var summaries = fileRows
            .GroupBy(file => file.VideoId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    FileCount = group.Count(),
                    MaxDuration = group.Max(file => file.Duration),
                    MaxResolution = group.Max(file => Math.Max(file.Width, file.Height)),
                    MaxHeight = group.Max(file => file.Height),
                    MaxFrameRate = group.Max(file => file.FrameRate),
                    MaxBitRate = group.Max(file => file.BitRate),
                    MaxFileSize = group.Max(file => file.Size),
                    MaxFileModTime = group.Max(file => (DateTime?)file.ModTime),
                    MinPath = group.Min(file => file.Path),
                    MaxPath = group.Max(file => file.Path),
                    FileSearchText = BuildFileSearchText(group.Select(file => file.Path)),
                    HasDimensionData = group.Any(file => file.Width > 0 && file.Height > 0),
                    HasLandscapeFiles = group.Any(file => file.Width > file.Height),
                    HasPortraitFiles = group.Any(file => file.Height > file.Width),
                    HasSquareFiles = group.Any(file => file.Width > 0 && file.Width == file.Height),
                });

        foreach (var video in videos.Values)
        {
            var sourceVideoId = video.ParentVideoId ?? video.Id;
            if (!summaries.TryGetValue(sourceVideoId, out var summary))
            {
                video.FileCount = 0;
                video.MaxDuration = 0;
                video.MaxResolution = 0;
                video.MaxHeight = 0;
                video.MaxFrameRate = 0;
                video.MaxBitRate = 0;
                video.MaxFileSize = 0;
                video.MaxFileModTime = null;
                video.MinPath = null;
                video.MaxPath = null;
                video.FileSearchText = null;
                video.HasDimensionData = false;
                video.HasLandscapeFiles = false;
                video.HasPortraitFiles = false;
                video.HasSquareFiles = false;
                continue;
            }

            video.FileCount = summary.FileCount;
            video.MaxDuration = video.ParentVideoId.HasValue
                ? Math.Max(0, Math.Min(video.ClipEndSec ?? summary.MaxDuration, summary.MaxDuration) - Math.Max(0, video.ClipStartSec ?? 0))
                : summary.MaxDuration;
            video.MaxResolution = summary.MaxResolution;
            video.MaxHeight = summary.MaxHeight;
            video.MaxFrameRate = summary.MaxFrameRate;
            video.MaxBitRate = summary.MaxBitRate;
            video.MaxFileSize = summary.MaxFileSize;
            video.MaxFileModTime = summary.MaxFileModTime;
            video.MinPath = summary.MinPath;
            video.MaxPath = summary.MaxPath;
            video.FileSearchText = summary.FileSearchText;
            video.HasDimensionData = summary.HasDimensionData;
            video.HasLandscapeFiles = summary.HasLandscapeFiles;
            video.HasPortraitFiles = summary.HasPortraitFiles;
            video.HasSquareFiles = summary.HasSquareFiles;
        }
    }

    private async Task RefreshVideoMetricsAsync(HashSet<int> affectedVideoIds, CancellationToken cancellationToken)
    {
        var videos = await Videos.Where(BuildIdContainsPredicate<Video>(affectedVideoIds.ToArray())).ToDictionaryAsync(video => video.Id, cancellationToken);
        if (videos.Count == 0)
            return;

        var ids = videos.Keys.ToArray();
        var sourceIds = ids.Concat(videos.Values.Select(video => video.ParentVideoId).Where(parentId => parentId.HasValue).Select(parentId => parentId!.Value)).Distinct().ToArray();
        var fileRows = await VideoFiles.AsNoTracking()
            .Where(file => file.VideoId.HasValue && sourceIds.Contains(file.VideoId.Value))
            .Select(file => new
            {
                VideoId = file.VideoId!.Value,
                file.Path,
                file.Duration,
                file.Width,
                file.Height,
                file.FrameRate,
                file.BitRate,
                file.Size,
                file.ModTime,
            })
            .ToListAsync(cancellationToken);
        var summaries = fileRows
            .GroupBy(file => file.VideoId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    FileCount = group.Count(),
                    MaxDuration = group.Max(file => file.Duration),
                    MaxResolution = group.Max(file => Math.Max(file.Width, file.Height)),
                    MaxHeight = group.Max(file => file.Height),
                    MaxFrameRate = group.Max(file => file.FrameRate),
                    MaxBitRate = group.Max(file => file.BitRate),
                    MaxFileSize = group.Max(file => file.Size),
                    MaxFileModTime = group.Max(file => (DateTime?)file.ModTime),
                    MinPath = group.Min(file => file.Path),
                    MaxPath = group.Max(file => file.Path),
                    FileSearchText = BuildFileSearchText(group.Select(file => file.Path)),
                    HasDimensionData = group.Any(file => file.Width > 0 && file.Height > 0),
                    HasLandscapeFiles = group.Any(file => file.Width > file.Height),
                    HasPortraitFiles = group.Any(file => file.Height > file.Width),
                    HasSquareFiles = group.Any(file => file.Width > 0 && file.Width == file.Height),
                });

        foreach (var video in videos.Values)
        {
            var sourceVideoId = video.ParentVideoId ?? video.Id;
            if (!summaries.TryGetValue(sourceVideoId, out var summary))
            {
                video.FileCount = 0;
                video.MaxDuration = 0;
                video.MaxResolution = 0;
                video.MaxHeight = 0;
                video.MaxFrameRate = 0;
                video.MaxBitRate = 0;
                video.MaxFileSize = 0;
                video.MaxFileModTime = null;
                video.MinPath = null;
                video.MaxPath = null;
                video.FileSearchText = null;
                video.HasDimensionData = false;
                video.HasLandscapeFiles = false;
                video.HasPortraitFiles = false;
                video.HasSquareFiles = false;
                continue;
            }

            video.FileCount = summary.FileCount;
            video.MaxDuration = video.ParentVideoId.HasValue
                ? Math.Max(0, Math.Min(video.ClipEndSec ?? summary.MaxDuration, summary.MaxDuration) - Math.Max(0, video.ClipStartSec ?? 0))
                : summary.MaxDuration;
            video.MaxResolution = summary.MaxResolution;
            video.MaxHeight = summary.MaxHeight;
            video.MaxFrameRate = summary.MaxFrameRate;
            video.MaxBitRate = summary.MaxBitRate;
            video.MaxFileSize = summary.MaxFileSize;
            video.MaxFileModTime = summary.MaxFileModTime;
            video.MinPath = summary.MinPath;
            video.MaxPath = summary.MaxPath;
            video.FileSearchText = summary.FileSearchText;
            video.HasDimensionData = summary.HasDimensionData;
            video.HasLandscapeFiles = summary.HasLandscapeFiles;
            video.HasPortraitFiles = summary.HasPortraitFiles;
            video.HasSquareFiles = summary.HasSquareFiles;
        }
    }

    private void RefreshImageMetrics(HashSet<int> affectedImageIds)
    {
        var images = Images.Where(BuildIdContainsPredicate<Image>(affectedImageIds.ToArray())).ToDictionary(image => image.Id);
        if (images.Count == 0)
            return;

        var ids = images.Keys.ToArray();
        var tagCounts = Set<ImageTag>().AsNoTracking().Where(imageTag => ids.Contains(imageTag.ImageId))
            .GroupBy(imageTag => imageTag.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var performerCounts = Set<ImagePerformer>().AsNoTracking().Where(imagePerformer => ids.Contains(imagePerformer.ImageId))
            .GroupBy(imagePerformer => imagePerformer.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var galleryCounts = Set<ImageGallery>().AsNoTracking().Where(imageGallery => ids.Contains(imageGallery.ImageId))
            .GroupBy(imageGallery => imageGallery.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var fileRows = ImageFiles.AsNoTracking()
            .Where(file => file.ImageId.HasValue && ids.Contains(file.ImageId.Value))
            .Select(file => new
            {
                ImageId = file.ImageId!.Value,
                file.Path,
                file.Width,
                file.Height,
                file.Size,
                file.ModTime,
            })
            .ToList();
        var summaries = fileRows
            .GroupBy(file => file.ImageId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    FileCount = group.Count(),
                    MaxResolution = group.Max(file => Math.Max(file.Width, file.Height)),
                    MaxFileSize = group.Max(file => file.Size),
                    MaxFileModTime = group.Max(file => (DateTime?)file.ModTime),
                    MinPath = group.Min(file => file.Path),
                    MaxPath = group.Max(file => file.Path),
                    FileSearchText = BuildFileSearchText(group.Select(file => file.Path)),
                    HasDimensionData = group.Any(file => file.Width > 0 && file.Height > 0),
                    HasLandscapeFiles = group.Any(file => file.Width > file.Height),
                    HasPortraitFiles = group.Any(file => file.Height > file.Width),
                    HasSquareFiles = group.Any(file => file.Width > 0 && file.Width == file.Height),
                });

        foreach (var image in images.Values)
        {
            image.TagCount = tagCounts.GetValueOrDefault(image.Id, 0);
            image.PerformerCount = performerCounts.GetValueOrDefault(image.Id, 0);
            image.GalleryCount = galleryCounts.GetValueOrDefault(image.Id, 0);

            if (!summaries.TryGetValue(image.Id, out var summary))
            {
                image.FileCount = 0;
                image.MaxResolution = 0;
                image.MaxFileSize = 0;
                image.MaxFileModTime = null;
                image.MinPath = null;
                image.MaxPath = null;
                image.FileSearchText = null;
                image.HasDimensionData = false;
                image.HasLandscapeFiles = false;
                image.HasPortraitFiles = false;
                image.HasSquareFiles = false;
                continue;
            }

            image.FileCount = summary.FileCount;
            image.MaxResolution = summary.MaxResolution;
            image.MaxFileSize = summary.MaxFileSize;
            image.MaxFileModTime = summary.MaxFileModTime;
            image.MinPath = summary.MinPath;
            image.MaxPath = summary.MaxPath;
            image.FileSearchText = summary.FileSearchText;
            image.HasDimensionData = summary.HasDimensionData;
            image.HasLandscapeFiles = summary.HasLandscapeFiles;
            image.HasPortraitFiles = summary.HasPortraitFiles;
            image.HasSquareFiles = summary.HasSquareFiles;
        }
    }

    private async Task RefreshImageMetricsAsync(HashSet<int> affectedImageIds, CancellationToken cancellationToken)
    {
        var images = await Images.Where(BuildIdContainsPredicate<Image>(affectedImageIds.ToArray())).ToDictionaryAsync(image => image.Id, cancellationToken);
        if (images.Count == 0)
            return;

        var ids = images.Keys.ToArray();
        var tagCounts = await Set<ImageTag>().AsNoTracking().Where(imageTag => ids.Contains(imageTag.ImageId))
            .GroupBy(imageTag => imageTag.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var performerCounts = await Set<ImagePerformer>().AsNoTracking().Where(imagePerformer => ids.Contains(imagePerformer.ImageId))
            .GroupBy(imagePerformer => imagePerformer.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var galleryCounts = await Set<ImageGallery>().AsNoTracking().Where(imageGallery => ids.Contains(imageGallery.ImageId))
            .GroupBy(imageGallery => imageGallery.ImageId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var fileRows = await ImageFiles.AsNoTracking()
            .Where(file => file.ImageId.HasValue && ids.Contains(file.ImageId.Value))
            .Select(file => new
            {
                ImageId = file.ImageId!.Value,
                file.Path,
                file.Width,
                file.Height,
                file.Size,
                file.ModTime,
            })
            .ToListAsync(cancellationToken);
        var summaries = fileRows
            .GroupBy(file => file.ImageId)
            .ToDictionary(
                group => group.Key,
                group => new
                {
                    FileCount = group.Count(),
                    MaxResolution = group.Max(file => Math.Max(file.Width, file.Height)),
                    MaxFileSize = group.Max(file => file.Size),
                    MaxFileModTime = group.Max(file => (DateTime?)file.ModTime),
                    MinPath = group.Min(file => file.Path),
                    MaxPath = group.Max(file => file.Path),
                    FileSearchText = BuildFileSearchText(group.Select(file => file.Path)),
                    HasDimensionData = group.Any(file => file.Width > 0 && file.Height > 0),
                    HasLandscapeFiles = group.Any(file => file.Width > file.Height),
                    HasPortraitFiles = group.Any(file => file.Height > file.Width),
                    HasSquareFiles = group.Any(file => file.Width > 0 && file.Width == file.Height),
                });

        foreach (var image in images.Values)
        {
            image.TagCount = tagCounts.GetValueOrDefault(image.Id, 0);
            image.PerformerCount = performerCounts.GetValueOrDefault(image.Id, 0);
            image.GalleryCount = galleryCounts.GetValueOrDefault(image.Id, 0);

            if (!summaries.TryGetValue(image.Id, out var summary))
            {
                image.FileCount = 0;
                image.MaxResolution = 0;
                image.MaxFileSize = 0;
                image.MaxFileModTime = null;
                image.MinPath = null;
                image.MaxPath = null;
                image.FileSearchText = null;
                image.HasDimensionData = false;
                image.HasLandscapeFiles = false;
                image.HasPortraitFiles = false;
                image.HasSquareFiles = false;
                continue;
            }

            image.FileCount = summary.FileCount;
            image.MaxResolution = summary.MaxResolution;
            image.MaxFileSize = summary.MaxFileSize;
            image.MaxFileModTime = summary.MaxFileModTime;
            image.MinPath = summary.MinPath;
            image.MaxPath = summary.MaxPath;
            image.FileSearchText = summary.FileSearchText;
            image.HasDimensionData = summary.HasDimensionData;
            image.HasLandscapeFiles = summary.HasLandscapeFiles;
            image.HasPortraitFiles = summary.HasPortraitFiles;
            image.HasSquareFiles = summary.HasSquareFiles;
        }
    }

    private void RefreshPerformerCounts(HashSet<int> affectedPerformerIds)
    {
        var performers = Performers.Where(BuildIdContainsPredicate<Performer>(affectedPerformerIds.ToArray())).ToDictionary(performer => performer.Id);
        if (performers.Count == 0)
            return;

        var ids = performers.Keys.ToArray();
        var videoCounts = Set<VideoPerformer>().AsNoTracking().Where(videoPerformer => ids.Contains(videoPerformer.PerformerId))
            .GroupBy(videoPerformer => videoPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var imageCounts = Set<ImagePerformer>().AsNoTracking().Where(imagePerformer => ids.Contains(imagePerformer.PerformerId))
            .GroupBy(imagePerformer => imagePerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var galleryCounts = Set<GalleryPerformer>().AsNoTracking().Where(galleryPerformer => ids.Contains(galleryPerformer.PerformerId))
            .GroupBy(galleryPerformer => galleryPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var tagCounts = Set<PerformerTag>().AsNoTracking().Where(performerTag => ids.Contains(performerTag.PerformerId))
            .GroupBy(performerTag => performerTag.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);

        foreach (var performer in performers.Values)
        {
            performer.VideoCount = videoCounts.GetValueOrDefault(performer.Id, 0);
            performer.ImageCount = imageCounts.GetValueOrDefault(performer.Id, 0);
            performer.GalleryCount = galleryCounts.GetValueOrDefault(performer.Id, 0);
            performer.TagCount = tagCounts.GetValueOrDefault(performer.Id, 0);
        }
    }

    private async Task RefreshPerformerCountsAsync(HashSet<int> affectedPerformerIds, CancellationToken cancellationToken)
    {
        var performers = await Performers.Where(BuildIdContainsPredicate<Performer>(affectedPerformerIds.ToArray())).ToDictionaryAsync(performer => performer.Id, cancellationToken);
        if (performers.Count == 0)
            return;

        var ids = performers.Keys.ToArray();
        var videoCounts = await Set<VideoPerformer>().AsNoTracking().Where(videoPerformer => ids.Contains(videoPerformer.PerformerId))
            .GroupBy(videoPerformer => videoPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var imageCounts = await Set<ImagePerformer>().AsNoTracking().Where(imagePerformer => ids.Contains(imagePerformer.PerformerId))
            .GroupBy(imagePerformer => imagePerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var galleryCounts = await Set<GalleryPerformer>().AsNoTracking().Where(galleryPerformer => ids.Contains(galleryPerformer.PerformerId))
            .GroupBy(galleryPerformer => galleryPerformer.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var tagCounts = await Set<PerformerTag>().AsNoTracking().Where(performerTag => ids.Contains(performerTag.PerformerId))
            .GroupBy(performerTag => performerTag.PerformerId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        foreach (var performer in performers.Values)
        {
            performer.VideoCount = videoCounts.GetValueOrDefault(performer.Id, 0);
            performer.ImageCount = imageCounts.GetValueOrDefault(performer.Id, 0);
            performer.GalleryCount = galleryCounts.GetValueOrDefault(performer.Id, 0);
            performer.TagCount = tagCounts.GetValueOrDefault(performer.Id, 0);
        }
    }

    private void RefreshGalleryCounts(HashSet<int> affectedGalleryIds)
    {
        var galleries = Galleries.Where(BuildIdContainsPredicate<Gallery>(affectedGalleryIds.ToArray())).ToDictionary(gallery => gallery.Id);
        if (galleries.Count == 0)
            return;

        var ids = galleries.Keys.ToArray();
        var imageCounts = Set<ImageGallery>().AsNoTracking().Where(imageGallery => ids.Contains(imageGallery.GalleryId))
            .GroupBy(imageGallery => imageGallery.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var videoCounts = Set<VideoGallery>().AsNoTracking().Where(videoGallery => ids.Contains(videoGallery.GalleryId))
            .GroupBy(videoGallery => videoGallery.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var performerCounts = Set<GalleryPerformer>().AsNoTracking().Where(galleryPerformer => ids.Contains(galleryPerformer.GalleryId))
            .GroupBy(galleryPerformer => galleryPerformer.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var tagCounts = Set<GalleryTag>().AsNoTracking().Where(galleryTag => ids.Contains(galleryTag.GalleryId))
            .GroupBy(galleryTag => galleryTag.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);

        foreach (var gallery in galleries.Values)
        {
            gallery.ImageCount = imageCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.VideoCount = videoCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.PerformerCount = performerCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.TagCount = tagCounts.GetValueOrDefault(gallery.Id, 0);
        }
    }

    private async Task RefreshGalleryCountsAsync(HashSet<int> affectedGalleryIds, CancellationToken cancellationToken)
    {
        var galleries = await Galleries.Where(BuildIdContainsPredicate<Gallery>(affectedGalleryIds.ToArray())).ToDictionaryAsync(gallery => gallery.Id, cancellationToken);
        if (galleries.Count == 0)
            return;

        var ids = galleries.Keys.ToArray();
        var imageCounts = await Set<ImageGallery>().AsNoTracking().Where(imageGallery => ids.Contains(imageGallery.GalleryId))
            .GroupBy(imageGallery => imageGallery.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var videoCounts = await Set<VideoGallery>().AsNoTracking().Where(videoGallery => ids.Contains(videoGallery.GalleryId))
            .GroupBy(videoGallery => videoGallery.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var performerCounts = await Set<GalleryPerformer>().AsNoTracking().Where(galleryPerformer => ids.Contains(galleryPerformer.GalleryId))
            .GroupBy(galleryPerformer => galleryPerformer.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var tagCounts = await Set<GalleryTag>().AsNoTracking().Where(galleryTag => ids.Contains(galleryTag.GalleryId))
            .GroupBy(galleryTag => galleryTag.GalleryId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        foreach (var gallery in galleries.Values)
        {
            gallery.ImageCount = imageCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.VideoCount = videoCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.PerformerCount = performerCounts.GetValueOrDefault(gallery.Id, 0);
            gallery.TagCount = tagCounts.GetValueOrDefault(gallery.Id, 0);
        }
    }

    private static string? BuildFileSearchText(IEnumerable<string?> paths)
    {
        var normalizedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (normalizedPaths.Length == 0)
            return null;

        return "\n" + string.Join("\n", normalizedPaths) + "\n";
    }

    private void RefreshStudioCounts(HashSet<int> affectedStudioIds)
    {
        var studios = Studios.Where(BuildIdContainsPredicate<Studio>(affectedStudioIds.ToArray())).ToDictionary(studio => studio.Id);
        if (studios.Count == 0)
            return;

        var ids = studios.Keys.ToArray();
        var videoCounts = Videos.AsNoTracking().Where(video => video.StudioId.HasValue && ids.Contains(video.StudioId.Value))
            .GroupBy(video => video.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var imageCounts = Images.AsNoTracking().Where(image => image.StudioId.HasValue && ids.Contains(image.StudioId.Value))
            .GroupBy(image => image.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var galleryCounts = Galleries.AsNoTracking().Where(gallery => gallery.StudioId.HasValue && ids.Contains(gallery.StudioId.Value))
            .GroupBy(gallery => gallery.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var groupCounts = Set<Group>().AsNoTracking().Where(groupEntity => groupEntity.StudioId.HasValue && ids.Contains(groupEntity.StudioId.Value))
            .GroupBy(groupEntity => groupEntity.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var performerCounts = Set<VideoPerformer>().AsNoTracking().Where(videoPerformer => videoPerformer.Video!.StudioId.HasValue && ids.Contains(videoPerformer.Video.StudioId.Value))
            .GroupBy(videoPerformer => videoPerformer.Video!.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Select(videoPerformer => videoPerformer.PerformerId).Distinct().Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var childCounts = Studios.AsNoTracking().Where(studio => studio.ParentId.HasValue && ids.Contains(studio.ParentId.Value))
            .GroupBy(studio => studio.ParentId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);
        var tagCounts = Set<StudioTag>().AsNoTracking().Where(studioTag => ids.Contains(studioTag.StudioId))
            .GroupBy(studioTag => studioTag.StudioId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionary(x => x.Key, x => x.Count);

        foreach (var studio in studios.Values)
        {
            studio.VideoCount = videoCounts.GetValueOrDefault(studio.Id, 0);
            studio.ImageCount = imageCounts.GetValueOrDefault(studio.Id, 0);
            studio.GalleryCount = galleryCounts.GetValueOrDefault(studio.Id, 0);
            studio.GroupCount = groupCounts.GetValueOrDefault(studio.Id, 0);
            studio.PerformerCount = performerCounts.GetValueOrDefault(studio.Id, 0);
            studio.ChildStudioCount = childCounts.GetValueOrDefault(studio.Id, 0);
            studio.TagCount = tagCounts.GetValueOrDefault(studio.Id, 0);
        }
    }

    private async Task RefreshStudioCountsAsync(HashSet<int> affectedStudioIds, CancellationToken cancellationToken)
    {
        var studios = await Studios.Where(BuildIdContainsPredicate<Studio>(affectedStudioIds.ToArray())).ToDictionaryAsync(studio => studio.Id, cancellationToken);
        if (studios.Count == 0)
            return;

        var ids = studios.Keys.ToArray();
        var videoCounts = await Videos.AsNoTracking().Where(video => video.StudioId.HasValue && ids.Contains(video.StudioId.Value))
            .GroupBy(video => video.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var imageCounts = await Images.AsNoTracking().Where(image => image.StudioId.HasValue && ids.Contains(image.StudioId.Value))
            .GroupBy(image => image.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var galleryCounts = await Galleries.AsNoTracking().Where(gallery => gallery.StudioId.HasValue && ids.Contains(gallery.StudioId.Value))
            .GroupBy(gallery => gallery.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var groupCounts = await Set<Group>().AsNoTracking().Where(groupEntity => groupEntity.StudioId.HasValue && ids.Contains(groupEntity.StudioId.Value))
            .GroupBy(groupEntity => groupEntity.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var performerCounts = await Set<VideoPerformer>().AsNoTracking().Where(videoPerformer => videoPerformer.Video!.StudioId.HasValue && ids.Contains(videoPerformer.Video.StudioId.Value))
            .GroupBy(videoPerformer => videoPerformer.Video!.StudioId!.Value)
            .Select(group => new { group.Key, Count = group.Select(videoPerformer => videoPerformer.PerformerId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var childCounts = await Studios.AsNoTracking().Where(studio => studio.ParentId.HasValue && ids.Contains(studio.ParentId.Value))
            .GroupBy(studio => studio.ParentId!.Value)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var tagCounts = await Set<StudioTag>().AsNoTracking().Where(studioTag => ids.Contains(studioTag.StudioId))
            .GroupBy(studioTag => studioTag.StudioId)
            .Select(group => new { group.Key, Count = group.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        foreach (var studio in studios.Values)
        {
            studio.VideoCount = videoCounts.GetValueOrDefault(studio.Id, 0);
            studio.ImageCount = imageCounts.GetValueOrDefault(studio.Id, 0);
            studio.GalleryCount = galleryCounts.GetValueOrDefault(studio.Id, 0);
            studio.GroupCount = groupCounts.GetValueOrDefault(studio.Id, 0);
            studio.PerformerCount = performerCounts.GetValueOrDefault(studio.Id, 0);
            studio.ChildStudioCount = childCounts.GetValueOrDefault(studio.Id, 0);
            studio.TagCount = tagCounts.GetValueOrDefault(studio.Id, 0);
        }
    }

    private static void CollectChangedIntKey<TEntity>(HashSet<int> ids, IEnumerable<EntityEntry<TEntity>> entries, Func<TEntity, int> currentSelector, string propertyName)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            AddIfPositive(ids, currentSelector(entry.Entity));
            AddIfPositive(ids, entry.Property<int>(propertyName).OriginalValue);
        }
    }

    private static void CollectChangedNullableIntKey<TEntity>(HashSet<int> ids, IEnumerable<EntityEntry<TEntity>> entries, Func<TEntity, int?> currentSelector, string propertyName)
        where TEntity : class
    {
        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            AddIfPositive(ids, currentSelector(entry.Entity));
            AddIfPositive(ids, entry.Property<int?>(propertyName).OriginalValue);
        }
    }

    private static void AddRelatedIdsFromDeletedParents(HashSet<int> ids, int[] deletedParentIds, Func<int[], IQueryable<int>> queryBuilder)
    {
        if (deletedParentIds.Length == 0)
            return;

        foreach (var tagId in queryBuilder(deletedParentIds).ToList())
            AddIfPositive(ids, tagId);
    }

    private static void AddIfPositive(HashSet<int> ids, int? value)
    {
        if (value is > 0)
            ids.Add(value.Value);
    }

    private HashSet<int> CollectChangedParentIds<TLink>(Func<TLink, int> parentId) where TLink : class
    {
        var ids = new HashSet<int>();
        foreach (var entry in ChangeTracker.Entries<TLink>())
        {
            if (entry.State is EntityState.Added or EntityState.Deleted or EntityState.Modified)
            {
                var id = parentId(entry.Entity);
                if (id > 0)
                    ids.Add(id);
            }
        }
        return ids;
    }

    private void InitializeAddedParentIdArrays()
    {
        foreach (var entry in ChangeTracker.Entries<Video>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.TagIds = entry.Entity.VideoTags
                .Select(videoTag => videoTag.TagId)
                .Where(tagId => tagId > 0)
                .Distinct()
                .OrderBy(tagId => tagId)
                .ToArray();
            entry.Entity.PerformerIds = entry.Entity.VideoPerformers
                .Select(videoPerformer => videoPerformer.PerformerId)
                .Where(performerId => performerId > 0)
                .Distinct()
                .OrderBy(performerId => performerId)
                .ToArray();
        }

        foreach (var entry in ChangeTracker.Entries<Image>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.TagIds = entry.Entity.ImageTags
                .Select(imageTag => imageTag.TagId)
                .Where(tagId => tagId > 0)
                .Distinct()
                .OrderBy(tagId => tagId)
                .ToArray();
            entry.Entity.PerformerIds = entry.Entity.ImagePerformers
                .Select(imagePerformer => imagePerformer.PerformerId)
                .Where(performerId => performerId > 0)
                .Distinct()
                .OrderBy(performerId => performerId)
                .ToArray();
        }

        foreach (var entry in ChangeTracker.Entries<Gallery>().Where(e => e.State == EntityState.Added))
        {
            entry.Entity.TagIds = entry.Entity.GalleryTags
                .Select(galleryTag => galleryTag.TagId)
                .Where(tagId => tagId > 0)
                .Distinct()
                .OrderBy(tagId => tagId)
                .ToArray();
            entry.Entity.PerformerIds = entry.Entity.GalleryPerformers
                .Select(galleryPerformer => galleryPerformer.PerformerId)
                .Where(performerId => performerId > 0)
                .Distinct()
                .OrderBy(performerId => performerId)
                .ToArray();
        }
    }

    private void RebuildArray<TParent, TLink>(
        HashSet<int> parentIds,
        System.Linq.Expressions.Expression<Func<TParent, int[]>> arrayProp,
        Expression<Func<TLink, int>> linkParentId,
        Expression<Func<TLink, int>> linkChildId)
        where TParent : class
        where TLink : class
    {
        // Build the new id-set per parent from the post-save state of the join table.
        // Use the change tracker to overlay pending Added/Deleted link rows on top of
        // whatever's in the database, so the array reflects the unit of work being saved
        // (NOT the pre-save DB state) and SaveChanges only does one INSERT/UPDATE pass.
        var ids = parentIds.ToArray();
        var linkParentFn = linkParentId.Compile();
        var linkChildFn = linkChildId.Compile();

        // Start from the DB rows for these parents.
        var dbLinks = Set<TLink>().AsNoTracking()
            .Where(BuildContainsPredicate(linkParentId, ids))
            .Select(link => new { Parent = linkParentFn(link), Child = linkChildFn(link) })
            .ToList();

        var byParent = new Dictionary<int, HashSet<int>>(parentIds.Count);
        foreach (var pid in parentIds)
            byParent[pid] = new HashSet<int>();
        foreach (var row in dbLinks)
        {
            if (byParent.TryGetValue(row.Parent, out var set))
                set.Add(row.Child);
        }

        // Overlay removals before additions. Whole-collection replacement can leave
        // a Deleted old link and an Added replacement with the same composite key
        // in the tracker. The database's final state keeps that relationship, so
        // additions must win regardless of ChangeTracker enumeration order.
        var trackedLinks = ChangeTracker.Entries<TLink>()
            .Where(entry => byParent.ContainsKey(linkParentFn(entry.Entity)))
            .ToArray();
        foreach (var entry in trackedLinks.Where(entry => entry.State == EntityState.Deleted))
        {
            var pid = linkParentFn(entry.Entity);
            var set = byParent[pid];
            var cid = linkChildFn(entry.Entity);
            set.Remove(cid);
        }
        foreach (var entry in trackedLinks.Where(entry =>
                     entry.State is EntityState.Added or EntityState.Modified))
        {
            var pid = linkParentFn(entry.Entity);
            var set = byParent[pid];
            var cid = linkChildFn(entry.Entity);
            set.Add(cid);
        }

        // Locate or load each parent and assign the new array.
        var arraySetter = BuildArraySetter(arrayProp);
        var trackedParents = ChangeTracker.Entries<TParent>()
            .Where(e => e.State != EntityState.Deleted)
            .ToDictionary(e => GetEntityId(e.Entity), e => e.Entity);

        var missingParentIds = parentIds.Where(pid => !trackedParents.ContainsKey(pid)).ToArray();
        var loadedParents = missingParentIds.Length > 0
            ? Set<TParent>().Where(BuildIdContainsPredicate<TParent>(missingParentIds)).ToList()
            : new List<TParent>();

        foreach (var parent in loadedParents)
            trackedParents[GetEntityId(parent)] = parent;

        foreach (var (pid, set) in byParent)
        {
            if (!trackedParents.TryGetValue(pid, out var parent)) continue;
            // Order for stable diffs and predictable serialization.
            var newArray = set.OrderBy(x => x).ToArray();
            arraySetter(parent, newArray);
        }
    }

    private static Expression<Func<TLink, bool>> BuildContainsPredicate<TLink>(
        Expression<Func<TLink, int>> selector, int[] ids)
    {
        var param = selector.Parameters[0];
        var contains = Expression.Call(
            typeof(System.Linq.Enumerable),
            nameof(System.Linq.Enumerable.Contains),
            new[] { typeof(int) },
            Expression.Constant(ids),
            selector.Body);
        return Expression.Lambda<Func<TLink, bool>>(contains, param);
    }

    private static Expression<Func<TParent, bool>> BuildIdContainsPredicate<TParent>(int[] ids) where TParent : class
    {
        var param = Expression.Parameter(typeof(TParent), "p");
        var idProperty = Expression.Property(param, nameof(BaseEntity.Id));
        var contains = Expression.Call(
            typeof(System.Linq.Enumerable),
            nameof(System.Linq.Enumerable.Contains),
            new[] { typeof(int) },
            Expression.Constant(ids),
            idProperty);
        return Expression.Lambda<Func<TParent, bool>>(contains, param);
    }

    private static int GetEntityId(object entity)
    {
        return entity switch
        {
            BaseEntity be => be.Id,
            _ => (int)(entity.GetType().GetProperty("Id")?.GetValue(entity) ?? 0)
        };
    }

    private static Action<TParent, int[]> BuildArraySetter<TParent>(Expression<Func<TParent, int[]>> arrayProp)
    {
        var memberExpr = (MemberExpression)arrayProp.Body;
        var prop = (System.Reflection.PropertyInfo)memberExpr.Member;
        return (parent, value) => prop.SetValue(parent, value);
    }

    private void ComputeFilePaths()
    {
        // Normalize any Added/Modified Folder.Path to forward-slash form so callers can
        // compare/sort/filter on the column directly without per-row REPLACE.
        foreach (var folderEntry in ChangeTracker.Entries<Folder>())
        {
            if (folderEntry.State != EntityState.Added && folderEntry.State != EntityState.Modified)
                continue;
            var folder = folderEntry.Entity;
            if (string.IsNullOrEmpty(folder.Path)) continue;
            var normalized = folder.Path.Replace('\\', '/');
            if (!ReferenceEquals(normalized, folder.Path) && normalized != folder.Path)
                folder.Path = normalized;
        }

        // Collect Added/Modified files whose denormalized Path needs to be (re)computed.
        var fileEntries = ChangeTracker.Entries<BaseFileEntity>()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified)
            .ToList();

        if (fileEntries.Count == 0)
        {
            CascadeFolderPathChanges();
            return;
        }

        // Build folder-path lookup. Prefer the in-memory navigation; for any file whose
        // ParentFolder navigation is null, batch-load just the folder paths we need.
        var folderPaths = new Dictionary<int, string>();
        var missingFolderIds = new HashSet<int>();
        foreach (var entry in fileEntries)
        {
            var file = entry.Entity;
            if (file.ParentFolder != null)
                folderPaths[file.ParentFolderId] = file.ParentFolder.Path;
            else if (file.ParentFolderId != 0 && !folderPaths.ContainsKey(file.ParentFolderId))
                missingFolderIds.Add(file.ParentFolderId);
        }

        if (missingFolderIds.Count > 0)
        {
            var ids = missingFolderIds.ToArray();
            var loaded = Folders
                .Where(f => ids.Contains(f.Id))
                .Select(f => new { f.Id, f.Path })
                .ToList();
            foreach (var f in loaded)
                folderPaths[f.Id] = f.Path;
        }

        foreach (var entry in fileEntries)
        {
            var file = entry.Entity;
            folderPaths.TryGetValue(file.ParentFolderId, out var folderPath);
            file.Path = BaseFileEntity.ComputePath(folderPath, file.Basename);
        }

        CascadeFolderPathChanges();
    }

    private void CascadeFolderPathChanges()
    {
        // When a Folder.Path is renamed, every child file's denormalized Path needs to
        // be refreshed. We update any tracked child files; folder renames at runtime
        // are rare today and untracked children should be migrated by an explicit job
        // when that feature is added.
        var folderEntries = ChangeTracker.Entries<Folder>()
            .Where(e => e.State == EntityState.Modified
                && e.Property(nameof(Folder.Path)).IsModified)
            .ToList();
        if (folderEntries.Count == 0) return;

        foreach (var entry in folderEntries)
        {
            var folder = entry.Entity;
            foreach (var fileEntry in ChangeTracker.Entries<BaseFileEntity>())
            {
                var file = fileEntry.Entity;
                if (file.ParentFolderId != folder.Id) continue;
                file.Path = BaseFileEntity.ComputePath(folder.Path, file.Basename);
                if (fileEntry.State == EntityState.Unchanged)
                    fileEntry.State = EntityState.Modified;
            }
        }
    }

    private void UpdateTimestamps()
    {
        var entries = ChangeTracker.Entries()
            .Where(e => e.State == EntityState.Added || e.State == EntityState.Modified);
        var now = DateTime.UtcNow;

        foreach (var entry in entries)
        {
            if (entry.Entity is BaseEntity entity)
            {
                if (entry.State == EntityState.Added)
                {
                    if (entity.CreatedAt == default)
                        entity.CreatedAt = now;
                    if (entity.UpdatedAt == default)
                        entity.UpdatedAt = entity.CreatedAt;
                }
                else
                {
                    entity.UpdatedAt = now;
                }
            }
            else if (entry.Entity is BaseFileEntity file)
            {
                if (entry.State == EntityState.Added)
                {
                    if (file.CreatedAt == default)
                        file.CreatedAt = now;
                    if (file.UpdatedAt == default)
                        file.UpdatedAt = file.CreatedAt;
                }
                else
                {
                    file.UpdatedAt = now;
                }
            }
            else if (entry.Entity is Folder folder)
            {
                if (entry.State == EntityState.Added)
                {
                    if (folder.CreatedAt == default)
                        folder.CreatedAt = now;
                    if (folder.UpdatedAt == default)
                        folder.UpdatedAt = folder.CreatedAt;
                }
                else
                {
                    folder.UpdatedAt = now;
                }
            }
        }
    }
}

/// <summary>
/// Keys EF Core's model cache by context type plus <see cref="CoveContext.ModelGeneration"/>, so the
/// model is rebuilt when a data extension is installed or removed at runtime. Paired with a non-pooled
/// DbContext registration: pooled context instances pin the model they first resolved, so a generation
/// bump would never reach already-rented instances; non-pooled contexts resolve the current model per
/// scope, making runtime extension entity changes take effect without an app restart.
/// </summary>
public sealed class CoveModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime)
        => context is CoveContext coveContext
            ? (context.GetType(), coveContext.IncludeDataExtensionsInModel,
                coveContext.IncludeDataExtensionsInModel ? CoveContext.ModelGeneration : 0, designTime)
            : (context.GetType(), false, 0, designTime);
}
