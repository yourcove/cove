using Cove.Core.Auth;
using Cove.Data.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Cove.PerformanceTests.Infrastructure;

public sealed class PostgresPerformanceFixture : IAsyncLifetime
{
    private readonly string _databaseName = $"cove_perf_{Guid.NewGuid():N}";
    private readonly CurrentPrincipalAccessor _principalAccessor = new();
    private string? _databaseConnectionString;
    private int? _benchmarkUserId;
    private PostgresSettings? _settings;

    public int SampleVideoId { get; private set; }
    public int SampleImageId { get; private set; }
    public int SampleTagId { get; private set; }
    public int SampleStudioId { get; private set; }
    public int SamplePerformerId { get; private set; }
    public int SampleGalleryId { get; private set; }
    public int SampleGroupId { get; private set; }

    public string DatabaseConnectionString => _databaseConnectionString
        ?? throw new InvalidOperationException("The performance database has not been initialized.");

    public async Task InitializeAsync()
    {
        CoveContext.SetDataExtensions([]);

        _settings = PostgresSettings.LoadFromEnvironment();
        try
        {
            await CreateDatabaseAsync(_settings, _databaseName);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Performance tests require a reachable PostgreSQL instance. Set COVE_PERF_PG_HOST, COVE_PERF_PG_PORT, COVE_PERF_PG_USER, COVE_PERF_PG_PASSWORD, and COVE_PERF_PG_ADMIN_DB as needed.",
                ex);
        }

        _databaseConnectionString = _settings.BuildDatabaseConnectionString(_databaseName);

        await using var context = CreateContext();
        await context.Database.MigrateAsync();
        await SeedDataAsync(context);
        await context.Database.ExecuteSqlRawAsync("ANALYZE");
    }

    public async Task DisposeAsync()
    {
        if (_settings is null)
        {
            return;
        }

        NpgsqlConnection.ClearAllPools();

        await using var adminConnection = new NpgsqlConnection(_settings.BuildAdminConnectionString());
        await adminConnection.OpenAsync();

        await using var dropCommand = adminConnection.CreateCommand();
        dropCommand.CommandText = $"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE);";
        await dropCommand.ExecuteNonQueryAsync();
    }

    public CoveContext CreateContext()
    {
        if (_benchmarkUserId is int benchmarkUserId)
        {
            _principalAccessor.Set(new CovePrincipal
            {
                UserId = benchmarkUserId,
                Username = "perf-user",
                Kind = PrincipalKind.User,
                Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*" },
                Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            });
        }
        else
        {
            _principalAccessor.Set(null);
        }

        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(DatabaseConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .EnableDetailedErrors()
            .Options;

        return new CoveContext(options, _principalAccessor);
    }

    private static async Task CreateDatabaseAsync(PostgresSettings settings, string databaseName)
    {
        await using var adminConnection = new NpgsqlConnection(settings.BuildAdminConnectionString());
        await adminConnection.OpenAsync();

        await using var createCommand = adminConnection.CreateCommand();
        createCommand.CommandText = $"CREATE DATABASE \"{databaseName}\";";
        await createCommand.ExecuteNonQueryAsync();
    }

    private async Task SeedDataAsync(CoveContext db)
    {
        var baseDate = new DateTime(2024, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var random = new Random(7331);

        var benchmarkUser = new User
        {
            Username = "perf-user",
            DisplayName = "Performance User",
            PasswordHash = "not-used",
            PasswordAlgo = "test",
            IsActive = true,
        };
        db.Users.Add(benchmarkUser);
        await db.SaveChangesAsync();
        _benchmarkUserId = benchmarkUser.Id;

        var folders = Enumerable.Range(1, 96)
            .Select(index => new Folder
            {
                Path = $"/library/set-{index:000}",
                ModTime = baseDate.AddDays(index),
                CreatedAt = baseDate.AddDays(-60 + index),
                UpdatedAt = baseDate.AddDays(-20 + index),
            })
            .ToList();
        db.Folders.AddRange(folders);

        var tags = Enumerable.Range(1, 36)
            .Select(index => new Tag
            {
                Name = $"Tag {index:000}",
                SortName = $"Tag Sort {index:000}",
                Description = $"Descriptive tag {index:000}",
                Favorite = index % 5 == 0,
                CreatedAt = baseDate.AddDays(-120 + index),
                UpdatedAt = baseDate.AddDays(-10 + index),
                Aliases =
                [
                    new TagAlias { Alias = $"tag-alias-{index:000}" },
                ],
            })
            .ToList();
        db.Tags.AddRange(tags);

        var studios = Enumerable.Range(1, 18)
            .Select(index => new Studio
            {
                Name = $"Studio {index:000}",
                Favorite = index % 4 == 0,
                Details = $"Studio details {index:000}",
                Organized = index % 3 != 0,
                CreatedAt = baseDate.AddDays(-180 + index),
                UpdatedAt = baseDate.AddDays(-12 + index),
                Urls =
                [
                    new StudioUrl { Url = $"https://example.test/studios/{index:000}" },
                ],
                Aliases =
                [
                    new StudioAlias { Alias = $"studio-alias-{index:000}" },
                ],
            })
            .ToList();

        for (var index = 0; index < 6; index++)
        {
            studios.Add(new Studio
            {
                Name = $"Studio Child {index + 1:000}",
                Parent = studios[index],
                Favorite = index % 2 == 0,
                Details = $"Child studio details {index + 1:000}",
                Organized = true,
                CreatedAt = baseDate.AddDays(-90 + index),
                UpdatedAt = baseDate.AddDays(-6 + index),
            });
        }
        db.Studios.AddRange(studios);

        var performers = Enumerable.Range(1, 60)
            .Select(index => new Performer
            {
                Name = $"Performer {index:000}",
                Disambiguation = index % 2 == 0 ? $"Alt {index:000}" : null,
                Birthdate = new DateOnly(1980 + (index % 20), (index % 12) + 1, (index % 27) + 1),
                Country = index % 3 == 0 ? "US" : "CA",
                EyeColor = index % 2 == 0 ? "blue" : "brown",
                HairColor = index % 3 == 0 ? "black" : "blonde",
                HeightCm = 150 + (index % 40),
                Weight = 50 + (index % 35),
                Measurements = $"{30 + index % 6}-{25 + index % 6}-{34 + index % 6}",
                Favorite = index % 6 == 0,
                Details = $"Performer details {index:000}",
                CreatedAt = baseDate.AddDays(-220 + index),
                UpdatedAt = baseDate.AddDays(-16 + index),
                Urls =
                [
                    new PerformerUrl { Url = $"https://example.test/performers/{index:000}" },
                ],
                Aliases =
                [
                    new PerformerAlias { Alias = $"performer-alias-{index:000}" },
                ],
            })
            .ToList();
        db.Performers.AddRange(performers);

        await db.SaveChangesAsync();

        for (var index = 0; index < performers.Count; index++)
        {
            var performer = performers[index];
            performer.PerformerTags.Add(new PerformerTag { PerformerId = performer.Id, TagId = tags[index % tags.Count].Id });
            performer.PerformerTags.Add(new PerformerTag { PerformerId = performer.Id, TagId = tags[(index + 5) % tags.Count].Id });
        }

        for (var index = 0; index < studios.Count; index++)
        {
            var studio = studios[index];
            studio.StudioTags.Add(new StudioTag { StudioId = studio.Id, TagId = tags[(index + 3) % tags.Count].Id });
            studio.StudioTags.Add(new StudioTag { StudioId = studio.Id, TagId = tags[(index + 11) % tags.Count].Id });
        }

        await db.SaveChangesAsync();

        var galleries = new List<Gallery>();
        for (var index = 1; index <= 40; index++)
        {
            var studio = studios[(index - 1) % 18];
            var gallery = new Gallery
            {
                Title = $"Gallery {index:000}",
                Code = $"GAL-{index:000}",
                Date = DateOnly.FromDateTime(baseDate.AddDays(-120 + index)),
                Details = $"Gallery details {index:000}",
                Photographer = $"Photographer {index % 9}",
                Organized = index % 3 != 0,
                StudioId = studio.Id,
                CreatedAt = baseDate.AddDays(-150 + index),
                UpdatedAt = baseDate.AddDays(-8 + index),
                Urls =
                [
                    new GalleryUrl { Url = $"https://example.test/galleries/{index:000}" },
                ],
            };

            var folder = folders[(index * 2) % folders.Count];
            var basename = $"gallery-{index:000}.zip";
            gallery.Files.Add(new GalleryFile
            {
                Basename = basename,
                ParentFolderId = folder.Id,
                Path = BaseFileEntity.ComputePath(folder.Path, basename),
                Size = 80_000_000L + (index * 250_000L),
                ModTime = baseDate.AddDays(-40 + index),
            });

            galleries.Add(gallery);
        }
        db.Galleries.AddRange(galleries);

        var groups = new List<Group>();
        for (var index = 1; index <= 26; index++)
        {
            var studio = studios[(index + 2) % 18];
            var group = new Group
            {
                Name = $"Group {index:000}",
                Aliases = $"group-alias-{index:000}",
                Duration = 600 + (index * 35),
                Date = DateOnly.FromDateTime(baseDate.AddDays(-100 + index)),
                StudioId = studio.Id,
                Director = $"Director {index % 8}",
                Synopsis = $"Group synopsis {index:000}",
                CreatedAt = baseDate.AddDays(-130 + index),
                UpdatedAt = baseDate.AddDays(-6 + index),
                Urls =
                [
                    new GroupUrl { Url = $"https://example.test/groups/{index:000}" },
                ],
            };

            group.GroupTags.Add(new GroupTag { TagId = tags[(index + 1) % tags.Count].Id });
            group.GroupTags.Add(new GroupTag { TagId = tags[(index + 13) % tags.Count].Id });
            groups.Add(group);
        }
        db.Groups.AddRange(groups);

        await db.SaveChangesAsync();

        for (var index = 0; index < galleries.Count; index++)
        {
            var gallery = galleries[index];
            var galleryOrdinal = index + 1;
            gallery.GalleryTags.Add(new GalleryTag { GalleryId = gallery.Id, TagId = tags[galleryOrdinal % tags.Count].Id });
            gallery.GalleryTags.Add(new GalleryTag { GalleryId = gallery.Id, TagId = tags[(galleryOrdinal + 7) % tags.Count].Id });
            gallery.GalleryPerformers.Add(new GalleryPerformer { GalleryId = gallery.Id, PerformerId = performers[galleryOrdinal % performers.Count].Id });
            gallery.GalleryPerformers.Add(new GalleryPerformer { GalleryId = gallery.Id, PerformerId = performers[(galleryOrdinal + 9) % performers.Count].Id });
        }

        await db.SaveChangesAsync();

        var videos = new List<Video>();
        for (var index = 1; index <= 220; index++)
        {
            var studio = studios[(index - 1) % 18];
            var video = new Video
            {
                Title = $"Video {index:000}",
                Code = $"SCN-{index:000}",
                Details = $"Video details {index:000}",
                Director = $"Director {index % 12}",
                Date = DateOnly.FromDateTime(baseDate.AddDays(-200 + index)),
                Organized = index % 4 != 0,
                StudioId = studio.Id,
                Captions = index % 3 == 0 ? "en" : null,
                CreatedAt = baseDate.AddDays(-240 + index),
                UpdatedAt = baseDate.AddDays(-4 + index),
                Urls =
                [
                    new VideoUrl { Url = $"https://example.test/videos/{index:000}" },
                ],
            };

            videos.Add(video);
        }
        db.Videos.AddRange(videos);

        await db.SaveChangesAsync();

        for (var index = 0; index < videos.Count; index++)
        {
            var video = videos[index];
            var videoOrdinal = index + 1;
            var gallery = galleries[index % galleries.Count];
            var group = groups[index % groups.Count];

            video.VideoTags.Add(new VideoTag { VideoId = video.Id, TagId = tags[videoOrdinal % tags.Count].Id });
            video.VideoTags.Add(new VideoTag { VideoId = video.Id, TagId = tags[(videoOrdinal + 9) % tags.Count].Id });
            video.VideoPerformers.Add(new VideoPerformer { VideoId = video.Id, PerformerId = performers[videoOrdinal % performers.Count].Id });
            video.VideoPerformers.Add(new VideoPerformer { VideoId = video.Id, PerformerId = performers[(videoOrdinal + 11) % performers.Count].Id });
            video.VideoGalleries.Add(new VideoGallery { VideoId = video.Id, GalleryId = gallery.Id });
            video.GroupItems.Add(new GroupItem { VideoId = video.Id, GroupId = group.Id, OrderIndex = (videoOrdinal % 8) + 1, Kind = GroupItemKind.Video });

            var fileCount = videoOrdinal % 3 == 0 ? 2 : 1;
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var folder = folders[(videoOrdinal + fileIndex) % folders.Count];
                var orientation = (videoOrdinal + fileIndex) % 3;
                var (width, height) = orientation switch
                {
                    0 => (1920, 1080),
                    1 => (1080, 1920),
                    _ => (1400, 1400),
                };

                var basename = $"video-{videoOrdinal:000}-{fileIndex:00}.mp4";
                var videoFile = new VideoFile
                {
                    VideoId = video.Id,
                    Basename = basename,
                    ParentFolderId = folder.Id,
                    Path = BaseFileEntity.ComputePath(folder.Path, basename),
                    Size = 180_000_000L + (videoOrdinal * 450_000L) + (fileIndex * 80_000L),
                    ModTime = baseDate.AddDays(-30 + videoOrdinal + fileIndex),
                    Format = "mp4",
                    Width = width,
                    Height = height,
                    Duration = 180 + ((videoOrdinal * 11 + fileIndex * 17) % 1_200),
                    VideoCodec = videoOrdinal % 2 == 0 ? "h264" : "hevc",
                    AudioCodec = videoOrdinal % 3 == 0 ? "aac" : "opus",
                    FrameRate = new[] { 23.976, 24.0, 30.0, 60.0 }[(videoOrdinal + fileIndex) % 4],
                    BitRate = 1_500_000L + (((videoOrdinal + fileIndex) % 6) * 850_000L),
                };

                videoFile.Fingerprints.Add(new FileFingerprint { Type = "md5", Value = $"video-md5-{videoOrdinal:000}-{fileIndex:00}" });
                videoFile.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = $"video-osh-{videoOrdinal:000}-{fileIndex:00}" });
                if ((videoOrdinal + fileIndex) % 4 == 0)
                {
                    videoFile.Captions.Add(new VideoCaption
                    {
                        LanguageCode = "en",
                        CaptionType = "vtt",
                        Filename = $"video-{videoOrdinal:000}-{fileIndex:00}.vtt",
                    });
                }

                video.Files.Add(videoFile);
            }

            if (videoOrdinal % 3 == 0)
            {
                db.Segments.Add(new Segment
                {
                    HostType = SegmentHostType.Video,
                    HostId = video.Id,
                    StartSec = videoOrdinal,
                    EndSec = videoOrdinal + 5,
                    SourceKey = "performance",
                });
            }
        }

        var images = new List<Image>();
        for (var index = 1; index <= 180; index++)
        {
            var studio = studios[(index + 3) % 18];
            var image = new Image
            {
                Title = $"Image {index:000}",
                Code = $"IMG-{index:000}",
                Details = $"Image details {index:000}",
                Photographer = $"Photographer {index % 10}",
                Organized = index % 3 != 0,
                StudioId = studio.Id,
                Date = DateOnly.FromDateTime(baseDate.AddDays(-160 + index)),
                CreatedAt = baseDate.AddDays(-200 + index),
                UpdatedAt = baseDate.AddDays(-3 + index),
                Urls =
                [
                    new ImageUrl { Url = $"https://example.test/images/{index:000}" },
                ],
            };

            images.Add(image);
        }
        db.Images.AddRange(images);

        await db.SaveChangesAsync();

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var imageOrdinal = index + 1;
            var gallery = galleries[(imageOrdinal + 5) % galleries.Count];

            image.ImageTags.Add(new ImageTag { ImageId = image.Id, TagId = tags[(imageOrdinal + 2) % tags.Count].Id });
            image.ImageTags.Add(new ImageTag { ImageId = image.Id, TagId = tags[(imageOrdinal + 14) % tags.Count].Id });
            image.ImagePerformers.Add(new ImagePerformer { ImageId = image.Id, PerformerId = performers[(imageOrdinal + 1) % performers.Count].Id });
            image.ImagePerformers.Add(new ImagePerformer { ImageId = image.Id, PerformerId = performers[(imageOrdinal + 7) % performers.Count].Id });
            image.ImageGalleries.Add(new ImageGallery { ImageId = image.Id, GalleryId = gallery.Id });

            var fileCount = imageOrdinal % 4 == 0 ? 2 : 1;
            for (var fileIndex = 0; fileIndex < fileCount; fileIndex++)
            {
                var folder = folders[(imageOrdinal * 3 + fileIndex) % folders.Count];
                var orientation = (imageOrdinal + fileIndex + 1) % 3;
                var (width, height) = orientation switch
                {
                    0 => (2400, 1600),
                    1 => (1200, 1800),
                    _ => (1500, 1500),
                };

                var basename = $"image-{imageOrdinal:000}-{fileIndex:00}.jpg";
                var imageFile = new ImageFile
                {
                    ImageId = image.Id,
                    Basename = basename,
                    ParentFolderId = folder.Id,
                    Path = BaseFileEntity.ComputePath(folder.Path, basename),
                    Size = 8_000_000L + (imageOrdinal * 75_000L) + (fileIndex * 10_000L),
                    ModTime = baseDate.AddDays(-20 + imageOrdinal + fileIndex),
                    Format = "jpg",
                    Width = width,
                    Height = height,
                };

                imageFile.Fingerprints.Add(new FileFingerprint { Type = "md5", Value = $"image-md5-{imageOrdinal:000}-{fileIndex:00}" });
                image.Files.Add(imageFile);
            }
        }

        await db.SaveChangesAsync();

        foreach (var gallery in galleries)
        {
            gallery.CoverImageId = gallery.ImageGalleries.OrderBy(link => link.ImageId).Select(link => (int?)link.ImageId).FirstOrDefault();
        }

        await db.SaveChangesAsync();

        var benchmarkUserId = _benchmarkUserId ?? throw new InvalidOperationException("Benchmark user was not initialized.");

        for (var index = 0; index < studios.Count; index++)
        {
            var studio = studios[index];
            var rating = index < 18 ? 35 + ((index + 1) % 60) : 45 + (index - 18);
            AddRating(db, benchmarkUserId, RatingHostType.Studio, studio.Id, rating);
        }

        for (var index = 0; index < performers.Count; index++)
        {
            var performer = performers[index];
            AddRating(db, benchmarkUserId, RatingHostType.Performer, performer.Id, 40 + ((index + 1) % 55));
        }

        for (var index = 0; index < galleries.Count; index++)
        {
            var gallery = galleries[index];
            AddRating(db, benchmarkUserId, RatingHostType.Gallery, gallery.Id, 30 + ((index + 1) % 65));
        }

        for (var index = 0; index < groups.Count; index++)
        {
            var group = groups[index];
            AddRating(db, benchmarkUserId, RatingHostType.Group, group.Id, 35 + ((index + 1) % 60));
        }

        for (var index = 0; index < videos.Count; index++)
        {
            var video = videos[index];
            var videoOrdinal = index + 1;
            AddRating(db, benchmarkUserId, RatingHostType.Video, video.Id, 30 + (videoOrdinal % 70));
            AddAffinity(
                db,
                benchmarkUserId,
                AffinityHostType.Video,
                video.Id,
                viewCount: videoOrdinal % 12,
                totalConsumedSec: 200 + (videoOrdinal % 900),
                lastPositionSec: videoOrdinal % 240,
                lastConsumedAt: baseDate.AddHours(videoOrdinal),
                likeCount: videoOrdinal % 20);
        }

        for (var index = 0; index < images.Count; index++)
        {
            var image = images[index];
            var imageOrdinal = index + 1;
            AddRating(db, benchmarkUserId, RatingHostType.Image, image.Id, 25 + (imageOrdinal % 70));
            AddAffinity(db, benchmarkUserId, AffinityHostType.Image, image.Id, likeCount: imageOrdinal % 15);
        }

        await db.SaveChangesAsync();

        SampleVideoId = videos[random.Next(videos.Count)].Id;
        SampleImageId = images[random.Next(images.Count)].Id;
        SampleTagId = tags[random.Next(tags.Count)].Id;
        SampleStudioId = studios[random.Next(18)].Id;
        SamplePerformerId = performers[random.Next(performers.Count)].Id;
        SampleGalleryId = galleries[random.Next(galleries.Count)].Id;
        SampleGroupId = groups[random.Next(groups.Count)].Id;

        db.ChangeTracker.Clear();
    }

    private static void AddRating(CoveContext db, int userId, RatingHostType hostType, int hostId, int value)
    {
        db.Ratings.Add(new Rating
        {
            UserId = userId,
            HostType = hostType,
            HostId = hostId,
            Aspect = "overall",
            Value = value,
        });
    }

    private static void AddAffinity(
        CoveContext db,
        int userId,
        AffinityHostType hostType,
        int hostId,
        int viewCount = 0,
        double totalConsumedSec = 0,
        double? lastPositionSec = null,
        DateTime? lastConsumedAt = null,
        int likeCount = 0)
    {
        db.UserEntityAffinities.Add(new UserEntityAffinity
        {
            UserId = userId,
            HostType = hostType,
            HostId = hostId,
            ViewCount = viewCount,
            TotalConsumedSec = totalConsumedSec,
            LastPositionSec = lastPositionSec,
            LastConsumedAt = lastConsumedAt,
            LikeCount = likeCount,
        });
    }

    private sealed record PostgresSettings(string Host, int Port, string User, string Password, string AdminDatabase)
    {
        public static PostgresSettings LoadFromEnvironment()
        {
            var host = Environment.GetEnvironmentVariable("COVE_PERF_PG_HOST") ?? "127.0.0.1";
            var portRaw = Environment.GetEnvironmentVariable("COVE_PERF_PG_PORT");
            var port = int.TryParse(portRaw, out var parsedPort) ? parsedPort : 5443;
            var user = Environment.GetEnvironmentVariable("COVE_PERF_PG_USER") ?? "postgres";
            var password = Environment.GetEnvironmentVariable("COVE_PERF_PG_PASSWORD") ?? string.Empty;
            var adminDatabase = Environment.GetEnvironmentVariable("COVE_PERF_PG_ADMIN_DB") ?? "postgres";

            return new PostgresSettings(host, port, user, password, adminDatabase);
        }

        public string BuildAdminConnectionString()
            => BuildConnectionString(AdminDatabase);

        public string BuildDatabaseConnectionString(string databaseName)
            => BuildConnectionString(databaseName);

        private string BuildConnectionString(string databaseName)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = Host,
                Port = Port,
                Username = User,
                Password = Password,
                Database = databaseName,
                IncludeErrorDetail = true,
                Pooling = true,
                Timeout = 15,
                CommandTimeout = 30,
            };

            return builder.ConnectionString;
        }
    }
}
