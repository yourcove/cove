using Cove.Core.DTOs;

namespace Cove.Core.Interfaces;

/// <summary>
/// Reads and writes the shot boundaries of video files: for each file, at most one contiguous,
/// gapless partition of its timeline into shots.
/// </summary>
/// <remarks>
/// <para>
/// This service is the only writer of <see cref="Entities.VideoShotSet"/>, and it keeps every set a
/// valid partition: shots are ordered, the first starts at 0, each starts where the previous ended,
/// and the last ends at the set's duration. Boundaries within
/// <see cref="Common.VideoShotRules.BoundaryToleranceSec"/> of where they belong are snapped into
/// place; anything else is rejected rather than repaired.
/// </para>
/// <para>
/// Sets belong to files, not videos, because timestamps belong to the analysed file.
/// <see cref="GetForVideoAsync"/> reads the set of the video's primary file.
/// </para>
/// <para>
/// Writes run on the scoped Cove database context but bypass its change tracker: every write that
/// changes a set is a single statement on the set's row, and joins the caller's transaction when there
/// is one, without a savepoint: a statement that fails, for example when cancelled, aborts that
/// transaction. A write neither saves the context's other pending changes nor refreshes instances of
/// <see cref="Entities.VideoShotSet"/> that the context tracks, so those can be stale. Read sets
/// through this service rather than tracking them, and never modify them directly. A write that loses
/// a race with another changes nothing: an edit, or a replace of an expected set, answers Conflict with
/// the current set, and a write that would create a set where another has just been created finds
/// that set instead. In a REPEATABLE READ or SERIALIZABLE transaction, a write that races another can
/// fail with a serialization error instead; retry the whole transaction.
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
