using Cove.Core.DTOs;

namespace Cove.Core.Interfaces;

/// <summary>
/// Corrects which face a host's detected occurrences belong to.
///
/// Cove records that a face appears on a video or image, but the evidence behind that — per-track
/// embeddings, the identity graph they were clustered into — belongs to whichever extension produced
/// them. So the host owns the routes and the UI, and defers the actual re-homing to the provider,
/// exactly as it does for suggestions (<see cref="IFaceSuggester"/>) and deletion
/// (<see cref="IFaceLifecycleParticipant"/>).
///
/// Implementations are published through the extension service exchange. When none is registered the
/// host reports the capability as unavailable and hides the corresponding actions, rather than
/// hardcoding any single extension's endpoints.
/// </summary>
public interface IFaceOccurrenceEditor
{
    /// <summary>
    /// The face's separate tracked appearances on one host — the units <see cref="SplitAsync"/> moves.
    /// Empty when the face is not on that host, or when the provider has no track-level evidence for it.
    /// </summary>
    Task<IReadOnlyList<FaceHostTrackDto>> GetHostTracksAsync(
        int faceId,
        string hostType,
        int hostId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the named appearances of a face on one host onto a different face — an existing one that
    /// matches them, or a new one. This is how two people merged into a single face inside the same
    /// video get pulled apart; <see cref="MarkNotPresentAsync"/> can only reject a face from a host
    /// wholesale.
    /// </summary>
    Task<FaceOccurrenceSplitResultDto> SplitAsync(
        int faceId,
        string hostType,
        int hostId,
        IReadOnlyList<string> groupKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a face is not really present on a host, re-homing its occurrences there (and any
    /// elsewhere that clearly match them) onto the correct face, and suppressing re-attachment on a
    /// future re-analysis.
    /// </summary>
    Task<FaceNotPresentResultDto> MarkNotPresentAsync(
        int faceId,
        string hostType,
        int hostId,
        CancellationToken cancellationToken = default);
}
