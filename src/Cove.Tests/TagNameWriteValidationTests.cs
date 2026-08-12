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
        Assert.Equal("Alternate", Assert.Single(tag.Aliases).Alias);
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
