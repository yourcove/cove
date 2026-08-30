namespace Cove.Core.Entities;

public class Video : BaseEntity
{
    public string? Title { get; set; }
    public string? Code { get; set; }
    public string? Details { get; set; }
    public string? Director { get; set; }
    public DateOnly? Date { get; set; }
    public Cove.Core.Enums.DatePrecision DatePrecision { get; set; }
    public bool Organized { get; set; }
    public bool IsVr { get; set; }
    public int? StudioId { get; set; }
    public string? Captions { get; set; }
    public string? ImageBlobId { get; set; }
    public int? ParentVideoId { get; set; }
    public double? ClipStartSec { get; set; }
    public double? ClipEndSec { get; set; }

    // Denormalized M2M id sets, GIN-indexed. Maintained from VideoTags/VideoPerformers
    // by CoveContext on save. Lets tag/performer combo filters use a single index-only
    // array containment scan (e.g. WHERE tag_ids @> ARRAY[1,2,3]) instead of N joins.
    public int[] TagIds { get; set; } = [];
    public int[] PerformerIds { get; set; } = [];

    // Denormalized file summaries for hot list filters and sorts.
    public int FileCount { get; set; }
    public double MaxDuration { get; set; }
    public int MaxResolution { get; set; }
    public int MaxHeight { get; set; }
    public double MaxFrameRate { get; set; }
    public long MaxBitRate { get; set; }
    public long MaxFileSize { get; set; }
    public DateTime? MaxFileModTime { get; set; }
    public string? MinPath { get; set; }
    public string? MaxPath { get; set; }
    public string? FileSearchText { get; set; }
    public string? SearchText { get; set; }
    public bool HasDimensionData { get; set; }
    public bool HasLandscapeFiles { get; set; }
    public bool HasPortraitFiles { get; set; }
    public bool HasSquareFiles { get; set; }

    // Navigation properties
    public Studio? Studio { get; set; }
    public Video? ParentVideo { get; set; }
    public ICollection<Video> ChildVideos { get; set; } = [];
    public ICollection<VideoUrl> Urls { get; set; } = [];
    public ICollection<VideoFile> Files { get; set; } = [];
    public ICollection<VideoTag> VideoTags { get; set; } = [];
    public ICollection<VideoPerformer> VideoPerformers { get; set; } = [];
    public ICollection<VideoGallery> VideoGalleries { get; set; } = [];
    public ICollection<GroupItem> GroupItems { get; set; } = [];
    public ICollection<VideoRemoteId> RemoteIds { get; set; } = [];
    public ICollection<VideoPlayHistory> PlayHistory { get; set; } = [];
}

public class VideoUrl
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public string Url { get; set; } = string.Empty;
    public Video? Video { get; set; }
}

public class VideoTag
{
    public int VideoId { get; set; }
    public int TagId { get; set; }
    public Video? Video { get; set; }
    public Tag? Tag { get; set; }
}

public class VideoPerformer
{
    public int VideoId { get; set; }
    public int PerformerId { get; set; }
    public Video? Video { get; set; }
    public Performer? Performer { get; set; }
}

public class VideoGallery
{
    public int VideoId { get; set; }
    public int GalleryId { get; set; }
    public Video? Video { get; set; }
    public Gallery? Gallery { get; set; }
}

public class VideoRemoteId
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public Video? Video { get; set; }
}

public class VideoPlayHistory
{
    public int Id { get; set; }
    public int VideoId { get; set; }
    public DateTime PlayedAt { get; set; }
    public Video? Video { get; set; }
}
