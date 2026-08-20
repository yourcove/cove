using Cove.Api.Controllers;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class MetadataImportIdentityTests
{
    [Fact]
    public async Task ImportStudiosAsync_CollapsesNormalizedInputIntoAnExistingIdentity()
    {
        await using var db = CreateContext();
        var existing = new Studio { Name = "Existing studio", Details = "Old" };
        db.Studios.Add(existing);
        await db.SaveChangesAsync();

        await MetadataController.ImportStudiosAsync(
            db,
            [
                new Studio { Name = " existing STUDIO ", Details = "First" },
                new Studio { Name = "EXISTING STUDIO", Details = "Last", Favorite = true },
            ],
            overwrite: true,
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var studio = await db.Studios.SingleAsync();
        Assert.Equal(existing.Id, studio.Id);
        Assert.Equal("Existing studio", studio.Name);
        Assert.Equal("Last", studio.Details);
        Assert.True(studio.Favorite);
    }

    [Fact]
    public async Task ImportPerformersAsync_CollapsesExactNormalizedPairsButKeepsDifferentDisambiguations()
    {
        await using var db = CreateContext();

        await MetadataController.ImportPerformersAsync(
            db,
            [
                new Performer { Name = " Shared name ", Disambiguation = " First ", Details = "Earlier" },
                new Performer { Name = "SHARED NAME", Disambiguation = "FIRST", Details = "Later", Favorite = true },
                new Performer { Name = "Shared name", Disambiguation = "Second" },
            ],
            overwrite: false,
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var performers = await db.Performers.OrderBy(performer => performer.Disambiguation).ToListAsync();
        Assert.Equal(2, performers.Count);
        var first = performers.Single(performer => performer.Disambiguation == "First");
        Assert.Equal("Shared name", first.Name);
        Assert.Equal("Later", first.Details);
        Assert.True(first.Favorite);
        Assert.Contains(performers, performer => performer.Disambiguation == "Second");
    }

    [Fact]
    public async Task ImportPerformersAsync_RejectsAnAmbiguousLegacyTargetInsteadOfChoosingOne()
    {
        await using var db = CreateContext();
        db.Performers.AddRange(
            new Performer { Name = "Legacy", Disambiguation = "Pair" },
            new Performer { Name = " legacy ", Disambiguation = " pair " });
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<EntityNameConflictException>(() =>
            MetadataController.ImportPerformersAsync(
                db,
                [new Performer { Name = "LEGACY", Disambiguation = "PAIR" }],
                overwrite: true,
                CancellationToken.None));

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
