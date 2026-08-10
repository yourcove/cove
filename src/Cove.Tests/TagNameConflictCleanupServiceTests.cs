using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Cove.Tests;

public sealed class TagNameConflictCleanupServiceTests
{
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
    public async Task ResolveAllRecommendedAsync_PrefersCanonicalOwnerOverLowerIdAliasOwner()
    {
        await using var db = CreateContext();
        var olderAliasOwner = new Tag { Name = "Older", Aliases = [new TagAlias { Alias = "Shared" }] };
        var canonicalOwner = new Tag { Name = " shared " };
        var referencedVideo = new Video { Title = "Alias owner reference fixture" };
        db.AddRange(olderAliasOwner, canonicalOwner, referencedVideo);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();
        db.Set<VideoTag>().Add(new VideoTag { VideoId = referencedVideo.Id, TagId = olderAliasOwner.Id });
        await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var group = Assert.Single((await scanner.ScanAsync()).Groups);
        Assert.Equal(canonicalOwner.Id, group.RecommendedSurvivorTagId);
        Assert.Empty(group.RecommendedMergeTagIds);
        Assert.Equal([olderAliasOwner.Aliases.Single().Id], group.RecommendedRemoveAliasIds);

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var refreshed = await cleanup.ResolveAllRecommendedAsync();

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Equal(2, await db.Tags.CountAsync());
        Assert.Empty(await db.Set<TagAlias>().ToListAsync());
        Assert.Equal("shared", (await db.Tags.SingleAsync(tag => tag.Id == canonicalOwner.Id)).Name);
    }

    [Fact]
    public async Task ResolveAllRecommendedAsync_MergesIntoTheCanonicalOwnerWithTheMostReferences()
    {
        await using var db = CreateContext();
        var lowerId = new Tag { Name = "Alpha" };
        var referenced = new Tag { Name = " alpha " };
        var video = new Video { Title = "Recommended merge target fixture" };
        db.AddRange(lowerId, referenced, video);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();
        db.Set<VideoTag>().Add(new VideoTag { VideoId = video.Id, TagId = referenced.Id });
        await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var scan = await scanner.ScanAsync();
        Assert.Equal(referenced.Id, Assert.Single(scan.Groups).RecommendedSurvivorTagId);

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var refreshed = await cleanup.ResolveAllRecommendedAsync(scan.Revision);

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == lowerId.Id));
        Assert.True(await db.Tags.AnyAsync(tag => tag.Id == referenced.Id));
        Assert.Equal(referenced.Id, (await db.Set<VideoTag>().SingleAsync()).TagId);
    }

    [Fact]
    public async Task ResolveAllRecommendedAsync_RejectsAConfirmedScanWhenReferenceCountsChangeItsRecommendation()
    {
        await using var db = CreateContext();
        var lowerId = new Tag { Name = "Alpha" };
        var newlyReferenced = new Tag { Name = " alpha " };
        var video = new Video { Title = "Stale recommendation fixture" };
        db.AddRange(lowerId, newlyReferenced, video);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var staleScan = await scanner.ScanAsync();
        Assert.Equal(lowerId.Id, Assert.Single(staleScan.Groups).RecommendedSurvivorTagId);

        db.Set<VideoTag>().Add(new VideoTag { VideoId = video.Id, TagId = newlyReferenced.Id });
        await db.SaveChangesAsync();

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleanup.ResolveAllRecommendedAsync(staleScan.Revision));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, await db.Tags.CountAsync());
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
    public async Task ResolveAllRecommendedAsync_RejectsANewConflictThatWasNotInTheConfirmedScan()
    {
        await using var db = CreateContext();
        db.Tags.AddRange(new Tag { Name = "Alpha" }, new Tag { Name = " alpha " });
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var staleScan = await scanner.ScanAsync();
        Assert.Single(staleScan.Groups);

        db.Tags.AddRange(new Tag { Name = "Beta" }, new Tag { Name = " beta " });
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cleanup.ResolveAllRecommendedAsync(staleScan.Revision));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, await db.Tags.CountAsync());
        Assert.Equal(2, (await scanner.ScanAsync()).UnresolvedGroupCount);
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
    public async Task ResolveAllRecommendedAsync_CleansSimpleConflictsAndMergesEmptyNameCollision()
    {
        await using var db = CreateContext();
        var duplicateAliases = new Tag
        {
            Name = "Delta",
            Aliases =
            [
                new TagAlias { Alias = " same " },
                new TagAlias { Alias = "SAME" },
                new TagAlias { Alias = " delta " },
                new TagAlias { Alias = "  " },
            ],
        };
        var whitespace = new Tag { Name = "   " };
        var literalEmpty = new Tag { Name = TagNameRules.EmptyCanonicalName };
        db.Tags.AddRange(duplicateAliases, whitespace, literalEmpty);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var scanner = new TagNameConflictScanner(db);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var refreshed = await cleanup.ResolveAllRecommendedAsync();

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.Empty(db.ChangeTracker.Entries());
        var aliases = await db.Set<TagAlias>().Where(alias => alias.TagId == duplicateAliases.Id).ToListAsync();
        Assert.Equal("same", Assert.Single(aliases).Alias);
        Assert.False(await db.Tags.AnyAsync(tag => tag.Id == literalEmpty.Id));
        Assert.Equal(TagNameRules.EmptyCanonicalName, (await db.Tags.SingleAsync(tag => tag.Id == whitespace.Id)).Name);
    }

    [Fact]
    public async Task ResolveAllRecommendedAsync_ClearsCompletedMergeGraphBeforeSavingLaterGroup()
    {
        var priorFilterId = 0;
        var laterAliasOwnerId = 0;
        var observedLaterGroupSave = false;
        var priorFilterTrackedDuringLaterGroupSave = false;
        var interceptor = new SavingChangesProbe(context =>
        {
            if (!context.ChangeTracker.Entries<TagAlias>().Any(entry =>
                entry.Entity.TagId == laterAliasOwnerId
                && entry.State is EntityState.Modified or EntityState.Deleted))
                return;

            observedLaterGroupSave = true;
            priorFilterTrackedDuringLaterGroupSave |= context.ChangeTracker.Entries<SavedFilter>()
                .Any(entry => entry.Entity.Id == priorFilterId);
        });
        await using var db = CreateContext(interceptor);
        var mergeTarget = new Tag
        {
            Name = "Alpha",
            Description = "Preferred survivor fixture",
            Favorite = true,
        };
        var mergeSource = new Tag { Name = " alpha " };
        var laterAliasOwner = new Tag
        {
            Name = "Zulu",
            Aliases =
            [
                new TagAlias { Alias = "Duplicate" },
                new TagAlias { Alias = " duplicate " },
            ],
        };
        db.Tags.AddRange(mergeTarget, mergeSource, laterAliasOwner);
        using (db.SuppressTagNameValidation())
            await db.SaveChangesAsync();

        var storedFilter = new SavedFilter
        {
            Mode = "videos",
            Name = "Prior merge graph fixture",
            ObjectFilter = System.Text.Json.JsonSerializer.Serialize(new { tagIds = new[] { mergeSource.Id } }),
        };
        db.SavedFilters.Add(storedFilter);
        await db.SaveChangesAsync();
        priorFilterId = storedFilter.Id;
        laterAliasOwnerId = laterAliasOwner.Id;
        db.ChangeTracker.Clear();

        var scanner = new TagNameConflictScanner(db);
        var cleanup = new TagNameConflictCleanupService(db, scanner, new TagMergeService(db));
        var refreshed = await cleanup.ResolveAllRecommendedAsync();

        Assert.Equal(0, refreshed.UnresolvedGroupCount);
        Assert.True(observedLaterGroupSave);
        Assert.False(priorFilterTrackedDuringLaterGroupSave);
    }

    private static CoveContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite("Data Source=:memory:");
        if (interceptors.Length > 0)
            options.AddInterceptors(interceptors);
        var context = new CoveContext(options.Options);
        context.Database.OpenConnection();
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class SavingChangesProbe(Action<DbContext> inspect) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } context)
                inspect(context);

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ThrowingImpactInspector : ITagExternalReferenceInspector
    {
        public Task<IReadOnlyDictionary<int, int>> CountAsync(
            IReadOnlyCollection<int> tagIds,
            CancellationToken ct = default)
            => throw new InvalidOperationException("Expected refreshed impact scan failure.");
    }
}
