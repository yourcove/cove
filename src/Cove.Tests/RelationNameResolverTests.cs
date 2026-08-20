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
        await db.SaveChangesAsync();

        var matches = await RelationNameResolver.ResolvePerformersAsync(db, ["I", "ı"]);

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
        await db.SaveChangesAsync();

        var matches = await RelationNameResolver.ResolveStudiosAsync(db, ["I", "ı"]);

        Assert.Equal(2, matches.Count);
        Assert.Equal(latinI.Id, matches["I"].Id);
        Assert.Equal(dotlessI.Id, matches["ı"].Id);
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
        await db.SaveChangesAsync();

        var matches = await RelationNameResolver.ResolvePerformersAsync(db, ["Shared relation"]);

        Assert.Empty(matches);
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
