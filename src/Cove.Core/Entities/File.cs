namespace Cove.Core.Entities;

public class Folder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int? ParentFolderId { get; set; }
    public int? ZipFileId { get; set; }
    public DateTime ModTime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public Folder? ParentFolder { get; set; }
    public ICollection<Folder> SubFolders { get; set; } = [];
    public ICollection<BaseFileEntity> Files { get; set; } = [];
}

public abstract class BaseFileEntity
{
    public int Id { get; set; }
    public string Basename { get; set; } = string.Empty;
    public int ParentFolderId { get; set; }
    public int? ZipFileId { get; set; }
    public long Size { get; set; }
    public DateTime ModTime { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Denormalized full path (forward-slash form). Populated by CoveContext.SaveChanges
    // from ParentFolder.Path + "/" + Basename. Stored + btree-indexed so list endpoints
    // can sort/filter on path with an index instead of a per-row correlated subquery.
    public string Path { get; set; } = string.Empty;

    // Navigation
    public Folder? ParentFolder { get; set; }
    public ICollection<FileFingerprint> Fingerprints { get; set; } = [];

    public static string ComputePath(string? folderPath, string basename)
    {
        if (string.IsNullOrEmpty(folderPath))
            return basename;
        var normalized = folderPath.Replace('\\', '/');
        if (normalized.EndsWith('/'))
            return normalized + basename;
        return normalized + "/" + basename;
    }
}

public class VideoFile : BaseFileEntity
{
    public string Format { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }
    public double Duration { get; set; }
    public string VideoCodec { get; set; } = string.Empty;
    public string AudioCodec { get; set; } = string.Empty;
    public double FrameRate { get; set; }
    public long BitRate { get; set; }

    // FK to Video
    public int? VideoId { get; set; }
    public Video? Video { get; set; }

    // Navigation
    public ICollection<VideoCaption> Captions { get; set; } = [];
}

public class ImageFile : BaseFileEntity
{
    public string Format { get; set; } = string.Empty;
    public int Width { get; set; }
    public int Height { get; set; }

    // FK to Image
    public int? ImageId { get; set; }
    public Image? Image { get; set; }
}

public class GalleryFile : BaseFileEntity
{
    // FK to Gallery
    public int? GalleryId { get; set; }
    public Gallery? Gallery { get; set; }
}

public class AudioFile : BaseFileEntity
{
    public string Format { get; set; } = string.Empty;
    public double Duration { get; set; }
    public string AudioCodec { get; set; } = string.Empty;
    public long BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? Channels { get; set; }
    public bool HasVideoTrack { get; set; }

    // FK to Audio
    public int? AudioId { get; set; }
    public Audio? Audio { get; set; }
}

public class TextFile : BaseFileEntity
{
    public string Format { get; set; } = string.Empty;
    public int? PageCount { get; set; }
    public int? WordCount { get; set; }
    public string? ExcerptText { get; set; }

    // FK to Text document
    public int? TextDocumentId { get; set; }
    public TextDocument? TextDocument { get; set; }
}

public class FileFingerprint
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public string Type { get; set; } = string.Empty; // "oshash", "md5", "phash"
    public string Value { get; set; } = string.Empty;
    public BaseFileEntity? File { get; set; }
}

public class VideoCaption
{
    public int Id { get; set; }
    public int FileId { get; set; }
    public string LanguageCode { get; set; } = "00"; // ISO 639-1 or "00" for unknown
    public string CaptionType { get; set; } = "vtt"; // "vtt" or "srt"
    public string Filename { get; set; } = string.Empty; // Sidecar caption filename
    public VideoFile? File { get; set; }
}

