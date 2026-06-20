namespace Cove.Core.Interfaces;

public class CoveConfiguration
{
    public List<CovePath> CovePaths { get; set; } = [];
    public string DatabaseConnectionString { get; set; } = string.Empty;
    public string GeneratedPath { get; set; } = CoveDefaultPaths.GetDataSubdirectory("generated");
    public string CachePath { get; set; } = CoveDefaultPaths.GetDataSubdirectory("cache");
    // Where database/config backups are written. Empty => DataRoot/backups (back-compat). In Docker
    // this is pointed at the dedicated /backups bind mount so backups survive `docker compose down -v`.
    public string BackupPath { get; set; } = string.Empty;
    public string? FfmpegPath { get; set; }
    public string? FfprobePath { get; set; }
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 5073;
    // Default for a fresh install: leave ~3 logical processors free for the UI and the
    // rest of the system, and use the rest. On a typical 8-core/16-thread machine that
    // is 13; floored at 1 so low-core machines still work. Users can override this.
    public int MaxParallelTasks { get; set; } = System.Math.Max(1, System.Environment.ProcessorCount - 3);
    public int MaxConcurrentDownloads { get; set; } = 3;
    public List<DownloaderPathOverride> DownloaderPathOverrides { get; set; } = [];
    public bool CalculateMd5 { get; set; }
    // Frame-extraction engine for thumbnails, sprites, and pHash. "external" spawns the ffmpeg CLI
    // (most compatible, crash-isolated). "managed" decodes in-process via FFmpeg.AutoGen — much
    // faster, but a malformed file can crash the process on some systems, so it is opt-in.
    public string FrameExtractionMode { get; set; } = "external";
    public List<string> VideoExtensions { get; set; } = [".m4v", ".mp4", ".mov", ".wmv", ".avi", ".mpg", ".mpeg", ".rmvb", ".rm", ".flv", ".asf", ".mkv", ".webm", ".f4v"];
    public List<string> ImageExtensions { get; set; } = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif"];
    public List<string> GalleryExtensions { get; set; } = [".zip", ".cbz"];
    public List<string> AudioExtensions { get; set; } = [".mp3", ".m4a", ".m4b", ".flac", ".wav", ".ogg", ".oga", ".opus", ".aac", ".alac", ".aif", ".aiff", ".wma", ".mka", ".weba", ".amr"];
    public List<string> TextExtensions { get; set; } = [".txt", ".md", ".markdown", ".pdf", ".epub", ".rtf", ".nfo", ".log", ".srt", ".vtt", ".ass", ".ssa", ".lrc", ".html", ".htm"];
    public List<string> ExcludePatterns { get; set; } = [];
    public List<string> ExcludeImagePatterns { get; set; } = [];
    public List<string> ExcludeGalleryPatterns { get; set; } = [];
    public bool CreateGalleriesFromFolders { get; set; }
    public bool WriteImageThumbnails { get; set; } = true;
    public bool CreateImageClipsFromVideos { get; set; }
    public string GalleryCoverRegex { get; set; } = "(poster|cover|folder|board)\\.[^\\.]+$";
    public bool DeleteGeneratedDefault { get; set; } = true;
    // Transcoding / hardware acceleration
    public int MaxStreamingTranscodeSize { get; set; } // 0 = original
    // Unified hardware-acceleration policy. "off" = CPU only; "auto" = detect and use the best verified
    // accelerator, always falling back to CPU; or a specific accelerator: "nvenc", "qsv", "vaapi", "amf",
    // "videotoolbox". Drives BOTH the video encoder (live transcode + preview generation) AND in-process
    // ("managed") hardware decode. Replaces the old EnableFfmpegHwAccel + TranscodeHardwareAcceleration split.
    public string HardwareAcceleration { get; set; } = "auto";
    // Max concurrent hardware-encode sessions. Consumer NVENC GPUs cap simultaneous encode sessions;
    // 0 = use the built-in safe default. Software (libx264) encodes are never throttled.
    public int HardwareEncodeSessionLimit { get; set; }
    // Optional raw ffmpeg overrides for power users. FfmpegInputArgs = decode/input side (e.g. a specific
    // -hwaccel); FfmpegOutputArgs = encode side for live transcode. Empty => Cove builds them automatically
    // from HardwareAcceleration. Replaces the old Transcode/LiveTranscode Input/Output args quartet.
    public string? FfmpegInputArgs { get; set; }
    public string? FfmpegOutputArgs { get; set; }
    // Preview generation
    public string PreviewPreset { get; set; } = "slow"; // ultrafast, veryfast, fast, medium, slow, slower, veryslow
    public string PreviewAudio { get; set; } = "false"; // true, false
    // Logging
    public string LogLevel { get; set; } = "Info"; // Trace, Debug, Info, Warning, Error
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
    public bool ExcludeAudio { get; set; }
    public bool ExcludeText { get; set; }
}

public class DownloaderPathOverride
{
    public string DownloaderId { get; set; } = string.Empty;
    public string? Site { get; set; }
    public string Path { get; set; } = string.Empty;
}

public class AuthConfig
{
    public bool Enabled { get; set; }
    public string? Username { get; set; }
    public string? HashedPassword { get; set; } // bcrypt hash
    public string? ApiKey { get; set; }
    public string JwtSecret { get; set; } = GenerateJwtSecret();
    public int AccessTokenMinutes { get; set; } = 15;
    public int RefreshTokenDays { get; set; } = 30;
    public bool AllowAnonymousShareLinks { get; set; } = true;
    public List<string> KnownProxies { get; set; } = [];
    /// <summary>
    /// Hostnames that are considered trusted even when auth is disabled. When the
    /// request's Host (or X-Forwarded-Host) matches one of these, the auth-disabled
    /// failsafe is not tripped, regardless of the client IP. This lets an operator
    /// intentionally run Cove with authentication disabled behind a trusted reverse
    /// proxy on a custom public hostname. Supports exact hostnames, a leading
    /// wildcard ("*.example.com" matches subdomains), or "*" to trust any host.
    /// </summary>
    public List<string> TrustedHosts { get; set; } = [];
    /// <summary>
    /// When true, every controller action MUST declare a permission policy via
    /// [RequiresPermission], [AllowWithoutPermission], or [AllowAnonymous]; actions
    /// without any are rejected with 403.
    /// </summary>
    public bool EnforceDefaultDeny { get; set; } = true;

    private static string GenerateJwtSecret()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
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
    public static readonly string[] LegacyDefaultMenuItems =
    [
        "videos",
        "images",
        "performers",
        "galleries",
        "studios",
        "tags",
        "groups",
        "audios",
        "texts"
    ];

    public static readonly string[] SegmentsDefaultMenuItems =
    [
        "videos",
        "segments",
        "images",
        "performers",
        "galleries",
        "studios",
        "tags",
        "groups"
    ];

    public static readonly string[] FacesDefaultMenuItems =
    [
        "videos",
        "segments",
        "images",
        "faces",
        "performers",
        "galleries",
        "studios",
        "tags",
        "groups"
    ];

    public static readonly string[] DefaultMenuItems =
    [
        "videos",
        "images",
        "audios",
        "texts",
        "galleries",
        "segments",
        "performers",
        "tags",
        "groups",
        "studios"
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
    public string? FaviconPath { get; set; }
    /// <summary>Path or uploaded asset used as the app logo in the top-left of the navbar. Null = built-in Cove logo.</summary>
    public string? LogoPath { get; set; }
    public bool TroubleshootingModeEnabled { get; set; }
    public bool AbbreviateCounters { get; set; }
    public RatingSystemOptions RatingSystemOptions { get; set; } = new();
    public bool ShowStudioAsText { get; set; }
    public string? CustomCss { get; set; }
    public string? CustomJs { get; set; }
    public bool EnableCSSCustomization { get; set; }
    public bool EnableJSCustomization { get; set; }
    public string? CustomLocalesPath { get; set; }
    // Video Player
    public bool AutostartVideo { get; set; } = true;
    public bool AutostartVideoOnPlaySelected { get; set; } = true;
    public bool AutoplayOnListClick { get; set; }
    public int MaxLoopDuration { get; set; }
    public bool AlwaysResumeOnPlayback { get; set; } = true;
    public double PlayerVideoStartPercent { get; set; }
    public double PlayerVideoStartMinDuration { get; set; }
    public bool ContinuePlaylistDefault { get; set; }
    public bool ShowAbLoopControls { get; set; } = true;
    // Preview
    public bool SoundOnPreview { get; set; }
    public double PreviewSegmentDuration { get; set; } = 0.75;
    public int PreviewSegments { get; set; } = 12;
    public string PreviewExcludeStart { get; set; } = "0";
    public string PreviewExcludeEnd { get; set; } = "0";
    // Wall
    public bool WallShowTitle { get; set; } = true;
    public int WallPlayback { get; set; } = 1; // 0=Audio, 1=Silent
    public string WallPreviewType { get; set; } = "video";
    public string ImageObjectFit { get; set; } = "cover";
    public string VideoObjectFit { get; set; } = "cover";
    public string FeedVideoSource { get; set; } = "preview";
    public bool FeedVideoSound { get; set; }
    public double FeedVideoStartPercent { get; set; }
    public double FeedVideoStartMinDuration { get; set; }
    // Lightbox
    public bool DeleteFileDefault { get; set; }
    public int SlideshowDelay { get; set; } = 5000;
    // Video list
    public bool NoBrowser { get; set; }
    public bool NotificationsEnabled { get; set; } = true;
    public Dictionary<string, string> KeybindingOverrides { get; set; } = [];
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
    public List<MetadataServerInstance> MetadataServers { get; set; } = [];
    public List<ScraperPreference> ScraperPreferences { get; set; } = [];
    public IdentifyDefaultsConfig IdentifyDefaults { get; set; } = new();
    public ScrapeApplyDefaultsConfig ScrapeApplyDefaults { get; set; } = new();
    public MetadataBatchDefaultsConfig MetadataBatchDefaults { get; set; } = new();
}

public class ScraperPreference
{
    public string EntityType { get; set; } = string.Empty;
    public string Site { get; set; } = string.Empty;
    public string ScraperId { get; set; } = string.Empty;
}

public class IdentifyDefaultsConfig
{
    public bool CreateTags { get; set; } = true;
    public bool CreatePerformers { get; set; } = true;
    public bool CreateStudios { get; set; } = true;
    // Minimum number of matching fingerprint submissions (oshash, md5, or phash incl. close
    // phash) required to auto-apply a match. Primary confidence signal; works for servers
    // without phashes. Blank disables this requirement.
    public int? AutoApplyMinFingerprintMatches { get; set; } = 4;
    // Secondary guard: only rejects when both local and remote durations are known and
    // disagree by more than this. A missing duration never blocks a fingerprint match.
    public int? AutoApplyMaxDurationDifferenceSeconds { get; set; } = 5;
    // Optional phash tightness guard: only applies when a phash distance is computable.
    public int? AutoApplyMaxPhashDistance { get; set; }
}

public class ScrapeApplyDefaultsConfig
{
    public bool CreateMissingTags { get; set; }
    public bool CreateMissingPerformers { get; set; }
    public bool CreateMissingStudio { get; set; }
    public bool MarkOrganized { get; set; }
    public bool HydratePerformers { get; set; }
}

public class MetadataBatchDefaultsConfig
{
    public bool RefreshAlreadyTagged { get; set; }
    public bool CreateParentStudios { get; set; } = true;
    public List<string> ExcludeFields { get; set; } = [];
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
    public const string DataRootEnvironmentVariable = "COVE_HOME";

    public static string GetDataRoot()
    {
        var configured = Environment.GetEnvironmentVariable(DataRootEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(configured.Trim()));

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cove"
        );
    }

    public static string GetDataSubdirectory(string name)
    {
        return Path.Combine(GetDataRoot(), name);
    }

    public static string ResolveDataPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return GetDataRoot();

        var expanded = Environment.ExpandEnvironmentVariables(path.Trim());
        return Path.IsPathRooted(expanded)
            ? Path.GetFullPath(expanded)
            : Path.GetFullPath(Path.Combine(GetDataRoot(), expanded));
    }
}

