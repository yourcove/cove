using System.Net;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;

namespace Cove.Tests;

public class ScraperServiceTests
{
    private const string YamlScraperPackId = "cove.official.scrapers.yaml-video";

    [Fact]
    public void FindScrapersForUrl_ReturnsGenericTextFallbackForHttpUrls()
    {
        var service = CreateService();

        var matches = service.FindScrapersForUrl("https://example.com/story/chapter-0", "text");

        var match = Assert.Single(matches);
        Assert.Equal("builtin.generic:text", match.Id);
    }

    [Fact]
    public async Task ScrapeUrlAutoAsync_TextExtensionScraper_BeatsGenericFallback()
    {
        var service = CreateService(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["https://literotica.com/s/example-story"] = "<html><head><title>Generic Title</title></head><body><article><p>Body content.</p></article></body></html>",
            },
            new FakeTextScraperProvider());

        var result = await service.ScrapeUrlAutoAsync("https://literotica.com/s/example-story", "text", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("fake.literotica/text", result?.ScraperId);
        Assert.Equal("Specific Title", Assert.IsType<JsonElement>(result?.Result["title"]).GetString());
        Assert.Equal(["Fetish"], Assert.IsType<JsonElement>(result?.Result["tagNames"]).EnumerateArray().Select(item => item.GetString()).ToList());
    }

    [Fact]
    public void FindScrapersForUrl_TextExtensionScraper_SortsBeforeGenericFallback()
    {
        var service = CreateService(scraperProvider: new FakeTextScraperProvider());

        var matches = service.FindScrapersForUrl("https://literotica.com/s/example-story", "text");

        Assert.Equal("fake.literotica/text", matches[0].Id);
        Assert.Contains(matches, match => match.Id == "builtin.generic:text");
    }

    [Fact]
    public void FindScrapersForUrl_MatchesExtensionPreferenceSites()
    {
        var service = CreateService(scraperProvider: new FakeDynamicScraperProvider());

        var matches = service.FindScrapersForUrl("https://www.dynamic.example.com/watch/123", "video");

        var match = Assert.Single(matches);
        Assert.Equal("fake.dynamic/video", match.Id);
        Assert.Equal(["dynamic.example.com"], match.PreferenceSites);
    }

    [Fact]
    public async Task ScrapeUrlAutoAsync_GenericTextPage_ExtractsMetadataAndBracketTags()
    {
        var service = CreateService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.com/story/chapter-1"] = """
                <html>
                  <head>
                    <title>[F4F] Example Story</title>
                    <meta name="author" content="Example Author" />
                    <meta name="description" content="[Romance] Chapter intro" />
                    <link rel="canonical" href="https://example.com/story/chapter-1" />
                  </head>
                  <body>
                    <article><h1>[F4F] Example Story</h1><p>Body content.</p></article>
                  </body>
                </html>
                """,
        });

        var result = await service.ScrapeUrlAutoAsync("https://example.com/story/chapter-1", "text", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("builtin.generic:text", result?.ScraperId);
        Assert.Equal("Example Story", Assert.IsType<string>(result?.Result["title"]));
        Assert.Equal("Chapter intro", Assert.IsType<string>(result?.Result["details"]));
        Assert.Equal(["Example Author"], Assert.IsAssignableFrom<IEnumerable<string>>(result?.Result["performers"]));
        Assert.Equal(["F4F", "Romance"], Assert.IsAssignableFrom<IEnumerable<string>>(result?.Result["tags"]));
    }

    [Fact]
    public async Task ScrapeUrlAutoAsync_GenericTextPage_PreservesTitleVerbatim()
    {
        var service = CreateService(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.com/story/chapter-2"] = """
                <html>
                  <head><title>Example Story - Sample Site</title></head>
                  <body><article><p>First paragraph.</p></article></body>
                </html>
                """,
        });

        var result = await service.ScrapeUrlAutoAsync("https://example.com/story/chapter-2", "text", TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Example Story - Sample Site", Assert.IsType<string>(result?.Result["title"]));
    }

    [Fact]
    public async Task ScrapeUrlAutoAsync_AudioUrlWithoutExtensionScraper_ReturnsNull()
    {
        var service = CreateService();

        var result = await service.ScrapeUrlAutoAsync("https://audio.example.net/track/example", "audio", TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetScrapers_LoadsEnabledYamlScraperPackFromInstalledExtension()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-yaml-scraper-pack-{Guid.NewGuid():N}");

        try
        {
            var extensionManager = await CreateYamlScraperPackExtensionManagerAsync(root);
            var service = CreateService(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["https://example.com/watch/123"] = "<html><head><title>Pack Video</title></head></html>",
                },
                extensionManager: extensionManager);

            var scraper = Assert.Single(service.GetScrapers(), scraper => scraper.Id == $"{YamlScraperPackId}/Example:video");
            Assert.Equal("Example YAML", scraper.Name);
            Assert.Equal("video", scraper.EntityType);
            Assert.Contains("URL", scraper.SupportedScrapes);
            Assert.Equal(["example.com/watch/"], scraper.Urls);

            var result = await service.ScrapeUrlAsync($"{YamlScraperPackId}/Example:video", "video", "https://example.com/watch/123", TestContext.Current.CancellationToken);

            Assert.NotNull(result);
            Assert.Equal("Pack Video", Assert.IsType<string>(result?["Title"]));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetScrapers_SkipsDisabledYamlScraperPack()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-yaml-scraper-pack-{Guid.NewGuid():N}");

        try
        {
            var extensionManager = await CreateYamlScraperPackExtensionManagerAsync(root);
            await extensionManager.DisableExtensionAsync(YamlScraperPackId, TestContext.Current.CancellationToken);

            var service = CreateService(extensionManager: extensionManager);

            Assert.DoesNotContain(service.GetScrapers(), scraper => scraper.Id.StartsWith($"{YamlScraperPackId}/", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<ExtensionManager> CreateYamlScraperPackExtensionManagerAsync(string root)
    {
        var dataDir = Path.Combine(root, "data");
        var extensionsDir = Path.Combine(root, "extensions");
        var extensionDir = Path.Combine(extensionsDir, YamlScraperPackId);
        var scraperDir = Path.Combine(extensionDir, "scrapers");

        Directory.CreateDirectory(scraperDir);
        await File.WriteAllTextAsync(Path.Combine(extensionDir, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = YamlScraperPackId,
            Name = "YAML Site Scrapers",
            Version = "1.0.0",
            Kind = "scraper-pack",
            MinCoveVersion = "0.0.16",
            Categories = ["scraper", "metadata", "yaml-scraper"],
        }));
        await File.WriteAllTextAsync(Path.Combine(scraperDir, "Example.yml"), """
            name: Example YAML
            videoByURL:
              - action: scrapeXPath
                url:
                  - example.com/watch/
                scraper: videoScraper
            xPathScrapers:
              videoScraper:
                video:
                  Title: //title
            """);

        var extensionManager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = dataDir,
            CoveVersion = "test",
        });
        extensionManager.DiscoverExtensions(extensionsDir);
        return extensionManager;
    }

    private static ScraperService CreateService(
        IReadOnlyDictionary<string, string>? responses = null,
        IScraperProvider? scraperProvider = null,
        ExtensionManager? extensionManager = null)
    {
        extensionManager ??= new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.GetTempPath(),
            CoveVersion = "test",
        });
        if (scraperProvider != null)
            extensionManager.Register(scraperProvider);

        return new ScraperService(
            new CoveConfiguration(),
            NullLogger<ScraperService>.Instance,
            new FakeHttpClientFactory(responses ?? new Dictionary<string, string>()),
            extensionManager);
    }

    private sealed class FakeHttpClientFactory(IReadOnlyDictionary<string, string> responses) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new FakeHttpMessageHandler(responses))
            {
                BaseAddress = new Uri("https://example.test/"),
            };
    }

    private sealed class FakeHttpMessageHandler(IReadOnlyDictionary<string, string> responses) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            if (!responses.TryGetValue(url, out var html))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request,
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html),
                RequestMessage = request,
            });
        }
    }

    private sealed class FakeTextScraperProvider : IScraperProvider
    {
        private static readonly ScraperDescriptor Descriptor = new(
            "fake.literotica/text",
            "Fake Literotica Text",
            ScraperEntity.Text,
            ScraperCapabilities.ByUrl,
            ["literotica.com/s/*", "www.literotica.com/s/*"],
            ScraperRiskLevel.NetworkOnly);

        public string Id => "fake.literotica";
        public string Name => "Fake Literotica";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public IReadOnlyList<ScraperDescriptor> GetScrapers() => [Descriptor];

        public Task<ScrapedTextDto?> ScrapeTextAsync(ScraperRequest<TextScrapeInput> request, CancellationToken ct)
            => Task.FromResult<ScrapedTextDto?>(new ScrapedTextDto
            {
                Title = "Specific Title",
                TagNames = ["Fetish"],
            });
    }

    private sealed class FakeDynamicScraperProvider : IScraperProvider
    {
        private static readonly ScraperDescriptor Descriptor = new(
            "fake.dynamic/video",
            "Fake Dynamic Video",
            ScraperEntity.Video,
            ScraperCapabilities.ByUrl,
            [],
            ScraperRiskLevel.NetworkOnly,
            ["www.dynamic.example.com"]);

        public string Id => "fake.dynamic";
        public string Name => "Fake Dynamic";
        public string Version => "1.0.0";
        public string? Description => null;
        public string? Author => null;
        public string? Url => null;
        public string? IconUrl => null;

        public void ConfigureServices(IServiceCollection services, ExtensionContext context)
        {
        }

        public IReadOnlyList<ScraperDescriptor> GetScrapers() => [Descriptor];
    }
}
