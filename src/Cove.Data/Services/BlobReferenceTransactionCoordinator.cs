using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

/// <summary>
/// Holds the blob-reference lease across an explicit database transaction and defers detached-blob
/// cleanup until the caller confirms that the transaction committed. This is intentionally opt-in:
/// ordinary explicit transactions remain rejected by <see cref="BlobReferenceSaveChangesInterceptor"/>.
/// </summary>
public sealed class BlobReferenceTransactionCoordinator(
    IBlobReferenceCoordinator referenceCoordinator,
    IServiceProvider serviceProvider,
    ILogger<BlobReferenceTransactionCoordinator> logger) : IDisposable, IAsyncDisposable
{
    private IBlobReferenceLease? _lease;
    private DbContext? _ownerContext;
    private readonly HashSet<string> _cleanupBlobIds = new(StringComparer.Ordinal);

    internal bool IsActive => _lease != null;
    internal bool IsActiveFor(DbContext? context) => _lease != null && ReferenceEquals(_ownerContext, context);

    public async ValueTask<Transaction> BeginAsync(DbContext ownerContext, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(ownerContext);
        if (_lease != null)
            throw new InvalidOperationException("A blob-reference transaction is already active in this service scope.");

        _lease = await referenceCoordinator.AcquireAsync(ct);
        _ownerContext = ownerContext;
        _cleanupBlobIds.Clear();
        return new Transaction(this);
    }

    internal void RegisterPlan(
        IReadOnlyCollection<string> assignedBlobIds,
        IReadOnlyCollection<string> cleanupBlobIds)
    {
        if (_lease == null)
            throw new InvalidOperationException("No blob-reference transaction is active.");

        foreach (var blobId in assignedBlobIds)
            if (referenceCoordinator.WasDeleted(blobId))
                throw new InvalidOperationException($"Cannot persist reference to deleted blob {blobId}.");

        foreach (var blobId in cleanupBlobIds)
            _cleanupBlobIds.Add(blobId);
    }

    private async Task CompleteAsync()
    {
        var cleanupBlobIds = _cleanupBlobIds.ToArray();
        _cleanupBlobIds.Clear();
        await ReleaseLeaseAsync();

        var blobService = serviceProvider.GetService(typeof(IBlobService)) as IBlobService;
        if (blobService == null)
            return;

        foreach (var blobId in cleanupBlobIds)
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

    private async ValueTask AbortAsync()
    {
        _cleanupBlobIds.Clear();
        await ReleaseLeaseAsync();
    }

    private async ValueTask ReleaseLeaseAsync()
    {
        _ownerContext = null;
        var lease = Interlocked.Exchange(ref _lease, null);
        if (lease != null)
            await lease.DisposeAsync();
    }

    public ValueTask DisposeAsync() => AbortAsync();

    public void Dispose()
    {
        _cleanupBlobIds.Clear();
        _ownerContext = null;
        Interlocked.Exchange(ref _lease, null)?.Dispose();
    }

    public sealed class Transaction(BlobReferenceTransactionCoordinator owner) : IAsyncDisposable
    {
        private BlobReferenceTransactionCoordinator? _owner = owner;

        public async Task CompleteAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null)
                ?? throw new InvalidOperationException("The blob-reference transaction has already completed.");
            await current.CompleteAsync();
        }

        public async ValueTask DisposeAsync()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current != null)
                await current.AbortAsync();
        }
    }
}
