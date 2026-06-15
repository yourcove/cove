namespace Cove.Core.Entities;

public class Tag : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? SortName { get; set; }
    public string? Description { get; set; }
    public string? Color { get; set; }
    public int? TagGroupId { get; set; }
    public bool Favorite { get; set; }
    public bool Organized { get; set; }
    public double? MinOccurrenceSec { get; set; }
    public double? MinOccurrencePercent { get; set; }
    public bool? ShowAsSegment { get; set; }
    public string? SegmentColorOverride { get; set; }
    public int? SegmentLaneOverride { get; set; }
    public string? SearchText { get; set; }

    // Image stored as blob reference
    public string? ImageBlobId { get; set; }
    public string? ImageOverrideBlobId { get; set; }

    // Denormalized usage counters for hot list/detail reads.
    public int VideoCount { get; set; }
    public int VideoMarkerCount { get; set; }
    public int ImageCount { get; set; }
    public int GalleryCount { get; set; }
    public int GroupCount { get; set; }
    public int PerformerCount { get; set; }
    public int StudioCount { get; set; }

    // Navigation properties
    public ICollection<TagAlias> Aliases { get; set; } = [];
    public ICollection<TagParent> ParentRelations { get; set; } = [];
    public ICollection<TagParent> ChildRelations { get; set; } = [];
    public ICollection<TagRemoteId> RemoteIds { get; set; } = [];
    public TagGroup? TagGroup { get; set; }

    // Reverse nav for many-to-many
    public ICollection<VideoTag> VideoTags { get; set; } = [];
    public ICollection<PerformerTag> PerformerTags { get; set; } = [];
    public ICollection<ImageTag> ImageTags { get; set; } = [];
    public ICollection<GalleryTag> GalleryTags { get; set; } = [];
    public ICollection<StudioTag> StudioTags { get; set; } = [];
    public ICollection<GroupTag> GroupTags { get; set; } = [];
}

public class TagGroup : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }

    public ICollection<Tag> Tags { get; set; } = [];
}

public class TagAlias
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public Tag? Tag { get; set; }
}

public class TagParent
{
    public int ParentId { get; set; }
    public int ChildId { get; set; }
    public Tag? Parent { get; set; }
    public Tag? Child { get; set; }
}

public class TagRemoteId
{
    public int Id { get; set; }
    public int TagId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public Tag? Tag { get; set; }
}

