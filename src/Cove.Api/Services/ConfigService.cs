using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// Persists user-editable configuration to a JSON file alongside the app.
/// The config file is loaded at startup and merged with appsettings.json,
/// with the user config taking precedence.
/// </summary>
public class ConfigService
{
    private readonly CoveConfiguration _config;
    private readonly ILogger<ConfigService> _logger;
    private readonly string _configPath;
    private readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public ConfigService(CoveConfiguration config, ILogger<ConfigService> logger)
    {
        _config = config;
        _logger = logger;

        var coveDir = CoveDefaultPaths.GetDataRoot();
        Directory.CreateDirectory(coveDir);
        _configPath = Path.Combine(coveDir, "cove-config.json");
    }

    public string ConfigPath => _configPath;

    /// <summary>Get the current effective configuration as a DTO.</summary>
    public CoveConfigDto GetConfig()
    {
        var cfg = _config;
        return new CoveConfigDto
        {
            CovePaths = cfg.CovePaths.Select(p => new CovePathDto
            {
                Path = p.Path,
                ExcludeVideo = p.ExcludeVideo,
                ExcludeImage = p.ExcludeImage,
                ExcludeAudio = p.ExcludeAudio,
                ExcludeText = p.ExcludeText,
            }).ToList(),
            GeneratedPath = cfg.GeneratedPath,
            CachePath = cfg.CachePath,
            Host = cfg.Host,
            Port = cfg.Port,
            MaxParallelTasks = cfg.MaxParallelTasks,
            MaxConcurrentDownloads = cfg.MaxConcurrentDownloads,
            DownloaderPathOverrides = cfg.DownloaderPathOverrides
                .Select(overridePath => new DownloaderPathOverrideDto
                {
                    DownloaderId = overridePath.DownloaderId,
                    Site = overridePath.Site,
                    Path = overridePath.Path,
                })
                .ToList(),
            CalculateMd5 = cfg.CalculateMd5,
            EnableFfmpegHwAccel = cfg.EnableFfmpegHwAccel,
            FrameExtractionMode = cfg.FrameExtractionMode,
            FfmpegPath = cfg.FfmpegPath,
            FfprobePath = cfg.FfprobePath,
            MaxTranscodeSize = cfg.MaxTranscodeSize,
            MaxStreamingTranscodeSize = cfg.MaxStreamingTranscodeSize,
            TranscodeHardwareAcceleration = cfg.TranscodeHardwareAcceleration,
            TranscodeInputArgs = cfg.TranscodeInputArgs,
            TranscodeOutputArgs = cfg.TranscodeOutputArgs,
            LiveTranscodeInputArgs = cfg.LiveTranscodeInputArgs,
            LiveTranscodeOutputArgs = cfg.LiveTranscodeOutputArgs,
            PreviewPreset = cfg.PreviewPreset,
            PreviewAudio = cfg.PreviewAudio,
            VideoExtensions = cfg.VideoExtensions,
            ImageExtensions = cfg.ImageExtensions,
            GalleryExtensions = cfg.GalleryExtensions,
            AudioExtensions = cfg.AudioExtensions,
            TextExtensions = cfg.TextExtensions,
            ExcludePatterns = cfg.ExcludePatterns,
            ExcludeImagePatterns = cfg.ExcludeImagePatterns,
            ExcludeGalleryPatterns = cfg.ExcludeGalleryPatterns,
            CreateGalleriesFromFolders = cfg.CreateGalleriesFromFolders,
            WriteImageThumbnails = cfg.WriteImageThumbnails,
            CreateImageClipsFromVideos = cfg.CreateImageClipsFromVideos,
            GalleryCoverRegex = cfg.GalleryCoverRegex,
            DeleteGeneratedDefault = cfg.DeleteGeneratedDefault,
            LogLevel = cfg.LogLevel,
            Interface = new InterfaceConfigDto
            {
                Language = cfg.Interface.Language,
                MenuItems = NormalizeMenuItems(cfg.Interface.MenuItems),
                HandyConnectionEnabled = cfg.Interface.HandyConnectionEnabled,
                HandyKey = cfg.Interface.HandyKey,
                DefaultDurationForImages = cfg.Interface.DefaultDurationForImages,
                DisableDropdownCreatePerformer = cfg.Interface.DisableDropdownCreatePerformer,
                DisableDropdownCreateStudio = cfg.Interface.DisableDropdownCreateStudio,
                DisableDropdownCreateTag = cfg.Interface.DisableDropdownCreateTag,
            },
            Ui = new UiConfigDto
            {
                Title = cfg.Ui.Title,
                FaviconPath = cfg.Ui.FaviconPath,
                TroubleshootingModeEnabled = cfg.Ui.TroubleshootingModeEnabled,
                AbbreviateCounters = cfg.Ui.AbbreviateCounters,
                RatingSystemOptions = new RatingSystemOptionsDto
                {
                    Type = cfg.Ui.RatingSystemOptions.Type,
                    StarPrecision = cfg.Ui.RatingSystemOptions.StarPrecision,
                },
                ShowStudioAsText = cfg.Ui.ShowStudioAsText,
                CustomCss = cfg.Ui.CustomCss,
                CustomJs = cfg.Ui.CustomJs,
                EnableCSSCustomization = cfg.Ui.EnableCSSCustomization,
                EnableJSCustomization = cfg.Ui.EnableJSCustomization,
                CustomLocalesPath = cfg.Ui.CustomLocalesPath,
                AutostartVideo = cfg.Ui.AutostartVideo,
                AutostartVideoOnPlaySelected = cfg.Ui.AutostartVideoOnPlaySelected,
                AutoplayOnListClick = cfg.Ui.AutoplayOnListClick,
                MaxLoopDuration = cfg.Ui.MaxLoopDuration,
                AlwaysResumeOnPlayback = cfg.Ui.AlwaysResumeOnPlayback,
                PlayerVideoStartPercent = cfg.Ui.PlayerVideoStartPercent,
                PlayerVideoStartMinDuration = cfg.Ui.PlayerVideoStartMinDuration,
                ContinuePlaylistDefault = cfg.Ui.ContinuePlaylistDefault,
                ShowAbLoopControls = cfg.Ui.ShowAbLoopControls,
                SoundOnPreview = cfg.Ui.SoundOnPreview,
                PreviewSegmentDuration = cfg.Ui.PreviewSegmentDuration,
                PreviewSegments = cfg.Ui.PreviewSegments,
                PreviewExcludeStart = cfg.Ui.PreviewExcludeStart,
                PreviewExcludeEnd = cfg.Ui.PreviewExcludeEnd,
                WallShowTitle = cfg.Ui.WallShowTitle,
                WallPlayback = cfg.Ui.WallPlayback,
                WallPreviewType = cfg.Ui.WallPreviewType,
                ImageObjectFit = NormalizeObjectFit(cfg.Ui.ImageObjectFit),
                VideoObjectFit = NormalizeObjectFit(cfg.Ui.VideoObjectFit),
                FeedVideoSource = cfg.Ui.FeedVideoSource,
                FeedVideoSound = cfg.Ui.FeedVideoSound,
                FeedVideoStartPercent = cfg.Ui.FeedVideoStartPercent,
                FeedVideoStartMinDuration = cfg.Ui.FeedVideoStartMinDuration,
                DeleteFileDefault = cfg.Ui.DeleteFileDefault,
                SlideshowDelay = cfg.Ui.SlideshowDelay,
                NoBrowser = cfg.Ui.NoBrowser,
                NotificationsEnabled = cfg.Ui.NotificationsEnabled,
                KeybindingOverrides = new Dictionary<string, string>(cfg.Ui.KeybindingOverrides, StringComparer.OrdinalIgnoreCase),
            },
            Security = new SecurityConfigDto
            {
                Enabled = cfg.Auth.Enabled,
                Username = cfg.Auth.Username,
                AllowAnonymousShareLinks = cfg.Auth.AllowAnonymousShareLinks,
                EnforceDefaultDeny = cfg.Auth.EnforceDefaultDeny,
                KnownProxies = cfg.Auth.KnownProxies,
                TrustedHosts = cfg.Auth.TrustedHosts,
            },
            Scraping = new ScrapingConfigDto
            {
                ScraperDirectories = cfg.Scraping.ScraperDirectories,
                MetadataServers = cfg.Scraping.MetadataServers
                    .Select(box => new MetadataServerDto
                    {
                        Endpoint = box.Endpoint,
                        ApiKey = box.ApiKey,
                        Name = box.Name,
                        MaxRequestsPerMinute = box.MaxRequestsPerMinute,
                    })
                    .ToList(),
                ScraperPreferences = cfg.Scraping.ScraperPreferences
                    .Select(preference => new ScraperPreferenceDto
                    {
                        EntityType = preference.EntityType,
                        Site = preference.Site,
                        ScraperId = preference.ScraperId,
                    })
                    .ToList(),
                IdentifyDefaults = new IdentifyDefaultsConfigDto
                {
                    CreateTags = cfg.Scraping.IdentifyDefaults.CreateTags,
                    CreatePerformers = cfg.Scraping.IdentifyDefaults.CreatePerformers,
                    CreateStudios = cfg.Scraping.IdentifyDefaults.CreateStudios,
                    AutoApplyMaxDurationDifferenceSeconds = cfg.Scraping.IdentifyDefaults.AutoApplyMaxDurationDifferenceSeconds,
                    AutoApplyMaxPhashDistance = cfg.Scraping.IdentifyDefaults.AutoApplyMaxPhashDistance,
                },
                ScrapeApplyDefaults = new ScrapeApplyDefaultsConfigDto
                {
                    CreateMissingTags = cfg.Scraping.ScrapeApplyDefaults.CreateMissingTags,
                    CreateMissingPerformers = cfg.Scraping.ScrapeApplyDefaults.CreateMissingPerformers,
                    CreateMissingStudio = cfg.Scraping.ScrapeApplyDefaults.CreateMissingStudio,
                    MarkOrganized = cfg.Scraping.ScrapeApplyDefaults.MarkOrganized,
                    HydratePerformers = cfg.Scraping.ScrapeApplyDefaults.HydratePerformers,
                },
                MetadataBatchDefaults = new MetadataBatchDefaultsConfigDto
                {
                    RefreshAlreadyTagged = cfg.Scraping.MetadataBatchDefaults.RefreshAlreadyTagged,
                    CreateParentStudios = cfg.Scraping.MetadataBatchDefaults.CreateParentStudios,
                    ExcludeFields = [.. cfg.Scraping.MetadataBatchDefaults.ExcludeFields],
                },
            },
            PluginConfigurations = cfg.PluginConfigurations,
            DisabledPlugins = [.. cfg.DisabledPlugins],
        };
    }

    /// <summary>
    /// Save a config DTO to disk and update the live IOptions.
    /// </summary>
    public async Task SaveConfigAsync(CoveConfigDto dto)
    {
        await _lock.WaitAsync();
        try
        {
            // Apply to live options immediately
            ApplyToLive(dto);

            // Persist the effective config shape after sensitive fields are normalized.
            var json = JsonSerializer.Serialize(GetConfig(), _jsonOpts);
            await File.WriteAllTextAsync(_configPath, json);
            _logger.LogInformation("Configuration saved to {Path}", _configPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveCurrentConfigAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var json = JsonSerializer.Serialize(GetConfig(), _jsonOpts);
            await File.WriteAllTextAsync(_configPath, json);
            _logger.LogInformation("Configuration saved to {Path}", _configPath);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Load saved config from disk (called at startup).
    /// Returns null if no saved config exists.
    /// </summary>
    public async Task<CoveConfigDto?> LoadSavedConfigAsync()
    {
        if (!File.Exists(_configPath))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<CoveConfigDto>(json, _jsonOpts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load saved config from {Path}", _configPath);
            return null;
        }
    }

    /// <summary>Apply DTO values to the live CoveConfiguration singleton.</summary>
    private void ApplyToLive(CoveConfigDto dto)
    {
        var cfg = _config;
        cfg.CovePaths = dto.CovePaths.Select(p => new CovePath
        {
            Path = p.Path,
            ExcludeVideo = p.ExcludeVideo,
            ExcludeImage = p.ExcludeImage,
            ExcludeAudio = p.ExcludeAudio,
            ExcludeText = p.ExcludeText,
        }).ToList();

        if (!string.IsNullOrEmpty(dto.GeneratedPath))
            cfg.GeneratedPath = CoveDefaultPaths.ResolveDataPath(dto.GeneratedPath);
        if (!string.IsNullOrEmpty(dto.CachePath))
            cfg.CachePath = CoveDefaultPaths.ResolveDataPath(dto.CachePath);
        if (dto.Host != null)
            cfg.Host = dto.Host;
        cfg.Port = dto.Port;
        cfg.MaxParallelTasks = dto.MaxParallelTasks;
        cfg.MaxConcurrentDownloads = dto.MaxConcurrentDownloads > 0 ? dto.MaxConcurrentDownloads : cfg.MaxConcurrentDownloads;
        cfg.DownloaderPathOverrides = dto.DownloaderPathOverrides
            .Where(overridePath => !string.IsNullOrWhiteSpace(overridePath.DownloaderId) && !string.IsNullOrWhiteSpace(overridePath.Path))
            .Select(overridePath => new DownloaderPathOverride
            {
                DownloaderId = overridePath.DownloaderId.Trim(),
                Site = string.IsNullOrWhiteSpace(overridePath.Site) ? null : overridePath.Site.Trim(),
                Path = overridePath.Path.Trim(),
            })
            .ToList();
        cfg.CalculateMd5 = dto.CalculateMd5;
        cfg.EnableFfmpegHwAccel = dto.EnableFfmpegHwAccel;
        cfg.FrameExtractionMode = string.Equals(dto.FrameExtractionMode, "managed", StringComparison.OrdinalIgnoreCase) ? "managed" : "external";
        cfg.FfmpegPath = string.IsNullOrWhiteSpace(dto.FfmpegPath) ? null : dto.FfmpegPath;
        cfg.FfprobePath = string.IsNullOrWhiteSpace(dto.FfprobePath) ? null : dto.FfprobePath;
        cfg.MaxTranscodeSize = dto.MaxTranscodeSize;
        cfg.MaxStreamingTranscodeSize = dto.MaxStreamingTranscodeSize;
        if (!string.IsNullOrWhiteSpace(dto.TranscodeHardwareAcceleration))
            cfg.TranscodeHardwareAcceleration = dto.TranscodeHardwareAcceleration;
        cfg.TranscodeInputArgs = string.IsNullOrWhiteSpace(dto.TranscodeInputArgs) ? null : dto.TranscodeInputArgs;
        cfg.TranscodeOutputArgs = string.IsNullOrWhiteSpace(dto.TranscodeOutputArgs) ? null : dto.TranscodeOutputArgs;
        cfg.LiveTranscodeInputArgs = string.IsNullOrWhiteSpace(dto.LiveTranscodeInputArgs) ? null : dto.LiveTranscodeInputArgs;
        cfg.LiveTranscodeOutputArgs = string.IsNullOrWhiteSpace(dto.LiveTranscodeOutputArgs) ? null : dto.LiveTranscodeOutputArgs;
        if (!string.IsNullOrWhiteSpace(dto.PreviewPreset))
            cfg.PreviewPreset = dto.PreviewPreset;
        if (!string.IsNullOrWhiteSpace(dto.PreviewAudio))
            cfg.PreviewAudio = dto.PreviewAudio;

        if (dto.VideoExtensions.Count > 0)
            cfg.VideoExtensions = dto.VideoExtensions;
        if (dto.ImageExtensions.Count > 0)
            cfg.ImageExtensions = dto.ImageExtensions;
        if (dto.GalleryExtensions.Count > 0)
            cfg.GalleryExtensions = dto.GalleryExtensions;
        if (dto.AudioExtensions.Count > 0)
            cfg.AudioExtensions = dto.AudioExtensions;
        if (dto.TextExtensions.Count > 0)
            cfg.TextExtensions = dto.TextExtensions;

        cfg.ExcludePatterns = dto.ExcludePatterns;
        cfg.ExcludeImagePatterns = dto.ExcludeImagePatterns;
        cfg.ExcludeGalleryPatterns = dto.ExcludeGalleryPatterns;
        cfg.CreateGalleriesFromFolders = dto.CreateGalleriesFromFolders;
        cfg.WriteImageThumbnails = dto.WriteImageThumbnails;
        cfg.CreateImageClipsFromVideos = dto.CreateImageClipsFromVideos;
        cfg.GalleryCoverRegex = string.IsNullOrWhiteSpace(dto.GalleryCoverRegex) ? cfg.GalleryCoverRegex : dto.GalleryCoverRegex.Trim();
        cfg.DeleteGeneratedDefault = dto.DeleteGeneratedDefault;
        if (!string.IsNullOrWhiteSpace(dto.LogLevel))
            cfg.LogLevel = dto.LogLevel.Trim();

        cfg.Interface.Language = dto.Interface.Language;
        cfg.Interface.MenuItems = NormalizeMenuItems(dto.Interface.MenuItems);
        cfg.Interface.HandyConnectionEnabled = dto.Interface.HandyConnectionEnabled;
        cfg.Interface.HandyKey = string.IsNullOrWhiteSpace(dto.Interface.HandyKey) ? null : dto.Interface.HandyKey.Trim();
        cfg.Interface.DefaultDurationForImages = dto.Interface.DefaultDurationForImages;
        cfg.Interface.DisableDropdownCreatePerformer = dto.Interface.DisableDropdownCreatePerformer;
        cfg.Interface.DisableDropdownCreateStudio = dto.Interface.DisableDropdownCreateStudio;
        cfg.Interface.DisableDropdownCreateTag = dto.Interface.DisableDropdownCreateTag;

        cfg.Ui.Title = string.IsNullOrWhiteSpace(dto.Ui.Title) ? null : dto.Ui.Title.Trim();
        cfg.Ui.FaviconPath = string.IsNullOrWhiteSpace(dto.Ui.FaviconPath) ? null : dto.Ui.FaviconPath.Trim();
        cfg.Ui.TroubleshootingModeEnabled = dto.Ui.TroubleshootingModeEnabled;
        cfg.Ui.AbbreviateCounters = dto.Ui.AbbreviateCounters;
        cfg.Ui.RatingSystemOptions = new RatingSystemOptions
        {
            Type = dto.Ui.RatingSystemOptions.Type,
            StarPrecision = dto.Ui.RatingSystemOptions.StarPrecision,
        };
        cfg.Ui.ShowStudioAsText = dto.Ui.ShowStudioAsText;
        cfg.Ui.CustomCss = dto.Ui.CustomCss;
        cfg.Ui.CustomJs = dto.Ui.CustomJs;
        cfg.Ui.EnableCSSCustomization = dto.Ui.EnableCSSCustomization;
        cfg.Ui.EnableJSCustomization = dto.Ui.EnableJSCustomization;
        cfg.Ui.CustomLocalesPath = string.IsNullOrWhiteSpace(dto.Ui.CustomLocalesPath) ? null : dto.Ui.CustomLocalesPath.Trim();
        cfg.Ui.AutostartVideo = dto.Ui.AutostartVideo;
        cfg.Ui.AutostartVideoOnPlaySelected = dto.Ui.AutostartVideoOnPlaySelected;
        cfg.Ui.AutoplayOnListClick = dto.Ui.AutoplayOnListClick;
        cfg.Ui.MaxLoopDuration = dto.Ui.MaxLoopDuration;
        cfg.Ui.AlwaysResumeOnPlayback = dto.Ui.AlwaysResumeOnPlayback;
        cfg.Ui.PlayerVideoStartPercent = Math.Clamp(dto.Ui.PlayerVideoStartPercent, 0, 95);
        cfg.Ui.PlayerVideoStartMinDuration = Math.Max(0, dto.Ui.PlayerVideoStartMinDuration);
        cfg.Ui.ContinuePlaylistDefault = dto.Ui.ContinuePlaylistDefault;
        cfg.Ui.ShowAbLoopControls = dto.Ui.ShowAbLoopControls;
        cfg.Ui.SoundOnPreview = dto.Ui.SoundOnPreview;
        cfg.Ui.PreviewSegmentDuration = dto.Ui.PreviewSegmentDuration;
        cfg.Ui.PreviewSegments = dto.Ui.PreviewSegments;
        cfg.Ui.PreviewExcludeStart = string.IsNullOrWhiteSpace(dto.Ui.PreviewExcludeStart) ? "0" : dto.Ui.PreviewExcludeStart.Trim();
        cfg.Ui.PreviewExcludeEnd = string.IsNullOrWhiteSpace(dto.Ui.PreviewExcludeEnd) ? "0" : dto.Ui.PreviewExcludeEnd.Trim();
        cfg.Ui.WallShowTitle = dto.Ui.WallShowTitle;
        cfg.Ui.WallPlayback = dto.Ui.WallPlayback;
        cfg.Ui.WallPreviewType = string.IsNullOrWhiteSpace(dto.Ui.WallPreviewType) ? "video" : dto.Ui.WallPreviewType.Trim();
        cfg.Ui.ImageObjectFit = NormalizeObjectFit(dto.Ui.ImageObjectFit);
        cfg.Ui.VideoObjectFit = NormalizeObjectFit(dto.Ui.VideoObjectFit);
        cfg.Ui.FeedVideoSource = string.Equals(dto.Ui.FeedVideoSource, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "preview";
        cfg.Ui.FeedVideoSound = dto.Ui.FeedVideoSound;
        cfg.Ui.FeedVideoStartPercent = Math.Clamp(dto.Ui.FeedVideoStartPercent, 0, 95);
        cfg.Ui.FeedVideoStartMinDuration = Math.Max(0, dto.Ui.FeedVideoStartMinDuration);
        cfg.Ui.DeleteFileDefault = dto.Ui.DeleteFileDefault;
        cfg.Ui.SlideshowDelay = dto.Ui.SlideshowDelay;
        cfg.Ui.NoBrowser = dto.Ui.NoBrowser;
        cfg.Ui.NotificationsEnabled = dto.Ui.NotificationsEnabled;
        cfg.Ui.KeybindingOverrides = dto.Ui.KeybindingOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);

        cfg.Auth.Enabled = dto.Security.Enabled;
        cfg.Auth.Username = string.IsNullOrWhiteSpace(dto.Security.Username) ? null : dto.Security.Username.Trim();
        cfg.Auth.AllowAnonymousShareLinks = dto.Security.AllowAnonymousShareLinks;
        cfg.Auth.EnforceDefaultDeny = dto.Security.EnforceDefaultDeny;
        cfg.Auth.KnownProxies = (dto.Security.KnownProxies ?? [])
            .Where(proxy => !string.IsNullOrWhiteSpace(proxy))
            .Select(proxy => proxy.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Auth.TrustedHosts = (dto.Security.TrustedHosts ?? [])
            .Where(host => !string.IsNullOrWhiteSpace(host))
            .Select(host => host.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!string.IsNullOrWhiteSpace(dto.Security.NewPassword))
            cfg.Auth.HashedPassword = BCrypt.Net.BCrypt.HashPassword(dto.Security.NewPassword);

        cfg.Scraping.ScraperDirectories = dto.Scraping.ScraperDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Scraping.MetadataServers = dto.Scraping.MetadataServers
            .Where(box => !string.IsNullOrWhiteSpace(box.Endpoint))
            .Select(box => new MetadataServerInstance
            {
                Endpoint = box.Endpoint.Trim(),
                ApiKey = box.ApiKey?.Trim() ?? string.Empty,
                Name = box.Name?.Trim() ?? string.Empty,
                MaxRequestsPerMinute = box.MaxRequestsPerMinute > 0 ? box.MaxRequestsPerMinute : 240,
            })
            .DistinctBy(box => box.Endpoint, StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Scraping.ScraperPreferences = dto.Scraping.ScraperPreferences
            .Where(preference => !string.IsNullOrWhiteSpace(preference.Site) && !string.IsNullOrWhiteSpace(preference.ScraperId))
            .Select(preference => new ScraperPreference
            {
                EntityType = preference.EntityType?.Trim().ToLowerInvariant() ?? string.Empty,
                Site = preference.Site.Trim().ToLowerInvariant(),
                ScraperId = preference.ScraperId.Trim(),
            })
            .DistinctBy(preference => $"{preference.EntityType}\u001f{preference.Site}", StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Scraping.IdentifyDefaults = new IdentifyDefaultsConfig
        {
            CreateTags = dto.Scraping.IdentifyDefaults.CreateTags,
            CreatePerformers = dto.Scraping.IdentifyDefaults.CreatePerformers,
            CreateStudios = dto.Scraping.IdentifyDefaults.CreateStudios,
            AutoApplyMaxDurationDifferenceSeconds = dto.Scraping.IdentifyDefaults.AutoApplyMaxDurationDifferenceSeconds,
            AutoApplyMaxPhashDistance = dto.Scraping.IdentifyDefaults.AutoApplyMaxPhashDistance,
        };
        cfg.Scraping.ScrapeApplyDefaults = new ScrapeApplyDefaultsConfig
        {
            CreateMissingTags = dto.Scraping.ScrapeApplyDefaults.CreateMissingTags,
            CreateMissingPerformers = dto.Scraping.ScrapeApplyDefaults.CreateMissingPerformers,
            CreateMissingStudio = dto.Scraping.ScrapeApplyDefaults.CreateMissingStudio,
            MarkOrganized = dto.Scraping.ScrapeApplyDefaults.MarkOrganized,
            HydratePerformers = dto.Scraping.ScrapeApplyDefaults.HydratePerformers,
        };
        cfg.Scraping.MetadataBatchDefaults = new MetadataBatchDefaultsConfig
        {
            RefreshAlreadyTagged = dto.Scraping.MetadataBatchDefaults.RefreshAlreadyTagged,
            CreateParentStudios = dto.Scraping.MetadataBatchDefaults.CreateParentStudios,
            ExcludeFields = dto.Scraping.MetadataBatchDefaults.ExcludeFields
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Select(field => field.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };

        cfg.PluginConfigurations = dto.PluginConfigurations ?? [];
        cfg.DisabledPlugins = dto.DisabledPlugins
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> NormalizeMenuItems(IEnumerable<string>? items)
    {
        var normalizedItems = items?
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        if (normalizedItems.Count == 0)
            return [.. InterfaceConfig.DefaultMenuItems];

        if (MatchesMenuItems(normalizedItems, InterfaceConfig.LegacyDefaultMenuItems)
            || MatchesMenuItems(normalizedItems, InterfaceConfig.SegmentsDefaultMenuItems)
            || MatchesMenuItems(normalizedItems, InterfaceConfig.FacesDefaultMenuItems))
            return [.. InterfaceConfig.DefaultMenuItems];

        return normalizedItems;
    }

    private static string NormalizeObjectFit(string? value)
        => string.Equals(value, "contain", StringComparison.OrdinalIgnoreCase) ? "contain" : "cover";

    private static bool MatchesMenuItems(IReadOnlyList<string> items, IReadOnlyList<string> expected)
    {
        if (items.Count != expected.Count)
            return false;

        for (var i = 0; i < expected.Count; i++)
        {
            if (!string.Equals(items[i], expected[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
