using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class RelationNameResolverTests
{
    [Fact]
    public async Task ResolvePerformersAsync_UsesTheManagedIdentityKeyForUnicodeNames()
    {
        await using var db = CreateContext();
        var latinI = new Performer { Name = "I" };
        var dotlessI = new Performer { Name = "ı" };
        db.Performers.AddRange(latinI, dotlessI);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await RelationNameResolver.ResolvePerformersAsync(db, ["I", "ı"], TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal(latinI.Id, matches["I"].Id);
        Assert.Equal(dotlessI.Id, matches["ı"].Id);
    }

    [Fact]
    public async Task ResolveStudiosAsync_UsesTheManagedIdentityKeyForUnicodeNames()
    {
        await using var db = CreateContext();
        var latinI = new Studio { Name = "I" };
        var dotlessI = new Studio { Name = "ı" };
        db.Studios.AddRange(latinI, dotlessI);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await RelationNameResolver.ResolveStudiosAsync(db, ["I", "ı"], TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal(latinI.Id, matches["I"].Id);
        Assert.Equal(dotlessI.Id, matches["ı"].Id);
    }

    [Fact]
    public async Task ResolveStudiosAsync_PrefersCanonicalNamesAndFallsBackToAliases()
    {
        await using var db = CreateContext();
        var aliasOwner = new Studio
        {
            Name = "Surviving Studio",
            Aliases =
            [
                new StudioAlias { Alias = "Merged Studio" },
                new StudioAlias { Alias = "Canonical Studio" },
            ],
        };
        var canonicalOwner = new Studio { Name = "Canonical Studio" };
        db.Studios.AddRange(aliasOwner, canonicalOwner);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await RelationNameResolver.ResolveStudiosAsync(
            db,
            [" merged studio ", "CANONICAL STUDIO"],
            TestContext.Current.CancellationToken);
        var singleMatch = await RelationNameResolver.ResolveStudioAsync(
            db,
            "MERGED STUDIO",
            TestContext.Current.CancellationToken);

        Assert.Equal(aliasOwner.Id, matches["merged studio"].Id);
        Assert.Equal(canonicalOwner.Id, matches["CANONICAL STUDIO"].Id);
        Assert.Equal(aliasOwner.Id, singleMatch?.Id);
    }

    [Fact]
    public async Task NameOnlyPerformerRelations_DoNotResolveAliasesOrDisambiguatedIdentities()
    {
        await using var db = CreateContext();
        db.Performers.AddRange(
            new Performer
            {
                Name = "Canonical one",
                Aliases = [new PerformerAlias { Alias = "Shared relation" }],
            },
            new Performer
            {
                Name = "Shared relation",
                Disambiguation = "Specific person",
                Aliases = [new PerformerAlias { Alias = "Shared relation" }],
            });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await RelationNameResolver.ResolvePerformersAsync(db, ["Shared relation"], TestContext.Current.CancellationToken);

        Assert.Empty(matches);
    }

    [Fact]
    public async Task ResolveTagsAsync_MatchesNamesAndAliasesByTheirNamespaceKey()
    {
        await using var db = CreateContext();
        var worship = new Tag { Name = "Tit Worship", Aliases = [new TagAlias { Alias = "Tit Tease" }] };
        var blonde = new Tag { Name = "Blonde" };
        db.Tags.AddRange(worship, blonde);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var matches = await RelationNameResolver.ResolveTagsAsync(
            db,
            [" tit tease ", "BLONDE", "Brand new"],
            TestContext.Current.CancellationToken);

        Assert.Equal(2, matches.Count);
        Assert.Equal(worship.Id, matches["tit tease"].Id);
        Assert.Equal(blonde.Id, matches["blonde"].Id);
        Assert.Contains(matches["tit tease"].Aliases, alias => alias.Alias == "Tit Tease");
    }

    [Fact]
    public async Task ResolveTagsAsync_LoadsOnlyTheMatchedTags()
    {
        await using var db = CreateContext();
        var wanted = new Tag { Name = "Wanted" };
        var aliased = new Tag { Name = "Aliased", Aliases = [new TagAlias { Alias = "Other name" }] };
        db.Tags.AddRange(wanted, aliased);
        for (var index = 0; index < 50; index++)
            db.Tags.Add(new Tag { Name = $"Unrelated {index}", Aliases = [new TagAlias { Alias = $"Unrelated alias {index}" }] });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();

        var matches = await RelationNameResolver.ResolveTagsAsync(
            db,
            ["wanted", "other name"],
            TestContext.Current.CancellationToken);

        Assert.Equal([wanted.Id, aliased.Id], matches.Values.Select(tag => tag.Id).Order().ToArray());
        Assert.Equal(
            [wanted.Id, aliased.Id],
            db.ChangeTracker.Entries<Tag>().Select(entry => entry.Entity.Id).Order().ToArray());
    }

    [Fact]
    public async Task ResolvePerformerAsync_FindsTheExactIdentityByItsStoredKey()
    {
        await using var db = CreateContext();
        var plain = new Performer { Name = "Jane Doe" };
        var disambiguated = new Performer { Name = "Jane Doe", Disambiguation = "Other person" };
        db.Performers.AddRange(plain, disambiguated);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exact = await RelationNameResolver.ResolvePerformerAsync(db, " jane doe ", "OTHER PERSON", TestContext.Current.CancellationToken);
        var nameOnly = await RelationNameResolver.ResolvePerformersAsync(db, ["JANE DOE"], TestContext.Current.CancellationToken);

        Assert.Equal(disambiguated.Id, exact?.Id);
        Assert.Equal(plain.Id, nameOnly["JANE DOE"].Id);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;
        var context = new CoveContext(options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }
}
