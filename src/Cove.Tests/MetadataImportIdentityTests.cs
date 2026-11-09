using Cove.Api.Controllers;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class MetadataImportIdentityTests
{
    [Fact]
    public async Task ImportStudiosAsync_CollapsesNormalizedInputIntoAnExistingIdentity()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        var existing = new Studio { Name = "Existing studio", Details = "Old" };
        db.Studios.Add(existing);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await MetadataController.ImportStudiosAsync(
            db,
            [
                new Studio { Name = " existing STUDIO ", Details = "First" },
                new Studio { Name = "EXISTING STUDIO", Details = "Last" },
            ],
            overwrite: true,
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var studio = await db.Studios.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(existing.Id, studio.Id);
        Assert.Equal("Existing studio", studio.Name);
        Assert.Equal("Last", studio.Details);
    }

    [Fact]
    public async Task ImportPerformersAsync_CollapsesExactNormalizedPairsButKeepsDifferentDisambiguations()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();

        await MetadataController.ImportPerformersAsync(
            db,
            [
                new Performer { Name = " Shared name ", Disambiguation = " First ", Details = "Earlier" },
                new Performer { Name = "SHARED NAME", Disambiguation = "FIRST", Details = "Later" },
                new Performer { Name = "Shared name", Disambiguation = "Second" },
            ],
            overwrite: false,
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var performers = await db.Performers.OrderBy(performer => performer.Disambiguation).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(2, performers.Count);
        var first = performers.Single(performer => performer.Disambiguation == "First");
        Assert.Equal("Shared name", first.Name);
        Assert.Equal("Later", first.Details);
        Assert.Contains(performers, performer => performer.Disambiguation == "Second");
    }

    [Fact]
    public async Task ImportPerformersAsync_RejectsAnAmbiguousLegacyTargetInsteadOfChoosingOne()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var db = database.CreateContext();
        // The name constraints refuse a committed duplicate but allow one until the transaction commits, so the
        // legacy pair is held in a transaction that is never committed.
        await using var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        db.Performers.AddRange(
            new Performer { Name = "Legacy", Disambiguation = "Pair" },
            new Performer { Name = " legacy ", Disambiguation = " pair " });
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() =>
            MetadataController.ImportPerformersAsync(
                db,
                [new Performer { Name = "LEGACY", Disambiguation = "PAIR" }],
                overwrite: true,
                CancellationToken.None));

        Assert.Equal(NameConflictEntityTypes.Performer, exception.EntityType);
    }
}
