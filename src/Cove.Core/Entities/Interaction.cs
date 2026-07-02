using System.Text.Json;

namespace Cove.Core.Entities;

public enum InteractionHostType
{
    Video = 1,
    Image = 2,
    Performer = 3,
    Tag = 4,
    Face = 5,
    Segment = 6,
    Studio = 7,
    Gallery = 8,
    Group = 9,
    Search = 10,
    Collection = 11,
    Audio = 12,
    Text = 13,
}

public enum InteractionKind
{
    // 1-3 reserved (formerly PlayStart, PlayProgress, PlayEnd — replaced by PlaybackInterval)
    Pause = 4,
    Seek = 5,
    // 6, 7 reserved (formerly Like, Dislike — likes use the dedicated LikeCount path; Dislike was unused)
    LikeCount = 8,
    // 9, 10 reserved (formerly Share, Hide — never had a producer)
    OpenDetail = 11,
    OpenLightbox = 12,
    CloseLightbox = 13,
    Navigate = 14,
    Zoom = 15,
    SearchQuery = 16,
    SearchSelect = 17,
    FilterApply = 18,
    FilterClear = 19,
    PageVisit = 20,
    DerivedLike = 21,
    Fullscreen = 22,
    SlideshowDelay = 23,
}

/// <summary>Non-playback engagement event (search, filter, image open, etc.). Playback is tracked in PlaybackSession/PlaybackInterval.</summary>
public class Interaction : BaseEntity
{
    public int UserId { get; set; }
    public InteractionHostType HostType { get; set; }
    public int HostId { get; set; }
    public InteractionKind Kind { get; set; }
    public DateTime At { get; set; } = DateTime.UtcNow;
    public JsonDocument? Meta { get; set; }
}
