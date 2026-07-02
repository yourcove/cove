using System.Text.Json;

namespace Cove.Core.Entities;

public enum PlaybackSessionState
{
    Active = 1,
    Paused = 2,
    Ended = 3,
    Abandoned = 4,
}

/// <summary>One per (user, media, client-generated session ID). Tracks session-level state and watched-time totals.</summary>
public class PlaybackSession : BaseEntity
{
    public int UserId { get; set; }
    public InteractionHostType HostType { get; set; }
    public int HostId { get; set; }
    /// <summary>Client-generated GUID for this player mount. Advisory only — the server resolves session
    /// continuity via <see cref="UserSessionId"/>, so reloads/devices don't fragment a watch.</summary>
    public Guid SessionId { get; set; }
    /// <summary>The user-global <see cref="UserSession"/> this per-entity session belongs to.</summary>
    public int? UserSessionId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; set; }
    public PlaybackSessionState State { get; set; } = PlaybackSessionState.Active;
    /// <summary>Duration of the media as last reported by the client.</summary>
    public double MediaDurationSec { get; set; }
    /// <summary>Last known playback position.</summary>
    public double? LastPositionSec { get; set; }
    /// <summary>Sum of distinct seconds watched in this session (computed from merged intervals on each update).</summary>
    public double TotalWatchedSec { get; set; }
    public bool IsCompleted { get; set; }
    public bool CountsAsView { get; set; }
    public bool DerivedLikeAwarded { get; set; }
    public string? Surface { get; set; }
    public string? ScopeKey { get; set; }
    public InteractionHostType? ParentHostType { get; set; }
    public int? ParentHostId { get; set; }
    public InteractionHostType? ItemHostType { get; set; }
    public int? ItemHostId { get; set; }
    public int? GroupItemId { get; set; }
    public int? SegmentId { get; set; }
    public double? ClipStartSec { get; set; }
    public double? ClipEndSec { get; set; }
    public bool? Autoplay { get; set; }
    public bool? Muted { get; set; }
    public bool? Fullscreen { get; set; }
    public double? PlaybackRate { get; set; }
    public string? Route { get; set; }
    public string? Referrer { get; set; }
    public string? RecommendationSource { get; set; }
    public JsonDocument? Context { get; set; }
    public List<PlaybackInterval> Intervals { get; set; } = [];
}

/// <summary>Append-only record of one contiguous play segment as reported by the client.</summary>
public class PlaybackInterval : BaseEntity
{
    public int PlaybackSessionId { get; set; }
    public PlaybackSession Session { get; set; } = null!;
    public int UserId { get; set; }
    public InteractionHostType HostType { get; set; }
    public int HostId { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
    public string? Surface { get; set; }
    public string? ScopeKey { get; set; }
    public InteractionHostType? ParentHostType { get; set; }
    public int? ParentHostId { get; set; }
    public InteractionHostType? ItemHostType { get; set; }
    public int? ItemHostId { get; set; }
    public int? GroupItemId { get; set; }
    public int? SegmentId { get; set; }
    public double? ClipStartSec { get; set; }
    public double? ClipEndSec { get; set; }
    public double? PlaybackRate { get; set; }
    public JsonDocument? Context { get; set; }
}