using Cove.Api.Services;

namespace Cove.Tests;

public class TextExtractionServiceTests
{
    [Fact]
    public async Task ExtractContentAsync_PreservesReadableStructureForHtmlFiles()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "story.html");

        await File.WriteAllTextAsync(path, """
            <html>
              <head>
                <title>Example Story</title>
                <meta name="author" content="Example Author" />
              </head>
              <body>
                <article>
                  <h1>Chapter One</h1>
                  <p>First paragraph with <em>emphasis</em>.</p>
                  <ul>
                    <li>First bullet</li>
                    <li>Second bullet</li>
                  </ul>
                  <p>Second paragraph.</p>
                </article>
              </body>
            </html>
            """, TestContext.Current.CancellationToken);

        try
        {
            var service = new TextExtractionService();

            var content = await service.ExtractContentAsync(path, TestContext.Current.CancellationToken);
          var normalized = content.Content.Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Equal("html", content.Format);
          Assert.Equal("html", content.RenderMode);
            Assert.Equal("Example Story", content.Title);
            Assert.Equal("Example Author", content.Author);
          Assert.Contains("<article>", normalized);
          Assert.Contains("<h1>Chapter One</h1>", normalized);
          Assert.Contains("<p>First paragraph with <em>emphasis</em>.</p>", normalized);
          Assert.Contains("<li>First bullet</li>", normalized);
          Assert.Contains("<p>Second paragraph.</p>", normalized);
          Assert.Equal(14, content.WordCount);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task ExtractContentAsync_HtmlStoryBodyWithBareNewlines_WrapsParagraphsAndDropsChrome()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        Directory.CreateDirectory(tempDirectory);
        var path = Path.Combine(tempDirectory, "story.html");

        await File.WriteAllTextAsync(path, """
            <html>
              <head><title>Example Story</title></head>
              <body>
                <header>Site header junk</header>
                <nav>Navigation junk</nav>
                <div class="story-body">
                  First paragraph.

                  Second paragraph.
                </div>
                <section class="comments">Comment junk</section>
              </body>
            </html>
            """, TestContext.Current.CancellationToken);

        try
        {
            var service = new TextExtractionService();

            var content = await service.ExtractContentAsync(path, TestContext.Current.CancellationToken);
            var normalized = content.Content.Replace("\r\n", "\n", StringComparison.Ordinal);

            Assert.Equal("html", content.RenderMode);
            Assert.Contains("<p>First paragraph.</p>", normalized);
            Assert.Contains("<p>Second paragraph.</p>", normalized);
            Assert.DoesNotContain("Site header junk", normalized);
            Assert.DoesNotContain("Navigation junk", normalized);
            Assert.DoesNotContain("Comment junk", normalized);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }
}