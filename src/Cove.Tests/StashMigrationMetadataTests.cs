using System.Reflection;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class StashMigrationMetadataTests
{
    [Fact]
    public async Task CoveContext_PreservesExplicitTimestampsOnImportedEntities()
    {
        await using var context = CreateContext();
        var createdAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updatedAt = new DateTime(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var scene = new Scene
        {
            Title = "Imported Scene",
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
        };

        context.Scenes.Add(scene);
        await context.SaveChangesAsync();

        Assert.Equal(createdAt, scene.CreatedAt);
        Assert.Equal(updatedAt, scene.UpdatedAt);
    }

    [Fact]
    public async Task ImportPerformersAsync_ImportsPerformerTags()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Imported Tag" };
        context.Tags.Add(tag);
        await context.SaveChangesAsync();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE performers (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  disambiguation TEXT,
  gender TEXT,
  birthdate TEXT,
  ethnicity TEXT,
  country TEXT,
  eye_color TEXT,
  hair_color TEXT,
  height INTEGER,
  weight INTEGER,
  measurements TEXT,
  fake_tits TEXT,
  penis_length REAL,
  circumcised TEXT,
  career_length TEXT,
  death_date TEXT,
  tattoos TEXT,
  piercings TEXT,
  favorite INTEGER NOT NULL,
  rating INTEGER,
  details TEXT,
  ignore_auto_tag INTEGER NOT NULL,
  image_blob TEXT
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performers_tags (performer_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO performers (id, name, favorite, ignore_auto_tag) VALUES (1, 'Tagged Performer', 0, 0);
INSERT INTO performers_tags (performer_id, tag_id) VALUES (1, 7);
");

        var service = CreateService(context);
        await InvokePrivateAsync(
            service,
            "ImportPerformersAsync",
            stash,
            new Dictionary<string, string>(),
            new Dictionary<int, int> { [7] = tag.Id },
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var performer = await context.Performers.Include(p => p.PerformerTags).SingleAsync();
        Assert.Equal("Tagged Performer", performer.Name);
        Assert.Equal([tag.Id], performer.PerformerTags.Select(pt => pt.TagId).ToArray());
    }

    [Fact]
    public async Task ImportBlobsAsync_DetectsAvifContentType()
    {
        await using var context = CreateContext();
        var recordingBlobService = new RecordingBlobService();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, "CREATE TABLE blobs (checksum TEXT PRIMARY KEY, blob BLOB);");

        await using (var command = stash.CreateCommand())
        {
            command.CommandText = "INSERT INTO blobs (checksum, blob) VALUES ($checksum, $blob);";
            command.Parameters.AddWithValue("$checksum", "avif-checksum");
            command.Parameters.Add("$blob", SqliteType.Blob).Value = new byte[]
            {
                0x00, 0x00, 0x00, 0x1C,
                0x66, 0x74, 0x79, 0x70,
                0x61, 0x76, 0x69, 0x66,
                0x00, 0x00, 0x00, 0x00,
            };
            await command.ExecuteNonQueryAsync();
        }

        var service = CreateService(context, recordingBlobService);
        await InvokePrivateAsync(
            service,
            "ImportBlobsAsync",
            stash,
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        Assert.Equal(["image/avif"], recordingBlobService.ContentTypes);
    }

    [Fact]
    public async Task ImportScenesAsync_UsesSceneLastPlayedAtAndPreservesImportedFileTimestamps()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = @"C:\library", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE scenes (
  id INTEGER PRIMARY KEY,
  title TEXT,
  details TEXT,
  date TEXT,
  rating INTEGER,
  studio_id INTEGER,
  organized INTEGER NOT NULL,
  code TEXT,
  director TEXT,
  resume_time REAL NOT NULL,
  play_duration REAL NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  last_played_at TEXT
);
CREATE TABLE scenes_tags (scene_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
CREATE TABLE performers_scenes (scene_id INTEGER NOT NULL, performer_id INTEGER NOT NULL);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
CREATE TABLE scene_urls (scene_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE scenes_o_dates (scene_id INTEGER NOT NULL, o_date TEXT NOT NULL);
CREATE TABLE scenes_view_dates (scene_id INTEGER NOT NULL, view_date TEXT NOT NULL);
CREATE TABLE scenes_files (scene_id INTEGER NOT NULL, file_id INTEGER NOT NULL, [primary] INTEGER NOT NULL);
CREATE TABLE files (
  id INTEGER PRIMARY KEY,
  basename TEXT NOT NULL,
  parent_folder_id INTEGER NOT NULL,
  size INTEGER NOT NULL,
  mod_time TEXT NOT NULL,
  created_at TEXT NOT NULL
);
CREATE TABLE video_files (
  file_id INTEGER PRIMARY KEY,
  duration REAL NOT NULL,
  video_codec TEXT NOT NULL,
  format TEXT NOT NULL,
  audio_codec TEXT NOT NULL,
  width INTEGER NOT NULL,
  height INTEGER NOT NULL,
  frame_rate REAL NOT NULL,
  bit_rate INTEGER NOT NULL,
  interactive INTEGER NOT NULL,
  interactive_speed INTEGER
);
CREATE TABLE files_fingerprints (file_id INTEGER NOT NULL, type TEXT NOT NULL, fingerprint TEXT NOT NULL);
INSERT INTO scenes (id, title, organized, resume_time, play_duration, created_at, updated_at, last_played_at)
VALUES (1, 'Imported Scene', 0, 15, 45, '2024-01-01T00:00:00Z', '2024-02-01T00:00:00Z', '2024-03-01T00:00:00Z');
INSERT INTO scenes_view_dates (scene_id, view_date) VALUES (1, '2024-01-15T00:00:00Z');
INSERT INTO scenes_files (scene_id, file_id, [primary]) VALUES (1, 10, 1);
INSERT INTO files (id, basename, parent_folder_id, size, mod_time, created_at)
VALUES (10, 'clip.mp4', 99, 2048, '2024-04-01T00:00:00Z', '2024-01-05T00:00:00Z');
INSERT INTO video_files (file_id, duration, video_codec, format, audio_codec, width, height, frame_rate, bit_rate, interactive, interactive_speed)
VALUES (10, 120, 'H264', 'mp4', 'AAC', 1920, 1080, 30, 2000000, 0, NULL);
");

        var service = CreateService(context);
        await InvokePrivateAsync(
            service,
            "ImportScenesAsync",
            stash,
            new Dictionary<string, string>(),
            new Dictionary<int, int> { [99] = folder.Id },
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var scene = await context.Scenes.Include(s => s.Files).SingleAsync();
        var file = Assert.Single(scene.Files);

        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), scene.LastPlayedAt);
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), scene.CreatedAt);
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), scene.UpdatedAt);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), file.CreatedAt);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), file.UpdatedAt);
    }

        [Fact]
        public async Task ImportScenesAsync_NormalizesIntegerPhashFingerprintsToLowercaseHex()
        {
                await using var context = CreateContext();
                var folder = new Folder { Path = @"C:\library", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
                context.Folders.Add(folder);
                await context.SaveChangesAsync();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync();
                await ExecuteSqlAsync(stash, @"
CREATE TABLE scenes (
    id INTEGER PRIMARY KEY,
    title TEXT,
    details TEXT,
    date TEXT,
    rating INTEGER,
    studio_id INTEGER,
    organized INTEGER NOT NULL,
    code TEXT,
    director TEXT,
    resume_time REAL NOT NULL,
    play_duration REAL NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    last_played_at TEXT
);
CREATE TABLE scenes_tags (scene_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
CREATE TABLE performers_scenes (scene_id INTEGER NOT NULL, performer_id INTEGER NOT NULL);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
CREATE TABLE scene_urls (scene_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE scenes_o_dates (scene_id INTEGER NOT NULL, o_date TEXT NOT NULL);
CREATE TABLE scenes_view_dates (scene_id INTEGER NOT NULL, view_date TEXT NOT NULL);
CREATE TABLE scenes_files (scene_id INTEGER NOT NULL, file_id INTEGER NOT NULL, [primary] INTEGER NOT NULL);
CREATE TABLE files (
    id INTEGER PRIMARY KEY,
    basename TEXT NOT NULL,
    parent_folder_id INTEGER NOT NULL,
    size INTEGER NOT NULL,
    mod_time TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE video_files (
    file_id INTEGER PRIMARY KEY,
    duration REAL NOT NULL,
    video_codec TEXT NOT NULL,
    format TEXT NOT NULL,
    audio_codec TEXT NOT NULL,
    width INTEGER NOT NULL,
    height INTEGER NOT NULL,
    frame_rate REAL NOT NULL,
    bit_rate INTEGER NOT NULL,
    interactive INTEGER NOT NULL,
    interactive_speed INTEGER
);
CREATE TABLE files_fingerprints (file_id INTEGER NOT NULL, type TEXT NOT NULL, fingerprint);
INSERT INTO scenes (id, title, organized, resume_time, play_duration, created_at, updated_at)
VALUES (1, 'Imported Scene', 0, 0, 0, '2024-01-01T00:00:00Z', '2024-02-01T00:00:00Z');
INSERT INTO scenes_files (scene_id, file_id, [primary]) VALUES (1, 10, 1);
INSERT INTO files (id, basename, parent_folder_id, size, mod_time, created_at)
VALUES (10, 'clip.mp4', 99, 2048, '2024-04-01T00:00:00Z', '2024-01-05T00:00:00Z');
INSERT INTO video_files (file_id, duration, video_codec, format, audio_codec, width, height, frame_rate, bit_rate, interactive, interactive_speed)
VALUES (10, 120, 'H264', 'mp4', 'AAC', 1920, 1080, 30, 2000000, 0, NULL);
INSERT INTO files_fingerprints (file_id, type, fingerprint) VALUES (10, 'phash', 170);
");

                var service = CreateService(context);
                await InvokePrivateAsync(
                        service,
                        "ImportScenesAsync",
                        stash,
                        new Dictionary<string, string>(),
                        new Dictionary<int, int> { [99] = folder.Id },
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None);

                var fingerprint = await context.FileFingerprints.SingleAsync();

                Assert.Equal("phash", fingerprint.Type);
                Assert.Equal("aa", fingerprint.Value);
        }

    [Fact]
    public async Task ImportGalleriesAsync_DerivesTitleFromFolderNameWhenMissing()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = @"C:\galleries\Summer Set", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
        context.Folders.Add(folder);
        await context.SaveChangesAsync();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE folders (id INTEGER PRIMARY KEY, path TEXT NOT NULL);
CREATE TABLE galleries (
  id INTEGER PRIMARY KEY,
  folder_id INTEGER,
  title TEXT,
  date TEXT,
  details TEXT,
  studio_id INTEGER,
  rating INTEGER,
  organized INTEGER NOT NULL,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL,
  code TEXT,
  photographer TEXT
);
CREATE TABLE galleries_tags (gallery_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
CREATE TABLE performers_galleries (gallery_id INTEGER NOT NULL, performer_id INTEGER NOT NULL);
CREATE TABLE gallery_urls (gallery_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE galleries_files (gallery_id INTEGER NOT NULL, file_id INTEGER NOT NULL, [primary] INTEGER NOT NULL);
CREATE TABLE galleries_images (gallery_id INTEGER NOT NULL, image_id INTEGER NOT NULL);
CREATE TABLE galleries_chapters (gallery_id INTEGER NOT NULL, title TEXT NOT NULL, image_index INTEGER);
CREATE TABLE files (
  id INTEGER PRIMARY KEY,
  basename TEXT NOT NULL,
  parent_folder_id INTEGER NOT NULL,
  size INTEGER NOT NULL,
  mod_time TEXT NOT NULL,
  created_at TEXT NOT NULL
);
INSERT INTO folders (id, path) VALUES (50, 'C:\\galleries\\Summer Set');
INSERT INTO galleries (id, folder_id, title, organized, created_at, updated_at)
VALUES (1, 50, NULL, 0, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
");

        var service = CreateService(context);
        var result = await InvokePrivateAsync(
            service,
            "ImportGalleriesAsync",
            stash,
            new Dictionary<int, int> { [50] = folder.Id },
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

                var galleryImport = Assert.IsType<(int Count, Dictionary<int, int> GalleryFileIdMap)>(result);
                Assert.Equal(1, galleryImport.Count);
        var gallery = await context.Galleries.SingleAsync();
        Assert.Equal("Summer Set", gallery.Title);
    }

        [Fact]
        public async Task ReconcileImportedZipLinksAsync_PreservesZipFileIdsForImportedImages()
        {
                await using var context = CreateContext();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync();
                await ExecuteSqlAsync(stash, @"
CREATE TABLE folders (
    id INTEGER PRIMARY KEY,
    path TEXT NOT NULL,
    parent_folder_id INTEGER,
    zip_file_id INTEGER,
    mod_time TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE images (
    id INTEGER PRIMARY KEY,
    title TEXT,
    code TEXT,
    details TEXT,
    photographer TEXT,
    rating INTEGER,
    organized INTEGER NOT NULL,
    o_counter INTEGER NOT NULL,
    studio_id INTEGER,
    date TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE files (
    id INTEGER PRIMARY KEY,
    basename TEXT NOT NULL,
    parent_folder_id INTEGER NOT NULL,
    zip_file_id INTEGER,
    size INTEGER NOT NULL,
    mod_time TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE image_files (
    file_id INTEGER PRIMARY KEY,
    format TEXT,
    width INTEGER,
    height INTEGER
);
CREATE TABLE images_files (
    image_id INTEGER NOT NULL,
    file_id INTEGER NOT NULL,
    [primary] INTEGER NOT NULL
);
CREATE TABLE galleries (
    id INTEGER PRIMARY KEY,
    folder_id INTEGER,
    title TEXT,
    date TEXT,
    details TEXT,
    studio_id INTEGER,
    rating INTEGER,
    organized INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    code TEXT,
    photographer TEXT
);
CREATE TABLE galleries_files (
    gallery_id INTEGER NOT NULL,
    file_id INTEGER NOT NULL,
    [primary] INTEGER NOT NULL
);
INSERT INTO folders (id, path, parent_folder_id, zip_file_id, mod_time, created_at) VALUES
    (1, 'C:\\library', NULL, NULL, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
    (2, 'C:\\library\\archive.zip\\nested', 1, 10, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');
INSERT INTO images (id, title, organized, o_counter, created_at, updated_at) VALUES
    (100, 'Imported Zip Image', 0, 0, '2024-01-02T00:00:00Z', '2024-01-03T00:00:00Z');
INSERT INTO files (id, basename, parent_folder_id, zip_file_id, size, mod_time, created_at) VALUES
    (10, 'archive.zip', 1, NULL, 4096, '2024-01-04T00:00:00Z', '2024-01-04T00:00:00Z'),
    (20, 'cover.jpg', 2, 10, 1024, '2024-01-05T00:00:00Z', '2024-01-05T00:00:00Z');
INSERT INTO image_files (file_id, format, width, height) VALUES (20, 'jpeg', 800, 600);
INSERT INTO images_files (image_id, file_id, [primary]) VALUES (100, 20, 1);
INSERT INTO galleries (id, folder_id, title, organized, created_at, updated_at) VALUES
    (200, 1, 'Imported Gallery', 0, '2024-01-06T00:00:00Z', '2024-01-06T00:00:00Z');
INSERT INTO galleries_files (gallery_id, file_id, [primary]) VALUES (200, 10, 1);
");

                var service = CreateService(context);
                var folderIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
                        service,
                        "ImportFoldersAsync",
                        stash,
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None));

                var imageIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
                        service,
                        "ImportImagesAsync",
                        stash,
                        folderIdMap,
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None));

                var galleryImport = Assert.IsType<(int Count, Dictionary<int, int> GalleryFileIdMap)>(await InvokePrivateAsync(
                        service,
                        "ImportGalleriesAsync",
                        stash,
                        folderIdMap,
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        new Dictionary<int, int>(),
                        imageIdMap,
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None));

                await InvokePrivateAsync(
                        service,
                        "ReconcileImportedZipLinksAsync",
                        stash,
                        folderIdMap,
                        imageIdMap,
                        galleryImport.GalleryFileIdMap,
                        CancellationToken.None);

                var importedImageFile = await context.ImageFiles.SingleAsync();
                var importedFolder = await context.Folders.SingleAsync(folder => folder.Path.Contains("archive.zip"));
                var importedGalleryFile = await context.GalleryFiles.SingleAsync();

                Assert.Equal(importedGalleryFile.Id, importedImageFile.ZipFileId);
                Assert.Equal(importedGalleryFile.Id, importedFolder.ZipFileId);
        }

    private static StashMigrationService CreateService(CoveContext context, IBlobService? blobService = null)
    {
        var config = new CoveConfiguration();
        var configService = new ConfigService(config, NullLogger<ConfigService>.Instance);
        var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new StashMigrationService(
            context,
            blobService ?? new NullBlobService(),
            configService,
            config,
            new NullJobService(),
            scopeFactory,
            NullLogger<StashMigrationService>.Instance);
    }

    private static async Task<object?> InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(target, args) as Task;
        Assert.NotNull(task);
        await task!;
        return task!.GetType().GetProperty("Result")?.GetValue(task);
    }

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"stash-metadata-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Scene>().Ignore(scene => scene.CustomFields);
            modelBuilder.Entity<Image>().Ignore(image => image.CustomFields);
            modelBuilder.Entity<Tag>().Ignore(tag => tag.CustomFields);
            modelBuilder.Entity<Studio>().Ignore(studio => studio.CustomFields);
            modelBuilder.Entity<Performer>().Ignore(performer => performer.CustomFields);
            modelBuilder.Entity<Gallery>().Ignore(gallery => gallery.CustomFields);
            modelBuilder.Entity<Group>().Ignore(group => group.CustomFields);
        }
    }

    private sealed class NullJobService : IJobService
    {
        public bool Cancel(string jobId) => false;

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
            => throw new NotSupportedException();

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullJobProgress : IJobProgress
    {
        public static readonly NullJobProgress Instance = new();

        public void Report(double progress, string? subTask = null)
        {
        }
    }

    private sealed class NullBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) => Task.FromResult("blob-id");
        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) => Task.FromResult<(Stream, string)?>(null);
        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecordingBlobService : IBlobService
    {
        public List<string> ContentTypes { get; } = [];

        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
        {
            ContentTypes.Add(contentType);
            return Task.FromResult($"blob-{ContentTypes.Count}");
        }

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream, string)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
    }
}
