using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>
/// The host metadata-server surface exposed to extensions: apply a configured metadata source's record to
/// a library entity by that source's own id. It is the direct, by-id counterpart to the interactive
/// Identify — no fingerprint match or candidate ranking — for a caller that already knows the id.
/// </summary>
public interface IMetadataServerService
{
    /// <summary>
    /// Fetches the record identified by <paramref name="videoId"/> from the metadata source configured at
    /// <paramref name="endpoint"/> and applies it to <paramref name="video"/> — title, date, studio,
    /// performers, tags, and cover — governed by <paramref name="importConfig"/> (the source's defaults
    /// apply when it is null, creating a missing studio and performers).
    /// </summary>
    /// <returns><c>true</c> when a remote record was found and applied; <c>false</c> when none matched.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// <paramref name="endpoint"/> matches no configured metadata source; a caller that cannot guarantee a
    /// configured source should treat the call as best-effort and catch this.
    /// </exception>
    Task<bool> MergeVideoAsync(Video video, string endpoint, string videoId, MetadataServerVideoImportRequestDto? importConfig, CancellationToken ct);
}
