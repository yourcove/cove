namespace Cove.Core.Entities;

public class Group : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public GroupKind Kind { get; set; } = GroupKind.Static;
    public string? QuerySourceKey { get; set; }
    public string? QueryJson { get; set; }
    public DateTime? LastResolvedAt { get; set; }
    public int? CachedItemCount { get; set; }
    public int CacheTtlSec { get; set; } = 60;
    public bool ShowInVideoLists { get; set; } = false;
    public int SortOrder { get; set; }
    public List<string> AllowedHostTypes { get; set; } = ["video", "image", "audio", "text", "group", "performer", "studio", "tag", "gallery", "face", "segment"];
    public string? Aliases { get; set; }
    public int? Duration { get; set; } // seconds
    public DateOnly? Date { get; set; }
    public Cove.Core.Enums.DatePrecision DatePrecision { get; set; }
    public int? StudioId { get; set; }
    public string? Director { get; set; }
    public string? Synopsis { get; set; }
    public string? SearchText { get; set; }

    // Image blobs
    public string? FrontImageBlobId { get; set; }
    public string? BackImageBlobId { get; set; }

    // Navigation properties
    public Studio? Studio { get; set; }
    public ICollection<GroupUrl> Urls { get; set; } = [];
    public ICollection<GroupTag> GroupTags { get; set; } = [];
    public ICollection<GroupItem> GroupItems { get; set; } = [];
    public ICollection<GroupRelation> ContainingGroupRelations { get; set; } = [];
    public ICollection<GroupRelation> SubGroupRelations { get; set; } = [];
}

public enum GroupKind
{
    Static = 1,
    Dynamic = 2,
}

public enum GroupItemKind
{
    Video = 1,
    VideoRange = 2,
    Image = 3,
    Audio = 4,
    Text = 5,
    Group = 6,
    Performer = 7,
    Studio = 8,
    Tag = 9,
    Gallery = 10,
    Face = 11,
    Segment = 12,
}

public class GroupItem : BaseEntity
{
    public int GroupId { get; set; }
    public int OrderIndex { get; set; }
    public GroupItemKind Kind { get; set; }
    public string HostType { get; set; } = "video";
    public int HostId { get; set; }
    public int? VideoId { get; set; }
    public int? ImageId { get; set; }
    public int? ChildGroupId { get; set; }
    public double? StartSec { get; set; }
    public double? EndSec { get; set; }
    public string? Title { get; set; }
    public string? Notes { get; set; }
    public string? SourceSpanKey { get; set; }
    public int? SourceProfileId { get; set; }
    public string? SourceQueryJson { get; set; }
    public DateTime? SnapshotAt { get; set; }

    public Group? Group { get; set; }
    public Video? Video { get; set; }
    public Image? Image { get; set; }
    public Group? ChildGroup { get; set; }
}

public class GroupUrl
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string Url { get; set; } = string.Empty;
    public Group? Group { get; set; }
}

public class GroupTag
{
    public int GroupId { get; set; }
    public int TagId { get; set; }
    public Group? Group { get; set; }
    public Tag? Tag { get; set; }
}

public class GroupRelation
{
    public int ContainingGroupId { get; set; }
    public int SubGroupId { get; set; }
    public int OrderIndex { get; set; }
    public string? Description { get; set; }
    public Group? ContainingGroup { get; set; }
    public Group? SubGroup { get; set; }
}
