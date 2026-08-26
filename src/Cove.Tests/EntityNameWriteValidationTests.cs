using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class EntityNameWriteValidationTests
{
    [Fact]
    public async Task PerformerWrites_TrimIdentityAndRejectMatchingNameDisambiguationPairs()
    {
        await using var db = CreateContext();
        var first = new Performer { Name = " Alpha ", Disambiguation = " One " };
        db.Performers.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Alpha", first.Name);
        Assert.Equal("One", first.Disambiguation);
        Assert.Equal(EntityNameRules.PerformerIdentityKey("Alpha", "One"), first.IdentityKey);

        db.Performers.Add(new Performer { Name = "alpha", Disambiguation = "one" });
        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(NameConflictEntityTypes.Performer, exception.EntityType);
        Assert.Equal(
            "A performer with name \"Alpha\" and disambiguation \"One\" already exists. Performer name and disambiguation combinations must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task PerformerWrites_AllowSameNameWithDifferentDisambiguationAndOverlappingAliases()
    {
        await using var db = CreateContext();
        db.Performers.AddRange(
            new Performer
            {
                Name = "Alpha",
                Disambiguation = "One",
                Aliases = [new PerformerAlias { Alias = "Shared" }],
            },
            new Performer
            {
                Name = "alpha",
                Disambiguation = "Two",
                Aliases = [new PerformerAlias { Alias = "Alpha" }, new PerformerAlias { Alias = "Shared" }],
            });

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, await db.Performers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(3, await db.Set<PerformerAlias>().CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PerformerWrites_TreatBlankDisambiguationAsNull()
    {
        await using var db = CreateContext();
        var first = new Performer { Name = "Alpha", Disambiguation = null };
        db.Performers.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Performers.Add(new Performer { Name = " alpha ", Disambiguation = " \t " });
        await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StudioWrites_TrimNamesAndRejectCaseFoldedDuplicates()
    {
        await using var db = CreateContext();
        var first = new Studio { Name = " Studio " };
        db.Studios.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Studio", first.Name);
        Assert.Equal(EntityNameRules.StudioIdentityKey("Studio"), first.NameKey);
        db.Studios.Add(new Studio { Name = "studio" });

        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(NameConflictEntityTypes.Studio, exception.EntityType);
        Assert.Equal(
            "A studio with name \"Studio\" already exists. Studio names must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task PerformerWrites_IdentifyAnExistingIdentityWithoutDisambiguation()
    {
        await using var db = CreateContext();
        db.Performers.Add(new Performer { Name = "Single Name" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Performers.Add(new Performer { Name = " single NAME ", Disambiguation = " " });
        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            "A performer with name \"Single Name\" and no disambiguation already exists. Performer name and disambiguation combinations must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task RepositoryUnrelatedUpdates_NormalizeButDoNotBlockOnPreExistingConflicts()
    {
        await using var db = CreateContext();
        var first = new Performer { Name = "Duplicate", Disambiguation = "One" };
        var second = new Performer { Name = " duplicate ", Disambiguation = "one" };
        var firstStudio = new Studio { Name = "Duplicate studio" };
        var secondStudio = new Studio { Name = " duplicate STUDIO " };
        db.AddRange(first, second, firstStudio, secondStudio);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        second.Favorite = true;
        await new PerformerRepository(db).UpdateAsync(second, TestContext.Current.CancellationToken);
        secondStudio.Details = "Updated without changing its identity";
        await new StudioRepository(db).UpdateAsync(secondStudio, TestContext.Current.CancellationToken);

        Assert.True(second.Favorite);
        Assert.Equal("duplicate", second.Name);
        Assert.Equal("one", second.Disambiguation);
        Assert.Equal(EntityNameRules.PerformerIdentityKey(second.Name, second.Disambiguation), second.IdentityKey);
        Assert.Equal("duplicate STUDIO", secondStudio.Name);
        Assert.Equal(EntityNameRules.StudioIdentityKey(secondStudio.Name), secondStudio.NameKey);
        Assert.Equal("Updated without changing its identity", secondStudio.Details);
    }

    [Fact]
    public async Task DetachedRepositoryUpdate_StillRejectsAnIdentityChangeThatCollides()
    {
        await using var db = CreateContext();
        var first = new Performer { Name = "Alpha", Disambiguation = "One" };
        var second = new Performer { Name = "Beta", Disambiguation = "Two" };
        db.Performers.AddRange(first, second);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.ChangeTracker.Clear();
        var detached = await db.Performers.AsNoTracking().SingleAsync(performer => performer.Id == second.Id, cancellationToken: TestContext.Current.CancellationToken);
        detached.Name = " alpha ";
        detached.Disambiguation = " one ";

        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(
            () => new PerformerRepository(db).UpdateAsync(detached, TestContext.Current.CancellationToken));

        Assert.Equal(NameConflictEntityTypes.Performer, exception.EntityType);
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
