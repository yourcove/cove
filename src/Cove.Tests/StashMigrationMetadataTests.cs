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
    public async Task ImportAsync_RejectsImportWhenNoEngagementOwnerExists()
    {
        await using var context = CreateContext(includeOwner: false);
        var dbPath = await CreateSqliteDatabaseAsync("", "cove-ownerless-stash-migration");

        try
        {
            var service = CreateService(context);

            var exception = await Assert.ThrowsAsync<StashMigrationOwnerRequiredException>(
                () => service.ImportAsync(dbPath, new StashImportOptions(CoveGeneratedPath: null, MigrateGeneratedContent: false), TestContext.Current.CancellationToken));

            Assert.Contains("Owner", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task StartImportAsync_RejectsImportWhenNoEngagementOwnerExists()
    {
        await using var context = CreateContext(includeOwner: false);
        var service = CreateService(context);

        var exception = await Assert.ThrowsAsync<StashMigrationOwnerRequiredException>(
            () => service.StartImportAsync("/path/that-must-not-be-queued.sqlite", new StashImportOptions(CoveGeneratedPath: null, MigrateGeneratedContent: false), TestContext.Current.CancellationToken));

        Assert.Contains("Owner", exception.Message, StringComparison.Ordinal);
    }

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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(createdAt, scene.CreatedAt);
        Assert.Equal(updatedAt, scene.UpdatedAt);
    }

    [Fact]
    public async Task ImportPerformersAsync_ImportsPerformerTags()
    {
        await using var context = CreateContext();
        var tag = new Tag { Name = "Imported Tag" };
        context.Tags.Add(tag);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var performer = await context.Performers.Include(p => p.PerformerTags).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Tagged Performer", performer.Name);
        Assert.Equal([tag.Id], performer.PerformerTags.Select(pt => pt.TagId).ToArray());
    }

    [Fact]
    public async Task ImportPerformersAsync_AllowsMissingCareerLengthColumn()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var performer = await context.Performers.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Legacy Performer", performer.Name);
        Assert.Null(performer.CareerStart);
        Assert.Null(performer.CareerEnd);
    }

    [Fact]
    public async Task ImportPerformersAsync_ImportsCurrentCareerDateColumns()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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
  career_start TEXT,
  career_end TEXT,
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
INSERT INTO performers (id, name, career_length, career_start, career_end, favorite, ignore_auto_tag) VALUES
  (1, 'Current Performer', '1999-2001', '2008-01-01', '2020-01-01', 0, 0),
  (2, 'Partial Current Performer', '1995-2005', NULL, '2010-01-01', 0, 0);
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

        var performers = await context.Performers.ToDictionaryAsync(performer => performer.Name, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new DateOnly(2008, 1, 1), performers["Current Performer"].CareerStart);
        Assert.Equal(new DateOnly(2020, 1, 1), performers["Current Performer"].CareerEnd);
        Assert.Equal(new DateOnly(1995, 1, 1), performers["Partial Current Performer"].CareerStart);
        Assert.Equal(new DateOnly(2010, 1, 1), performers["Partial Current Performer"].CareerEnd);
    }

        [Fact]
        public async Task ImportPerformersAsync_ImportsMultiplePerformersWithUrls()
        {
                await using var context = CreateContext();

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync(TestContext.Current.CancellationToken);
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
                        .ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal(2, performers.Count);
                Assert.Equal(["https://performer-a.local"], performers[0].Urls.Select(url => url.Url).ToArray());
                Assert.Equal(["https://performer-b.local"], performers[1].Urls.Select(url => url.Url).ToArray());
        }

    [Fact]
    public async Task ImportTagsStudiosPerformers_PreserveStashTimestamps()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var tag = await context.Tags.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new DateTime(2021, 5, 6, 7, 8, 9, DateTimeKind.Utc), tag.CreatedAt);
        Assert.Equal(new DateTime(2022, 6, 7, 8, 9, 10, DateTimeKind.Utc), tag.UpdatedAt);

        var studio = await context.Studios.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new DateTime(2021, 3, 4, 5, 6, 7, DateTimeKind.Utc), studio.CreatedAt);
        Assert.Equal(new DateTime(2022, 4, 5, 6, 7, 8, DateTimeKind.Utc), studio.UpdatedAt);

        var performer = await context.Performers.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new DateTime(2021, 1, 2, 3, 4, 5, DateTimeKind.Utc), performer.CreatedAt);
        Assert.Equal(new DateTime(2022, 2, 3, 4, 5, 6, DateTimeKind.Utc), performer.UpdatedAt);
    }

    [Fact]
    public async Task ImportTagsAsync_UsesTheSharedNamespaceForAliasesCaseAndWhitespace()
    {
        await using var context = CreateContext();
        var existing = new Tag
        {
            Name = "Canonical",
            Aliases = [new TagAlias { Alias = "Alternate" }],
        };
        context.Tags.Add(existing);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, """
CREATE TABLE tags (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  sort_name TEXT,
  description TEXT,
  favorite INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL,
  updated_at TEXT NOT NULL
);
CREATE TABLE tag_aliases (tag_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE tag_stash_ids (tag_id INTEGER NOT NULL, endpoint TEXT NOT NULL, stash_id TEXT NOT NULL);
INSERT INTO tags (id, name, sort_name, description, favorite, image_blob, created_at, updated_at) VALUES
  (1, ' alternate ', 'Existing import sort', 'Existing import description', 1, 'existing-import-image', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
  (2, ' New ', NULL, NULL, 0, NULL, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z'),
  (3, 'new', 'New import sort', 'New import description', 1, 'new-import-image', '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');
INSERT INTO tag_aliases (tag_id, alias) VALUES
  (1, 'Existing imported alias'),
  (3, 'New imported alias');
INSERT INTO tag_stash_ids (tag_id, endpoint, stash_id) VALUES
  (1, 'fixture', 'existing-remote'),
  (3, 'fixture', 'new-remote');
""");

        var service = CreateService(context);
        var idMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportTagsAsync",
            stash,
            new Dictionary<string, string>
            {
                ["existing-import-image"] = "existing-import-artwork",
                ["new-import-image"] = "new-import-artwork",
            },
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));

        Assert.Equal(existing.Id, idMap[1]);
        Assert.Equal(idMap[2], idMap[3]);
        context.ChangeTracker.Clear();
        Assert.Equal(2, await context.Tags.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        var mergedExisting = await context.Tags
            .Include(tag => tag.Aliases)
            .Include(tag => tag.RemoteIds)
            .SingleAsync(tag => tag.Id == existing.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Existing import sort", mergedExisting.SortName);
        Assert.Equal("Existing import description", mergedExisting.Description);
        Assert.True(mergedExisting.Favorite);
        Assert.Equal("existing-import-artwork", mergedExisting.ImageBlobId);
        Assert.Contains(mergedExisting.Aliases, alias => alias.Alias == "Existing imported alias");
        Assert.Contains(mergedExisting.RemoteIds, remote => remote.Endpoint == "fixture" && remote.RemoteId == "existing-remote");

        var imported = await context.Tags
            .Include(tag => tag.Aliases)
            .Include(tag => tag.RemoteIds)
            .SingleAsync(tag => tag.Id == idMap[2], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("New", imported.Name);
        Assert.Equal("New import sort", imported.SortName);
        Assert.Equal("New import description", imported.Description);
        Assert.True(imported.Favorite);
        Assert.Equal("new-import-artwork", imported.ImageBlobId);
        Assert.Contains(imported.Aliases, alias => alias.Alias == "New imported alias");
        Assert.Contains(imported.RemoteIds, remote => remote.Endpoint == "fixture" && remote.RemoteId == "new-remote");
    }

    [Fact]
    public async Task ImportBlobsAsync_DetectsAvifContentType()
    {
        await using var context = CreateContext();
        var recordingBlobService = new RecordingBlobService();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, """
CREATE TABLE blobs (checksum TEXT PRIMARY KEY, blob BLOB);
CREATE TABLE performers (id INTEGER PRIMARY KEY, image_blob TEXT);
INSERT INTO performers (id, image_blob) VALUES (1, 'avif-checksum');
""");

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
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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
    public async Task ImportBlobsAsync_SkipsRowsNotReferencedByImportedEntities()
    {
        await using var context = CreateContext();
        var recordingBlobService = new RecordingBlobService();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, """
CREATE TABLE blobs (checksum TEXT PRIMARY KEY, blob BLOB);
CREATE TABLE performers (id INTEGER PRIMARY KEY, image_blob TEXT);
CREATE TABLE scenes (id INTEGER PRIMARY KEY, cover_blob TEXT);
INSERT INTO blobs (checksum, blob) VALUES
    ('used-image', X'89504E47'),
    ('unreferenced-blob', X'89504E47'),
    ('scene-cover', X'89504E47');
INSERT INTO performers (id, image_blob) VALUES (1, 'used-image');
INSERT INTO scenes (id, cover_blob) VALUES (1, 'scene-cover');
""");

        var service = CreateService(context, recordingBlobService);
        var result = await InvokePrivateAsync(
            service,
            "ImportBlobsAsync",
            stash,
            null,
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var imported = Assert.IsType<Dictionary<string, string>>(result);
        Assert.Equal(["scene-cover", "used-image"], imported.Keys.Order());
        Assert.Equal(2, recordingBlobService.ContentTypes.Count);
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
""", TestContext.Current.CancellationToken);

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
""", TestContext.Current.CancellationToken);

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
""", TestContext.Current.CancellationToken);

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
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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
        await File.WriteAllBytesAsync(imagePath, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], TestContext.Current.CancellationToken);

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

            var performer = await context.Performers.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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
  last_played_at TEXT,
  cover_blob TEXT
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
CREATE TABLE video_captions (
  file_id INTEGER NOT NULL,
  language_code TEXT NOT NULL,
  filename TEXT NOT NULL,
  caption_type TEXT NOT NULL
);
CREATE TABLE files_fingerprints (file_id INTEGER NOT NULL, type TEXT NOT NULL, fingerprint TEXT NOT NULL);
INSERT INTO scenes (id, title, organized, resume_time, play_duration, created_at, updated_at, last_played_at, cover_blob)
VALUES (1, 'Imported Scene', 0, 15, 45, '2024-01-01T00:00:00Z', '2024-02-01T00:00:00Z', '2024-03-01T00:00:00Z', 'scene-cover');
INSERT INTO scenes_view_dates (scene_id, view_date) VALUES (1, '2024-01-15T00:00:00Z');
INSERT INTO scenes_o_dates (scene_id, o_date) VALUES
  (1, '2024-01-20T12:30:00Z'),
  (1, '2024-02-10T08:15:00Z');
INSERT INTO scenes_files (scene_id, file_id, [primary]) VALUES (1, 10, 1);
INSERT INTO files (id, basename, parent_folder_id, size, mod_time, created_at)
VALUES (10, 'clip.mp4', 99, 2048, '2024-04-01T00:00:00Z', '2024-01-05T00:00:00Z');
INSERT INTO video_files (file_id, duration, video_codec, format, audio_codec, width, height, frame_rate, bit_rate, interactive, interactive_speed)
VALUES (10, 120, 'H264', 'mp4', 'AAC', 1920, 1080, 30, 2000000, 0, NULL);
INSERT INTO video_captions (file_id, language_code, filename, caption_type) VALUES
  (10, 'en', 'clip.en.vtt', 'vtt'),
  (10, 'es', 'clip.es.srt', 'srt');
");

        var service = CreateService(context);
        await InvokePrivateAsync(
            service,
            "ImportScenesAsync",
            stash,
            new Dictionary<string, string> { ["scene-cover"] = "cove-scene-cover" },
            new Dictionary<int, int> { [99] = folder.Id },
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var scene = await context.Videos.Include(s => s.Files).ThenInclude(file => file.Captions).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var file = Assert.Single(scene.Files);
        Assert.Equal(
            [("en", "clip.en.vtt", "vtt"), ("es", "clip.es.srt", "srt")],
            file.Captions.OrderBy(caption => caption.LanguageCode)
                .Select(caption => (caption.LanguageCode, caption.Filename, caption.CaptionType))
                .ToArray());
        var affinity = await context.UserEntityAffinities.SingleAsync(item => item.HostType == AffinityHostType.Video && item.HostId == scene.Id, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), affinity.LastConsumedAt);
        Assert.Equal(1, affinity.ViewCount);
        Assert.Equal(2, affinity.LikeCount);
        Assert.Equal(15, affinity.LastPositionSec);
        Assert.Equal(45, affinity.TotalConsumedSec);
        Assert.Equal(
            [
                new DateTime(2024, 1, 20, 12, 30, 0, DateTimeKind.Utc),
                new DateTime(2024, 2, 10, 8, 15, 0, DateTimeKind.Utc),
            ],
            await context.Interactions
                .Where(item => item.UserId == affinity.UserId
                    && item.HostType == InteractionHostType.Video
                    && item.HostId == scene.Id
                    && item.Kind == InteractionKind.LikeCount)
                .OrderBy(item => item.At)
                .Select(item => item.At)
                .ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), scene.CreatedAt);
        Assert.Equal(new DateTime(2024, 2, 1, 0, 0, 0, DateTimeKind.Utc), scene.UpdatedAt);
        Assert.Equal("cove-scene-cover", scene.ImageBlobId);
        Assert.Equal(new DateTime(2024, 1, 5, 0, 0, 0, DateTimeKind.Utc), file.CreatedAt);
        Assert.Equal(new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), file.UpdatedAt);
    }

    [Fact]
    public async Task ImportScenesAsync_ImportsCaptionsForMatchingPersistedVideoFiles()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = @"C:\library", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
        var existingVideo = new Scene
        {
            Title = "Existing video",
            Files =
            [
                new VideoFile
                {
                    Basename = "sample-captioned-video.mp4",
                    ParentFolder = folder,
                    Format = "mp4",
                    VideoCodec = "H264",
                    AudioCodec = "AAC",
                },
            ],
        };
        context.Videos.Add(existingVideo);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, @"
CREATE TABLE scenes (
  id INTEGER PRIMARY KEY, title TEXT, details TEXT, date TEXT, rating INTEGER, studio_id INTEGER,
  organized INTEGER NOT NULL, code TEXT, director TEXT, resume_time REAL NOT NULL,
  play_duration REAL NOT NULL, created_at TEXT NOT NULL, updated_at TEXT NOT NULL
);
CREATE TABLE scenes_tags (scene_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
CREATE TABLE performers_scenes (scene_id INTEGER NOT NULL, performer_id INTEGER NOT NULL);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
CREATE TABLE scene_urls (scene_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE scenes_o_dates (scene_id INTEGER NOT NULL, o_date TEXT NOT NULL);
CREATE TABLE scenes_view_dates (scene_id INTEGER NOT NULL, view_date TEXT NOT NULL);
CREATE TABLE scenes_files (scene_id INTEGER NOT NULL, file_id INTEGER NOT NULL, [primary] INTEGER NOT NULL);
CREATE TABLE files (
  id INTEGER PRIMARY KEY, basename TEXT NOT NULL, parent_folder_id INTEGER NOT NULL,
  size INTEGER NOT NULL, mod_time TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE video_files (
  file_id INTEGER PRIMARY KEY, duration REAL NOT NULL, video_codec TEXT NOT NULL,
  format TEXT NOT NULL, audio_codec TEXT NOT NULL, width INTEGER NOT NULL, height INTEGER NOT NULL,
  frame_rate REAL NOT NULL, bit_rate INTEGER NOT NULL, interactive INTEGER NOT NULL,
  interactive_speed INTEGER
);
CREATE TABLE video_captions (
  file_id INTEGER NOT NULL, language_code TEXT NOT NULL, filename TEXT NOT NULL,
  caption_type TEXT NOT NULL
);
CREATE TABLE files_fingerprints (file_id INTEGER NOT NULL, type TEXT NOT NULL, fingerprint TEXT NOT NULL);
INSERT INTO scenes (id, title, organized, resume_time, play_duration, created_at, updated_at)
VALUES (1, 'Captioned Video', 0, 0, 0, '2024-01-01T00:00:00Z', '2024-01-01T00:00:00Z');
INSERT INTO scenes_files (scene_id, file_id, [primary]) VALUES (1, 10, 1);
INSERT INTO files (id, basename, parent_folder_id, size, mod_time, created_at)
VALUES (10, 'sample-captioned-video.mp4', 99, 2048, '2024-04-01T00:00:00Z', '2024-01-05T00:00:00Z');
INSERT INTO video_files (file_id, duration, video_codec, format, audio_codec, width, height, frame_rate, bit_rate, interactive, interactive_speed)
VALUES (10, 120, 'H264', 'mp4', 'AAC', 1920, 1080, 30, 2000000, 0, NULL);
INSERT INTO video_captions (file_id, language_code, filename, caption_type)
VALUES (10, '00', 'sample-captioned-video.srt', 'srt');
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

        var file = await context.Set<VideoFile>().Include(item => item.Captions).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        var caption = Assert.Single(file.Captions);
        Assert.Equal("00", caption.LanguageCode);
        Assert.Equal("sample-captioned-video.srt", caption.Filename);
        Assert.Equal("srt", caption.CaptionType);
    }

        [Fact]
        public async Task ImportScenesAsync_NormalizesIntegerPhashFingerprintsToLowercaseHex()
        {
                await using var context = CreateContext();
                var folder = new Folder { Path = @"C:\library", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
                context.Folders.Add(folder);
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync(TestContext.Current.CancellationToken);
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

                var fingerprint = await context.FileFingerprints.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

                Assert.Equal("phash", fingerprint.Type);
                Assert.Equal("aa", fingerprint.Value);
        }

    [Fact]
    public async Task ImportGalleriesAsync_DerivesTitleFromFolderNameWhenMissing()
    {
        await using var context = CreateContext();
        var folder = new Folder { Path = @"C:\galleries\Summer Set", ModTime = new DateTime(2024, 1, 4, 0, 0, 0, DateTimeKind.Utc) };
        context.Folders.Add(folder);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

                var galleryImport = Assert.IsType<(int Count, Dictionary<int, int> GalleryFileIdMap, Dictionary<int, int> GalleryIdMap)>(result);
                Assert.Equal(1, galleryImport.Count);
        var gallery = await context.Galleries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("Summer Set", gallery.Title);
    }

    [Fact]
    public async Task ImportGalleriesAsync_ImportsSelectedCoverImage()
    {
        await using var context = CreateContext();
        var image = new Image { Title = "Selected Cover" };
        var otherImage = new Image { Title = "Other Image" };
        context.Images.AddRange(image, otherImage);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, @"
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
CREATE TABLE galleries_images (gallery_id INTEGER NOT NULL, image_id INTEGER NOT NULL, cover INTEGER NOT NULL);
CREATE TABLE files (
  id INTEGER PRIMARY KEY,
  basename TEXT NOT NULL,
  parent_folder_id INTEGER NOT NULL,
  size INTEGER NOT NULL,
  mod_time TEXT NOT NULL,
  created_at TEXT NOT NULL
);
INSERT INTO galleries (id, title, organized, created_at, updated_at)
VALUES (10, 'Imported Gallery', 0, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
INSERT INTO galleries_images (gallery_id, image_id, cover) VALUES (10, 20, 1), (10, 21, 0);
");

        var service = CreateService(context);
        await InvokePrivateAsync(
            service,
            "ImportGalleriesAsync",
            stash,
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int>(),
            new Dictionary<int, int> { [20] = image.Id, [21] = otherImage.Id },
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None);

        var gallery = await context.Galleries.Include(item => item.ImageGalleries).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(image.Id, gallery.CoverImageId);
        Assert.Equal([image.Id, otherImage.Id], gallery.ImageGalleries.Select(link => link.ImageId).Order().ToArray());
    }

    [Fact]
    public async Task ImportAsync_ImportsRelationshipsThatRequireDeferredIdMaps()
    {
        await using var context = CreateContext();
        var dbPath = await CreateSqliteDatabaseAsync(@"
CREATE TABLE blobs (checksum TEXT NOT NULL, blob BLOB);
CREATE TABLE folders (
    id INTEGER PRIMARY KEY,
    path TEXT NOT NULL,
    parent_folder_id INTEGER,
    zip_file_id INTEGER,
    mod_time TEXT NOT NULL,
    created_at TEXT NOT NULL
);
CREATE TABLE studios (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    parent_id INTEGER,
    details TEXT,
    rating INTEGER,
    favorite INTEGER NOT NULL,
    image_blob TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE studio_urls (studio_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE studio_aliases (studio_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE studio_stash_ids (studio_id INTEGER NOT NULL, endpoint TEXT NOT NULL, stash_id TEXT NOT NULL);
CREATE TABLE tags (
    id INTEGER PRIMARY KEY,
    name TEXT NOT NULL,
    sort_name TEXT,
    description TEXT,
    favorite INTEGER NOT NULL,
    image_blob TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE studios_tags (studio_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
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
    image_blob TEXT,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE performer_urls (performer_id INTEGER NOT NULL, url TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0);
CREATE TABLE performer_aliases (performer_id INTEGER NOT NULL, alias TEXT NOT NULL);
CREATE TABLE performer_stash_ids (performer_id INTEGER NOT NULL, endpoint TEXT NOT NULL, stash_id TEXT NOT NULL);
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
CREATE TABLE groups_tags (group_id INTEGER NOT NULL, tag_id INTEGER NOT NULL);
CREATE TABLE groups_relations (containing_id INTEGER NOT NULL, sub_id INTEGER NOT NULL, order_index INTEGER NOT NULL, description TEXT);
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
    cover_blob TEXT
);
CREATE TABLE groups_scenes (scene_id INTEGER NOT NULL, group_id INTEGER NOT NULL, scene_index INTEGER);
CREATE TABLE scenes_files (scene_id INTEGER NOT NULL, file_id INTEGER NOT NULL, [primary] INTEGER NOT NULL);
CREATE TABLE files (
    id INTEGER PRIMARY KEY,
    basename TEXT NOT NULL,
    parent_folder_id INTEGER NOT NULL,
    zip_file_id INTEGER,
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
    bit_rate INTEGER NOT NULL
);
CREATE TABLE files_fingerprints (file_id INTEGER NOT NULL, type TEXT NOT NULL, fingerprint);
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
CREATE TABLE scenes_galleries (scene_id INTEGER NOT NULL, gallery_id INTEGER NOT NULL);
WITH RECURSIVE sequence(value) AS (
    SELECT 0
    UNION ALL
    SELECT value + 1 FROM sequence WHERE value < 500
)
INSERT INTO studios (id, name, details, favorite, created_at, updated_at)
SELECT
    30 + value,
    CASE WHEN value % 2 = 0 THEN ' Imported Studio ' ELSE 'IMPORTED STUDIO' END,
    CASE WHEN value = 500 THEN 'Metadata from collapsed studio' ELSE NULL END,
    CASE WHEN value = 500 THEN 1 ELSE 0 END,
    '2024-01-01T00:00:00Z',
    '2024-01-02T00:00:00Z'
FROM sequence;
INSERT INTO studio_urls (studio_id, url) VALUES (530, 'https://collapsed-studio.local');
INSERT INTO studio_aliases (studio_id, alias) VALUES (530, 'Collapsed studio alias');
INSERT INTO studio_stash_ids (studio_id, endpoint, stash_id) VALUES (530, 'fixture', 'collapsed-studio');
INSERT INTO tags (id, name, favorite, created_at, updated_at)
VALUES (40, 'Imported Tag', 0, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
WITH RECURSIVE sequence(value) AS (
    SELECT 0
    UNION ALL
    SELECT value + 1 FROM sequence WHERE value < 500
)
INSERT INTO performers (id, name, disambiguation, favorite, details, created_at, updated_at)
SELECT
    1000 + value,
    CASE WHEN value % 2 = 0 THEN ' Imported Performer ' ELSE 'IMPORTED PERFORMER' END,
    CASE WHEN value % 2 = 0 THEN ' Same identity ' ELSE 'SAME IDENTITY' END,
    CASE WHEN value = 500 THEN 1 ELSE 0 END,
    CASE WHEN value = 500 THEN 'Metadata from collapsed performer' ELSE NULL END,
    '2024-01-01T00:00:00Z',
    '2024-01-02T00:00:00Z'
FROM sequence;
INSERT INTO performer_urls (performer_id, url) VALUES (1500, 'https://collapsed-performer.local');
INSERT INTO performer_aliases (performer_id, alias) VALUES (1500, 'Collapsed performer alias');
INSERT INTO performer_stash_ids (performer_id, endpoint, stash_id) VALUES (1500, 'fixture', 'collapsed-performer');
INSERT INTO studios_tags (studio_id, tag_id) VALUES
    (30, 40),
    (30, 40),
    (999, 40),
    (30, 999);
INSERT INTO groups (id, name, front_image_blob) VALUES
    (50, 'Containing Group', NULL),
    (51, 'Sub Group', NULL),
    (52, 'Containing Group', 'cover-only-group');
INSERT INTO blobs (checksum, blob) VALUES
    ('cover-only-group', X'FFD8FF'),
    ('scene-only-cover', X'FFD8FF');
INSERT INTO groups_tags (group_id, tag_id) VALUES
    (50, 40),
    (50, 40),
    (999, 40),
    (50, 999);
INSERT INTO groups_relations (containing_id, sub_id, order_index, description) VALUES
    (50, 51, 3, 'Imported relation'),
    (52, 51, 7, 'Collapsed duplicate relation'),
    (50, 52, 8, 'Collapsed self relation'),
    (999, 51, 4, 'Missing containing group'),
    (50, 999, 5, 'Missing subgroup');
INSERT INTO scenes (id, title, organized, resume_time, play_duration, created_at, updated_at, cover_blob)
VALUES (10, 'Imported Scene', 0, 0, 0, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z', 'scene-only-cover');
INSERT INTO groups_scenes (group_id, scene_id, scene_index) VALUES (50, 10, 1);
INSERT INTO galleries (id, title, organized, created_at, updated_at)
VALUES (20, 'Imported Gallery', 0, '2024-01-01T00:00:00Z', '2024-01-02T00:00:00Z');
INSERT INTO scenes_galleries (scene_id, gallery_id) VALUES
    (10, 20),
    (10, 20),
    (999, 20),
    (10, 999);
", "cove-scene-gallery-migration");

        try
        {
            var recordingBlobService = new RecordingBlobService();
            var service = CreateService(context, recordingBlobService);
            var result = await service.ImportAsync(dbPath, new StashImportOptions(CoveGeneratedPath: null, MigrateGeneratedContent: false), TestContext.Current.CancellationToken);

            Assert.Equal(1, result.Videos);
            Assert.Equal(1, result.Galleries);
            context.ChangeTracker.Clear();
            var video = await context.Videos.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var gallery = await context.Galleries.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var relationship = await context.Set<VideoGallery>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(video.Id, relationship.VideoId);
            Assert.Equal(gallery.Id, relationship.GalleryId);
            var studio = await context.Studios
                .Include(item => item.Urls)
                .Include(item => item.Aliases)
                .Include(item => item.RemoteIds)
                .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Imported Studio", studio.Name);
            Assert.Equal("Metadata from collapsed studio", studio.Details);
            Assert.True(studio.Favorite);
            Assert.Contains(studio.Urls, item => item.Url == "https://collapsed-studio.local");
            Assert.Contains(studio.Aliases, item => item.Alias == "Collapsed studio alias");
            Assert.Contains(studio.RemoteIds, item => item.Endpoint == "fixture" && item.RemoteId == "collapsed-studio");
            var performer = await context.Performers
                .Include(item => item.Urls)
                .Include(item => item.Aliases)
                .Include(item => item.RemoteIds)
                .SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal("Imported Performer", performer.Name);
            Assert.Equal("Same identity", performer.Disambiguation);
            Assert.Equal("Metadata from collapsed performer", performer.Details);
            Assert.True(performer.Favorite);
            Assert.Contains(performer.Urls, item => item.Url == "https://collapsed-performer.local");
            Assert.Contains(performer.Aliases, item => item.Alias == "Collapsed performer alias");
            Assert.Contains(performer.RemoteIds, item => item.Endpoint == "fixture" && item.RemoteId == "collapsed-performer");
            var tag = await context.Tags.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            var studioTag = await context.Set<StudioTag>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(studio.Id, studioTag.StudioId);
            Assert.Equal(tag.Id, studioTag.TagId);
            var group = await context.Groups.SingleAsync(item => item.Name == "Containing Group", cancellationToken: TestContext.Current.CancellationToken);
            var groupTag = await context.Set<GroupTag>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(group.Id, groupTag.GroupId);
            Assert.Equal(tag.Id, groupTag.TagId);
            var subGroup = await context.Groups.SingleAsync(item => item.Name == "Sub Group", cancellationToken: TestContext.Current.CancellationToken);
            var groupRelation = await context.Set<GroupRelation>().SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
            Assert.Equal(group.Id, groupRelation.ContainingGroupId);
            Assert.Equal(subGroup.Id, groupRelation.SubGroupId);
            Assert.Equal(3, groupRelation.OrderIndex);
            Assert.Equal("Imported relation", groupRelation.Description);
            Assert.Equal(2, recordingBlobService.ContentTypes.Count);
            Assert.NotNull(video.ImageBlobId);
            Assert.NotNull(group.FrontImageBlobId);
            Assert.NotEqual(video.ImageBlobId, group.FrontImageBlobId);
        }
        finally
        {
            TryDeleteFile(dbPath);
        }
    }

    [Fact]
    public async Task CopyGeneratedContentAsync_SkipsScreenshotForExplicitCoverAndRetainsLegacyFallback()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generated-migration-{Guid.NewGuid():N}");
        var stashGeneratedPath = Path.Combine(tempRoot, "stash-generated");
        var stashScreenshotsPath = Path.Combine(stashGeneratedPath, "screenshots");
        var coveGeneratedPath = Path.Combine(tempRoot, "cove-generated");
        Directory.CreateDirectory(stashScreenshotsPath);

        try
        {
            const string generatedHash = "scene-hash";
            await File.WriteAllBytesAsync(Path.Combine(stashScreenshotsPath, $"{generatedHash}.jpg"), [1, 2, 3], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(stashScreenshotsPath, $"{generatedHash}.mp4"), [4, 5, 6], TestContext.Current.CancellationToken);

            var configPath = Path.Combine(tempRoot, "config.yml");
            await File.WriteAllTextAsync(configPath, $"generated: {stashGeneratedPath}\nvideo_file_naming_algorithm: MD5\n", TestContext.Current.CancellationToken);

            var stashConfig = InvokePrivateStatic(typeof(StashMigrationService), "ParseStashConfig", configPath);
            Assert.NotNull(stashConfig);

            var generatedDataType = typeof(StashMigrationService).GetNestedType("SceneGeneratedData", BindingFlags.NonPublic);
            Assert.NotNull(generatedDataType);
            var generatedMapType = typeof(Dictionary<,>).MakeGenericType(typeof(int), generatedDataType!);
            var generatedMap = Assert.IsAssignableFrom<System.Collections.IDictionary>(Activator.CreateInstance(generatedMapType));
            generatedMap.Add(41, Activator.CreateInstance(generatedDataType!, null, generatedHash, true));
            generatedMap.Add(42, Activator.CreateInstance(generatedDataType!, null, generatedHash, false));

            await using var context = CreateContext();
            var service = CreateService(
                context,
                configuration: new CoveConfiguration { GeneratedPath = coveGeneratedPath });

            await InvokePrivateAsync(
                service,
                "CopyGeneratedContentAsync",
                stashConfig,
                generatedMap,
                NullJobProgress.Instance,
                0d,
                1d,
                CancellationToken.None);

            Assert.Empty(Directory.EnumerateFiles(coveGeneratedPath, "41.jpg", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(coveGeneratedPath, "42.jpg", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(coveGeneratedPath, "41.mp4", SearchOption.AllDirectories));
            Assert.Single(Directory.EnumerateFiles(coveGeneratedPath, "42.mp4", SearchOption.AllDirectories));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
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
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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
        Assert.Equal(2, await context.Folders.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await context.Folders.CountAsync(folder => folder.Path == "C:/library", cancellationToken: TestContext.Current.CancellationToken));

        var importedChild = await context.Folders.SingleAsync(folder => folder.Id == folderIdMap[2], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("C:/library/clips", importedChild.Path);
        Assert.Equal(existingFolder.Id, importedChild.ParentFolderId);
    }

    [Fact]
    public async Task ImportStudiosAsync_ImportsMultipleRemoteIdsForSingleStudio()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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
            .SingleAsync(studio => studio.Id == studioIdMap[1], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Imported Studio", importedStudio.Name);
        Assert.Equal(2, importedStudio.RemoteIds.Count);
        Assert.Contains(importedStudio.RemoteIds, remoteId => remoteId.Endpoint == "https://stash-a.local" && remoteId.RemoteId == "101");
        Assert.Contains(importedStudio.RemoteIds, remoteId => remoteId.Endpoint == "https://stash-b.local" && remoteId.RemoteId == "202");
    }

    [Fact]
    public async Task ImportStudiosAsync_DoesNotCreateAParentCycleWhenDuplicateIdentitiesCollapse()
    {
        await using var context = CreateContext();
        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
        await ExecuteSqlAsync(stash, """
CREATE TABLE studios (
  id INTEGER PRIMARY KEY,
  name TEXT NOT NULL,
  parent_id INTEGER,
  details TEXT,
  rating INTEGER,
  favorite INTEGER NOT NULL,
  image_blob TEXT,
  created_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z',
  updated_at TEXT NOT NULL DEFAULT '2024-01-01T00:00:00Z'
);
INSERT INTO studios (id, name, parent_id, favorite) VALUES
  (1, 'First identity', 3, 0),
  (2, ' first identity ', NULL, 0),
  (3, 'Second identity', 2, 0);
""");

        var service = CreateService(context);
        context.ChangeTracker.AutoDetectChangesEnabled = false;
        var idMap = Assert.IsType<Dictionary<int, int>>(await InvokePrivateAsync(
            service,
            "ImportStudiosAsync",
            stash,
            new Dictionary<string, string>(),
            NullJobProgress.Instance,
            0d,
            1d,
            CancellationToken.None));
        context.ChangeTracker.AutoDetectChangesEnabled = true;
        context.ChangeTracker.Clear();

        Assert.Equal(idMap[1], idMap[2]);
        var first = await context.Studios.SingleAsync(studio => studio.Id == idMap[1], cancellationToken: TestContext.Current.CancellationToken);
        var second = await context.Studios.SingleAsync(studio => studio.Id == idMap[3], cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(second.Id, first.ParentId);
        Assert.Null(second.ParentId);
    }

    [Fact]
    public async Task ImportGroupsAsync_ImportsFrontAndBackImageBlobs()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var importedGroup = await context.Groups.SingleAsync(group => group.Id == groupIdMap[1], cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("Imported Group", importedGroup.Name);
        Assert.Equal("cove-front", importedGroup.FrontImageBlobId);
        Assert.Equal("cove-back", importedGroup.BackImageBlobId);
    }

    [Fact]
    public async Task ImportGroupsAsync_MergesDuplicateCoverOnlyGroupIntoSceneLinkedGroup()
    {
        await using var context = CreateContext();

        await using var stash = new SqliteConnection("Data Source=:memory:");
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var importedGroup = await context.Groups.Include(group => group.Urls).SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
        await stash.OpenAsync(TestContext.Current.CancellationToken);
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

        var importedGroup = await context.Groups.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
                await stash.OpenAsync(TestContext.Current.CancellationToken);
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

                var galleryImport = Assert.IsType<(int Count, Dictionary<int, int> GalleryFileIdMap, Dictionary<int, int> GalleryIdMap)>(await InvokePrivateAsync(
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

                var importedImageFile = await context.ImageFiles.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
                var importedFolder = await context.Folders.SingleAsync(folder => folder.Path.Contains("archive.zip"), cancellationToken: TestContext.Current.CancellationToken);
                var importedGalleryFile = await context.GalleryFiles.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);

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
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync(TestContext.Current.CancellationToken);
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

                var segment = await context.Segments.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
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
                await context.SaveChangesAsync(TestContext.Current.CancellationToken);

                await using var stash = new SqliteConnection("Data Source=:memory:");
                await stash.OpenAsync(TestContext.Current.CancellationToken);
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

                var segments = await context.Segments.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
                var segment = Assert.Single(segments);
                Assert.Equal(manualTag.Id, segment.TagId);
                Assert.Equal(3L, segment.RefId);
                Assert.Equal("Manual marker", segment.Title);
        }

    private static StashMigrationService CreateService(
        CoveContext context,
        IBlobService? blobService = null,
        CoveConfiguration? configuration = null)
    {
        var config = configuration ?? new CoveConfiguration();
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

    private static CoveContext CreateContext(bool includeOwner = true)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"stash-metadata-{Guid.NewGuid():N}")
            .Options;

        var context = new TestCoveContext(options);
        if (includeOwner)
        {
            context.Users.Add(new User
            {
                Username = "owner",
                PasswordHash = "test",
                PasswordAlgo = "test",
                IsSystem = true,
                IsActive = true,
            });
            context.SaveChanges();
        }

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
