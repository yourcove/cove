namespace Cove.Core.Interfaces;

public interface IBlobService
{
    Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default);
    Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default);

    /// <summary>Deletes a payload only when no persisted entity still references it.</summary>
    Task DeleteBlobAsync(string blobId, CancellationToken ct = default);

    /// <summary>
    /// Cleans up a payload after the caller has persisted its reference removal. Cleanup failure may
    /// leave an orphaned payload, but must never invalidate a persisted entity reference.
    /// </summary>
    Task DeleteBlobIfUnreferencedAsync(string blobId, CancellationToken ct = default)
        => DeleteBlobAsync(blobId, ct);
}
