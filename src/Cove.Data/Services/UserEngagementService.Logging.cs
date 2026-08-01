using Cove.Core.Entities;
using Cove.Core.Events;
using Microsoft.Extensions.Logging;

namespace Cove.Data.Services;

public sealed partial class UserEngagementService
{
    [LoggerMessage(
        EventId = 2701,
        Level = LogLevel.Trace,
        Message = "Recorded {InteractionKind} interaction for {HostType} {HostId}")]
    private partial void TraceInteractionRecorded(
        InteractionKind interactionKind,
        InteractionHostType hostType,
        int hostId);

    [LoggerMessage(
        EventId = 2702,
        Level = LogLevel.Trace,
        Message = "Set favorite for {HostType} {HostId} to {IsFavorite}")]
    private partial void TraceFavoriteSet(
        AffinityHostType hostType,
        int hostId,
        bool isFavorite);

    [LoggerMessage(
        EventId = 2703,
        Level = LogLevel.Trace,
        Message = "Set bookmark for {HostType} {HostId} to {IsBookmarked}")]
    private partial void TraceBookmarkSet(
        AffinityHostType hostType,
        int hostId,
        bool isBookmarked);

    [LoggerMessage(
        EventId = 2704,
        Level = LogLevel.Trace,
        Message = "Updated like count for {HostType} {HostId}; operation={Operation}, count={LikeCount}")]
    private partial void TraceLikeCountChanged(
        AffinityHostType hostType,
        int hostId,
        string operation,
        int likeCount);

    [LoggerMessage(
        EventId = 2705,
        Level = LogLevel.Trace,
        Message = "Recorded playback for {HostType} {HostId}; state={State}, submittedIntervals={SubmittedIntervalCount}, acceptedIntervals={AcceptedIntervalCount}, addedWatchedSeconds={AddedWatchedSeconds:F3}, totalWatchedSeconds={TotalWatchedSeconds:F3}, surface={Surface}, countsAsView={CountsAsView}, completed={IsCompleted}")]
    private partial void TracePlaybackRecorded(
        InteractionHostType hostType,
        int hostId,
        PlaybackSessionState state,
        int submittedIntervalCount,
        int acceptedIntervalCount,
        double addedWatchedSeconds,
        double totalWatchedSeconds,
        string? surface,
        bool countsAsView,
        bool isCompleted);

    [LoggerMessage(
        EventId = 2706,
        Level = LogLevel.Trace,
        Message = "Applied {ChangeType} rating for {HostType} {HostId}; aspect={Aspect}, value={Value}")]
    private partial void TraceRatingChanged(
        EventType changeType,
        AffinityHostType hostType,
        int hostId,
        string aspect,
        int? value);
}
