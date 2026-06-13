using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

public interface IFaceLifecycleParticipant
{
    Task OnDeletingAsync(Face face, CancellationToken cancellationToken = default);

    /// <summary>
    /// Called after a bulk AI-data face purge so providers can clean up derived/internal face state that
    /// is not represented by a <see cref="Face"/> row (e.g. an extension's provisional identity graph),
    /// which the per-face <see cref="OnDeletingAsync"/> path would never reach. The default no-op keeps
    /// existing implementers source-compatible.
    /// </summary>
    Task OnFacesPurgedAsync(FacePurgeScope scope, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Called after a host's faces have all been deleted (no faces remain on the host), reporting the model
    /// keys of the removed face detections. Providers that record run/processing history keyed by those
    /// models can prune it so a future re-run no longer treats the work as satisfied and redoes it. The host
    /// owns the "no faces remain" decision and stays agnostic of any provider's run/source layout; providers
    /// act only on their own records. The default no-op keeps existing implementers source-compatible.
    /// </summary>
    Task OnHostFacesClearedAsync(FaceRunEvidenceCleared cleared, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

/// <summary>
/// Reported to <see cref="IFaceLifecycleParticipant.OnHostFacesClearedAsync"/> when a host no longer has any
/// faces after a deletion. <see cref="ModelKeys"/> are the model keys of the face detections that were
/// removed, so a provider can prune the matching evidence from its own run history.
/// </summary>
public sealed record FaceRunEvidenceCleared(
    DetectionHostType HostType,
    int HostId,
    IReadOnlyCollection<string> ModelKeys);

/// <summary>
/// Describes the scope of a face data purge so lifecycle participants can clean up their own derived data.
/// <see cref="IsEntireSource"/> is true when the purge was not narrowed to a specific entity or run — i.e.
/// it clears all face data for the selected source — which is when providers should drop internal state
/// such as provisional identities.
/// </summary>
public sealed record FacePurgeScope(
    string? SourceKey,
    string? HostType,
    int? HostId,
    bool IsEntireSource,
    IReadOnlyCollection<int> PurgedFaceIds);
