using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

public interface ITagProvenanceService
{
    Task RecordAsync(
        AffinityHostType hostType,
        int hostId,
        int tagId,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        string? contextType = null,
        int? contextId = null,
        double? totalDurationSec = null,
        double? hostDurationSec = null,
        CancellationToken cancellationToken = default);

    Task RecordAsync(
        AffinityHostType hostType,
        int hostId,
        Tag tag,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        string? contextType = null,
        int? contextId = null,
        double? totalDurationSec = null,
        double? hostDurationSec = null,
        CancellationToken cancellationToken = default);

    Task SyncTagSetAsync(
        AffinityHostType hostType,
        int hostId,
        IReadOnlyCollection<int> previousTagIds,
        IReadOnlyCollection<int> currentTagIds,
        string sourceKey = "user",
        CancellationToken cancellationToken = default);

    Task RemoveForHostAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken cancellationToken = default);

    // Removes a single source's context-null tag applications for a host EXCEPT the given tag ids.
    // Used when a tag "replace"/"overwrite" apply rebuilds a source's contribution: the manual join
    // rows are cleared directly, but without this the source's provenance rows would linger and keep
    // surfacing removed tags as "derived" effective tags.
    Task RemoveHostSourceApplicationsExceptAsync(
        AffinityHostType hostType,
        int hostId,
        string sourceKey,
        IReadOnlyCollection<int> keepTagIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<int, List<TagProvenanceDto>>> GetLookupAsync(
        AffinityHostType hostType,
        int hostId,
        IReadOnlyCollection<int> tagIds,
        CancellationToken cancellationToken = default);
}