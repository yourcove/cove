using Cove.Core.Entities;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Cove.Tests;

public sealed class VideoSearchScoringTests
{
    public static bool UsesPostgres => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COVE_TEST_POSTGRES_CONNECTION"));

    [Fact(SkipUnless = nameof(UsesPostgres), Skip = "Authorization query filters require PostgreSQL.")]
    public async Task DeniedRelatedEntities_DoNotIncludeOrBoostVideos()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var hiddenTag = new Tag { Name = "Private cue" };
        var tagged = new Video { Title = "Same title", Details = "Private cue", VideoTags = [new() { Tag = hiddenTag }] };
        var recent = new Video { Title = "Same title", Details = "Private cue", UpdatedAt = DateTime.UtcNow.AddDays(1) };
        var tagOnly = new Video { Title = "Unrelated", VideoTags = [new() { Tag = hiddenTag }] };
        var galleryOnly = new Video { Title = "Unrelated", VideoGalleries = [new() { Gallery = new Gallery { Title = "Private cue" } }] };
        var groupOnly = new Video { Title = "Unrelated" };
        var hiddenGroup = new Group { Name = "Private cue" };
        db.AddRange(tagged, recent, tagOnly, galleryOnly, groupOnly, hiddenGroup);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Add(new GroupItem { GroupId = hiddenGroup.Id, Kind = GroupItemKind.Video, HostType = "video", HostId = groupOnly.Id, VideoId = groupOnly.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var principal = new CurrentPrincipalAccessor();
        principal.Set(new CovePrincipal { UserId = 1, Username = "search-reader", Kind = PrincipalKind.User,
            Permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Permissions.VideosRead },
            Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) });
        await using var scoped = new CoveContext(new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(db.Database.GetDbConnection(), options => options.UseVector()).Options, principal);

        var result = await new VideoRepository(scoped).FindAsync(null, new FindFilter { Q = "private cue", Sort = "relevance" }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { recent.Id, tagged.Id }, result.Items.Select(video => video.Id));
        Assert.Equal(2, result.TotalCount);
    }

    [Fact]
    public async Task ContiguousTitlePhrase_OutranksScatteredTitleWords()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var phrase = new Video { Title = "My Love Story" };
        var scattered = new Video { Title = "Love for My Friend", UpdatedAt = DateTime.UtcNow.AddDays(1) };
        db.Videos.AddRange(scattered, phrase);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "my love", Sort = "relevance" }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { phrase.Id, scattered.Id }, result.Items.Select(video => video.Id));
    }

    [Theory]
    [InlineData("My Love Twosome Kissing")]
    [InlineData("My Love Twosome Kissing kissing")]
    [InlineData("my_love twosome kissing")]
    public async Task RepeatedTermsAndSeparators_DoNotChangeTagEvidence(string query)
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var kissing = new Tag { Name = "Kissing", Aliases = [new() { Alias = "Kissing Together" }] };
        var twosome = new Tag { Name = "Twosome" };
        var confirmed = new Video { Title = "My Love", VideoTags = [new() { Tag = kissing }, new() { Tag = twosome }] };
        var mentioned = new Video { Title = "My Love", Details = "Not ready for kissing", VideoTags = [new() { Tag = twosome }], UpdatedAt = DateTime.UtcNow.AddDays(1) };
        db.Videos.AddRange(mentioned, confirmed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = query, Sort = "relevance" }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { confirmed.Id, mentioned.Id }, result.Items.Select(video => video.Id));
    }

    [Fact]
    public async Task MultiwordPerformerAlias_CanCombineWithTitleAndTag()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var performer = new Performer { Name = "Example Performer", Aliases = [new() { Alias = "Stage Name" }] };
        var tag = new Tag { Name = "Slow Dance" };
        var confirmed = new Video { Title = "Evening", VideoPerformers = [new() { Performer = performer }], VideoTags = [new() { Tag = tag }] };
        var mentioned = new Video { Title = "Evening", Details = "Stage name and slow dance", UpdatedAt = DateTime.UtcNow.AddDays(1) };
        db.Videos.AddRange(mentioned, confirmed, new Video { Title = "Evening Stage", Details = "Missing the other terms" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "Evening Stage Name Slow Dance", Sort = "relevance" }, TestContext.Current.CancellationToken);
        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { confirmed.Id, mentioned.Id }, result.Items.Select(video => video.Id));
    }

    [Fact]
    public async Task DuplicateAliasesAndOverlappingTags_DoNotMultiplyEvidence()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var tag = new Tag { Name = "Kissing", Aliases = [new() { Alias = "Shared Alias" }] };
        var recent = new Video { Title = "Same title", VideoTags = [new() { Tag = tag }], UpdatedAt = DateTime.UtcNow.AddDays(1) };
        var crowded = new Video { Title = "Same title", VideoTags = [new() { Tag = tag }, new() { Tag = new Tag { Name = "Kissing Together" } }] };
        db.Videos.AddRange(crowded, recent);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "kissing", Sort = "relevance" }, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { recent.Id, crowded.Id }, result.Items.Select(video => video.Id));
    }

    [Fact]
    public async Task WholeTagTerms_KeepNumericBoundaries_AndHonorCallerFilters()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var included = new Video { Title = "Included", VideoTags = [new() { Tag = new Tag { Name = "1F" } }] };
        var excluded = new Video { Title = "Excluded", VideoTags = [new() { Tag = new Tag { Name = "1F1M" } }] };
        db.Videos.AddRange(included, excluded);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new VideoRepository(db);
        var result = await repository.FindAsync(null, new FindFilter { Q = "1f" }, TestContext.Current.CancellationToken);
        Assert.Equal(included.Id, Assert.Single(result.Items).Id);
        var filtered = await repository.FindAsync(new VideoFilter { Ids = [excluded.Id] }, new FindFilter { Q = "1f" }, TestContext.Current.CancellationToken);
        Assert.Empty(filtered.Items);
        Assert.Equal(0, filtered.TotalCount);
    }

    // Past the member cap the search stops resolving gallery and group members and tests the link
    // tables instead, which must still find every member.
    [Fact]
    public async Task GalleryAndGroupNames_PastTheResolveCap_StillMatchEveryMember()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var gallery = new Gallery { Title = "Orchard" };
        var group = new Group { Name = "Orchard Season" };
        var inGallery = Enumerable.Range(0, 3).Select(index => new Video { Title = $"Gallery member {index}", VideoGalleries = [new() { Gallery = gallery }] }).ToArray();
        var inGroup = Enumerable.Range(0, 3).Select(index => new Video { Title = $"Group member {index}" }).ToArray();
        db.AddRange([group, .. inGallery, .. inGroup, new Video { Title = "Unrelated" }]);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.AddRange(inGroup.Select((video, index) => new GroupItem { GroupId = group.Id, OrderIndex = index, Kind = GroupItemKind.Video, HostType = "video", HostId = video.Id, VideoId = video.Id }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var search = await VideoTextSearch.CreateAsync(db, "orchard", TestContext.Current.CancellationToken, memberSubqueryThreshold: 2);
        var query = search.Apply(db.Videos.AsNoTracking());
        var ids = await query.Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Contains("video_galleries", query.ToQueryString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(inGallery.Concat(inGroup).Select(video => video.Id).Order(), ids.Order());
    }

    [Fact]
    public async Task GalleryAndGroupNames_MatchMemberVideos_AndRankExactNamesFirst()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var exactGallery = new Gallery { Title = "Orchard" };
        var partialGroup = new Group { Name = "Orchard Season" };
        var inGallery = new Video { Title = "First", VideoGalleries = [new() { Gallery = exactGallery }] };
        var inGroup = new Video { Title = "Second", UpdatedAt = DateTime.UtcNow.AddDays(1) };
        var unrelated = new Video { Title = "Unrelated" };
        db.AddRange(partialGroup, inGallery, inGroup, unrelated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Add(new GroupItem { GroupId = partialGroup.Id, Kind = GroupItemKind.Video, HostType = "video", HostId = inGroup.Id, VideoId = inGroup.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "orchard", Sort = "relevance" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { inGallery.Id, inGroup.Id }, result.Items.Select(video => video.Id));
    }

    // Past the path candidate cap the search tests the files table directly, which must still find
    // every video with a matching file.
    [Fact]
    public async Task PathFallback_PastTheCandidateCap_StillMatchesEveryVideo()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var folder = new Folder { Path = "/library" };
        db.Folders.Add(folder);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var matching = Enumerable.Range(0, 3).Select(index => new Video
        {
            Title = $"Match {index}",
            Files = { new VideoFile { Basename = $"abcNeedle{index}.mp4", Path = $"/library/abcNeedle{index}.mp4", ParentFolderId = folder.Id, Format = "mp4" } },
        }).ToArray();
        db.Videos.AddRange([.. matching, new Video { Title = "Unrelated" }]);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var search = await VideoTextSearch.CreateAsync(db, "needle", TestContext.Current.CancellationToken, pathSubqueryThreshold: 2);
        var query = search.Apply(db.Videos.AsNoTracking());
        var ids = await query.Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Matches("FROM \"?files\"?", query.ToQueryString());
        Assert.Equal(matching.Select(video => video.Id).Order(), ids.Order());
    }

    // The member and path lookups only test whether a cap was passed, but an unordered limit still
    // logs EF's row-limiting warning on every search.
    [Fact]
    public async Task CandidateCapLookups_AreOrderedBeforeLimiting()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var folder = new Folder { Path = "/library" };
        var gallery = new Gallery { Title = "Orchard" };
        var group = new Group { Name = "Orchard Season" };
        db.AddRange(folder, gallery, group);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var video = new Video
        {
            Title = "Unrelated",
            VideoGalleries = [new() { Gallery = gallery }],
            Files = { new VideoFile { Basename = "orchard.mp4", Path = "/library/orchard.mp4", ParentFolderId = folder.Id, Format = "mp4" } },
        };
        db.Videos.Add(video);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.Add(new GroupItem { GroupId = group.Id, Kind = GroupItemKind.Video, HostType = "video", HostId = video.Id, VideoId = video.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var connection = db.Database.GetDbConnection();
        var options = new DbContextOptionsBuilder<CoveContext>();
        if (db.Database.IsNpgsql())
            options.UseNpgsql(connection, npgsql => npgsql.UseVector());
        else
            options.UseSqlite(connection);
        options.ConfigureWarnings(warnings => warnings.Throw(CoreEventId.RowLimitingOperationWithoutOrderByWarning));
        await using var strict = new CoveContext(options.Options);

        var search = await VideoTextSearch.CreateAsync(strict, "orchard", TestContext.Current.CancellationToken);
        var ids = await search.Apply(strict.Videos.AsNoTracking()).Select(row => row.Id).ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal([video.Id], ids);
    }

    [Fact]
    public async Task PathFallback_MatchesInsidePathSegments()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var folder = new Folder { Path = "/library" };
        var source = new Video
        {
            Title = "Source",
            Files = { new VideoFile { Basename = "abcNeedle123.mp4", Path = "/library/abcNeedle123.mp4", ParentFolder = folder, Format = "mp4" } },
        };
        var other = new Video
        {
            Title = "Other",
            Files = { new VideoFile { Basename = "haystack.mp4", Path = "/library/haystack.mp4", ParentFolder = folder, Format = "mp4" } },
        };
        db.Videos.AddRange(source, other);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "needle" }, TestContext.Current.CancellationToken);

        Assert.Equal(source.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.TotalCount);
    }

    // Sub-videos inherit their parent's FileSearchText, which SQLite's substring text match
    // already searches; only PostgreSQL shows that the path fallback itself excludes them.
    [Fact(SkipUnless = nameof(UsesPostgres), Skip = "Lexeme-based text matching requires PostgreSQL.")]
    public async Task PathFallback_ExcludesSubVideosOfTheMatchingFile()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var folder = new Folder { Path = "/library" };
        var source = new Video
        {
            Title = "Source",
            Files = { new VideoFile { Basename = "abcNeedle123.mp4", Path = "/library/abcNeedle123.mp4", ParentFolder = folder, Format = "mp4" } },
        };
        db.Videos.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        // Sub-video creation copies the source file summary, as VideosController does.
        db.Videos.Add(new Video { Title = "Clip", ParentVideoId = source.Id, ClipStartSec = 1, ClipEndSec = 2, FileSearchText = source.FileSearchText });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "needle" }, TestContext.Current.CancellationToken);

        Assert.Equal(source.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.TotalCount);
    }

    // SQLite's text match is itself a substring test over all paths, so only PostgreSQL shows
    // the path fallback's same-file rule.
    [Fact(SkipUnless = nameof(UsesPostgres), Skip = "Lexeme-based text matching requires PostgreSQL.")]
    public async Task PathFallback_RequiresEveryTermInOneFile()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var folder = new Folder { Path = "/library" };
        var together = new Video
        {
            Title = "Together",
            Files = { new VideoFile { Basename = "xxredxx_yybluey.mp4", Path = "/library/xxredxx_yybluey.mp4", ParentFolder = folder, Format = "mp4" } },
        };
        var split = new Video
        {
            Title = "Split",
            Files =
            {
                new VideoFile { Basename = "xxredxx.mp4", Path = "/library/xxredxx.mp4", ParentFolder = folder, Format = "mp4" },
                new VideoFile { Basename = "yybluey.mp4", Path = "/library/yybluey.mp4", ParentFolder = folder, Format = "mp4" },
            },
        };
        db.Videos.AddRange(together, split);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "red blue" }, TestContext.Current.CancellationToken);

        Assert.Equal(together.Id, Assert.Single(result.Items).Id);
        Assert.Equal(1, result.TotalCount);

        // The fallback past the candidate cap keeps the same-file rule.
        var unresolved = await VideoTextSearch.CreateAsync(db, "red blue", TestContext.Current.CancellationToken, pathSubqueryThreshold: 0);
        var fallbackIds = await unresolved.Apply(db.Videos.AsNoTracking()).Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(together.Id, Assert.Single(fallbackIds));
    }

    private sealed class SearchFixture(System.Data.Common.DbConnection connection, CoveContext db) : IAsyncDisposable
    {
        public CoveContext Db => db;
        public static async Task<SearchFixture> CreateAsync()
        {
            // Opt in using an existing migrated, disposable Cove PostgreSQL database
            // (including its public authorization functions). Every test gets its own
            // schema; no database-creation privilege is needed.
            var postgres = Environment.GetEnvironmentVariable("COVE_TEST_POSTGRES_CONNECTION");
            if (!string.IsNullOrWhiteSpace(postgres))
            {
                var schema = "video_search_test_" + Guid.NewGuid().ToString("N");
                var settings = new NpgsqlConnectionStringBuilder(postgres) { SearchPath = schema + ",public" };
                var pg = new NpgsqlConnection(settings.ConnectionString);
                await pg.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", pg);
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
                var pgDb = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseNpgsql(pg, options => options.UseVector()).Options);
                // The shot set CHECK constraints call these functions, so they must exist before the tables.
                await pgDb.Database.ExecuteSqlRawAsync(Cove.Data.Services.VideoShotSqlDefinitions.CreateFunctionsSql, TestContext.Current.CancellationToken);
                await pgDb.Database.ExecuteSqlRawAsync(pgDb.Database.GenerateCreateScript(), TestContext.Current.CancellationToken);
                // Retain the isolated schema for inspection in the disposable sidecar.
                return new(pg, pgDb);
            }
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var db = new CoveContext(new DbContextOptionsBuilder<CoveContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new(connection, db);
        }
        public async ValueTask DisposeAsync()
        {
            await db.DisposeAsync();
            await connection.DisposeAsync();
        }
    }

    [Fact]
    public async Task MixedTitleAndTags_ConfirmedTagOutranksDescriptionMention()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var sharedTag = new Tag { Name = "Twosome" };
        var kissing = new Tag { Name = "Kissing" };
        var confirmed = new Video { Title = "My Love", VideoTags = [new() { Tag = sharedTag }, new() { Tag = kissing }] };
        var mentioned = new Video { Title = "My Love", Details = "They are not ready for kissing", VideoTags = [new() { Tag = sharedTag }], UpdatedAt = DateTime.UtcNow.AddDays(1) };
        db.Videos.AddRange(mentioned, confirmed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "My Love Twosome Kissing", Sort = "relevance" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { confirmed.Id, mentioned.Id }, result.Items.Select(video => video.Id));
    }

    [Fact]
    public async Task TagMatchOutranksDescriptionPhrase_AndBothRemainSearchable()
    {
        await using var fixture = await SearchFixture.CreateAsync();
        var db = fixture.Db;
        var confirmed = new Video { Title = "Confirmed", VideoTags = [new() { Tag = new Tag { Name = "Kissing" } }] };
        var mentioned = new Video { Title = "Mentioned", Details = "They are not ready for kissing", UpdatedAt = DateTime.UtcNow.AddDays(1) };
        db.Videos.AddRange(mentioned, confirmed);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await new VideoRepository(db).FindAsync(null, new FindFilter { Q = "kissing", Sort = "relevance" }, TestContext.Current.CancellationToken);

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { confirmed.Id, mentioned.Id }, result.Items.Select(video => video.Id));
    }
}
