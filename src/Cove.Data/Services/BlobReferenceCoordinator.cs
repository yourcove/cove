using System.Collections.Concurrent;
using Cove.Core.Interfaces;

namespace Cove.Data.Services;

public sealed class BlobReferenceCoordinator : IBlobReferenceCoordinator
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _deletedBlobIds = new(StringComparer.Ordinal);

    public IBlobReferenceLease Acquire(CancellationToken ct = default)
    {
        _gate.Wait(ct);
        return new Lease(_gate);
    }

    public async ValueTask<IBlobReferenceLease> AcquireAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        return new Lease(_gate);
    }

    public bool WasDeleted(string blobId) => _deletedBlobIds.ContainsKey(blobId);

    public void MarkAvailable(string blobId) => _deletedBlobIds.TryRemove(blobId, out _);

    public void MarkDeleted(string blobId) => _deletedBlobIds.TryAdd(blobId, 0);

    private sealed class Lease(SemaphoreSlim gate) : IBlobReferenceLease
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
