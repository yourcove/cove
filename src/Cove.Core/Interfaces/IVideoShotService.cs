using Cove.Core.DTOs;

namespace Cove.Core.Interfaces;

/// <summary>
/// Reads and writes the shot boundaries of video files: for each file, at most one contiguous,
/// gapless partition of its timeline into shots.
/// </summary>
/// <remarks>
/// <para>
/// This service is the only writer of <see cref="Entities.VideoShotSet"/> and
/// <see cref="Entities.VideoShot"/>, and it keeps every set a valid partition: shots are ordered,
/// the first starts at 0, each starts where the previous ended, and the last ends at the set's
/// duration. Boundaries within <see cref="Common.VideoShotRules.BoundaryToleranceSec"/> of where they
/// belong are snapped into place; anything else is rejected rather than repaired.
/// </para>
/// <para>
/// Sets belong to files, not videos, because timestamps belong to the analysed file.
/// <see cref="GetForVideoAsync"/> reads the set of the video's primary file.
/// </para>
/// <para>
/// Writes run on the scoped Cove database context. Every write that changes a set saves the context's
/// other pending changes along with its own; an outcome that changes nothing (SkippedExisting,
/// Conflict, Invalid, FileNotFound, NotFound, or a moved cut that stays where it was) saves nothing.
/// When the caller has already begun a transaction on the context, a write joins that transaction
/// inside a savepoint; otherwise it runs in a transaction of its own, which the retrying execution
/// strategy may run again after a transient failure, and pending changes that a failed attempt had
/// saved are not saved again. Begin a transaction first when your changes must commit together with
/// the shots.
/// </para>
/// <para>
/// Writers of the same file wait for each other on row locks, which assumes READ COMMITTED, the
/// default. In a REPEATABLE READ or SERIALIZABLE transaction, a write that waited reads the file's set
/// as it was before the other write committed and can fail with a serialization or unique-key error;
/// retry the whole transaction. Instances of these entities that the caller tracks are set aside while
/// a write runs and put back afterwards unchanged, so they can be stale; instances of rows the write
/// deleted are left detached. Read them through this service rather than tracking them, and never
/// modify them directly.
/// </para>
/// <para>
/// The service authorizes nothing and publishes no events: check the caller's permissions before
/// calling it. It is implemented by Cove only, so members added later arrive with default
/// implementations.
/// </para>
/// </remarks>
public interface IVideoShotService
{
    /// <summary>The file's set with its shots, or null when the file has none.</summary>
    Task<VideoShotSetDto?> GetForFileAsync(int fileId, CancellationToken cancellationToken = default);

    /// <summary>The set of the video's primary file with its shots, or null when that file has none.</summary>
    Task<VideoShotSetDto?> GetForVideoAsync(int videoId, CancellationToken cancellationToken = default);

    /// <summary>Summaries, without shots, of the sets of every file of the video.</summary>
    Task<IReadOnlyList<VideoShotSetDto>> ListForVideoAsync(int videoId, CancellationToken cancellationToken = default);

    /// <summary>Summaries, without shots, keyed by file id, for those of the given files that have a set.</summary>
    Task<IReadOnlyDictionary<int, VideoShotSetDto>> GetSummariesForFilesAsync(
        IReadOnlyCollection<int> fileIds,
        CancellationToken cancellationToken = default);

    /// <summary>Writes a whole set for a file, according to <see cref="VideoShotSetWrite.WriteMode"/>.</summary>
    Task<VideoShotWriteResult> WriteSetAsync(VideoShotSetWrite write, CancellationToken cancellationToken = default);

    Task<VideoShotEditResult> SplitAsync(VideoShotSplitRequest request, CancellationToken cancellationToken = default);

    Task<VideoShotEditResult> MergeAsync(VideoShotMergeRequest request, CancellationToken cancellationToken = default);

    Task<VideoShotEditResult> MoveCutAsync(VideoShotMoveCutRequest request, CancellationToken cancellationToken = default);

    Task<VideoShotEditResult> ReplaceShotsAsync(VideoShotReplaceRequest request, CancellationToken cancellationToken = default);

    Task<VideoShotEditResult> DeleteAsync(VideoShotDeleteRequest request, CancellationToken cancellationToken = default);
}
