using System.Text;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.System;

[Collection(ApiTestLane2Collection.Name)]
public sealed class SystemControllerApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/system/scrapers")]
    [CoversEndpoint("POST", "/api/system/scrapers/reload")]
    [CoversEndpoint("POST", "/api/system/scrapers/match-url")]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-url")]
    [CoversEndpoint("POST", "/api/system/scrapers/scrape-url-auto")]
    public async Task GivenLocalHtml_WhenBuiltinTextScraperRuns_ThenMetadataIsDeterministic()
    {
        var source = AsDownloadSource().CreateFile("page.html", "text/html", Encoding.UTF8.GetBytes("<html><head><title>Local [Tag One] page</title><meta name=\"description\" content=\"Local description\"></head><body></body></html>"));
        var eva = AsUser(ApiTestUsers.Eva);
        (await eva.GetScrapersAsync()).Should().Contain(scraper => scraper.Id == "builtin.generic:text");
        (await AsUser().ReloadScrapersAsync()).Should().Contain(scraper => scraper.Id == "builtin.generic:text");
        (await eva.MatchScrapersAsync(new ScraperMatchUrlRequest(source.Uri.AbsoluteUri, "text"))).Select(scraper => scraper.Id).Should().Equal("builtin.generic:text");
        var forbiddenReload = () => eva.ReloadScrapersAsync();
        await forbiddenReload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");
        (await eva.GetScrapersAsync()).Should().ContainSingle(scraper => scraper.Id == "builtin.generic:text");
        var scraped = await eva.ScrapeUrlAsync(new ScrapeUrlRequest("builtin.generic:text", "text", source.Uri.AbsoluteUri));
        scraped.GetProperty("title").GetString().Should().Be("Local page");
        scraped.GetProperty("details").GetString().Should().Be("Local description");
        scraped.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("Tag One");
        scraped.GetProperty("urls").EnumerateArray().Select(url => url.GetString()).Should().Equal(source.Uri.AbsoluteUri);
        var automatic = await eva.ScrapeUrlAutoAsync(new ScraperMatchUrlRequest(source.Uri.AbsoluteUri, "text"));
        automatic.GetProperty("scraperId").GetString().Should().Be("builtin.generic:text");
        var automaticResult = automatic.GetProperty("result");
        automaticResult.GetProperty("title").GetString().Should().Be("Local page");
        automaticResult.GetProperty("details").GetString().Should().Be("Local description");
        automaticResult.GetProperty("tags").EnumerateArray().Select(tag => tag.GetString()).Should().Equal("Tag One");
        automaticResult.GetProperty("urls").EnumerateArray().Select(url => url.GetString()).Should().Equal(source.Uri.AbsoluteUri);

        var noName = () => eva.ScrapeNameAsync(new ScrapeNameRequest("builtin.generic:text", "text", "not found"));
        var noFragment = () => eva.ScrapeFragmentAsync(new ScrapeFragmentRequest("builtin.generic:text", "text", new Dictionary<string, object>()));
        await noName.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
        await noFragment.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/system/metadata-servers/validate")]
    public async Task GivenLocalMetadataServer_WhenOwnerValidatesIt_ThenAuthenticatedIdentityIsReturned()
    {
        var result = await AsUser().ValidateMetadataServerAsync(new MetadataServerDto { Endpoint = AsMetadataService().Endpoint.AbsoluteUri, ApiKey = MetadataServiceSimulator.ApiKey, Name = "Local metadata" });
        result.Valid.Should().BeTrue();
        result.Username.Should().Be("API test metadata user");
        result.Status.Should().Contain("Successfully authenticated");
        var invalid = await AsUser().ValidateMetadataServerAsync(new MetadataServerDto { Endpoint = AsMetadataService().Endpoint.AbsoluteUri, ApiKey = "invalid", Name = "Local metadata" });
        invalid.Valid.Should().BeFalse();
        invalid.Username.Should().BeNull();
        invalid.Status.Should().NotBeNullOrWhiteSpace();
    }
}
