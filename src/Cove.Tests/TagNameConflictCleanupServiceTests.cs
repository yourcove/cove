using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Tests;

public sealed class TagNameConflictCleanupServiceTests
{
    [Theory]
    [InlineData(TagExternalReferenceActions.UpdateToSurvivor)]
    [InlineData(TagExternalReferenceActions.DeleteRows)]
    public async Task ResolveAsync_AppliesReviewedNonCoreReferenceRepairBeforeMerging(string repairAction)
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag { Name = " alpha " };
        db.Tags.AddRange(survivor, source);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var inspector = new MutableExternalReferenceInspector(source.Id, rowCount: 2);
        var scanner = new TagNameConflictScanner(db, inspector);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var externalReference = Assert.Single(
            Assert.Single(group.Impacts, impact => impact.TagId == source.Id).ExternalReferences);
        var cleanup = new TagNameConflictCleanupService(
            db,
            scanner,
            new TagMergeService(db, externalReferenceInspector: inspector),
            externalReferenceInspector: inspector);

        var refreshed = await cleanup.ResolveAsync(
            group.Key,
            group.Revision,
            survivor.Id,
            [new TagNameClaimResolutionDto(source.Id, null, TagNameConflictActions.MergeTag)],
            [new TagExternalReferenceResolutionDto(source.Id, externalReference.ReferenceKey, repairAction)]);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == source.Id));
        var applied = Assert.Single(inspector.AppliedResolutions);
        Assert.Equal(survivor.Id, inspector.AppliedTargetTagId);
        Assert.Equal(repairAction, applied.Action);
    }

    [Fact]
    public async Task ResolveAsync_RequiresAReviewedActionForEveryNonCoreReferenceOnAMergeSource()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag { Name = " alpha " };
        db.Tags.AddRange(survivor, source);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var inspector = new MutableExternalReferenceInspector(source.Id, rowCount: 2);
        var scanner = new TagNameConflictScanner(db, inspector);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var cleanup = new TagNameConflictCleanupService(
            db,
            scanner,
            new TagMergeService(db, externalReferenceInspector: inspector),
            externalReferenceInspector: inspector);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => cleanup.ResolveAsync(
            group.Key,
            group.Revision,
            survivor.Id,
            [new TagNameClaimResolutionDto(source.Id, null, TagNameConflictActions.MergeTag)],
            [],
            default));

        Assert.Contains("every non-core reference", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == source.Id));
        Assert.Empty(inspector.AppliedResolutions);
    }

    [Fact]
    public async Task ResolveAsync_BlocksRestrictedNonCoreReferencesWithoutOfferingGenericRepair()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag { Name = " alpha " };
        db.Tags.AddRange(survivor, source);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var inspector = new MutableExternalReferenceInspector(source.Id, rowCount: 1);
        inspector.SetAccessLimitation(source.Id, TagExternalReferenceAccessLimitations.RowLevelSecurity);
        var scanner = new TagNameConflictScanner(db, inspector);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var reference = Assert.Single(
            Assert.Single(group.Impacts, impact => impact.TagId == source.Id).ExternalReferences);
        Assert.Null(reference.RowCount);
        var cleanup = new TagNameConflictCleanupService(
            db,
            scanner,
            new TagMergeService(db, externalReferenceInspector: inspector),
            externalReferenceInspector: inspector);

        var exception = await Assert.ThrowsAsync<TagExternalReferenceRepairException>(() => cleanup.ResolveAsync(
            group.Key,
            group.Revision,
            survivor.Id,
            [new TagNameClaimResolutionDto(source.Id, null, TagNameConflictActions.MergeTag)],
            [],
            default));

        Assert.Contains("cannot be inspected", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == source.Id));
        Assert.Empty(inspector.AppliedResolutions);
    }

    [Fact]
    public async Task ResolveAsync_RejectsAStaleNonCoreReferenceInventory()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag { Name = " alpha " };
        db.Tags.AddRange(survivor, source);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var inspector = new MutableExternalReferenceInspector(source.Id, rowCount: 1);
        var scanner = new TagNameConflictScanner(db, inspector);
        var staleGroup = Assert.Single((await scanner.ScanAsync()).Groups);
        var externalReference = Assert.Single(
            Assert.Single(staleGroup.Impacts, impact => impact.TagId == source.Id).ExternalReferences);
        inspector.SetRowCount(source.Id, 2);
        var cleanup = new TagNameConflictCleanupService(
            db,
            scanner,
            new TagMergeService(db, externalReferenceInspector: inspector),
            externalReferenceInspector: inspector);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ResolveAsync(
            staleGroup.Key,
            staleGroup.Revision,
            survivor.Id,
            [new TagNameClaimResolutionDto(source.Id, null, TagNameConflictActions.MergeTag)],
            [new TagExternalReferenceResolutionDto(
                source.Id,
                externalReference.ReferenceKey,
                TagExternalReferenceActions.UpdateToSurvivor)],
            default));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == source.Id));
        Assert.Empty(inspector.AppliedResolutions);
    }

    [Fact]
    public async Task ResolveAsync_CanMixMergeAndAliasRemovalWithinOneGroup()
    {
        await using var db = CreateContext();
        var main = new Tag { Name = "Shared" };
        var mergeOwner = new Tag
        {
            Name = "Merge owner",
            Description = "Metadata to transfer",
            Aliases = [new TagAlias { Alias = " shared " }],
        };
        var aliasOnlyOwner = new Tag
        {
            Name = "Alias-only owner",
            Aliases = [new TagAlias { Alias = "SHARED" }],
        };
        db.Tags.AddRange(main, mergeOwner, aliasOnlyOwner);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var mergeAliasId = Assert.Single(mergeOwner.Aliases).Id;
        var removeAliasId = Assert.Single(aliasOnlyOwner.Aliases).Id;
        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups, candidate => candidate.NormalizedName == "Shared");
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var refreshed = await cleanup.ResolveAsync(
            group.Key,
            main.Id,
            [
                new TagNameClaimResolutionDto(mergeOwner.Id, mergeAliasId, TagNameConflictActions.MergeTag),
                new TagNameClaimResolutionDto(aliasOnlyOwner.Id, removeAliasId, TagNameConflictActions.RemoveAlias),
            ]);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == mergeOwner.Id));
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == aliasOnlyOwner.Id));
        Assert.False(await db.Set<TagAlias>().AnyAsync(alias => alias.Id == removeAliasId));
        Assert.Equal("Metadata to transfer", (await db.Tags.SingleAsync(tag => tag.Id == main.Id)).Description);
    }

    [Fact]
    public async Task ResolveAsync_CanRemoveASurvivorAliasAndMergeAnotherAliasOwner()
    {
        await using var db = CreateContext();
        var survivor = new Tag
        {
            Name = "Survivor",
            Aliases =
            [
                new TagAlias { Alias = "Shared alias" },
                new TagAlias { Alias = " shared alias " },
            ],
        };
        var mergeOwner = new Tag
        {
            Name = "Merge owner",
            Aliases = [new TagAlias { Alias = "SHARED ALIAS" }],
        };
        db.Tags.AddRange(survivor, mergeOwner);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var survivorAliases = survivor.Aliases.OrderBy(alias => alias.Id).ToArray();
        var mergeAlias = Assert.Single(mergeOwner.Aliases);
        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var refreshed = await cleanup.ResolveAsync(
            group.Key,
            survivor.Id,
            [
                new TagNameClaimResolutionDto(survivor.Id, survivorAliases[1].Id, TagNameConflictActions.RemoveAlias),
                new TagNameClaimResolutionDto(mergeOwner.Id, mergeAlias.Id, TagNameConflictActions.MergeTag),
            ]);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == mergeOwner.Id));
        var aliases = await db.Set<TagAlias>().Where(alias => alias.TagId == survivor.Id).ToListAsync();
        Assert.Single(aliases, alias => TagNameRules.NamesEqual(alias.Alias, "Shared alias"));
    }

    [Fact]
    public async Task ResolveAsync_CanRenameCanonicalAndAliasClaimsInsteadOfMerging()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var renamedTag = new Tag { Name = " alpha " };
        var aliasOwner = new Tag { Name = "Alias owner", Aliases = [new TagAlias { Alias = "ALPHA" }] };
        db.Tags.AddRange(survivor, renamedTag, aliasOwner);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var aliasId = Assert.Single(aliasOwner.Aliases).Id;
        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var refreshed = await cleanup.ResolveAsync(
            group.Key,
            survivor.Id,
            [
                new TagNameClaimResolutionDto(renamedTag.Id, null, TagNameConflictActions.Rename, "Beta"),
                new TagNameClaimResolutionDto(aliasOwner.Id, aliasId, TagNameConflictActions.Rename, "Gamma"),
            ]);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Equal("Beta", (await db.Tags.SingleAsync(tag => tag.Id == renamedTag.Id)).Name);
        Assert.Equal("Gamma", (await db.Set<TagAlias>().SingleAsync(alias => alias.Id == aliasId)).Alias);
        Assert.Equal(3, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_RejectsMixedMergeAndNonMergeActionsForClaimsOnTheSameTag()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag
        {
            Name = " alpha ",
            Aliases = [new TagAlias { Alias = "ALPHA" }],
        };
        db.Tags.AddRange(survivor, source);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var sourceAlias = Assert.Single(source.Aliases);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => cleanup.ResolveAsync(
            group.Key,
            group.Revision,
            survivor.Id,
            [
                new TagNameClaimResolutionDto(source.Id, null, TagNameConflictActions.Rename, "Beta"),
                new TagNameClaimResolutionDto(source.Id, sourceAlias.Id, TagNameConflictActions.MergeTag),
            ]));

        Assert.Contains("every claim", exception.Message, StringComparison.OrdinalIgnoreCase);
        db.ChangeTracker.Clear();
        Assert.Equal(2, await db.Tags.AsNoTracking().CountAsync());
        Assert.Equal(" alpha ", (await db.Tags.AsNoTracking().SingleAsync(tag => tag.Id == source.Id)).Name);
    }

    [Fact]
    public async Task ResolveAsync_RejectsARenameThatClaimsAnotherNamespaceAndRollsBack()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var conflicting = new Tag { Name = " alpha " };
        var occupied = new Tag { Name = "Beta" };
        db.Tags.AddRange(survivor, conflicting, occupied);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        await Assert.ThrowsAsync<TagNameConflictException>(() => cleanup.ResolveAsync(
            group.Key,
            survivor.Id,
            [new TagNameClaimResolutionDto(conflicting.Id, null, TagNameConflictActions.Rename, " beta ")]));

        db.ChangeTracker.Clear();
        Assert.Equal(" alpha ", (await db.Tags.AsNoTracking().SingleAsync(tag => tag.Id == conflicting.Id)).Name);
        Assert.Equal(3, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_RejectsAConcurrentlyChangedGroupRevision()
    {
        await using var db = CreateContext();
        var first = new Tag { Name = "Alpha" };
        var second = new Tag { Name = " alpha " };
        db.Tags.AddRange(first, second);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var staleGroup = Assert.Single((await scanner.ScanAsync()).Groups);
        db.Tags.Add(new Tag { Name = "ALPHA" });
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ResolveAsync(
            staleGroup.Key,
            staleGroup.Revision,
            staleGroup.RecommendedSurvivorTagId,
            null));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_RejectsAStaleAliasOwnerNameEvenWhenTheAliasClaimIsUnchanged()
    {
        await using var db = CreateContext();
        var canonicalOwner = new Tag { Name = "Shared" };
        var aliasOwner = new Tag { Name = "Original owner", Aliases = [new TagAlias { Alias = "shared" }] };
        db.Tags.AddRange(canonicalOwner, aliasOwner);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var staleGroup = Assert.Single((await scanner.ScanAsync()).Groups);
        aliasOwner.Name = "Renamed owner";
        await db.SaveChangesAsync();

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ResolveAsync(
            staleGroup.Key,
            staleGroup.Revision,
            staleGroup.RecommendedSurvivorTagId,
            null));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveAsync_UsesSelectedSurvivorAndRefreshesTheScan()
    {
        await using var db = CreateContext();
        var lowerId = new Tag { Name = " Alpha " };
        var selected = new Tag { Name = "alpha", Description = "Selected survivor" };
        db.Tags.AddRange(lowerId, selected);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var refreshed = await cleanup.ResolveAsync(group.Key, selected.Id);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == lowerId.Id));
        var survivor = await db.Tags.Include(tag => tag.Aliases).SingleAsync(tag => tag.Id == selected.Id);
        Assert.Equal("alpha", survivor.Name);
        Assert.Equal("Selected survivor", survivor.Description);
        Assert.DoesNotContain(survivor.Aliases, alias => TagNameRules.NamesEqual(alias.Alias, survivor.Name));
    }

    [Fact]
    public async Task ResolveAsync_RollsBackWhenTheRequiredRefreshedImpactScanFails()
    {
        await using var db = CreateContext();
        var survivor = new Tag { Name = "Alpha" };
        var source = new Tag { Name = " alpha " };
        db.Tags.AddRange(
            survivor,
            source,
            new Tag { Name = "Beta" },
            new Tag { Name = " beta " });
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var initialScanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await initialScanner.ScanAsync()).Groups, candidate => candidate.NormalizedName == "Alpha");
        var failingScanner = new TagNameConflictScanner(db, new ThrowingImpactInspector());
        var cleanup = new TagNameConflictCleanupService(db, failingScanner, new TagMergeService(db));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cleanup.ResolveAsync(
            group.Key,
            group.Revision,
            survivor.Id,
            null));

        db.ChangeTracker.Clear();
        Assert.Equal(4, await db.Tags.AsNoTracking().CountAsync());
        Assert.Equal(" alpha ", (await db.Tags.AsNoTracking().SingleAsync(tag => tag.Id == source.Id)).Name);
    }

    [Fact]
    public async Task ResolveBatchAsync_HonorsExplicitChoicesAcrossEveryGroup()
    {
        await using var db = CreateContext();
        var alphaRecommended = new Tag { Name = "Alpha" };
        var alphaSelected = new Tag { Name = " alpha ", Description = "Selected survivor" };
        var betaSurvivor = new Tag { Name = "Beta" };
        var betaRenamed = new Tag { Name = " beta " };
        db.Tags.AddRange(alphaRecommended, alphaSelected, betaSurvivor, betaRenamed);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var scan = await scanner.ScanAsync();
        var alpha = Assert.Single(scan.Groups, group => group.NormalizedName == "Alpha");
        var beta = Assert.Single(scan.Groups, group => group.NormalizedName == "Beta");
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var refreshed = await cleanup.ResolveBatchAsync(new ResolveTagNameConflictBatchDto(
            scan.Revision,
            [
                new ResolveTagNameConflictDto(
                    alpha.Key,
                    alpha.Revision,
                    alphaSelected.Id,
                    [new TagNameClaimResolutionDto(alphaRecommended.Id, null, TagNameConflictActions.MergeTag)],
                    []),
                new ResolveTagNameConflictDto(
                    beta.Key,
                    beta.Revision,
                    betaSurvivor.Id,
                    [new TagNameClaimResolutionDto(betaRenamed.Id, null, TagNameConflictActions.Rename, "Gamma")],
                    []),
            ]));

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Equal("Selected survivor", (await db.Tags.SingleAsync(tag => tag.Id == alphaSelected.Id)).Description);
        Assert.Equal("Gamma", (await db.Tags.SingleAsync(tag => tag.Id == betaRenamed.Id)).Name);
    }

    [Fact]
    public async Task ResolveBatchAsync_RollsBackEarlierGroupsWhenALaterChoiceFails()
    {
        await using var db = CreateContext();
        var alpha = new Tag { Name = "Alpha" };
        var alphaSource = new Tag { Name = " alpha " };
        var beta = new Tag { Name = "Beta" };
        var betaSource = new Tag { Name = " beta " };
        var occupied = new Tag { Name = "Gamma" };
        db.Tags.AddRange(alpha, alphaSource, beta, betaSource, occupied);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var scan = await scanner.ScanAsync();
        var alphaGroup = Assert.Single(scan.Groups, group => string.Equals(group.NormalizedName, "Alpha", StringComparison.OrdinalIgnoreCase));
        var betaGroup = Assert.Single(scan.Groups, group => string.Equals(group.NormalizedName, "Beta", StringComparison.OrdinalIgnoreCase));
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        await Assert.ThrowsAsync<TagNameConflictException>(() => cleanup.ResolveBatchAsync(new ResolveTagNameConflictBatchDto(
            scan.Revision,
            [
                new ResolveTagNameConflictDto(alphaGroup.Key, alphaGroup.Revision, alpha.Id, [new TagNameClaimResolutionDto(alphaSource.Id, null, TagNameConflictActions.MergeTag)], []),
                new ResolveTagNameConflictDto(betaGroup.Key, betaGroup.Revision, beta.Id, [new TagNameClaimResolutionDto(betaSource.Id, null, TagNameConflictActions.Rename, " gamma ")], []),
            ])));

        db.ChangeTracker.Clear();
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == alphaSource.Id));
        Assert.Equal(5, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveBatchAsync_RejectsAMergeSourceThatParticipatesInAnotherGroup()
    {
        await using var db = CreateContext();
        var linked = new Tag { Name = "Alpha", Aliases = [new TagAlias { Alias = "Beta" }] };
        var alphaSource = new Tag { Name = " alpha " };
        var betaSurvivor = new Tag { Name = " beta " };
        db.Tags.AddRange(linked, alphaSource, betaSurvivor);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var scan = await scanner.ScanAsync();
        var alphaGroup = Assert.Single(scan.Groups, group => string.Equals(group.NormalizedName, "Alpha", StringComparison.OrdinalIgnoreCase));
        var betaGroup = Assert.Single(scan.Groups, group => string.Equals(group.NormalizedName, "Beta", StringComparison.OrdinalIgnoreCase));
        var linkedBetaClaim = Assert.Single(betaGroup.Claims, claim => claim.TagId == linked.Id);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => cleanup.ResolveBatchAsync(new ResolveTagNameConflictBatchDto(
            scan.Revision,
            [
                new ResolveTagNameConflictDto(alphaGroup.Key, alphaGroup.Revision, linked.Id, [new TagNameClaimResolutionDto(alphaSource.Id, null, TagNameConflictActions.MergeTag)], []),
                new ResolveTagNameConflictDto(betaGroup.Key, betaGroup.Revision, betaSurvivor.Id, [new TagNameClaimResolutionDto(linked.Id, linkedBetaClaim.AliasId, TagNameConflictActions.MergeTag)], []),
            ])));

        Assert.Contains("more than one conflict group", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(3, await db.Tags.CountAsync());
    }

    [Fact]
    public async Task ResolveBatchAsync_AcceptsAnExplicitResolutionForEveryBlankAliasClaim()
    {
        await using var db = CreateContext();
        var tag = new Tag { Name = "Alpha", Aliases = [new TagAlias { Alias = " " }] };
        db.Tags.Add(tag);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var scan = await scanner.ScanAsync();
        var group = Assert.Single(scan.Groups);
        var claim = Assert.Single(group.Claims);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));

        var refreshed = await cleanup.ResolveBatchAsync(new ResolveTagNameConflictBatchDto(
            scan.Revision,
            [new ResolveTagNameConflictDto(
                group.Key,
                group.Revision,
                tag.Id,
                [new TagNameClaimResolutionDto(tag.Id, claim.AliasId, TagNameConflictActions.RemoveAlias)],
                [])]));

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Empty((await db.Tags.Include(candidate => candidate.Aliases).SingleAsync()).Aliases);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:");
        var context = new CoveContext(options.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class ThrowingImpactInspector : ITagExternalReferenceInspector
    {
        public Task<IReadOnlyList<TagExternalReferenceDto>> InspectAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Expected refreshed impact scan failure.");

        public Task ApplyResolutionsAsync(
            int targetTagId,
            IReadOnlyCollection<TagExternalReferenceResolutionDto> resolutions,
            CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class MutableExternalReferenceInspector : ITagExternalReferenceInspector
    {
        private readonly Dictionary<int, TagExternalReferenceDto> references = [];

        public MutableExternalReferenceInspector(int tagId, int rowCount)
            => SetRowCount(tagId, rowCount);

        public int? AppliedTargetTagId { get; private set; }
        public List<TagExternalReferenceResolutionDto> AppliedResolutions { get; } = [];

        public void SetRowCount(int tagId, int rowCount)
        {
            if (rowCount <= 0)
            {
                references.Remove(tagId);
                return;
            }

            references[tagId] = new TagExternalReferenceDto(
                tagId,
                "fixture-reference",
                "public",
                "extension_fixture",
                "tag_id",
                "restrict",
                rowCount);
        }

        public void SetAccessLimitation(int tagId, string accessLimitation)
        {
            references[tagId] = new TagExternalReferenceDto(
                tagId,
                "fixture-reference",
                "public",
                "extension_fixture",
                "tag_id",
                "cascade",
                null,
                accessLimitation);
        }

        public Task<IReadOnlyList<TagExternalReferenceDto>> InspectAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TagExternalReferenceDto>>(references.Values
                .Where(reference => tagIds.Contains(reference.TagId))
                .OrderBy(reference => reference.TagId)
                .ToArray());

        public Task ApplyResolutionsAsync(
            int targetTagId,
            IReadOnlyCollection<TagExternalReferenceResolutionDto> resolutions,
            CancellationToken ct = default)
        {
            AppliedTargetTagId = targetTagId;
            AppliedResolutions.AddRange(resolutions);
            foreach (var resolution in resolutions)
                references.Remove(resolution.TagId);
            return Task.CompletedTask;
        }
    }
}
