using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Transactions;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class BlobReferenceSafetyTests
{
    [Fact]
    public async Task ConcurrentExistingIdAssignment_CannotCommitAfterUnreferencedDeleteWins()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-reference-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var coordinator = new BlobReferenceCoordinator();
            var counter = new BlockingZeroReferenceCounter();
            var blobService = new BlobService(
                new CoveConfiguration { GeneratedPath = tempRoot },
                NullLogger<BlobService>.Instance,
                counter,
                coordinator);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IBlobReferenceCoordinator>(coordinator);
            services.AddSingleton<IBlobService>(blobService);
            services.AddScoped<BlobReferenceSaveChangesInterceptor>();
            services.AddDbContext<CoveContext>((provider, options) =>
                database.Configure(options)
                    .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));
            await using var provider = services.BuildServiceProvider();

            int performerId;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seed = seedScope.ServiceProvider.GetRequiredService<CoveContext>();
                var performer = new Performer { Name = "target" };
                seed.Performers.Add(performer);
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
                performerId = performer.Id;
            }

            await using var payload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await blobService.StoreBlobAsync(payload, "image/png", TestContext.Current.CancellationToken);
            var deletion = blobService.DeleteBlobIfUnreferencedAsync(blobId, TestContext.Current.CancellationToken);
            await counter.CountStarted.Task;

            await using var assignScope = provider.CreateAsyncScope();
            var assigningDb = assignScope.ServiceProvider.GetRequiredService<CoveContext>();
            var target = await assigningDb.Performers.SingleAsync(item => item.Id == performerId, cancellationToken: TestContext.Current.CancellationToken);
            target.ImageBlobId = blobId;
            var assignment = assigningDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(assignment.IsCompleted);

            counter.AllowCountToFinish.SetResult();
            await deletion;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => assignment);

            Assert.Contains(blobId, error.Message, StringComparison.Ordinal);
            Assert.Null(await blobService.GetBlobAsync(blobId, TestContext.Current.CancellationToken));
            await using var verifyScope = provider.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Null((await verify.Performers.AsNoTracking().SingleAsync(item => item.Id == performerId, cancellationToken: TestContext.Current.CancellationToken)).ImageBlobId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentExistingIdAssignment_IsRetainedWhenTheCommitWins()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-reference-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var coordinator = new HoldingReleaseCoordinator();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IBlobReferenceCoordinator>(coordinator);
            services.AddSingleton<IBlobReferenceCounter, BlobReferenceCounter>();
            services.AddSingleton<IBlobService>(provider => new BlobService(
                new CoveConfiguration { GeneratedPath = tempRoot },
                NullLogger<BlobService>.Instance,
                provider.GetRequiredService<IBlobReferenceCounter>(),
                coordinator));
            services.AddScoped<BlobReferenceSaveChangesInterceptor>();
            services.AddDbContext<CoveContext>((provider, options) =>
                database.Configure(options)
                    .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));
            await using var provider = services.BuildServiceProvider();

            int performerId;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seed = seedScope.ServiceProvider.GetRequiredService<CoveContext>();
                var performer = new Performer { Name = "target" };
                seed.Performers.Add(performer);
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
                performerId = performer.Id;
            }

            var blobService = provider.GetRequiredService<IBlobService>();
            await using var payload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await blobService.StoreBlobAsync(payload, "image/png", TestContext.Current.CancellationToken);

            await using var assignScope = provider.CreateAsyncScope();
            var assigningDb = assignScope.ServiceProvider.GetRequiredService<CoveContext>();
            var target = await assigningDb.Performers.SingleAsync(item => item.Id == performerId, cancellationToken: TestContext.Current.CancellationToken);
            target.ImageBlobId = blobId;
            coordinator.HoldNextLeaseRelease();
            var assignment = assigningDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            await coordinator.ReleaseAttempted.Task;

            var deletion = blobService.DeleteBlobIfUnreferencedAsync(blobId, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.False(deletion.IsCompleted);

            coordinator.AllowRelease.SetResult();
            await assignment;
            await deletion;

            var retained = await blobService.GetBlobAsync(blobId, TestContext.Current.CancellationToken);
            Assert.NotNull(retained);
            await retained.Value.Stream.DisposeAsync();
            await using var verifyScope = provider.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Equal(blobId, (await verify.Performers.AsNoTracking().SingleAsync(item => item.Id == performerId, cancellationToken: TestContext.Current.CancellationToken)).ImageBlobId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task UnreferencedDelete_KeepsABlobReferencedByAnEntityTheCurrentUserCannotRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await CovePostgresDatabase.CreateAsync(ct);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-reference-hidden-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var principalAccessor = new CurrentPrincipalAccessor();

        try
        {
            var coordinator = new BlobReferenceCoordinator();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ICurrentPrincipalAccessor>(principalAccessor);
            services.AddSingleton<IBlobReferenceCoordinator>(coordinator);
            services.AddSingleton<IBlobReferenceCounter, BlobReferenceCounter>();
            services.AddSingleton<IBlobService>(provider => new BlobService(
                new CoveConfiguration { GeneratedPath = tempRoot },
                NullLogger<BlobService>.Instance,
                provider.GetRequiredService<IBlobReferenceCounter>(),
                coordinator));
            services.AddScoped<BlobReferenceSaveChangesInterceptor>();
            services.AddDbContext<CoveContext>((provider, options) =>
                database.Configure(options)
                    .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));
            await using var provider = services.BuildServiceProvider();

            var blobService = provider.GetRequiredService<IBlobService>();
            await using var payload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await blobService.StoreBlobAsync(payload, "image/png", ct);
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seed = seedScope.ServiceProvider.GetRequiredService<CoveContext>();
                seed.Performers.Add(new Performer { Name = "hidden", ImageBlobId = blobId });
                await seed.SaveChangesAsync(ct);
            }

            // On PostgreSQL, CoveContext's authorization filters hide every performer from a user without
            // performers.read, so the reference count only sees this one if it ignores those filters.
            principalAccessor.Set(new CovePrincipal
            {
                UserId = 7,
                Username = "metadata-only-deleter",
                Kind = PrincipalKind.User,
                Permissions = new HashSet<string>(),
                Roles = new HashSet<string>(),
            });
            await using (var visibilityScope = provider.CreateAsyncScope())
                Assert.False(await visibilityScope.ServiceProvider.GetRequiredService<CoveContext>().Performers.AnyAsync(ct));
            await blobService.DeleteBlobIfUnreferencedAsync(blobId, ct);

            var retained = await blobService.GetBlobAsync(blobId, ct);
            Assert.NotNull(retained);
            await retained.Value.Stream.DisposeAsync();
        }
        finally
        {
            principalAccessor.Set(null);
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetachedUpdate_CannotAssignADeletedBlobId()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-blob-reference-detached-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var coordinator = new BlobReferenceCoordinator();
            var blobService = new BlobService(
                new CoveConfiguration { GeneratedPath = tempRoot },
                NullLogger<BlobService>.Instance,
                new ImmediateZeroReferenceCounter(),
                coordinator);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IBlobReferenceCoordinator>(coordinator);
            services.AddSingleton<IBlobService>(blobService);
            services.AddScoped<BlobReferenceSaveChangesInterceptor>();
            services.AddDbContext<CoveContext>((provider, options) =>
                database.Configure(options)
                    .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));
            await using var provider = services.BuildServiceProvider();

            int performerId;
            await using (var seedScope = provider.CreateAsyncScope())
            {
                var seed = seedScope.ServiceProvider.GetRequiredService<CoveContext>();
                var performer = new Performer { Name = "target" };
                seed.Performers.Add(performer);
                await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
                performerId = performer.Id;
            }

            await using var payload = new MemoryStream([1, 2, 3, 4]);
            var blobId = await blobService.StoreBlobAsync(payload, "image/png", TestContext.Current.CancellationToken);
            await blobService.DeleteBlobIfUnreferencedAsync(blobId, TestContext.Current.CancellationToken);

            await using var updateScope = provider.CreateAsyncScope();
            var update = updateScope.ServiceProvider.GetRequiredService<CoveContext>();
            update.Update(new Performer { Id = performerId, Name = "target", ImageBlobId = blobId });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => update.SaveChangesAsync(TestContext.Current.CancellationToken));
            Assert.Contains(blobId, error.Message, StringComparison.Ordinal);
            await using var verifyScope = provider.CreateAsyncScope();
            var verify = verifyScope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.Null((await verify.Performers.AsNoTracking().SingleAsync(item => item.Id == performerId, cancellationToken: TestContext.Current.CancellationToken)).ImageBlobId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
                Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrencyConflict_ReleasesTheBlobReferenceLease(bool saveAsync)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var provider = CreateInterceptorProvider(database);
        await using var winnerScope = provider.CreateAsyncScope();
        await using var loserScope = provider.CreateAsyncScope();
        var winner = winnerScope.ServiceProvider.GetRequiredService<CoveContext>();
        var loser = loserScope.ServiceProvider.GetRequiredService<CoveContext>();
        var studio = new Studio { Name = "target", ImageBlobId = "11111111-1111-4111-8111-111111111111" };
        winner.Studios.Add(studio);
        await winner.SaveChangesAsync(TestContext.Current.CancellationToken);
        var stale = await loser.Studios.SingleAsync(item => item.Id == studio.Id, TestContext.Current.CancellationToken);

        winner.Studios.Remove(studio);
        await winner.SaveChangesAsync(TestContext.Current.CancellationToken);
        loser.Studios.Remove(stale);
        if (saveAsync)
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => loser.SaveChangesAsync(TestContext.Current.CancellationToken));
        else
            Assert.Throws<DbUpdateConcurrencyException>(() => loser.SaveChanges());

        // The coordinator gate is process-wide, so a lease leaked by the failed save would block every
        // later blob-referencing save until restart.
        await AssertLeaseAvailableAsync(provider.GetRequiredService<IBlobReferenceCoordinator>());
    }

    [Fact]
    public async Task LeaseHeldWhenTheScopeEnds_IsReleasedOnDispose()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        var failingInterceptor = new FailingSavingChangesInterceptor();
        // A later interceptor failing in SavingChanges ends the save without any interceptor hook.
        services.AddDbContext<CoveContext>((provider, options) =>
            database.Configure(options)
                .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>(), failingInterceptor));
        await using var provider = services.BuildServiceProvider();
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
            context.Studios.Add(new Studio { Name = "target", ImageBlobId = "11111111-1111-4111-8111-111111111111" });
            failingInterceptor.Armed = true;
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

            // A further save in the same scope must fail fast rather than wait on its own leaked lease.
            failingInterceptor.Armed = false;
            var retry = context.SaveChangesAsync(TestContext.Current.CancellationToken);
            Assert.Same(retry, await Task.WhenAny(retry, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => retry);
        }

        await AssertLeaseAvailableAsync(provider.GetRequiredService<IBlobReferenceCoordinator>());
    }

    private static async Task AssertLeaseAvailableAsync(IBlobReferenceCoordinator coordinator)
    {
        var acquire = coordinator.AcquireAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.Same(acquire, await Task.WhenAny(acquire, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)));
        await (await acquire).DisposeAsync();
    }

    private sealed class FailingSavingChangesInterceptor : SaveChangesInterceptor
    {
        public bool Armed { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => Armed ? throw new InvalidOperationException("Injected interceptor failure") : ValueTask.FromResult(result);
    }

    [Fact]
    public async Task BlobReferenceChange_IsRejectedInsideAnExplicitTransaction()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var provider = CreateInterceptorProvider(database);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var performer = new Performer { Name = "target" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
        performer.ImageBlobId = "11111111-1111-4111-8111-111111111111";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Contains("explicit, enlisted, or ambient", error.Message, StringComparison.Ordinal);
        await transaction.RollbackAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BlobReferenceChange_IsRejectedInsideAnAmbientTransaction()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var provider = CreateInterceptorProvider(database, useTestEnlistmentManager: true);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var performer = new Performer { Name = "target" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        performer.ImageBlobId = "11111111-1111-4111-8111-111111111111";

        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("explicit, enlisted, or ambient", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlobReferenceChange_IsRejectedInsideAManuallyEnlistedTransaction()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var provider = CreateInterceptorProvider(database, useTestEnlistmentManager: true);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var performer = new Performer { Name = "target" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        performer.ImageBlobId = "11111111-1111-4111-8111-111111111111";

        using var transaction = new CommittableTransaction();
        context.Database.EnlistTransaction(transaction);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Contains("explicit, enlisted, or ambient", error.Message, StringComparison.Ordinal);
        transaction.Rollback();
    }

    [Fact]
    public async Task TransactionalTagMerge_TransfersArtworkWithTheProductionInterceptor()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var provider = CreateInterceptorProvider(database);
        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var target = new Tag { Name = "target", ImageBlobId = "target-artwork" };
        var source = new Tag
        {
            Name = "source",
            ImageBlobId = "source-artwork",
            ImageOverrideBlobId = "source-override-artwork",
        };
        context.Tags.AddRange(target, source);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await scope.ServiceProvider.GetRequiredService<TagMergeService>()
            .MergeAsync(target.Id, [source.Id], TestContext.Current.CancellationToken);

        context.ChangeTracker.Clear();
        var merged = await context.Tags.SingleAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(target.Id, merged.Id);
        Assert.Equal("target-artwork", merged.ImageBlobId);
        Assert.Equal("source-override-artwork", merged.ImageOverrideBlobId);
    }

    [Fact]
    public async Task SaveChanges_CleansDetachedBlobOnlyAfterTheReferenceIsPersisted()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var coordinator = new BlobReferenceCoordinator();
        var deleted = new List<string>();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator>(coordinator);
        services.AddSingleton<IBlobService>(provider => new ObservingBlobService(async (blobId, ct) =>
        {
            await using var scope = provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            Assert.False(await db.Videos.AsNoTracking().AnyAsync(video => video.ImageBlobId == blobId, ct));
            deleted.Add(blobId);
        }));
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddDbContext<CoveContext>((provider, options) =>
            database.Configure(options)
                .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>()));
        await using var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var video = new Video { Title = "covered", ImageBlobId = "existing-blob" };
        context.Videos.Add(video);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Empty(deleted);

        video.ImageBlobId = null;
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["existing-blob"], deleted);
    }

    private sealed class BlockingZeroReferenceCounter : IBlobReferenceCounter
    {
        public TaskCompletionSource CountStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowCountToFinish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<int> CountReferencesAsync(string blobId, int maximum, CancellationToken ct = default)
        {
            CountStarted.SetResult();
            await AllowCountToFinish.Task.WaitAsync(ct);
            return 0;
        }
    }

    private sealed class ImmediateZeroReferenceCounter : IBlobReferenceCounter
    {
        public Task<int> CountReferencesAsync(string blobId, int maximum, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    private sealed class HoldingReleaseCoordinator : IBlobReferenceCoordinator
    {
        private readonly BlobReferenceCoordinator _inner = new();
        private int _holdNextRelease;

        public TaskCompletionSource ReleaseAttempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void HoldNextLeaseRelease() => Interlocked.Exchange(ref _holdNextRelease, 1);

        public IBlobReferenceLease Acquire(CancellationToken ct = default) =>
            Wrap(_inner.Acquire(ct));

        public async ValueTask<IBlobReferenceLease> AcquireAsync(CancellationToken ct = default) =>
            Wrap(await _inner.AcquireAsync(ct));

        public bool WasDeleted(string blobId) => _inner.WasDeleted(blobId);
        public void MarkAvailable(string blobId) => _inner.MarkAvailable(blobId);
        public void MarkDeleted(string blobId) => _inner.MarkDeleted(blobId);

        private IBlobReferenceLease Wrap(IBlobReferenceLease lease) =>
            Interlocked.Exchange(ref _holdNextRelease, 0) == 1
                ? new HeldLease(lease, ReleaseAttempted, AllowRelease)
                : lease;

        private sealed class HeldLease(
            IBlobReferenceLease inner,
            TaskCompletionSource releaseAttempted,
            TaskCompletionSource allowRelease) : IBlobReferenceLease
        {
            public void Dispose()
            {
                releaseAttempted.SetResult();
                allowRelease.Task.GetAwaiter().GetResult();
                inner.Dispose();
            }

            public async ValueTask DisposeAsync()
            {
                releaseAttempted.SetResult();
                await allowRelease.Task;
                await inner.DisposeAsync();
            }
        }
    }

    private static ServiceProvider CreateInterceptorProvider(
        CovePostgresDatabase database,
        bool useTestEnlistmentManager = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBlobReferenceCoordinator, BlobReferenceCoordinator>();
        services.AddScoped<BlobReferenceTransactionCoordinator>();
        services.AddScoped<BlobReferenceSaveChangesInterceptor>();
        services.AddScoped<TagMergeService>();
        services.AddDbContext<CoveContext>((provider, options) =>
        {
            database.Configure(options)
                .AddInterceptors(provider.GetRequiredService<BlobReferenceSaveChangesInterceptor>());
            if (useTestEnlistmentManager)
                options.ReplaceService<IDbContextTransactionManager, TestTransactionManager>();
        });
        return services.BuildServiceProvider();
    }

    private sealed class TestTransactionManager : IDbContextTransactionManager, ITransactionEnlistmentManager
    {
        public IDbContextTransaction? CurrentTransaction => null;
        public Transaction? CurrentAmbientTransaction => Transaction.Current;
        public Transaction? EnlistedTransaction { get; private set; }
        public void EnlistTransaction(Transaction? transaction) => EnlistedTransaction = transaction;
        public IDbContextTransaction BeginTransaction() => throw new NotSupportedException();
        public Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void CommitTransaction() => throw new NotSupportedException();
        public Task CommitTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void RollbackTransaction() => throw new NotSupportedException();
        public Task RollbackTransactionAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void ResetState() => EnlistedTransaction = null;
        public Task ResetStateAsync(CancellationToken cancellationToken = default)
        {
            EnlistedTransaction = null;
            return Task.CompletedTask;
        }
    }

    private sealed class ObservingBlobService(Func<string, CancellationToken, Task> onDelete) : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default) => onDelete(blobId, ct);
    }
}
