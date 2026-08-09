namespace Cove.Core.Interfaces;

public interface IBlobReferenceCoordinator
{
    IBlobReferenceLease Acquire(CancellationToken ct = default);
    ValueTask<IBlobReferenceLease> AcquireAsync(CancellationToken ct = default);
    bool WasDeleted(string blobId);
    void MarkAvailable(string blobId);
    void MarkDeleted(string blobId);
}

public interface IBlobReferenceLease : IDisposable, IAsyncDisposable;
