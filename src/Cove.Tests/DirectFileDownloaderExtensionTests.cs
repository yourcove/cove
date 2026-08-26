using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using Cove.Api.Extensions;
using Cove.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class DirectFileDownloaderExtensionTests
{
    [Fact]
    public async Task MatchAsync_ReturnsVideoDownloader_ForVideoUrl()
    {
        var extension = new DirectFileDownloaderExtension();

        var match = await extension.MatchAsync("https://cdn.example.com/media/sample-video.mp4?token=abc", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("builtin.direct-file/video", match!.DownloaderId);
        Assert.Equal("sample-video.mp4", match.Label);
    }

    [Fact]
    public async Task MatchAsync_ReturnsImageDownloader_ForImageUrl()
    {
        var extension = new DirectFileDownloaderExtension();

        var match = await extension.MatchAsync("https://images.example.com/gallery/cover.jpeg", CancellationToken.None);

        Assert.NotNull(match);
        Assert.Equal("builtin.direct-file/image", match!.DownloaderId);
        Assert.Equal("cover.jpeg", match.Label);
    }

    [Fact]
    public async Task MatchAllAsync_ExtensionlessUrl_OffersWebTextDownloader()
    {
        var extension = new DirectFileDownloaderExtension();

        var matches = await extension.MatchAllAsync("https://example.com/story/chapter-1", CancellationToken.None);

        Assert.Contains(matches, match => match.DownloaderId == "builtin.web-text-page/text");
    }

    [Fact]
    public async Task DownloadAsync_WritesFileToHostTempDirectory()
    {
        var extension = new DirectFileDownloaderExtension();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "cove-direct-file-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var host = new FakeDownloaderHost(tempDirectory, new StubHttpClientFactory(new BinaryHttpMessageHandler()));
            var result = await extension.DownloadAsync(
                new DownloaderRequest(
                    "builtin.direct-file/video",
                    "https://cdn.example.com/video/test-video.mp4",
                    DownloaderEntity.Video,
                    new DownloaderPermissions(["cdn.example.com"])),
                host,
                CancellationToken.None);

            Assert.NotNull(result);
            var localPath = Path.Combine(tempDirectory, result!.LocalPath);
            Assert.True(File.Exists(localPath));
            Assert.Equal("test-video.mp4", result.OriginalFilename);
            Assert.Equal("video/mp4", result.Headers!["Content-Type"]);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAsync_WebTextPage_WritesReadableHtmlWithParagraphs()
    {
        var extension = new DirectFileDownloaderExtension();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "cove-direct-text-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var html = """
                <html>
                                    <head><title>Example Story</title></head>
                  <body>
                                        <header>Site header junk</header>
                    <nav>Navigation chrome</nav>
                                                                                <div class="story-body">
                                            First paragraph.

                                            Second paragraph.
                                        </div>
                                        <section class="comments">Comment junk</section>
                  </body>
                </html>
                """;
            var host = new FakeDownloaderHost(tempDirectory, new StubHttpClientFactory(new TextHttpMessageHandler(html, "text/html")));

            var result = await extension.DownloadAsync(
                new DownloaderRequest(
                    "builtin.web-text-page/text",
                    "https://example.com/story/chapter-1",
                    DownloaderEntity.Text,
                    new DownloaderPermissions()),
                host,
                CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("Example Story.html", result!.OriginalFilename);

            var savedHtml = await File.ReadAllTextAsync(Path.Combine(tempDirectory, result.LocalPath), TestContext.Current.CancellationToken);
            Assert.Contains("<p>First paragraph.</p>", savedHtml);
            Assert.Contains("<p>Second paragraph.</p>", savedHtml);
            Assert.DoesNotContain("Site header junk", savedHtml);
            Assert.DoesNotContain("Navigation chrome", savedHtml);
            Assert.DoesNotContain("Comment junk", savedHtml);
        }
        finally
        {
            if (Directory.Exists(tempDirectory))
                Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private sealed class FakeDownloaderHost(string tempDirectory, IHttpClientFactory httpClients) : IDownloaderHost
    {
        public string TempDirectory => tempDirectory;
        public IHttpClientFactory HttpClients => httpClients;
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;
        public void ReportProgress(double progress, string? message = null)
        {
        }
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class BinaryHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4, 5]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            response.Content.Headers.ContentLength = 5;
            return Task.FromResult(response);
        }
    }

    private sealed class TextHttpMessageHandler(string content, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content),
                RequestMessage = request,
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
            return Task.FromResult(response);
        }
    }
}

