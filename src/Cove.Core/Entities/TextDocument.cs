namespace Cove.Core.Entities;

public class TextDocument : BaseEntity
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
    public int? MaxWordCount { get; set; }
    public int? MaxPageCount { get; set; }
    public long MaxFileSize { get; set; }
    public DateTime? MaxFileModTime { get; set; }
    public string? MinPath { get; set; }
    public string? MaxPath { get; set; }
    public string? FileSearchText { get; set; }
    public string? SearchText { get; set; }

    public Studio? Studio { get; set; }
    public ICollection<TextUrl> Urls { get; set; } = [];
    public ICollection<TextFile> Files { get; set; } = [];
    public ICollection<TextTag> TextTags { get; set; } = [];
    public ICollection<TextPerformer> TextPerformers { get; set; } = [];
}

public class TextUrl
{
    public int Id { get; set; }
    public int TextDocumentId { get; set; }
    public string Url { get; set; } = string.Empty;
    public TextDocument? TextDocument { get; set; }
}

public class TextTag
{
    public int TextDocumentId { get; set; }
    public int TagId { get; set; }
    public TextDocument? TextDocument { get; set; }
    public Tag? Tag { get; set; }
}

public class TextPerformer
{
    public int TextDocumentId { get; set; }
    public int PerformerId { get; set; }
    public TextDocument? TextDocument { get; set; }
    public Performer? Performer { get; set; }
}
