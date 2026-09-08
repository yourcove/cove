using Cove.Core.Entities;
using Cove.Core.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Auth;
using Cove.Data.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
        db.Videos.AddRange(tagged, recent, tagOnly);
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
