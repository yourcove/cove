namespace Cove.Core.Entities;

public class Audio : BaseEntity
{
    public string? Title { get; set; }
    public string? Code { get; set; }
    public string? Details { get; set; }
    public bool Organized { get; set; }
    public int? StudioId { get; set; }
    public DateOnly? Date { get; set; }
    public Cove.Core.Enums.DatePrecision DatePrecision { get; set; }
    public string? ImageBlobId { get; set; }

    public int[] TagIds { get; set; } = [];
    public int[] PerformerIds { get; set; } = [];

    public int FileCount { get; set; }
    public double MaxDuration { get; set; }
    public long MaxBitRate { get; set; }
    public long MaxFileSize { get; set; }
    public DateTime? MaxFileModTime { get; set; }
    public string? MinPath { get; set; }
    public string? MaxPath { get; set; }
    public string? FileSearchText { get; set; }
    public string? SearchText { get; set; }
    public bool HasVideoFiles { get; set; }

    public Studio? Studio { get; set; }
    public ICollection<AudioUrl> Urls { get; set; } = [];
    public ICollection<AudioFile> Files { get; set; } = [];
    public ICollection<AudioTrack> Tracks { get; set; } = [];
    public ICollection<AudioTag> AudioTags { get; set; } = [];
    public ICollection<AudioPerformer> AudioPerformers { get; set; } = [];
}

public class AudioUrl
{
    public int Id { get; set; }
    public int AudioId { get; set; }
    public string Url { get; set; } = string.Empty;
    public Audio? Audio { get; set; }
}

public class AudioTrack : BaseEntity
{
    public int AudioId { get; set; }
    public int OrderIndex { get; set; }
    public string? Title { get; set; }
    public double StartSec { get; set; }
    public double? EndSec { get; set; }
    public Audio? Audio { get; set; }
}

public class AudioTag
{
    public int AudioId { get; set; }
    public int TagId { get; set; }
    public Audio? Audio { get; set; }
    public Tag? Tag { get; set; }
}

public class AudioPerformer
{
    public int AudioId { get; set; }
    public int PerformerId { get; set; }
    public Audio? Audio { get; set; }
    public Performer? Performer { get; set; }
}
