using Cove.Core.DTOs;
using Microsoft.Extensions.Logging;

namespace Cove.Plugins;

public sealed class ExtensionPermissionManifest
{
    public List<string> Network { get; set; } = [];
    public List<string> ScraperRuntime { get; set; } = [];
    public List<string> DownloaderRuntime { get; set; } = [];
}

[Flags]
public enum ScraperCapabilities
{
    None = 0,
    ByUrl = 1 << 0,
    ByName = 1 << 1,
    ByFragment = 1 << 2,
    ByQueryFragment = 1 << 3,
}

public enum ScraperRiskLevel
{
    None,
    NetworkOnly,
    RemoteCode,
}

public enum ScraperEntity
{
    Video,
    Performer,
    Gallery,
    Image,
    Group,
    Audio,
    Text,
}

public sealed record ScraperPermissions(
    IReadOnlyList<string>? AllowNetworkHosts = null,
    bool AllowJavaScript = false,
    bool AllowCdp = false);

public sealed record ScraperDescriptor(
    string Id,
    string Name,
    ScraperEntity Entity,
    ScraperCapabilities Capabilities,
    IReadOnlyList<string> SupportedUrls,
    ScraperRiskLevel Risk,
    IReadOnlyList<string>? PreferenceSites)
{
    public ScraperDescriptor(
        string Id,
        string Name,
        ScraperEntity Entity,
        ScraperCapabilities Capabilities,
        IReadOnlyList<string> SupportedUrls)
        : this(Id, Name, Entity, Capabilities, SupportedUrls, ScraperRiskLevel.NetworkOnly, null)
    {
    }

    public ScraperDescriptor(
        string Id,
        string Name,
        ScraperEntity Entity,
        ScraperCapabilities Capabilities,
        IReadOnlyList<string> SupportedUrls,
        ScraperRiskLevel Risk)
        : this(Id, Name, Entity, Capabilities, SupportedUrls, Risk, null)
    {
    }
}

public sealed record ScraperRequest<TInput>(
    string ScraperId,
    TInput Input,
    ScraperPermissions Permissions);

public interface IScraperHost
{
    IHttpClientFactory HttpClients { get; }
    ILogger CreateLogger(string categoryName);
    Task<VideoScrapeInput?> GetVideoAsync(int videoId, CancellationToken ct = default);
    Task<PerformerScrapeInput?> GetPerformerAsync(int performerId, CancellationToken ct = default);
    Task<GalleryScrapeInput?> GetGalleryAsync(int galleryId, CancellationToken ct = default);
    Task<ImageScrapeInput?> GetImageAsync(int imageId, CancellationToken ct = default);
    Task<GroupScrapeInput?> GetGroupAsync(int groupId, CancellationToken ct = default);
    Task<AudioScrapeInput?> GetAudioAsync(int audioId, CancellationToken ct = default)
        => Task.FromResult<AudioScrapeInput?>(null);
    Task<TextScrapeInput?> GetTextAsync(int textId, CancellationToken ct = default)
        => Task.FromResult<TextScrapeInput?>(null);
}

public interface IScraperProvider : IExtension
{
    IReadOnlyList<ScraperDescriptor> GetScrapers();

    Task<ScrapedVideoDto?> ScrapeVideoAsync(ScraperRequest<VideoScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedVideoDto?>(null);

    Task<IReadOnlyList<ScrapedVideoDto>> SearchVideosAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedVideoDto>>([]);

    Task<ScrapedPerformerDto?> ScrapePerformerAsync(ScraperRequest<PerformerScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedPerformerDto?>(null);

    Task<IReadOnlyList<ScrapedPerformerDto>> SearchPerformersAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedPerformerDto>>([]);

    Task<ScrapedGalleryDto?> ScrapeGalleryAsync(ScraperRequest<GalleryScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedGalleryDto?>(null);

    Task<IReadOnlyList<ScrapedGalleryDto>> SearchGalleriesAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedGalleryDto>>([]);

    Task<ScrapedImageDto?> ScrapeImageAsync(ScraperRequest<ImageScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedImageDto?>(null);

    Task<IReadOnlyList<ScrapedImageDto>> SearchImagesAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedImageDto>>([]);

    Task<ScrapedGroupDto?> ScrapeGroupAsync(ScraperRequest<GroupScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedGroupDto?>(null);

    Task<IReadOnlyList<ScrapedGroupDto>> SearchGroupsAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedGroupDto>>([]);

    Task<ScrapedAudioDto?> ScrapeAudioAsync(ScraperRequest<AudioScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedAudioDto?>(null);

    Task<IReadOnlyList<ScrapedAudioDto>> SearchAudiosAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedAudioDto>>([]);

    Task<ScrapedTextDto?> ScrapeTextAsync(ScraperRequest<TextScrapeInput> request, CancellationToken ct)
        => Task.FromResult<ScrapedTextDto?>(null);

    Task<IReadOnlyList<ScrapedTextDto>> SearchTextsAsync(ScraperRequest<string> request, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<ScrapedTextDto>>([]);
}

[Flags]
public enum DownloaderCapabilities
{
    None = 0,
    ResumeSupported = 1 << 0,
    RangeRequests = 1 << 1,
    MultiQuality = 1 << 2,
    InlineMetadata = 1 << 3,
}

public enum DownloaderEntity
{
    Video,
    Image,
    Gallery,
    Audio,
    Text,
}

public sealed record DownloaderPermissions(IReadOnlyList<string>? AllowNetworkHosts = null);

public sealed record DownloaderDescriptor(
    string Id,
    string Name,
    DownloaderEntity SupportedEntity,
    IReadOnlyList<string> SupportedUrlPatterns,
    DownloaderCapabilities Capabilities = DownloaderCapabilities.None);

public sealed record DownloaderQualityOption(string Id, string Label, string? Description = null);

public sealed record DownloaderUrlMatch(
    string DownloaderId,
    string NormalizedUrl,
    IReadOnlyList<DownloaderQualityOption>? QualityOptions = null,
    string? Label = null,
    string? SourceUrl = null,
    bool Divert = false);

public sealed record DownloaderRequest(
    string DownloaderId,
    string Url,
    DownloaderEntity Entity,
    DownloaderPermissions Permissions,
    string? QualityId = null,
    string? SourceUrl = null);

public sealed record DownloaderResult(
    string LocalPath,
    string? OriginalFilename = null,
    IReadOnlyDictionary<string, string>? Headers = null,
    ScrapedVideoDto? InlineVideoMetadata = null,
    ScrapedGalleryDto? InlineGalleryMetadata = null,
    ScrapedImageDto? InlineImageMetadata = null);

public interface IDownloaderHost
{
    string TempDirectory { get; }
    IHttpClientFactory HttpClients { get; }
    ILogger CreateLogger(string categoryName);
    void ReportProgress(double progress, string? message = null);
}

public interface IDownloaderProvider : IExtension
{
    IReadOnlyList<DownloaderDescriptor> GetDownloaders();

    Task<DownloaderUrlMatch?> MatchAsync(string url, CancellationToken ct)
        => Task.FromResult<DownloaderUrlMatch?>(null);

    async Task<IReadOnlyList<DownloaderUrlMatch>> MatchAllAsync(string url, CancellationToken ct)
    {
        var match = await MatchAsync(url, ct);
        return match == null ? [] : [match];
    }

    Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, IDownloaderHost host, CancellationToken ct)
        => Task.FromResult<DownloaderResult?>(null);
}

