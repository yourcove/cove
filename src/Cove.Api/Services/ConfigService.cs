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
        : this(config, logger, Path.Combine(CoveDefaultPaths.GetDataRoot(), "cove-config.json"))
    {
    }

    internal ConfigService(
        CoveConfiguration config,
        ILogger<ConfigService> logger,
        string configPath)
    {
        _config = config;
        _logger = logger;
        _configPath = configPath;
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)
            ?? throw new ArgumentException("The configuration path requires a parent directory.", nameof(configPath)));
    }

    public string ConfigPath => _configPath;

    private static readonly HashSet<string> ValidHardwareAccelerations = new(StringComparer.OrdinalIgnoreCase)
    { "off", "auto", "nvenc", "qsv", "vaapi", "amf", "videotoolbox" };

    private static string NormalizeHardwareAcceleration(string? value)
    {
        var v = value?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(v)) return "auto";
        // Back-compat: the old encoder setting used "none" to mean "auto-detect a hardware encoder".
        if (v == "none") return "auto";
        return ValidHardwareAccelerations.Contains(v) ? v : "auto";
    }

    /// <summary>Derive the unified HardwareAcceleration value from a legacy saved config. The old model
    /// pinned an encoder via TranscodeHardwareAcceleration ("none" meant auto-detect) and toggled in-process
    /// decode separately via EnableFfmpegHwAccel; the unified "auto" covers both (and always falls back to
    /// CPU), so a legacy pin maps straight across and everything else becomes "auto".</summary>
    private static string? MigrateLegacyHardwareAcceleration(CoveConfigDto dto)
    {
#pragma warning disable CS0618 // legacy fields read only for one-time migration
        return string.IsNullOrWhiteSpace(dto.TranscodeHardwareAcceleration) ? null : dto.TranscodeHardwareAcceleration;
#pragma warning restore CS0618
    }

    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }

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
            FrameExtractionMode = cfg.FrameExtractionMode,
            FfmpegPath = cfg.FfmpegPath,
            FfprobePath = cfg.FfprobePath,
            MaxStreamingTranscodeSize = cfg.MaxStreamingTranscodeSize,
            HardwareAcceleration = cfg.HardwareAcceleration,
            HardwareEncodeSessionLimit = cfg.HardwareEncodeSessionLimit,
            FfmpegInputArgs = cfg.FfmpegInputArgs,
            FfmpegOutputArgs = cfg.FfmpegOutputArgs,
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
            Ui = GetUiConfig(),
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
                    AutoApplyMinFingerprintMatches = cfg.Scraping.IdentifyDefaults.AutoApplyMinFingerprintMatches,
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
        await UpdateConfigAsync(_ => dto);
    }

    /// <summary>
    /// Atomically read, update, apply, and persist the effective configuration.
    /// </summary>
    public async Task<CoveConfigDto> UpdateConfigAsync(Func<CoveConfigDto, CoveConfigDto> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _lock.WaitAsync();
        try
        {
            var updated = update(GetConfig())
                ?? throw new InvalidOperationException("The configuration update returned null.");
            ApplyToLive(updated);

            await PersistCurrentConfigAsync();
            return GetConfig();
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Atomically update only the live UI configuration without replaying a stale snapshot of other sections.
    /// </summary>
    public async Task<UiConfigDto> UpdateUiConfigAsync(Func<UiConfigDto, UiConfigDto> update)
    {
        ArgumentNullException.ThrowIfNull(update);

        await _lock.WaitAsync();
        try
        {
            var updated = update(GetUiConfig())
                ?? throw new InvalidOperationException("The UI configuration update returned null.");
            ApplyUiToLive(updated);

            await PersistCurrentConfigAsync();
            return GetUiConfig();
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
            await PersistCurrentConfigAsync();
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task PersistCurrentConfigAsync()
    {
        var json = JsonSerializer.Serialize(GetConfig(), _jsonOpts);
        await File.WriteAllTextAsync(_configPath, json);
        _logger.LogInformation("Configuration saved to {Path}", _configPath);
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

    /// <summary>Apply DTO values to the live CoveConfiguration singleton. Internal for testing the
    /// legacy-config migration without touching disk.</summary>
    internal void ApplyToLive(CoveConfigDto dto)
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
        cfg.FrameExtractionMode = string.Equals(dto.FrameExtractionMode, "managed", StringComparison.OrdinalIgnoreCase) ? "managed" : "external";
        cfg.FfmpegPath = string.IsNullOrWhiteSpace(dto.FfmpegPath) ? null : dto.FfmpegPath;
        cfg.FfprobePath = string.IsNullOrWhiteSpace(dto.FfprobePath) ? null : dto.FfprobePath;
        cfg.MaxStreamingTranscodeSize = dto.MaxStreamingTranscodeSize;
        // Unified hardware acceleration. Use the new field when present; otherwise migrate the legacy
        // EnableFfmpegHwAccel + TranscodeHardwareAcceleration pair from an older saved config.
        cfg.HardwareAcceleration = NormalizeHardwareAcceleration(dto.HardwareAcceleration ?? MigrateLegacyHardwareAcceleration(dto));
        cfg.HardwareEncodeSessionLimit = Math.Max(0, dto.HardwareEncodeSessionLimit);
#pragma warning disable CS0618 // legacy fields read only for one-time migration
        cfg.FfmpegInputArgs = FirstNonBlank(dto.FfmpegInputArgs, dto.LiveTranscodeInputArgs, dto.TranscodeInputArgs);
        cfg.FfmpegOutputArgs = FirstNonBlank(dto.FfmpegOutputArgs, dto.LiveTranscodeOutputArgs, dto.TranscodeOutputArgs);
#pragma warning restore CS0618
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

        ApplyUiToLive(dto.Ui);

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
            AutoApplyMinFingerprintMatches = dto.Scraping.IdentifyDefaults.AutoApplyMinFingerprintMatches,
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

    private UiConfigDto GetUiConfig()
    {
        var ui = _config.Ui;
        return new UiConfigDto
        {
            Title = ui.Title,
            FaviconPath = ui.FaviconPath,
            LogoPath = ui.LogoPath,
            TroubleshootingModeEnabled = ui.TroubleshootingModeEnabled,
            AbbreviateCounters = ui.AbbreviateCounters,
            RatingSystemOptions = new RatingSystemOptionsDto
            {
                Type = ui.RatingSystemOptions.Type,
                StarPrecision = ui.RatingSystemOptions.StarPrecision,
            },
            ShowStudioAsText = ui.ShowStudioAsText,
            CustomCss = ui.CustomCss,
            CustomJs = ui.CustomJs,
            EnableCSSCustomization = ui.EnableCSSCustomization,
            EnableJSCustomization = ui.EnableJSCustomization,
            CustomLocalesPath = ui.CustomLocalesPath,
            AutostartVideo = ui.AutostartVideo,
            AutostartVideoOnPlaySelected = ui.AutostartVideoOnPlaySelected,
            AutoplayOnListClick = ui.AutoplayOnListClick,
            MaxLoopDuration = ui.MaxLoopDuration,
            AlwaysResumeOnPlayback = ui.AlwaysResumeOnPlayback,
            PlayerVideoStartPercent = ui.PlayerVideoStartPercent,
            PlayerVideoStartMinDuration = ui.PlayerVideoStartMinDuration,
            ContinuePlaylistDefault = ui.ContinuePlaylistDefault,
            ShowAbLoopControls = ui.ShowAbLoopControls,
            SoundOnPreview = ui.SoundOnPreview,
            PreviewSegmentDuration = ui.PreviewSegmentDuration,
            PreviewSegments = ui.PreviewSegments,
            PreviewExcludeStart = ui.PreviewExcludeStart,
            PreviewExcludeEnd = ui.PreviewExcludeEnd,
            WallShowTitle = ui.WallShowTitle,
            WallPlayback = ui.WallPlayback,
            WallPreviewType = ui.WallPreviewType,
            ImageObjectFit = NormalizeObjectFit(ui.ImageObjectFit),
            VideoObjectFit = NormalizeObjectFit(ui.VideoObjectFit),
            FeedVideoSource = ui.FeedVideoSource,
            FeedVideoSound = ui.FeedVideoSound,
            FeedVideoStartPercent = ui.FeedVideoStartPercent,
            FeedVideoStartMinDuration = ui.FeedVideoStartMinDuration,
            DeleteFileDefault = ui.DeleteFileDefault,
            SlideshowDelay = ui.SlideshowDelay,
            NoBrowser = ui.NoBrowser,
            NotificationsEnabled = ui.NotificationsEnabled,
            KeybindingOverrides = new Dictionary<string, string>(ui.KeybindingOverrides, StringComparer.OrdinalIgnoreCase),
        };
    }

    private void ApplyUiToLive(UiConfigDto dto)
    {
        var ui = _config.Ui;
        ui.Title = string.IsNullOrWhiteSpace(dto.Title) ? null : dto.Title.Trim();
        ui.FaviconPath = string.IsNullOrWhiteSpace(dto.FaviconPath) ? null : dto.FaviconPath.Trim();
        ui.LogoPath = string.IsNullOrWhiteSpace(dto.LogoPath) ? null : dto.LogoPath.Trim();
        ui.TroubleshootingModeEnabled = dto.TroubleshootingModeEnabled;
        ui.AbbreviateCounters = dto.AbbreviateCounters;
        ui.RatingSystemOptions = new RatingSystemOptions
        {
            Type = dto.RatingSystemOptions.Type,
            StarPrecision = dto.RatingSystemOptions.StarPrecision,
        };
        ui.ShowStudioAsText = dto.ShowStudioAsText;
        ui.CustomCss = dto.CustomCss;
        ui.CustomJs = dto.CustomJs;
        ui.EnableCSSCustomization = dto.EnableCSSCustomization;
        ui.EnableJSCustomization = dto.EnableJSCustomization;
        ui.CustomLocalesPath = string.IsNullOrWhiteSpace(dto.CustomLocalesPath) ? null : dto.CustomLocalesPath.Trim();
        ui.AutostartVideo = dto.AutostartVideo;
        ui.AutostartVideoOnPlaySelected = dto.AutostartVideoOnPlaySelected;
        ui.AutoplayOnListClick = dto.AutoplayOnListClick;
        ui.MaxLoopDuration = dto.MaxLoopDuration;
        ui.AlwaysResumeOnPlayback = dto.AlwaysResumeOnPlayback;
        ui.PlayerVideoStartPercent = Math.Clamp(dto.PlayerVideoStartPercent, 0, 95);
        ui.PlayerVideoStartMinDuration = Math.Max(0, dto.PlayerVideoStartMinDuration);
        ui.ContinuePlaylistDefault = dto.ContinuePlaylistDefault;
        ui.ShowAbLoopControls = dto.ShowAbLoopControls;
        ui.SoundOnPreview = dto.SoundOnPreview;
        ui.PreviewSegmentDuration = dto.PreviewSegmentDuration;
        ui.PreviewSegments = dto.PreviewSegments;
        ui.PreviewExcludeStart = string.IsNullOrWhiteSpace(dto.PreviewExcludeStart) ? "0" : dto.PreviewExcludeStart.Trim();
        ui.PreviewExcludeEnd = string.IsNullOrWhiteSpace(dto.PreviewExcludeEnd) ? "0" : dto.PreviewExcludeEnd.Trim();
        ui.WallShowTitle = dto.WallShowTitle;
        ui.WallPlayback = dto.WallPlayback;
        ui.WallPreviewType = string.IsNullOrWhiteSpace(dto.WallPreviewType) ? "video" : dto.WallPreviewType.Trim();
        ui.ImageObjectFit = NormalizeObjectFit(dto.ImageObjectFit);
        ui.VideoObjectFit = NormalizeObjectFit(dto.VideoObjectFit);
        ui.FeedVideoSource = string.Equals(dto.FeedVideoSource, "video", StringComparison.OrdinalIgnoreCase) ? "video" : "preview";
        ui.FeedVideoSound = dto.FeedVideoSound;
        ui.FeedVideoStartPercent = Math.Clamp(dto.FeedVideoStartPercent, 0, 95);
        ui.FeedVideoStartMinDuration = Math.Max(0, dto.FeedVideoStartMinDuration);
        ui.DeleteFileDefault = dto.DeleteFileDefault;
        ui.SlideshowDelay = dto.SlideshowDelay;
        ui.NoBrowser = dto.NoBrowser;
        ui.NotificationsEnabled = dto.NotificationsEnabled;
        ui.KeybindingOverrides = dto.KeybindingOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Key.Trim(), pair => pair.Value.Trim(), StringComparer.OrdinalIgnoreCase);
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
