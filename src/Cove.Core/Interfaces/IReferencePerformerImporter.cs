namespace Cove.Core.Interfaces;

/// <summary>
/// Best-effort import of a performer's full metadata from a configured metadata server, keyed by the
/// site endpoint and that site's remote id. Used when a face reference (SAIE) match is accepted and a
/// local performer was created or matched by name: if the user has the originating site configured as a
/// metadata server, the performer is enriched (image, bio, measurements, aliases, …) from it.
///
/// The originating site's remote id is always recorded on the performer (so a later scrape can enrich it
/// even if no server is configured yet); the metadata scrape itself is gated by <paramref name="importMetadata"/>.
///
/// Implemented by the host (which owns the metadata-server integration) and consumed by extensions.
/// Implementations must be safe to call when no matching metadata server is configured — in that case
/// they simply return <c>false</c> and leave the performer as-is (its remote id is still recorded so a
/// later scrape can enrich it).
/// </summary>
public interface IReferencePerformerImporter
{
    /// <param name="importMetadata">
    /// When <c>true</c> (default) the performer is scraped/enriched from the matching metadata server.
    /// When <c>false</c> only the remote id is recorded and no scrape is performed — used when the user
    /// has disabled "Update existing performers from metadata servers".
    /// </param>
    Task<bool> TryImportAsync(int performerId, string endpoint, string externalId, bool importMetadata = true, CancellationToken cancellationToken = default);
}
