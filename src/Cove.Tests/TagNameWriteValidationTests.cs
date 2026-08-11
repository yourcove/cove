using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class TagNameWriteValidationTests
{
    [Fact]
    public async Task SaveChanges_NormalizesNewClaimsAndRemovesBlankAliases()
    {
        await using var db = CreateContext();
        var tag = new Tag
        {
            Name = "  Alpha  ",
            Aliases =
            [
                new TagAlias { Alias = "  Alternate  " },
                new TagAlias { Alias = " \t " },
            ],
        };
        db.Tags.Add(tag);

        await db.SaveChangesAsync();

        Assert.Equal("Alpha", tag.Name);
        Assert.Equal("alpha", tag.NamespaceKey);
        var alias = Assert.Single(tag.Aliases);
        Assert.Equal("Alternate", alias.Alias);
        Assert.Equal("alternate", alias.NamespaceKey);
    }

    [Fact]
    public async Task SaveChanges_UsesEmptySentinelAndRejectsTheSharedNamespace()
    {
        await using var db = CreateContext();
        var empty = new Tag { Name = "   " };
        var aliasOwner = new Tag { Name = "Owner", Aliases = [new TagAlias { Alias = " Reserved " }] };
        db.Tags.AddRange(empty, aliasOwner);
        await db.SaveChangesAsync();

        Assert.Equal(TagNameRules.EmptyCanonicalName, empty.Name);
        var conflicting = new Tag { Name = "reserved" };
        db.Tags.Add(conflicting);

        var exception = await Assert.ThrowsAsync<TagNameConflictException>(() => db.SaveChangesAsync());
        Assert.Equal("reserved", exception.ConflictingName);
    }

    [Fact]
    public async Task SaveChanges_AllowsMetadataEditsOnAHistoricalConflictWhenTheNameIsUnchanged()
    {
        await using var db = CreateContext();
        var first = new Tag { Name = " Historical " };
        var second = new Tag { Name = "historical" };
        db.Tags.AddRange(first, second);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        Assert.Equal(" Historical ", first.Name);
        Assert.Equal("historical", first.NamespaceKey);
        Assert.Equal("historical", second.NamespaceKey);
        second.Description = "Metadata can still be repaired";
        await db.SaveChangesAsync();

        Assert.Equal("Metadata can still be repaired", second.Description);
    }

    [Fact]
    public async Task TagRepositoryUpdate_AllowsMetadataEditsOnAHistoricalConflictWhenTheNameIsUnchanged()
    {
        await using var db = CreateContext();
        var first = new Tag { Name = " Historical " };
        var second = new Tag { Name = "historical" };
        db.Tags.AddRange(first, second);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        second.Description = "Repository metadata repair";
        await new TagRepository(db).UpdateAsync(second);

        Assert.Equal("Repository metadata repair", second.Description);
    }

    [Fact]
    public async Task SaveChanges_RepairsKeyOnlyTagNamespaceMutations()
    {
        await using var db = CreateContext();
        var tag = new Tag
        {
            Name = "Canonical",
            Aliases = [new TagAlias { Alias = "Alternate" }],
        };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        tag.NamespaceKey = "tampered-tag-key";
        var alias = Assert.Single(tag.Aliases);
        alias.NamespaceKey = "tampered-alias-key";
        await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var persisted = await db.Tags.Include(entity => entity.Aliases).SingleAsync();
        Assert.Equal("canonical", persisted.NamespaceKey);
        Assert.Equal("alternate", Assert.Single(persisted.Aliases).NamespaceKey);
    }

    [Fact]
    public async Task SuppressedSaveChanges_RepairsKeyOnlyTagNamespaceMutations()
    {
        await using var db = CreateContext();
        var tag = new Tag
        {
            Name = "Canonical",
            Aliases = [new TagAlias { Alias = "Alternate" }],
        };
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        tag.NamespaceKey = "tampered-tag-key";
        var alias = Assert.Single(tag.Aliases);
        alias.NamespaceKey = "tampered-alias-key";
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        db.ChangeTracker.Clear();
        var persisted = await db.Tags.Include(entity => entity.Aliases).SingleAsync();
        Assert.Equal("canonical", persisted.NamespaceKey);
        Assert.Equal("alternate", Assert.Single(persisted.Aliases).NamespaceKey);
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
