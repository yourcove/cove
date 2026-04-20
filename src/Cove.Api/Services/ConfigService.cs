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

        // Store config next to the working directory (not the binary output dir)
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var coveDir = Path.Combine(baseDir, "cove");
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
            }).ToList(),
            GeneratedPath = cfg.GeneratedPath,
            CachePath = cfg.CachePath,
            Host = cfg.Host,
            Port = cfg.Port,
            MaxParallelTasks = cfg.MaxParallelTasks,
            CalculateMd5 = cfg.CalculateMd5,
            VideoExtensions = cfg.VideoExtensions,
            ImageExtensions = cfg.ImageExtensions,
            GalleryExtensions = cfg.GalleryExtensions,
            ExcludePatterns = cfg.ExcludePatterns,
            ExcludeImagePatterns = cfg.ExcludeImagePatterns,
            ExcludeGalleryPatterns = cfg.ExcludeGalleryPatterns,
            CreateGalleriesFromFolders = cfg.CreateGalleriesFromFolders,
            WriteImageThumbnails = cfg.WriteImageThumbnails,
            CreateImageClipsFromVideos = cfg.CreateImageClipsFromVideos,
            GalleryCoverRegex = cfg.GalleryCoverRegex,
            DeleteGeneratedDefault = cfg.DeleteGeneratedDefault,
            Interface = new InterfaceConfigDto
            {
                Language = cfg.Interface.Language,
                MenuItems = cfg.Interface.MenuItems,
            },
            Ui = new UiConfigDto
            {
                Title = cfg.Ui.Title,
                AbbreviateCounters = cfg.Ui.AbbreviateCounters,
                RatingSystemOptions = new RatingSystemOptionsDto
                {
                    Type = cfg.Ui.RatingSystemOptions.Type,
                    StarPrecision = cfg.Ui.RatingSystemOptions.StarPrecision,
                },
                DeleteFileDefault = cfg.Ui.DeleteFileDefault,
            },
            Security = new SecurityConfigDto
            {
                Enabled = cfg.Auth.Enabled,
                Username = cfg.Auth.Username,
                MaxSessionAgeMinutes = cfg.Auth.MaxSessionAgeMinutes,
            },
            Scraping = new ScrapingConfigDto
            {
                ScraperDirectories = cfg.Scraping.ScraperDirectories,
                ScraperPackageSources = cfg.Scraping.ScraperPackageSources
                    .Select(source => new PackageSourceDto
                    {
                        Name = source.Name,
                        Url = source.Url,
                    })
                    .ToList(),
                MetadataServers = cfg.Scraping.MetadataServers
                    .Select(box => new MetadataServerDto
                    {
                        Endpoint = box.Endpoint,
                        ApiKey = box.ApiKey,
                        Name = box.Name,
                        MaxRequestsPerMinute = box.MaxRequestsPerMinute,
                    })
                    .ToList(),
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
        }).ToList();

        if (!string.IsNullOrEmpty(dto.GeneratedPath))
            cfg.GeneratedPath = dto.GeneratedPath;
        if (!string.IsNullOrEmpty(dto.CachePath))
            cfg.CachePath = dto.CachePath;
        if (dto.Host != null)
            cfg.Host = dto.Host;
        cfg.Port = dto.Port;
        cfg.MaxParallelTasks = dto.MaxParallelTasks;
        cfg.CalculateMd5 = dto.CalculateMd5;

        if (dto.VideoExtensions.Count > 0)
            cfg.VideoExtensions = dto.VideoExtensions;
        if (dto.ImageExtensions.Count > 0)
            cfg.ImageExtensions = dto.ImageExtensions;
        if (dto.GalleryExtensions.Count > 0)
            cfg.GalleryExtensions = dto.GalleryExtensions;

        cfg.ExcludePatterns = dto.ExcludePatterns;
        cfg.ExcludeImagePatterns = dto.ExcludeImagePatterns;
        cfg.ExcludeGalleryPatterns = dto.ExcludeGalleryPatterns;
        cfg.CreateGalleriesFromFolders = dto.CreateGalleriesFromFolders;
        cfg.WriteImageThumbnails = dto.WriteImageThumbnails;
        cfg.CreateImageClipsFromVideos = dto.CreateImageClipsFromVideos;
        cfg.GalleryCoverRegex = string.IsNullOrWhiteSpace(dto.GalleryCoverRegex) ? cfg.GalleryCoverRegex : dto.GalleryCoverRegex.Trim();
        cfg.DeleteGeneratedDefault = dto.DeleteGeneratedDefault;

        cfg.Interface.Language = dto.Interface.Language;
        var menuItems = dto.Interface.MenuItems
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Interface.MenuItems = menuItems.Count > 0 ? menuItems : [.. InterfaceConfig.DefaultMenuItems];

        cfg.Ui.Title = string.IsNullOrWhiteSpace(dto.Ui.Title) ? null : dto.Ui.Title.Trim();
        cfg.Ui.AbbreviateCounters = dto.Ui.AbbreviateCounters;
        cfg.Ui.RatingSystemOptions = new RatingSystemOptions
        {
            Type = dto.Ui.RatingSystemOptions.Type,
            StarPrecision = dto.Ui.RatingSystemOptions.StarPrecision,
        };
        cfg.Ui.DeleteFileDefault = dto.Ui.DeleteFileDefault;

        cfg.Auth.Enabled = dto.Security.Enabled;
        cfg.Auth.Username = string.IsNullOrWhiteSpace(dto.Security.Username) ? null : dto.Security.Username.Trim();
        cfg.Auth.MaxSessionAgeMinutes = dto.Security.MaxSessionAgeMinutes > 0
            ? dto.Security.MaxSessionAgeMinutes
            : cfg.Auth.MaxSessionAgeMinutes;
        if (!string.IsNullOrWhiteSpace(dto.Security.NewPassword))
            cfg.Auth.HashedPassword = BCrypt.Net.BCrypt.HashPassword(dto.Security.NewPassword);

        cfg.Scraping.ScraperDirectories = dto.Scraping.ScraperDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        cfg.Scraping.ScraperPackageSources = dto.Scraping.ScraperPackageSources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => new PackageSource
            {
                Name = source.Name?.Trim() ?? string.Empty,
                Url = source.Url.Trim(),
            })
            .DistinctBy(source => source.Url, StringComparer.OrdinalIgnoreCase)
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

        cfg.PluginConfigurations = dto.PluginConfigurations ?? [];
        cfg.DisabledPlugins = dto.DisabledPlugins
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
