namespace Cove.Core.Entities;

public class Studio : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public bool Favorite { get; set; }
    public string? Details { get; set; }
    public bool Organized { get; set; }
    public string? SearchText { get; set; }

    // Image stored as blob reference
    public string? ImageBlobId { get; set; }
    public string? ImageOverrideBlobId { get; set; }

    // Denormalized relationship counters for hot list/detail reads.
    public int VideoCount { get; set; }
    public int ImageCount { get; set; }
    public int GalleryCount { get; set; }
    public int GroupCount { get; set; }
    public int PerformerCount { get; set; }
    public int ChildStudioCount { get; set; }
    public int TagCount { get; set; }

    // Navigation properties
    public Studio? Parent { get; set; }
    public ICollection<Studio> Children { get; set; } = [];
    public ICollection<StudioUrl> Urls { get; set; } = [];
    public ICollection<StudioAlias> Aliases { get; set; } = [];
    public ICollection<StudioTag> StudioTags { get; set; } = [];
    public ICollection<StudioRemoteId> RemoteIds { get; set; } = [];
    public ICollection<Video> Videos { get; set; } = [];
    public ICollection<Gallery> Galleries { get; set; } = [];
    public ICollection<Image> Images { get; set; } = [];
    public ICollection<Group> Groups { get; set; } = [];
}

public class StudioUrl
{
    public int Id { get; set; }
    public int StudioId { get; set; }
    public string Url { get; set; } = string.Empty;
    public Studio? Studio { get; set; }
}

public class StudioAlias
{
    public int Id { get; set; }
    public int StudioId { get; set; }
    public string Alias { get; set; } = string.Empty;
    public Studio? Studio { get; set; }
}

public class StudioTag
{
    public int StudioId { get; set; }
    public int TagId { get; set; }
    public Studio? Studio { get; set; }
    public Tag? Tag { get; set; }
}

public class StudioRemoteId
{
    public int Id { get; set; }
    public int StudioId { get; set; }
    public string Endpoint { get; set; } = string.Empty;
    public string RemoteId { get; set; } = string.Empty;
    public Studio? Studio { get; set; }
}

