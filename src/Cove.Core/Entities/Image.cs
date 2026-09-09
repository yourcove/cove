namespace Cove.Core.Entities;

public class Image : BaseEntity
{
    public string? Title { get; set; }
    public string? Code { get; set; }
    public string? Details { get; set; }
    public string? Photographer { get; set; }
    public bool Organized { get; set; }
    public int? StudioId { get; set; }
    public DateOnly? Date { get; set; }
    public Cove.Core.Enums.DatePrecision DatePrecision { get; set; }

    // Denormalized M2M id sets, GIN-indexed. See Video.TagIds for rationale.
    public int[] TagIds { get; set; } = [];
    public int[] PerformerIds { get; set; } = [];

    // Denormalized relationship counters for hot list/detail reads.
    public int TagCount { get; set; }
    public int PerformerCount { get; set; }
    public int GalleryCount { get; set; }

    // Denormalized file summaries for hot list filters and sorts.
    public int FileCount { get; set; }
    public int MaxResolution { get; set; }
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
    public ICollection<ImageUrl> Urls { get; set; } = [];
    public ICollection<ImageFile> Files { get; set; } = [];
    public ICollection<ImageTag> ImageTags { get; set; } = [];
    public ICollection<ImagePerformer> ImagePerformers { get; set; } = [];
    public ICollection<ImageGallery> ImageGalleries { get; set; } = [];
}

public class ImageUrl
{
    public int Id { get; set; }
    public int ImageId { get; set; }
    public string Url { get; set; } = string.Empty;
    public Image? Image { get; set; }
}

public class ImageTag
{
    public int ImageId { get; set; }
    public int TagId { get; set; }
    public Image? Image { get; set; }
    public Tag? Tag { get; set; }
}

public class ImagePerformer
{
    public int ImageId { get; set; }
    public int PerformerId { get; set; }
    public Image? Image { get; set; }
    public Performer? Performer { get; set; }
}

public class ImageGallery
{
    public int ImageId { get; set; }
    public int GalleryId { get; set; }
    public Image? Image { get; set; }
    public Gallery? Gallery { get; set; }
}
