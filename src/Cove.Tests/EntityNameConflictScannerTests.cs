using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class EntityNameConflictScannerTests
{
    [Theory]
    [InlineData("\u00a0Alpha\u00a0", "Alpha", "alpha")]
    [InlineData("\u2003Alpha\u2003", "Alpha", "alpha")]
    [InlineData(" STRA\u00dfE ", "STRA\u00dfE", "stra\u00dfe")]
    public void SharedRules_UseDotNetTrimAndInvariantLowercase(
        string original,
        string expectedNormalized,
        string expectedKey)
    {
        Assert.Equal(expectedNormalized, EntityNameRules.NormalizeCanonicalName(original));
        Assert.Equal(expectedKey, EntityNameRules.NameKey(original));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    public void PerformerIdentity_TreatsBlankDisambiguationAsNull(string? disambiguation)
    {
        Assert.Equal(
            EntityNameRules.PerformerIdentityKey("Alpha", null),
            EntityNameRules.PerformerIdentityKey(" alpha ", disambiguation));
    }

    [Fact]
    public async Task PerformerScan_GroupsOnlyMatchingNameAndDisambiguationAndIgnoresAliases()
    {
        await using var db = CreateContext();
        var lowerId = new Performer { Name = " Alpha ", Disambiguation = " One " };
        var higherId = new Performer { Name = "alpha", Disambiguation = "one" };
        var distinctDisambiguation = new Performer { Name = "ALPHA", Disambiguation = "Two" };
        var aliasOwner = new Performer
        {
            Name = "Beta",
            Aliases = [new PerformerAlias { Alias = "Alpha" }],
        };
        var firstVideo = new Video { Title = "First performer scanner fixture" };
        var secondVideo = new Video { Title = "Second performer scanner fixture" };
        db.AddRange(lowerId, higherId, distinctDisambiguation, aliasOwner, firstVideo, secondVideo);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();
        db.Set<VideoPerformer>().AddRange(
            new VideoPerformer { VideoId = firstVideo.Id, PerformerId = higherId.Id },
            new VideoPerformer { VideoId = secondVideo.Id, PerformerId = higherId.Id });
        await db.SaveChangesAsync();

        var scan = await new EntityNameConflictScanner(db).ScanAsync(NameConflictEntityTypes.Performer);

        var group = Assert.Single(scan.Groups);
        Assert.Equal("Alpha", group.NormalizedName);
        Assert.Equal("One", group.NormalizedDisambiguation);
        Assert.Equal(higherId.Id, group.RecommendedSurvivorEntityId);
        Assert.Equal([lowerId.Id], group.RecommendedMergeEntityIds);
        Assert.DoesNotContain(group.Candidates, candidate => candidate.EntityId == distinctDisambiguation.Id);
        Assert.DoesNotContain(group.Candidates, candidate => candidate.EntityId == aliasOwner.Id);
        Assert.Equal(2, Assert.Single(group.Impacts, impact => impact.EntityId == higherId.Id).ReferenceCount);
    }

    [Fact]
    public async Task StudioScan_GroupsTrimmedCaseInsensitiveNamesAndUsesLowestIdOnTies()
    {
        await using var db = CreateContext();
        var lowerId = new Studio { Name = " Studio " };
        var higherId = new Studio { Name = "studio" };
        var aliasOwner = new Studio
        {
            Name = "Other",
            Aliases = [new StudioAlias { Alias = "STUDIO" }],
        };
        db.AddRange(lowerId, higherId, aliasOwner);
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();

        var scan = await new EntityNameConflictScanner(db).ScanAsync(NameConflictEntityTypes.Studio);

        var group = Assert.Single(scan.Groups);
        Assert.Equal("Studio", group.NormalizedName);
        Assert.Null(group.NormalizedDisambiguation);
        Assert.Equal(lowerId.Id, group.RecommendedSurvivorEntityId);
        Assert.DoesNotContain(group.Candidates, candidate => candidate.EntityId == aliasOwner.Id);
    }

    [Fact]
    public async Task SummaryScan_DoesNotLoadImpactOrExtensionData()
    {
        await using var db = CreateContext();
        db.Performers.AddRange(
            new Performer { Name = "Duplicate" },
            new Performer { Name = " duplicate ", Disambiguation = " " });
        db.Studios.AddRange(
            new Studio { Name = "Shared" },
            new Studio { Name = "shared" });
        using (db.SuppressEntityNameValidation())
            await db.SaveChangesAsync();

        var summary = await new EntityNameConflictScanner(db, new ThrowingExternalReferenceInspector())
            .ScanSummaryAsync();

        Assert.Equal(1, summary.PerformerUnresolvedGroupCount);
        Assert.Equal(1, summary.StudioUnresolvedGroupCount);
        Assert.Equal(2, summary.UnresolvedGroupCount);
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

    private sealed class ThrowingExternalReferenceInspector : IEntityExternalReferenceInspector
    {
        public Task<IReadOnlyList<EntityExternalReferenceDto>> InspectAsync(
            string entityType,
            IReadOnlyCollection<int> entityIds,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Summary scans must not load impact data.");

        public Task ApplyResolutionsAsync(
            string entityType,
            int targetEntityId,
            IReadOnlyCollection<EntityExternalReferenceResolutionDto> resolutions,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
