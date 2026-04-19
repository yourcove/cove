namespace Cove.Core.Interfaces;

public class CoveConfiguration
{
    public List<CovePath> CovePaths { get; set; } = [];
    public string DatabaseConnectionString { get; set; } = string.Empty;
    public string GeneratedPath { get; set; } = CoveDefaultPaths.GetDataSubdirectory("generated");
    public string CachePath { get; set; } = CoveDefaultPaths.GetDataSubdirectory("cache");
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 9999;
    public int MaxParallelTasks { get; set; } = 1;
    public bool CalculateMd5 { get; set; }
    public List<string> VideoExtensions { get; set; } = [".m4v", ".mp4", ".mov", ".wmv", ".avi", ".mpg", ".mpeg", ".rmvb", ".rm", ".flv", ".asf", ".mkv", ".webm", ".f4v"];
    public List<string> ImageExtensions { get; set; } = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif"];
    public List<string> GalleryExtensions { get; set; } = [".zip", ".cbz"];
    public List<string> ExcludePatterns { get; set; } = [];
    public List<string> ExcludeImagePatterns { get; set; } = [];
    public List<string> ExcludeGalleryPatterns { get; set; } = [];
    public bool CreateGalleriesFromFolders { get; set; }
    public bool WriteImageThumbnails { get; set; } = true;
    public bool CreateImageClipsFromVideos { get; set; }
    public string GalleryCoverRegex { get; set; } = "(poster|cover|folder|board)\\.[^\\.]+$";
    public bool DeleteGeneratedDefault { get; set; } = true;
    // Transcoding
    public int MaxTranscodeSize { get; set; } // 0 = original
    public int MaxStreamingTranscodeSize { get; set; } // 0 = original
    public string TranscodeHardwareAcceleration { get; set; } = "none"; // none, nvenc, vaapi, qsv
    public string? TranscodeInputArgs { get; set; }
    public string? TranscodeOutputArgs { get; set; }
    public string? LiveTranscodeInputArgs { get; set; }
    public string? LiveTranscodeOutputArgs { get; set; }
    public bool DrawFunscriptHeatmapRange { get; set; }
    // Preview generation
    public string PreviewPreset { get; set; } = "slow"; // ultrafast, veryfast, fast, medium, slow, slower, veryslow
    public string PreviewAudio { get; set; } = "false"; // true, false
    // Logging
    public string LogLevel { get; set; } = "Info"; // Trace, Debug, Info, Warning, Error
    public string? LogFile { get; set; }
    public bool LogOut { get; set; } = true;
    public bool LogAccess { get; set; } = true;
    public InterfaceConfig Interface { get; set; } = new();
    public UiConfig Ui { get; set; } = new();
    public ScrapingConfig Scraping { get; set; } = new();
    public AuthConfig Auth { get; set; } = new();
    public PostgresConfig Postgres { get; set; } = new();
    public List<string> ExtensionPaths { get; set; } = [CoveDefaultPaths.GetDataSubdirectory("plugins")];
    public Dictionary<string, Dictionary<string, object?>> PluginConfigurations { get; set; } = [];
    public HashSet<string> DisabledPlugins { get; set; } = [];
}

public class CovePath
{
    public string Path { get; set; } = string.Empty;
    public bool ExcludeVideo { get; set; }
    public bool ExcludeImage { get; set; }
}

public class AuthConfig
{
    public bool Enabled { get; set; }
    public string? Username { get; set; }
    public string? HashedPassword { get; set; } // bcrypt hash
    public string? ApiKey { get; set; }
    public string JwtSecret { get; set; } = Guid.NewGuid().ToString();
    public int MaxSessionAgeMinutes { get; set; } = 60;
}

public class PostgresConfig
{
    public bool Managed { get; set; } = true; // If true, app manages its own PG instance
    public string? DataPath { get; set; } // Where to store PG data when managed
    public int Port { get; set; } = 5433; // Use non-default port to avoid conflicts
    public string Database { get; set; } = "cove";
    public string? ConnectionString { get; set; } // Override: use external PG
}

public class InterfaceConfig
{
    public static readonly string[] DefaultMenuItems =
    [
        "scenes",
        "images",
        "performers",
        "galleries",
        "studios",
        "tags",
        "groups"
    ];

    public string? Language { get; set; } = "en-US";
    public List<string> MenuItems { get; set; } = [.. DefaultMenuItems];
    public bool HandyConnectionEnabled { get; set; }
    public string? HandyKey { get; set; }
    public int? DefaultDurationForImages { get; set; }
    public bool DisableDropdownCreatePerformer { get; set; }
    public bool DisableDropdownCreateStudio { get; set; }
    public bool DisableDropdownCreateTag { get; set; }
}

public class UiConfig
{
    public string? Title { get; set; }
    public bool AbbreviateCounters { get; set; }
    public RatingSystemOptions RatingSystemOptions { get; set; } = new();
    public bool ShowStudioAsText { get; set; }
    public string? CustomCss { get; set; }
    public string? CustomJs { get; set; }
    public bool EnableCSSCustomization { get; set; }
    public bool EnableJSCustomization { get; set; }
    public string? CustomLocalesPath { get; set; }
    // Scene Player
    public bool AutostartVideo { get; set; } = true;
    public bool AutostartVideoOnPlaySelected { get; set; } = true;
    public bool ContinuePlaylistDefault { get; set; }
    public bool ShowAbLoopControls { get; set; } = true;
    public bool TrackActivity { get; set; } = true;
    // Preview
    public bool SoundOnPreview { get; set; }
    public double PreviewSegmentDuration { get; set; } = 0.75;
    public int PreviewSegments { get; set; } = 12;
    public string PreviewExcludeStart { get; set; } = "0";
    public string PreviewExcludeEnd { get; set; } = "0";
    // Wall
    public bool WallShowTitle { get; set; } = true;
    public int WallPlayback { get; set; } = 1; // 0=Audio, 1=Silent
    // Lightbox
    public bool DeleteFileDefault { get; set; }
    public int SlideshowDelay { get; set; } = 5000;
    // Scene list
    public bool NoBrowser { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
}

public enum RatingSystemType
{
    Stars,
    Decimal,
}

public enum RatingStarPrecision
{
    Full,
    Half,
    Quarter,
    Tenth,
}

public class RatingSystemOptions
{
    public RatingSystemType Type { get; set; } = RatingSystemType.Stars;
    public RatingStarPrecision StarPrecision { get; set; } = RatingStarPrecision.Full;
}

public class ScrapingConfig
{
    public List<string> ScraperDirectories { get; set; } = [CoveDefaultPaths.GetDataSubdirectory("scrapers")];
    public List<PackageSource> ScraperPackageSources { get; set; } = [];
    public List<MetadataServerInstance> MetadataServers { get; set; } = [];
}

public class PackageSource
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
}

public class MetadataServerInstance
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int MaxRequestsPerMinute { get; set; } = 240;
}

public static class CoveDefaultPaths
{
    public static string GetDataSubdirectory(string name)
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cove",
            name
        );
    }
}
