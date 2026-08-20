using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Transactions;

namespace Cove.Data.Services;

public sealed class BlobReferenceSaveChangesInterceptor(
    IBlobReferenceCoordinator coordinator,
    IServiceProvider serviceProvider,
    ILogger<BlobReferenceSaveChangesInterceptor> logger,
    BlobReferenceTransactionCoordinator? transactionCoordinator = null) : SaveChangesInterceptor
{
    private IBlobReferenceLease? _lease;
    private IReadOnlyList<string> _cleanupBlobIds = [];

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        var plan = CollectPlan(eventData.Context);
        if (!plan.HasChanges)
            return result;

        if (transactionCoordinator?.IsActive == true)
        {
            if (!transactionCoordinator.IsActiveFor(eventData.Context))
                throw new InvalidOperationException("A blob-reference transaction is active for another Cove database context in this service scope.");
            transactionCoordinator.RegisterPlan(plan.AssignedBlobIds, plan.CleanupBlobIds);
            return result;
        }

        RejectExplicitTransaction(eventData.Context);
        var lease = coordinator.Acquire();
        try
        {
            ValidateAssignments(plan.AssignedBlobIds);
            SetPending(lease, plan.CleanupBlobIds);
            return result;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        var plan = CollectPlan(eventData.Context);
        if (!plan.HasChanges)
            return result;

        if (transactionCoordinator?.IsActive == true)
        {
            if (!transactionCoordinator.IsActiveFor(eventData.Context))
                throw new InvalidOperationException("A blob-reference transaction is active for another Cove database context in this service scope.");
            transactionCoordinator.RegisterPlan(plan.AssignedBlobIds, plan.CleanupBlobIds);
            return result;
        }

        RejectExplicitTransaction(eventData.Context);
        var lease = await coordinator.AcquireAsync(cancellationToken);
        try
        {
            ValidateAssignments(plan.AssignedBlobIds);
            SetPending(lease, plan.CleanupBlobIds);
            return result;
        }
        catch
        {
            await lease.DisposeAsync();
            throw;
        }
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var cleanupBlobIds = TakePending();
        ReleaseLease();
        Cleanup(cleanupBlobIds);
        return result;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        var cleanupBlobIds = TakePending();
        await ReleaseLeaseAsync();
        await CleanupAsync(cleanupBlobIds);
        return result;
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => ResetPending();

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ResetPending();
        return Task.CompletedTask;
    }

    public override void SaveChangesCanceled(DbContextEventData eventData) => ResetPending();

    public override Task SaveChangesCanceledAsync(
        DbContextEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ResetPending();
        return Task.CompletedTask;
    }

    private static BlobReferencePlan CollectPlan(DbContext? db)
    {
        if (db == null)
            return BlobReferencePlan.Empty;

        var assigned = new HashSet<string>(StringComparer.Ordinal);
        var cleanup = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in db.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
                continue;

            foreach (var property in entry.Properties.Where(IsBlobReference))
            {
                var current = property.CurrentValue as string;
                var original = property.OriginalValue as string;
                if (entry.State == EntityState.Added)
                {
                    AddBlobId(assigned, current);
                }
                else if (entry.State == EntityState.Deleted)
                {
                    AddBlobId(cleanup, original ?? current);
                }
                else if (property.IsModified)
                {
                    AddBlobId(assigned, current);
                    if (!string.Equals(original, current, StringComparison.Ordinal))
                        AddBlobId(cleanup, original);
                }
            }
        }

        return new BlobReferencePlan([.. assigned], [.. cleanup]);
    }

    private static bool IsBlobReference(PropertyEntry property) =>
        property.Metadata.ClrType == typeof(string)
        && property.Metadata.Name.EndsWith("BlobId", StringComparison.Ordinal);

    private static void AddBlobId(ISet<string> blobIds, string? blobId)
    {
        if (!string.IsNullOrWhiteSpace(blobId))
            blobIds.Add(blobId);
    }

    private static void RejectExplicitTransaction(DbContext? db)
    {
        if (db?.Database.CurrentTransaction != null
            || db?.Database.GetEnlistedTransaction() != null
            || Transaction.Current != null)
        {
            throw new InvalidOperationException("Blob reference changes cannot be saved inside an explicit, enlisted, or ambient database transaction.");
        }
    }

    private void ValidateAssignments(IEnumerable<string> blobIds)
    {
        foreach (var blobId in blobIds)
        {
            if (coordinator.WasDeleted(blobId))
                throw new InvalidOperationException($"Cannot persist reference to deleted blob {blobId}.");
        }
    }

    private void SetPending(IBlobReferenceLease lease, IReadOnlyList<string> cleanupBlobIds)
    {
        if (_lease != null)
            throw new InvalidOperationException("Concurrent SaveChanges calls are not supported for a Cove database context.");
        _lease = lease;
        _cleanupBlobIds = cleanupBlobIds;
    }

    private IReadOnlyList<string> TakePending()
    {
        var result = _cleanupBlobIds;
        _cleanupBlobIds = [];
        return result;
    }

    private void ResetPending()
    {
        _cleanupBlobIds = [];
        ReleaseLease();
    }

    private void ReleaseLease() => Interlocked.Exchange(ref _lease, null)?.Dispose();

    private async ValueTask ReleaseLeaseAsync()
    {
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease != null)
            await lease.DisposeAsync();
    }

    private void Cleanup(IReadOnlyList<string> blobIds)
    {
        var blobService = serviceProvider.GetService(typeof(IBlobService)) as IBlobService;
        if (blobService == null)
            return;

        foreach (var blobId in blobIds)
        {
            try
            {
                blobService.DeleteBlobIfUnreferencedAsync(blobId, CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up detached blob {BlobId}; the persisted entity state remains valid", blobId);
            }
        }
    }

    private async Task CleanupAsync(IReadOnlyList<string> blobIds)
    {
        var blobService = serviceProvider.GetService(typeof(IBlobService)) as IBlobService;
        if (blobService == null)
            return;

        foreach (var blobId in blobIds)
        {
            try
            {
                await blobService.DeleteBlobIfUnreferencedAsync(blobId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up detached blob {BlobId}; the persisted entity state remains valid", blobId);
            }
        }
    }

    private sealed record BlobReferencePlan(
        IReadOnlyList<string> AssignedBlobIds,
        IReadOnlyList<string> CleanupBlobIds)
    {
        public static BlobReferencePlan Empty { get; } = new([], []);
        public bool HasChanges => AssignedBlobIds.Count > 0 || CleanupBlobIds.Count > 0;
    }
}
