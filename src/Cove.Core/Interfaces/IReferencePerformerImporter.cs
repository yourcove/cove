namespace Cove.Core.Interfaces;

/// <summary>
/// Best-effort import of a performer's full metadata from a configured metadata server, keyed by the
/// site endpoint and that site's remote id. Used when a face reference (SAIE) match is accepted and a
/// local performer was created or matched by name: if the user has the originating site configured as a
/// metadata server, the performer is enriched (image, bio, measurements, aliases, …) from it.
///
/// Implemented by the host (which owns the metadata-server integration) and consumed by extensions.
/// Implementations must be safe to call when no matching metadata server is configured — in that case
/// they simply return <c>false</c> and leave the performer as-is (its remote id is still recorded so a
/// later scrape can enrich it).
/// </summary>
public interface IReferencePerformerImporter
{
    Task<bool> TryImportAsync(int performerId, string endpoint, string externalId, CancellationToken cancellationToken = default);
}
