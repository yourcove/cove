using System.Reflection;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Scene = Cove.Core.Entities.Video;

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

        context.Videos.Add(scene);
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
  image_blob TEXT,
  created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
  updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
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
    public async Task ImportPerformersAsync_AllowsMissingCareerLengthColumn()
    {
        await using var context = CreateContext();

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
  death_date TEXT,
  tattoos TEXT,
  piercings TEXT,
  favorite INTEGER NOT NULL,
  rating INTEGER,
  details TEXT,
  ignore_auto_tag INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
  updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performers_tags (performer_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO performers (id, name, favorite, ignore_auto_tag) VALUES (1, 'Legacy Performer', 0, 0);
");

        var service = CreateService(context);
        await InvokePrivateAsync(
            service,
            "ImportPerformersAsync",
            stash,
            new Dictionary<string, string>(),
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var performer = await context.Performers.SingleAsync();
        Assert.Equal("Legacy Performer", performer.Name);
        Assert.Null(performer.CareerStart);
        Assert.Null(performer.CareerEnd);
    }

        [Fact]
        public async Task ImportPerformersAsync_ImportsMultiplePerformersWithUrls()
        {
                await using var context = CreateContext();

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
    image_blob TEXT,
    created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
    updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performers_tags (performer_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO performers (id, name, favorite, ignore_auto_tag) VALUES
    (1, 'First Performer', 0, 0),
    (2, 'Second Performer', 0, 0);
INSERT INTO performer_urls (performer_id, url) VALUES
    (1, 'https://performer-a.local'),
    (1, 'https://performer-a.local'),
    (2, 'https://performer-b.local');
");

                var service = CreateService(context);
                await InvokePrivateAsync(
                        service,
                        "ImportPerformersAsync",
                        stash,
                        new Dictionary<string, string>(),
                        new Dictionary<int, int>(),
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None);

                var performers = await context.Performers
                        .Include(performer => performer.Urls)
                        .OrderBy(performer => performer.Name)
                        .ToListAsync();

                Assert.Equal(2, performers.Count);
                Assert.Equal(["https://performer-a.local"], performers[0].Urls.Select(url => url.Url).ToArray());
                Assert.Equal(["https://performer-b.local"], performers[1].Urls.Select(url => url.Url).ToArray());
        }

    [Fact]
    public async Task ImportTagsStudiosPerformers_PreserveStashTimestamps()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE tags (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  sort_name TEXT,
  description TEXT,
  favorite INTEGER NOT NULL,
  ignore_auto_tag INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE tag_aliases (tag_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE studios (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  parent_id INTEGER,
  details TEXT,
  rating INTEGER,
  favorite INTEGER NOT NULL,
  ignore_auto_tag INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE studio_urls (studio_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE studio_aliases (studio_id INTEGER NOT NULL, alias TEXT NOT NULL);
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
  image_blob TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performers_tags (performer_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO tags (id, name, favorite, ignore_auto_tag, created_at, updated_at)
VALUES (1, 'Imported Tag', 0, 0, '2021-05-06T07:08:09Z', '2022-06-07T08:09:10Z');
INSERT INTO studios (id, name, favorite, ignore_auto_tag, created_at, updated_at)
VALUES (1, 'Imported Studio', 0, 0, '2021-03-04T05:06:07Z', '2022-04-05T06:07:08Z');
INSERT INTO performers (id, name, favorite, ignore_auto_tag, created_at, updated_at)
VALUES (1, 'Imported Performer', 0, 0, '2021-01-02T03:04:05Z', '2022-02-03T04:05:06Z');
");

        var service = CreateService(context);
        await InvokePrivateAsync(service, "ImportTagsAsync", stash, new Dictionary<string, string>(), NullJobProgress.Instance, 0d, 1d, CancellationToken.None);
        await InvokePrivateAsync(service, "ImportStudiosAsync", stash, new Dictionary<string, string>(), NullJobProgress.Instance, 0d, 1d, CancellationToken.None);
        await InvokePrivateAsync(service, "ImportPerformersAsync", stash, new Dictionary<string, string>(), new Dictionary<int, int>(), NullJobProgress.Instance, 0d, 1d, CancellationToken.None);

        var tag = await context.Tags.SingleAsync();
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), tag.CreatedAt);
        Assert.Equal(new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc), tag.UpdatedAt);

        var studio = await context.Studios.SingleAsync();
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), studio.CreatedAt);
        Assert.Equal(new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc), studio.UpdatedAt);

        var performer = await context.Performers.SingleAsync();
        Assert.Equal(new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc), performer.CreatedAt);
        Assert.Equal(new DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc), performer.UpdatedAt);
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
            null,
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        Assert.Equal(["image/avif"], recordingBlobService.ContentTypes);
    }

    [Fact]
    public async Task ParseStashConfig_ResolvesRelativePathsAndReadsMetadataServers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            await File.WriteAllTextAsync(configPath, """
generated: generated
blob_files_path: blobs
custom_performer_image_location: performer-images
stash:
  - path: library
stash_boxes:
  - endpoint: https://stash-box.example/graphql
    api_key: secret-key
    name: Example Box
    max_requests_per_minute: 123
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "generated")),
                GetPrivateProperty<string>(stashConfig!, "GeneratedPath"));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "blobs")),
                GetPrivateProperty<string>(stashConfig!, "BlobFilesPath"));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "performer-images")),
                GetPrivateProperty<string>(stashConfig!, "CustomPerformerImageLocation"));

            var metadataServers = Assert.IsAssignableFrom<System.Collections.IEnumerable>(GetPrivateProperty<object>(stashConfig!, "MetadataServers"));
            var metadataServer = Assert.Single(metadataServers.Cast<object>());
            Assert.Equal("https://stash-box.example/graphql", GetPrivateProperty<string>(metadataServer, "Endpoint"));
            Assert.Equal("secret-key", GetPrivateProperty<string>(metadataServer, "ApiKey"));
            Assert.Equal("Example Box", GetPrivateProperty<string>(metadataServer, "Name"));
            Assert.Equal(123, GetPrivateProperty<int>(metadataServer, "MaxRequestsPerMinute"));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ParseStashConfig_FallsBackToConfigDirWhenAbsolutePathsMissing()
    {
        // Reproduces the common Docker-migration case: config.yml records absolute paths from the
        // machine where Stash ran (e.g. "/root/.stash/blobs"), which do not exist after the user
        // mounts their Stash data into Cove — but blobs/generated sit next to config.yml.
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(Path.Combine(tempDir, "blobs"));
        Directory.CreateDirectory(Path.Combine(tempDir, "generated"));
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            await File.WriteAllTextAsync(configPath, """
generated: /root/.stash/generated
blobs_path: /root/.stash/blobs
stash:
  - path: /root/.stash/library
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "blobs")),
                GetPrivateProperty<string>(stashConfig!, "BlobFilesPath"));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "generated")),
                GetPrivateProperty<string>(stashConfig!, "GeneratedPath"));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ParseStashConfig_KeepsConfiguredBlobPathWhenNoConfigDirFallbackExists()
    {
        // When neither the configured path nor "<config_dir>/blobs" exists, keep the configured
        // value so the import's "blob files path does not exist" warning still names it.
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            await File.WriteAllTextAsync(configPath, """
blobs_path: /root/.stash/blobs
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            Assert.Equal(
                Path.GetFullPath("/root/.stash/blobs"),
                GetPrivateProperty<string>(stashConfig!, "BlobFilesPath"));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void MergeStashConfigIntoCoveConfig_ImportsMetadataServers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            File.WriteAllText(configPath, """
stash_boxes:
  - endpoint: https://stash-box.example/graphql
    api_key: secret-key
    name: Example Box
    max_requests_per_minute: 123
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            var dto = new CoveConfigDto();
            var result = InvokePrivateStatic(typeof(StashMigrationService), "MergeStashConfigIntoCoveConfig", dto, stashConfig!);
            Assert.IsType<ValueTuple<int, int, int>>(result);

            var server = Assert.Single(dto.Scraping.MetadataServers);
            Assert.Equal("https://stash-box.example/graphql", server.Endpoint);
            Assert.Equal("secret-key", server.ApiKey);
            Assert.Equal("Example Box", server.Name);
            Assert.Equal(123, server.MaxRequestsPerMinute);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ParseStashConfig_ReadsCamelCaseMetadataServerApiKey()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
                        File.WriteAllText(configPath, """
stashBoxes:
  - endpoint: https://stash-box.example/graphql
    apiKey: camel-secret
    name: Example Box
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            var metadataServers = Assert.IsAssignableFrom<System.Collections.IEnumerable>(GetPrivateProperty<object>(stashConfig!, "MetadataServers"));
            var metadataServer = Assert.Single(metadataServers.Cast<object>());
            Assert.Equal("camel-secret", GetPrivateProperty<string>(metadataServer, "ApiKey"));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public void ApplyStashPathMappings_UsesLongestSegmentAwarePrefix()
    {
        var mappings = Assert.IsAssignableFrom<IReadOnlyList<StashPathMapping>>(InvokePrivateStatic(
            typeof(StashMigrationService),
            "NormalizeStashPathMappings",
            (object)new StashPathMapping[]
            {
                new(@"C:", "/wrong"),
                new(@"C:\Content", "/media"),
            }));

        var mappedPath = Assert.IsType<string>(InvokePrivateStatic(
            typeof(StashMigrationService),
            "ApplyStashPathMappings",
            @"C:\Content\Nested\clip.mp4",
            mappings));
        var siblingPath = Assert.IsType<string>(InvokePrivateStatic(
            typeof(StashMigrationService),
            "ApplyStashPathMappings",
            @"C:\Content2\clip.mp4",
            mappings));

        Assert.Equal("/media/Nested/clip.mp4", mappedPath);
        Assert.Equal("/wrong/Content2/clip.mp4", siblingPath);
    }

    [Fact]
    public void ApplyStashConfigPathMappings_MapsWindowsGeneratedPathOnDockerHost()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"stash-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var configPath = Path.Combine(tempDir, "config.yml");

        try
        {
            File.WriteAllText(configPath, """
generated: E:/test/Content/Stash-PornServer
stash:
  - path: E:/test/Content
""");

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            var mappings = Assert.IsAssignableFrom<IReadOnlyList<StashPathMapping>>(InvokePrivateStatic(
                typeof(StashMigrationService),
                "NormalizeStashPathMappings",
                (object)new StashPathMapping[]
                {
                    new(@"C:\Coding\Testing\Stash-PornServer", "/stash"),
                    new("E:/test/Content", "/media"),
                }));

            var mappedConfig = InvokePrivateStatic(typeof(StashMigrationService), "ApplyStashConfigPathMappings", stashConfig!, mappings);
            Assert.NotNull(mappedConfig);
            Assert.Equal("/media/Stash-PornServer", GetPrivateProperty<string>(mappedConfig!, "GeneratedPath"));
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
    }

    [Fact]
    public async Task ImportPerformersAsync_UsesCustomPerformerImageLocationFallback()
    {
        await using var context = CreateContext();
        var recordingBlobService = new RecordingBlobService();

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
  image_blob TEXT,
  created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
  updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performers_tags (performer_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO performers (id, name, favorite, ignore_auto_tag, image_blob) VALUES (1, 'Fallback Performer', 0, 0, NULL);
");

        var tempDir = Path.Combine(Path.GetTempPath(), $"performer-fallback-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var imagePath = Path.Combine(tempDir, "fallback.png");
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        try
        {
            var service = CreateService(context, recordingBlobService);
            SetPrivateField(service, "_currentImportCustomPerformerImageLocation", tempDir);
            await InvokePrivateAsync(
                service,
                "ImportPerformersAsync",
                stash,
                new Dictionary<string, string>(),
                new Dictionary<int, int>(),
                NullJobProgress.Instance,
                0d,
                1d,
                CancellationToken.None);

            var performer = await context.Performers.SingleAsync();
            Assert.Equal("blob-1", performer.ImageBlobId);
            Assert.Equal(["image/png"], recordingBlobService.ContentTypes);
        }
        finally
        {
            TryDeleteDirectory(tempDir);
        }
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

        var scene = await context.Videos.Include(s => s.Files).SingleAsync();
        var file = Assert.Single(scene.Files);
        var affinity = await context.UserEntityAffinities.SingleAsync(item => item.HostType == AffinityHostType.Video && item.HostId == scene.Id);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), affinity.LastConsumedAt);
        Assert.Equal(1, affinity.ViewCount);
        Assert.Equal(15, affinity.LastPositionSec);
        Assert.Equal(45, affinity.TotalConsumedSec);
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
    public async Task ImportFoldersAsync_ReusesExistingFoldersWhenSlashDirectionDiffers()
    {
        await using var context = CreateContext();
        var existingFolder = new Folder
        {
            Path = "C:/library",
            ModTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        context.Folders.Add(existingFolder);
        await context.SaveChangesAsync();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE folders (
  id INTEGER PRIMARY KEY,
  path TEXT NOT NULL,
  parent_folder_id INTEGER,
  mod_time TEXT NOT NULL,
  created_at TEXT NOT NULL
);
INSERT INTO folders (id, path, parent_folder_id, mod_time, created_at) VALUES
    (1, 'C:\library', NULL, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
    (2, 'C:\library\clips', 1, '2024-01-02T00:00:00Z', '2024-01-02T00:00:00Z');
");

        var service = CreateService(context);
        var folderIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportFoldersAsync",
            stash,
            Array.Empty<StashPathMapping>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        Assert.Equal(existingFolder.Id, folderIdMap[1]);
        Assert.Equal(2, await context.Folders.CountAsync());
        Assert.Equal(1, await context.Folders.CountAsync(folder => folder.Path == "C:/library"));

        var importedChild = await context.Folders.SingleAsync(folder => folder.Id == folderIdMap[2]);
        Assert.Equal("C:/library/clips", importedChild.Path);
        Assert.Equal(existingFolder.Id, importedChild.ParentFolderId);
    }

    [Fact]
    public async Task ImportStudiosAsync_ImportsMultipleRemoteIdsForSingleStudio()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE studios (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  parent_id INTEGER,
  details TEXT,
  rating INTEGER,
  favorite INTEGER NOT NULL,
  ignore_auto_tag INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
  updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
);
CREATE TABLE studio_urls (studio_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE studio_aliases (studio_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE studio_stash_ids (studio_id INTEGER NOT NULL, endpoint TEXT NOT NULL, stash_id TEXT NOT NULL);
INSERT INTO studios (id, name, favorite, ignore_auto_tag) VALUES (1, 'Imported Studio', 0, 0);
INSERT INTO studio_stash_ids (studio_id, endpoint, stash_id) VALUES
  (1, 'https://stash-a.local', '101'),
    (1, 'https://stash-a.local', '101'),
  (1, 'https://stash-b.local', '202');
");

        var service = CreateService(context);
        var studioIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportStudiosAsync",
            stash,
            new Dictionary<string, string>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        var importedStudio = await context.Studios
            .Include(studio => studio.RemoteIds)
            .SingleAsync(studio => studio.Id == studioIdMap[1]);

        Assert.Equal("Imported Studio", importedStudio.Name);
        Assert.Equal(2, importedStudio.RemoteIds.Count);
        Assert.Contains(importedStudio.RemoteIds, remoteId => remoteId.Endpoint == "https://stash-a.local" && remoteId.RemoteId == "101");
        Assert.Contains(importedStudio.RemoteIds, remoteId => remoteId.Endpoint == "https://stash-b.local" && remoteId.RemoteId == "202");
    }

    [Fact]
    public async Task ImportGroupsAsync_ImportsFrontAndBackImageBlobs()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE groups (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  aliases TEXT,
  duration INTEGER,
  date TEXT,
  rating INTEGER,
  studio_id INTEGER,
  director TEXT,
  description TEXT,
  front_image_blob TEXT,
  back_image_blob TEXT
);
CREATE TABLE group_urls (group_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
INSERT INTO groups (id, name, front_image_blob, back_image_blob) VALUES (1, 'Imported Group', 'front-blob', 'back-blob');
");

        var service = CreateService(context);
        var groupIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportGroupsAsync",
            stash,
            new Dictionary<string, string>
            {
                ["front-blob"] = "cove-front",
                ["back-blob"] = "cove-back",
            },
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        var importedGroup = await context.Groups.SingleAsync(group => group.Id == groupIdMap[1]);

        Assert.Equal("Imported Group", importedGroup.Name);
        Assert.Equal("cove-front", importedGroup.FrontImageBlobId);
        Assert.Equal("cove-back", importedGroup.BackImageBlobId);
    }

    [Fact]
    public async Task ImportGroupsAsync_MergesDuplicateCoverOnlyGroupIntoSceneLinkedGroup()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE groups (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  aliases TEXT,
  duration INTEGER,
  date TEXT,
  rating INTEGER,
  studio_id INTEGER,
  director TEXT,
  description TEXT,
  front_image_blob TEXT,
  back_image_blob TEXT
);
CREATE TABLE group_urls (group_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
INSERT INTO groups (id, name, aliases, duration, date, rating, studio_id, director, description, front_image_blob, back_image_blob) VALUES
  (1, 'Imported Group', 'Alias', 120, '2024-01-02', 80, NULL, 'Director', 'Details', NULL, NULL),
  (2, 'Imported Group', 'Alias', 120, '2024-01-02', 80, NULL, 'Director', 'Details', 'front-blob', 'back-blob');
INSERT INTO group_urls (group_id, url, position) VALUES
  (1, 'https://example.test/video-group', 0),
  (2, 'https://example.test/cover-group', 0);
INSERT INTO groups_scenes (scene_id, group_id, scene_index) VALUES (10, 1, 1);
");

        var service = CreateService(context);
        var groupIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportGroupsAsync",
            stash,
            new Dictionary<string, string>
            {
                ["front-blob"] = "cove-front",
                ["back-blob"] = "cove-back",
            },
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        Assert.Equal(groupIdMap[1], groupIdMap[2]);

        var importedGroup = await context.Groups.Include(group => group.Urls).SingleAsync();
        Assert.Equal(groupIdMap[1], importedGroup.Id);
        Assert.Equal("Imported Group", importedGroup.Name);
        Assert.Equal("cove-front", importedGroup.FrontImageBlobId);
        Assert.Equal("cove-back", importedGroup.BackImageBlobId);
        Assert.Equal(
            ["https://example.test/cover-group", "https://example.test/video-group"],
            importedGroup.Urls.Select(url => url.Url).OrderBy(url => url, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ImportGroupsAsync_MergesDuplicateCoverOnlyGroupWhenHiddenMetadataDiffers()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync();
        await ExecuteSqlAsync(stash, @"
CREATE TABLE groups (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  aliases TEXT,
  duration INTEGER,
  date TEXT,
  rating INTEGER,
  studio_id INTEGER,
  director TEXT,
  description TEXT,
  front_image_blob TEXT,
  back_image_blob TEXT
);
CREATE TABLE group_urls (group_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
INSERT INTO groups (id, name, aliases, duration, date, rating, studio_id, director, description, front_image_blob, back_image_blob) VALUES
  (1, 'Imported Group', NULL, NULL, '2024-01-02', NULL, NULL, NULL, NULL, NULL, NULL),
  (2, 'Imported Group', 'Alias', 120, '2024-01-02', 80, NULL, 'Director', 'Details', 'front-blob', NULL);
INSERT INTO groups_scenes (scene_id, group_id, scene_index) VALUES (10, 1, 1);
");

        var service = CreateService(context);
        var groupIdMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportGroupsAsync",
            stash,
            new Dictionary<string, string>
            {
                ["front-blob"] = "cove-front",
            },
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        Assert.Equal(groupIdMap[1], groupIdMap[2]);

        var importedGroup = await context.Groups.SingleAsync();
        Assert.Equal("Imported Group", importedGroup.Name);
        Assert.Equal("Alias", importedGroup.Aliases);
        Assert.Equal(120, importedGroup.Duration);
        Assert.Equal("Director", importedGroup.Director);
        Assert.Equal("Details", importedGroup.Synopsis);
        Assert.Equal("cove-front", importedGroup.FrontImageBlobId);
    }

        [Fact]
        public async Task ReconcileImportedZipLinksAsync_PreservesZipFileIdsForImportedImages()
        {
                await using var context = CreateContext();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync();
                var legacyLikeCounterColumn = "o" + "_counter";
                await ExecuteSqlAsync(stash, $@"
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
    {legacyLikeCounterColumn} INTEGER NOT NULL,
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
INSERT INTO images (id, title, organized, {legacyLikeCounterColumn}, created_at, updated_at) VALUES
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
                        Array.Empty<StashPathMapping>(),
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

        [Fact]
        public async Task ImportSceneMarkerSegmentsAsync_ImportsMarkersAsUserSegments()
        {
                await using var context = CreateContext();
                var scene = new Scene { Title = "Imported Scene" };
                var primaryTag = new Tag { Name = "Favorite" };
                var secondaryTag = new Tag { Name = "Extra" };
                context.AddRange(scene, primaryTag, secondaryTag);
                await context.SaveChangesAsync();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync();
                await ExecuteSqlAsync(stash, @"
CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE scene_markers (
    id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    seconds REAL NOT NULL,
    end_seconds REAL,
    primary_tag_id INTEGER NOT NULL,
    scene_id INTEGER,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE scene_markers_tags (scene_marker_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
INSERT INTO tags (id, name) VALUES (7, 'Favorite'), (9, 'Extra');
INSERT INTO scene_markers (id, title, seconds, end_seconds, primary_tag_id, scene_id, created_at, updated_at)
VALUES (1, 'Imported marker', 12.5, 18.0, 7, 3, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
INSERT INTO scene_markers_tags (scene_marker_id, tag_id) VALUES (1, 9);
");

                var service = CreateService(context);
                var imported = (int)(await InvokePrivateAsync(
                        service,
                        "ImportSceneMarkerSegmentsAsync",
                        stash,
                        new Dictionary<int, int> { [3] = scene.Id },
                        new Dictionary<int, int> { [7] = primaryTag.Id, [9] = secondaryTag.Id },
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None))!;

                Assert.Equal(1, imported);

                var segment = await context.Segments.SingleAsync();
                Assert.Equal(SegmentHostType.Video, segment.HostType);
                Assert.Equal(scene.Id, segment.HostId);
                Assert.Equal(12.5, segment.StartSec);
                Assert.Equal(18.0, segment.EndSec);
                Assert.Equal(primaryTag.Id, segment.TagId);
                Assert.Equal("tag", segment.Kind);
                Assert.Equal("user", segment.SourceKey);
                Assert.Equal(1L, segment.RefId);
                Assert.Equal("Imported marker", segment.Title);
                Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), segment.CreatedAt);
                Assert.Equal(new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc), segment.UpdatedAt);
                Assert.NotNull(segment.Payload);
                var secondaryTagIds = segment.Payload!.RootElement.GetProperty("secondaryTagIds");
                Assert.Equal(1, secondaryTagIds.GetArrayLength());
                Assert.Equal(secondaryTag.Id, secondaryTagIds[0].GetInt32());
        }

        [Fact]
        public async Task ImportSceneMarkerSegmentsAsync_SkipsLegacyAiMarkersAndDescendants()
        {
                await using var context = CreateContext();
                var scene = new Scene { Title = "Imported Scene" };
                var aiTag = new Tag { Name = "AI" };
                var aiChildTag = new Tag { Name = "AI Child" };
                var manualTag = new Tag { Name = "Manual" };
                context.AddRange(scene, aiTag, aiChildTag, manualTag);
                await context.SaveChangesAsync();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync();
                await ExecuteSqlAsync(stash, @"
CREATE TABLE tags (id INTEGER PRIMARY KEY, name TEXT NOT NULL);
CREATE TABLE tags_relations (parent_id INTEGER NOT NULL, child_id INTEGER NOT NULL);
CREATE TABLE scene_markers (
    id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    seconds REAL NOT NULL,
    end_seconds REAL,
    primary_tag_id INTEGER NOT NULL,
    scene_id INTEGER,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
INSERT INTO tags (id, name) VALUES (1, 'AI'), (2, 'AI Child'), (3, 'Manual');
INSERT INTO tags_relations (parent_id, child_id) VALUES (1, 2);
INSERT INTO scene_markers (id, title, seconds, end_seconds, primary_tag_id, scene_id, created_at, updated_at)
VALUES
    (1, 'AI root marker', 1.0, NULL, 1, 3, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
    (2, 'AI child marker', 2.0, NULL, 2, 3, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
    (3, 'Manual marker', 3.0, 5.0, 3, 3, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');
");

                var service = CreateService(context);
                var imported = (int)(await InvokePrivateAsync(
                        service,
                        "ImportSceneMarkerSegmentsAsync",
                        stash,
                        new Dictionary<int, int> { [3] = scene.Id },
                        new Dictionary<int, int> { [1] = aiTag.Id, [2] = aiChildTag.Id, [3] = manualTag.Id },
                        NullJobProgress.Instance,
                        0d,
                        1d,
                        CancellationToken.None))!;

                Assert.Equal(1, imported);

                var segments = await context.Segments.ToListAsync();
                var segment = Assert.Single(segments);
                Assert.Equal(manualTag.Id, segment.TagId);
                Assert.Equal(3L, segment.RefId);
                Assert.Equal("Manual marker", segment.Title);
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

    private static object? InvokePrivateStatic(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return method!.Invoke(null, args);
    }

    private static T GetPrivateProperty<T>(object target, string propertyName)
    {
        var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property!.GetValue(target));
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static async Task ExecuteSqlAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> CreateSqliteDatabaseAsync(string sql, string prefix = "cove-test-db")
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}.sqlite");
        await using var connection = new SqliteConnection($"Data Source={dbPath}");
        await connection.OpenAsync();
        await ExecuteSqlAsync(connection, sql);
        return dbPath;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"stash-metadata-{Guid.NewGuid():N}")
            .Options;

        var context = new TestCoveContext(options);
        context.Users.Add(new User
        {
            Username = "owner",
            PasswordHash = "test",
            PasswordAlgo = "test",
            IsSystem = true,
            IsActive = true,
        });
        context.SaveChanges();
        return context;
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
            modelBuilder.Entity<Face>().Ignore(face => face.CustomFields);
        }
    }

    private sealed class NullJobService : IJobService
    {
        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

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
