using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public class NameMatchOrderingTests
{
    private const int TestUserId = 1;

    [Fact]
    [RequiresPostgres]
    public async Task TagRepository_RelevanceSearch_OrdersFavoritesThenNameMatchQuality()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Tags.AddRange(
            new Tag { Name = "Grand Piano" },
            new Tag { Name = "Piano Solo" },
            new Tag { Name = "Piano" },
            new Tag { Name = "Piano Coda" },
            new Tag { Name = "Upright Piano" },
            new Tag { Name = "Toy Piano" },
            // Search matches whole words or word prefixes, so the name-substring match needs "piano" after a hyphen.
            new Tag { Name = "Electro-piano" },
            new Tag { Name = "Keyboard", Aliases = { new TagAlias { Alias = "Digital Piano" } } },
            new Tag { Name = "Music Lesson", Aliases = { new TagAlias { Alias = "Piano Lesson" } } },
            new Tag { Name = "Ivory Keys", Description = "Played on a piano." },
            new Tag { Name = "Unrelated" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoriteTagsAsync(context, "Upright Piano", "Music Lesson");

        var repository = new TagRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "piano", Page = 1, PerPage = 20, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(10, totalCount);
        Assert.Equal(
            [
                "Upright Piano",
                "Music Lesson",
                "Piano",
                "Piano Coda",
                "Piano Solo",
                "Toy Piano",
                "Grand Piano",
                "Electro-piano",
                "Keyboard",
                "Ivory Keys",
            ],
            items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagRepository_RelevanceSearch_PagesByMatchQualityInsteadOfName()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Tags.AddRange(
            Enumerable.Range(0, 30).Select(index => new Tag { Name = $"A{index:00} Piano" }).Append(new Tag { Name = "Piano" }));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new TagRepository(context);
        var (items, _) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "piano", Page = 1, PerPage = 5, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal("Piano", items[0].Name);
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerRepository_RelevanceSearch_OrdersFavoritesThenNameMatchQuality()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Performers.AddRange(
            new Performer { Name = "Golden Needle" },
            new Performer { Name = "Needle Baker" },
            new Performer { Name = "Needle Artist" },
            new Performer { Name = "Needle Actor" },
            new Performer { Name = "Needle" },
            new Performer { Name = "Jane Doe", Aliases = { new PerformerAlias { Alias = "Needle Stage Name" } } },
            new Performer { Name = "Sample Performer", Aliases = { new PerformerAlias { Alias = "The Needle" } } },
            new Performer { Name = "Other" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoritePerformersAsync(context, "Needle Artist", "Jane Doe");

        var repository = new PerformerRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "Needle", Page = 1, PerPage = 20, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(7, totalCount);
        Assert.Equal(
            [
                "Needle Artist",
                "Jane Doe",
                "Needle",
                "Needle Actor",
                "Needle Baker",
                "Golden Needle",
                "Sample Performer",
            ],
            items.Select(performer => performer.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task PerformerRepository_ExplicitNameSort_KeepsAlphabeticalOrderWhileSearching()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Performers.AddRange(
            new Performer { Name = "Needle Baker" },
            new Performer { Name = "Needle Artist" },
            new Performer { Name = "Needle" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoritePerformersAsync(context, "Needle Artist");

        var repository = new PerformerRepository(context);
        var (items, _) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "Needle", Page = 1, PerPage = 20, Sort = "name" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Needle", "Needle Artist", "Needle Baker"], items.Select(performer => performer.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task StudioRepository_RelevanceSearch_OrdersFavoritesThenNameMatchQuality()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Studios.AddRange(
            new Studio { Name = "Needle Films" },
            new Studio { Name = "Golden Needle" },
            new Studio { Name = "Needle" },
            new Studio { Name = "Needle Cinema" },
            new Studio { Name = "Sample Studio", Aliases = { new StudioAlias { Alias = "The Needle" } } },
            new Studio { Name = "Other" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoriteStudiosAsync(context, "Needle Cinema");

        var repository = new StudioRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "Needle", Page = 1, PerPage = 20, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, totalCount);
        Assert.Equal(
            ["Needle Cinema", "Needle", "Needle Films", "Golden Needle", "Sample Studio"],
            items.Select(studio => studio.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task GroupRepository_RelevanceSearch_OrdersNameMatchQualityThenAliasText()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Groups.AddRange(
            new Group { Name = "Needle Collection" },
            new Group { Name = "Golden Needle" },
            new Group { Name = "Needle" },
            new Group { Name = "Sample Group", Aliases = "The Needle" },
            new Group { Name = "Other" });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var repository = new GroupRepository(context);
        var (items, totalCount) = await repository.FindAsync(
            filter: null,
            new FindFilter { Q = "Needle", Page = 1, PerPage = 20, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(4, totalCount);
        Assert.Equal(
            ["Needle", "Needle Collection", "Golden Needle", "Sample Group"],
            items.Select(group => group.Name).ToArray());
    }

    [Fact]
    [RequiresPostgres]
    public async Task TagRepository_RelevanceSortWithoutSearchTerm_DoesNotApplyNameMatchOrdering()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var context = await CreateContextAsync(database);
        context.Tags.AddRange(
            new Tag { Name = "Piano", UpdatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Tag { Name = "Keyboard", UpdatedAt = new DateTime(2024, 1, 2, 0, 0, 0, DateTimeKind.Utc) });
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        await FavoriteTagsAsync(context, "Piano");

        var repository = new TagRepository(context);
        var (items, _) = await repository.FindAsync(
            filter: null,
            new FindFilter { Page = 1, PerPage = 20, Sort = "relevance" },
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(["Piano", "Keyboard"], items.Select(tag => tag.Name).ToArray());
    }

    [Fact]
    public void NameMatchOrdering_TranslatesToPostgresCaseExpression()
    {
        using var db = CreatePostgresContext();
        var query = NameMatchOrdering.Apply(db.Tags, " Piano ", tag => tag.Name, tag => EngagementQueryHelpers.FavoriteHostIds(db, TestUserId, AffinityHostType.Tag).Contains(tag.Id), tag => tag.Aliases.Select(alias => alias.Alias));

        var sql = query.Select(tag => tag.Id).ToQueryString();

        Assert.Contains("FROM user_entity_affinities AS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("t.\"Favorite\"", sql, StringComparison.Ordinal);
        Assert.Contains("CASE", sql, StringComparison.Ordinal);
        Assert.Contains("lower(t.\"Name\") = 'piano'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE 'piano%'", sql, StringComparison.Ordinal);
        Assert.Contains("LIKE '% piano%'", sql, StringComparison.Ordinal);
        Assert.Contains("FROM tag_aliases AS", sql, StringComparison.Ordinal);
        Assert.Contains("length(t.\"Name\")", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ts_rank", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NameMatchOrdering_TranslatesAliasTextWithoutFavoritesToPostgres()
    {
        using var db = CreatePostgresContext();
        var query = NameMatchOrdering.Apply(db.Groups, "needle", group => group.Name, aliasTextSelector: group => group.Aliases);

        var sql = query.Select(group => group.Id).ToQueryString();

        Assert.DoesNotContain("\"Favorite\"", sql, StringComparison.Ordinal);
        Assert.Contains("lower(g.\"Aliases\") LIKE '%needle%'", sql, StringComparison.Ordinal);
        Assert.Contains("length(g.\"Name\")", sql, StringComparison.Ordinal);
    }

    private static CoveContext CreatePostgresContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseNpgsql(
                "Host=localhost;Database=query_shape;Username=query_shape;Password=query_shape",
                npgsql => npgsql.UseVector())
            .Options;

        return new CoveContext(options);
    }

    private static Task FavoriteTagsAsync(CoveContext context, params string[] names)
        => FavoriteAsync(context, AffinityHostType.Tag, context.Tags.Where(tag => names.Contains(tag.Name)).Select(tag => tag.Id).ToArray());

    private static Task FavoritePerformersAsync(CoveContext context, params string[] names)
        => FavoriteAsync(context, AffinityHostType.Performer, context.Performers.Where(performer => names.Contains(performer.Name)).Select(performer => performer.Id).ToArray());

    private static Task FavoriteStudiosAsync(CoveContext context, params string[] names)
        => FavoriteAsync(context, AffinityHostType.Studio, context.Studios.Where(studio => names.Contains(studio.Name)).Select(studio => studio.Id).ToArray());

    private static async Task FavoriteAsync(CoveContext context, AffinityHostType hostType, int[] hostIds)
    {
        context.UserEntityAffinities.AddRange(hostIds.Select(hostId => new UserEntityAffinity { UserId = TestUserId, HostType = hostType, HostId = hostId, IsFavorite = true }));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<CoveContext> CreateContextAsync(CovePostgresDatabase database)
    {
        // The favorites are affinities of this user, so the user row has to exist.
        await using (var seed = database.CreateContext())
        {
            seed.Users.Add(new User { Id = TestUserId, Username = "test-user", PasswordHash = "test" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        await database.SyncIdentitySequencesAsync(TestContext.Current.CancellationToken);

        // CurrentPrincipalAccessor keeps the principal in an AsyncLocal, which does not flow out of this async method.
        var principalAccessor = new FixedPrincipalAccessor(new CovePrincipal
        {
            UserId = TestUserId,
            Username = "test-user",
            Kind = PrincipalKind.User,
            Permissions = new HashSet<string> { "*" },
            Roles = new HashSet<string>(),
        });

        return new CoveContext(database.CreateOptions(), principalAccessor);
    }

    private sealed class FixedPrincipalAccessor(CovePrincipal principal) : ICurrentPrincipalAccessor
    {
        public CovePrincipal? Current => principal;

        public void Set(CovePrincipal? value) => throw new NotSupportedException();
    }
}
