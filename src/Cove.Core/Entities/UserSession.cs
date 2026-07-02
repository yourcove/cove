namespace Cove.Core.Entities;

/// <summary>
/// A user-global "session": a timeout-bounded span of a single user's engagement, spanning entities AND
/// devices. Server-authoritative — resolved from the user's last activity time, not a client id, so reloads
/// and other devices continue the same session instead of fragmenting it. Per-entity <see cref="PlaybackSession"/>
/// rows reference it. When a new session starts after the idle timeout, the previous one is finalized and its
/// single "derived like" is awarded to the last entity (of any type) the user engaged with.
/// </summary>
public class UserSession : BaseEntity
{
    public int UserId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
    /// <summary>The last entity (any host type) the user engaged with in this session — the derived-like target.</summary>
    public InteractionHostType? LastHostType { get; set; }
    public int? LastHostId { get; set; }
    public bool DerivedLikeAwarded { get; set; }
}
