using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

public sealed record FaceSuggestionOptions(bool IncludeReferenceMatches = true);
// PerformerId is the primary match. SecondaryPerformerIds carries the other competing matches when the
// decision is a merge; each id may be a real performer id or a provider-encoded reference id, mirroring
// PerformerId. Null/empty for accept and reject.
public sealed record FaceSuggestionDecisionRequest(int FaceId, int PerformerId, string Decision, bool SetPerformerImage, IReadOnlyList<int>? SecondaryPerformerIds = null);
public sealed record FaceSuggestionDecisionOutcome(bool Handled, bool Succeeded, string? Error = null, int? StatusCode = null)
{
    public static readonly FaceSuggestionDecisionOutcome NotHandled = new(false, false);
    public static readonly FaceSuggestionDecisionOutcome Success = new(true, true);

    public static FaceSuggestionDecisionOutcome Failure(string error, int? statusCode = null)
        => new(true, false, error, statusCode);
}

public interface IFaceSuggestionDecisionHandler
{
    Task<FaceSuggestionDecisionOutcome> TryHandleAsync(FaceSuggestionDecisionRequest request, CancellationToken cancellationToken = default);
}

public interface IFaceSuggester
{
    Task<IReadOnlyList<FaceSuggestionDto>> SuggestForAsync(int faceId, int maxResults, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FaceSuggestionDto>> SuggestForAsync(int faceId, int maxResults, FaceSuggestionOptions options, CancellationToken cancellationToken = default)
        => SuggestForAsync(faceId, maxResults, cancellationToken);

    async Task<IReadOnlyDictionary<int, IReadOnlyList<FaceSuggestionDto>>> SuggestForBatchAsync(
        IReadOnlyCollection<int> faceIds,
        int maxResults,
        FaceSuggestionOptions options,
        CancellationToken cancellationToken = default)
    {
        var suggestionsByFaceId = new Dictionary<int, IReadOnlyList<FaceSuggestionDto>>();
        foreach (var faceId in faceIds.Where(static id => id > 0).Distinct())
        {
            suggestionsByFaceId[faceId] = await SuggestForAsync(faceId, maxResults, options, cancellationToken);
        }

        return suggestionsByFaceId;
    }
}
/// <summary>
/// Propagates a face-to-performer link change to the video/image performer lists.
/// When a face is linked or unlinked from a performer, this service updates the
/// corresponding video and image performer associations. Available to any extension
/// that manages face identity assignments.
/// </summary>
public interface IFacePerformerPropagationService
{
    Task ApplyLinkChangeAsync(int faceId, int? oldPerformerId, int? newPerformerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles the performer assignments owned by face propagation for a single host
    /// (video/image). Applies the performer of every linked face currently appearing on the host
    /// (if not already present) and removes assignments for faces that no longer appear on it or are
    /// no longer linked. Used by the AI processing path after it (re)writes a host's face
    /// appearances, so matching an already-linked face applies that performer — and re-processing
    /// that drops a face removes the performer it had contributed.
    /// </summary>
    Task ReconcileHostAsync(FaceAppearanceHostType hostType, int hostId,
        CancellationToken cancellationToken = default);
}

