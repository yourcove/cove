using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class EntityNameWriteValidationTests
{
    [Fact]
    public async Task AsyncValidationRejectsIdentitiesBeyondSeveralReadPages()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        db.Performers.AddRange(Enumerable.Range(0, 600).Select(index => new Performer { Name = $"performer {index}" }));
        db.Studios.AddRange(Enumerable.Range(0, 600).Select(index => new Studio { Name = $"studio {index}" }));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        db.ChangeTracker.Clear();
        // The name constraints would refuse these rows too, but with a message that names no existing identity;
        // these messages come only from the validation, so they show the validation found each conflict.
        db.Performers.Add(new Performer { Name = " PERFORMER 599 " });
        var performerConflict = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "A performer with name \"performer 599\" and no disambiguation already exists. Performer name and disambiguation combinations must be unique.",
            performerConflict.Message);
        db.ChangeTracker.Clear();
        db.Studios.Add(new Studio { Name = " STUDIO 599 " });
        var studioConflict = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("A studio with name \"studio 599\" already exists. Studio names must be unique.", studioConflict.Message);
    }

    [Fact]
    public async Task PerformerWrites_TrimIdentityAndRejectMatchingNameDisambiguationPairs()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var first = new Performer { Name = "Alpha", Disambiguation = null };
        db.Performers.Add(first);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Performers.Add(new Performer { Name = " alpha ", Disambiguation = " \t " });
        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() => db.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(
            "A performer with name \"Alpha\" and no disambiguation already exists. Performer name and disambiguation combinations must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task StudioWrites_TrimNamesAndRejectCaseFoldedDuplicates()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        // The name constraints refuse a committed duplicate but allow one until the transaction commits, so the
        // pre-existing conflict is held in a transaction that is never committed.
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var first = new Performer { Name = "Duplicate", Disambiguation = "One" };
        var second = new Performer { Name = " duplicate ", Disambiguation = "one" };
        var firstStudio = new Studio { Name = "Duplicate studio" };
        var secondStudio = new Studio { Name = " duplicate STUDIO " };
        db.AddRange(first, second, firstStudio, secondStudio);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        second.Details = "Updated without changing its identity";
        await new PerformerRepository(db).UpdateAsync(second, TestContext.Current.CancellationToken);
        secondStudio.Details = "Updated without changing its identity";
        await new StudioRepository(db).UpdateAsync(secondStudio, TestContext.Current.CancellationToken);

        Assert.Equal("Updated without changing its identity", second.Details);
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
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
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
        Assert.Equal(
            "A performer with name \"Alpha\" and disambiguation \"One\" already exists. Performer name and disambiguation combinations must be unique.",
            exception.Message);
    }
}
