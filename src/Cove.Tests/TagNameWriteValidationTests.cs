using Cove.Core.Entities;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

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
        Assert.Equal(
            "A tag alias with name \"Reserved\" already exists. Tag names and tag aliases must be unique.",
            exception.Message);
    }

    [Fact]
    public async Task SaveChanges_IdentifiesCanonicalClaimWhenAddingAConflictingAlias()
    {
        await using var db = CreateContext();
        db.Tags.Add(new Tag { Name = "Facial" });
        await db.SaveChangesAsync();

        db.Tags.Add(new Tag
        {
            Name = "Category: Shot Type",
            Aliases = [new TagAlias { Alias = " facial " }],
        });

        var exception = await Assert.ThrowsAsync<TagNameConflictException>(() => db.SaveChangesAsync());

        Assert.Equal(
            "A tag with name \"Facial\" already exists. Tag names and tag aliases must be unique.",
            exception.Message);
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
    public async Task SaveChanges_SerializesConcurrentTagNamespaceWritesAcrossContexts()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"cove-tag-namespace-{Guid.NewGuid():N}.db");
        try
        {
            await using (var seed = CreateContext(databasePath))
            {
                seed.Tags.AddRange(new Tag { Name = "First" }, new Tag { Name = "Second" });
                await seed.SaveChangesAsync();
            }

            var interceptor = new BlockingSaveInterceptor();
            await using var first = CreateContext(databasePath, interceptor);
            await using var second = CreateContext(databasePath, interceptor);
            (await first.Tags.SingleAsync(tag => tag.Name == "First")).Aliases.Add(new TagAlias { Alias = "Shared" });
            (await second.Tags.SingleAsync(tag => tag.Name == "Second")).Aliases.Add(new TagAlias { Alias = "shared" });

            var firstSave = first.SaveChangesAsync();
            await interceptor.FirstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var secondSave = second.SaveChangesAsync();

            var prematureSecondEntry = await Task.WhenAny(
                interceptor.SecondEntered.Task,
                Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(interceptor.SecondEntered.Task, prematureSecondEntry);

            interceptor.ReleaseFirst.TrySetResult();
            await firstSave;
            await Assert.ThrowsAsync<TagNameConflictException>(() => secondSave);
        }
        finally
        {
            File.Delete(databasePath);
        }
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

    private static CoveContext CreateContext(string databasePath, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite($"Data Source={databasePath}")
            .AddInterceptors(interceptors)
            .Options;
        var context = new CoveContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    private sealed class BlockingSaveInterceptor : SaveChangesInterceptor
    {
        private int _invocations;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            var invocation = Interlocked.Increment(ref _invocations);
            if (invocation == 1)
            {
                FirstEntered.TrySetResult();
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            else
            {
                SecondEntered.TrySetResult();
            }

            return result;
        }
    }
}
