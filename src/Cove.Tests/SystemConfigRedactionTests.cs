using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class SystemConfigRedactionTests
{
    [Fact]
    public void GetConfig_ForReadOnlyPrincipal_RedactsSensitiveValuesWithoutChangingConsumerShapeOrSource()
    {
        var source = CreateSensitiveConfiguration();
        var configService = new ConfigService(source, NullLogger<ConfigService>.Instance);
        var principals = new CurrentPrincipalAccessor();
        principals.Set(CreatePrincipal(Permissions.SystemRead));
        var controller = CreateController(configService, source, principals);

        var fullBefore = configService.GetConfig();
        var redacted = GetConfig(controller);

        var redactedMetadataServer = Assert.Single(redacted.Scraping.MetadataServers);
        Assert.True(string.IsNullOrEmpty(redactedMetadataServer.ApiKey));
        Assert.Equal("https://metadata.invalid", redactedMetadataServer.Endpoint);
        Assert.Equal("Safe metadata name", redactedMetadataServer.Name);
        Assert.Equal(123, redactedMetadataServer.MaxRequestsPerMinute);

        var redactedCovePath = Assert.Single(redacted.CovePaths);
        var usesOpaquePathSentinel = string.Equals(redactedCovePath.Path, "[redacted]", StringComparison.Ordinal);
        Assert.True(usesOpaquePathSentinel);
        Assert.True(redactedCovePath.ExcludeAudio);
        var redactedDownloaderPath = Assert.Single(redacted.DownloaderPathOverrides);
        Assert.True(string.IsNullOrEmpty(redactedDownloaderPath.Path));
        Assert.Equal("safe-downloader", redactedDownloaderPath.DownloaderId);
        Assert.Equal("safe-site", redactedDownloaderPath.Site);

        Assert.True(string.IsNullOrEmpty(redacted.GeneratedPath));
        Assert.True(string.IsNullOrEmpty(redacted.CachePath));
        Assert.True(string.IsNullOrEmpty(redacted.FfmpegPath));
        Assert.True(string.IsNullOrEmpty(redacted.FfprobePath));
        Assert.True(string.IsNullOrEmpty(redacted.FfmpegInputArgs));
        Assert.True(string.IsNullOrEmpty(redacted.FfmpegOutputArgs));
        Assert.Empty(redacted.ExcludePatterns);
        Assert.Empty(redacted.ExcludeImagePatterns);
        Assert.Empty(redacted.ExcludeGalleryPatterns);
        Assert.True(string.IsNullOrEmpty(redacted.Interface.HandyKey));
        Assert.True(string.IsNullOrEmpty(redacted.Ui.CustomLocalesPath));
        Assert.True(string.IsNullOrEmpty(redacted.Security.Username));
        Assert.Empty(redacted.Security.KnownProxies ?? []);
        Assert.Empty(redacted.Security.TrustedHosts ?? []);
        Assert.Empty(redacted.Scraping.ScraperDirectories);
        Assert.Empty(redacted.PluginConfigurations);

        Assert.Equal("Safe UI title", redacted.Ui.Title);
        Assert.True(redacted.Security.Enabled);
        Assert.Equal(fullBefore.VideoExtensions, redacted.VideoExtensions);

        var fullAfter = configService.GetConfig();
        Assert.False(string.IsNullOrEmpty(fullAfter.Scraping.MetadataServers.Single().ApiKey));
        Assert.False(string.IsNullOrEmpty(fullAfter.CovePaths.Single().Path));
        Assert.False(string.IsNullOrEmpty(fullAfter.Interface.HandyKey));
        Assert.Single(fullAfter.PluginConfigurations);

        principals.Set(CreatePrincipal(Permissions.SystemRead, Permissions.LibraryScan));
        var scanner = GetConfig(controller);
        Assert.Equal("/private/library", scanner.CovePaths.Single().Path);
        Assert.True(string.IsNullOrEmpty(scanner.Scraping.MetadataServers.Single().ApiKey));

        principals.Set(CreatePrincipal(Permissions.SystemSettingsWrite));
        var writable = GetConfig(controller);
        Assert.False(string.IsNullOrEmpty(writable.Scraping.MetadataServers.Single().ApiKey));
        Assert.False(string.IsNullOrEmpty(writable.CovePaths.Single().Path));
        Assert.False(string.IsNullOrEmpty(writable.GeneratedPath));
        Assert.False(string.IsNullOrEmpty(writable.Interface.HandyKey));
        Assert.Single(writable.PluginConfigurations);
    }

    private static CoveConfiguration CreateSensitiveConfiguration()
    {
        return new CoveConfiguration
        {
            CovePaths = [new CovePath { Path = "/private/library", ExcludeAudio = true }],
            GeneratedPath = "/private/generated",
            CachePath = "/private/cache",
            DownloaderPathOverrides =
            [
                new DownloaderPathOverride
                {
                    DownloaderId = "safe-downloader",
                    Site = "safe-site",
                    Path = "/private/downloads",
                },
            ],
            FfmpegPath = "/private/ffmpeg",
            FfprobePath = "/private/ffprobe",
            FfmpegInputArgs = "-private-input",
            FfmpegOutputArgs = "-private-output",
            ExcludePatterns = ["private-video-pattern"],
            ExcludeImagePatterns = ["private-image-pattern"],
            ExcludeGalleryPatterns = ["private-gallery-pattern"],
            Interface = new InterfaceConfig
            {
                Language = "en-US",
                HandyConnectionEnabled = true,
                HandyKey = "private-handy-key",
            },
            Ui = new UiConfig
            {
                Title = "Safe UI title",
                CustomLocalesPath = "/private/locales",
            },
            Auth = new AuthConfig
            {
                Enabled = true,
                Username = "private-security-username",
                KnownProxies = ["private-proxy"],
                TrustedHosts = ["private-host"],
            },
            Scraping = new ScrapingConfig
            {
                ScraperDirectories = ["/private/scrapers"],
                MetadataServers =
                [
                    new MetadataServerInstance
                    {
                        Endpoint = "https://metadata.invalid",
                        ApiKey = "private-metadata-key",
                        Name = "Safe metadata name",
                        MaxRequestsPerMinute = 123,
                    },
                ],
            },
            PluginConfigurations = new Dictionary<string, Dictionary<string, object?>>
            {
                ["safe-plugin"] = new() { ["token"] = "private-plugin-token" },
            },
        };
    }

    private static SystemController CreateController(
        ConfigService configService,
        CoveConfiguration configuration,
        ICurrentPrincipalAccessor principals)
    {
        return new SystemController(
            configService,
            ffmpegCapabilities: null!,
            scraperService: null!,
            metadataServerService: null!,
            configuration,
            db: null!,
            principals,
            auditService: null!,
            applicationLifetime: null!,
            runtimeLogLevelManager: null!,
            NullLogger<SystemController>.Instance);
    }

    private static CoveConfigDto GetConfig(SystemController controller)
    {
        var result = Assert.IsType<OkObjectResult>(controller.GetConfig().Result);
        return Assert.IsType<CoveConfigDto>(result.Value);
    }

    private static CovePrincipal CreatePrincipal(params string[] permissions)
    {
        return new CovePrincipal
        {
            UserId = 1,
            Username = "config-test-user",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Permissions = new HashSet<string>(permissions, StringComparer.OrdinalIgnoreCase),
        };
    }
}
