namespace Cove.Api.Services;

// Kept out of Cove.Core: only the HTTP API returns these, so they need not become extension ABI.

/// <summary>How many of one kind of generated item a scope holds, and how many of those are not made yet.</summary>
public record GenerateWorkCountDto
{
    public int Missing { get; init; }
    public int Total { get; init; }
    /// <summary>The size of the files behind the counts, for work that reads whole files (MD5).</summary>
    public long MissingBytes { get; init; }
    public long TotalBytes { get; init; }
    /// <summary>Missing was scaled up from a sample rather than counted, so it is rough.</summary>
    public bool Estimated { get; init; }
}

/// <summary>The segment covers of one segment kind ("" for segments without one).</summary>
public record GenerateSegmentKindCountDto
{
    public string Kind { get; init; } = "";
    public int Missing { get; init; }
    public int Total { get; init; }
    public bool Estimated { get; init; }
}

/// <summary>
/// What Generate would find to do in a scope, per option, so a run can be sized before it starts. Options
/// for media the scope cannot reach (images, galleries, audio and text when videos were picked) are null.
/// </summary>
public record GenerateEstimateDto
{
    public GenerateWorkCountDto? Thumbnails { get; init; }
    public GenerateWorkCountDto? Previews { get; init; }
    public GenerateWorkCountDto? Sprites { get; init; }
    public GenerateWorkCountDto? VrStereo { get; init; }
    public List<GenerateSegmentKindCountDto>? SegmentCovers { get; init; }
    public GenerateWorkCountDto? Phashes { get; init; }
    /// <summary>Every file Generate's MD5 option reaches in the scope: video, image, gallery, audio and text.</summary>
    public GenerateWorkCountDto? Md5 { get; init; }
    public GenerateWorkCountDto? ImageThumbnails { get; init; }
    public GenerateWorkCountDto? ImagePhashes { get; init; }
    public GenerateWorkCountDto? GalleryThumbnails { get; init; }
    public GenerateWorkCountDto? AudioPhashes { get; init; }
    public GenerateWorkCountDto? TextPhashes { get; init; }
}
