using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Cove.Tests;

public class PerformerScrapeServiceTests
{
    [Fact]
    public async Task ApplyAsync_MergesPerformerFieldsAndCreatesMissingTags()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Original Name" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var service = new PerformerScrapeService(context, null!);
        var scraped = new ScrapedPerformerDto
        {
            Name = "Updated Name",
            Country = "US",
            Details = "Imported biography",
            Urls = ["https://site.example/models/updated-name"],
            Aliases = ["Alt Name"],
            TagNames = ["Tag One", "Tag Two"],
        };

        await service.ApplyAsync(performer, scraped, createMissingTags: true);
        await context.SaveChangesAsync();

        var updated = await context.Performers
            .Include(item => item.Urls)
            .Include(item => item.Aliases)
            .Include(item => item.PerformerTags)
            .ThenInclude(item => item.Tag)
            .SingleAsync();

        Assert.Equal("Updated Name", updated.Name);
        Assert.Equal("US", updated.Country);
        Assert.Equal("Imported biography", updated.Details);
        Assert.Contains(updated.Urls, item => item.Url == "https://site.example/models/updated-name");
        Assert.Contains(updated.Aliases, item => item.Alias == "Alt Name");
        Assert.Equal(2, updated.PerformerTags.Count);
        Assert.Equal(2, await context.Tags.CountAsync());
    }

    [Fact]
    public async Task ApplyAsync_SkipsMissingTags_WhenCreationIsDisabled()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Original Name" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var service = new PerformerScrapeService(context, null!);
        var scraped = new ScrapedPerformerDto
        {
            TagNames = ["Uncreated Tag"],
            Urls = ["https://site.example/models/original-name"],
        };

        await service.ApplyAsync(performer, scraped, createMissingTags: false);
        await context.SaveChangesAsync();

        var updated = await context.Performers
            .Include(item => item.PerformerTags)
            .Include(item => item.Urls)
            .SingleAsync();

        Assert.Empty(updated.PerformerTags);
        Assert.Empty(context.Tags);
        Assert.Contains(updated.Urls, item => item.Url == "https://site.example/models/original-name");
    }

    [Fact]
    public async Task ApplyAsync_RecordsScraperFieldAndTagProvenance()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Original Name" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var fieldProvenance = new FieldProvenanceService(context);
        var tagProvenance = new TagProvenanceService(context);
        var service = new PerformerScrapeService(context, null!, fieldProvenanceService: fieldProvenance, tagProvenanceService: tagProvenance);
        var scraped = new ScrapedPerformerDto
        {
            Name = "Updated Name",
            Details = "Imported biography",
            Birthdate = "2024-05-01",
            Urls = ["https://site.example/models/updated-name"],
            Aliases = ["Alt Name"],
            TagNames = ["Tag One"],
        };

        await service.ApplyAsync(performer, scraped, createMissingTags: true);
        await context.SaveChangesAsync();

        var rows = await fieldProvenance.GetForHostAsync(AffinityHostType.Performer, performer.Id);
        Assert.Contains(rows, row => row.FieldKey == "name" && row.Value.HasValue && row.Value.Value.GetString() == "Updated Name");
        Assert.Contains(rows, row => row.FieldKey == "details" && row.Value.HasValue && row.Value.Value.GetString() == "Imported biography");
        Assert.Contains(rows, row => row.FieldKey == "birthdate" && row.Value.HasValue && row.Value.Value.GetString() == "2024-05-01");
        Assert.All(rows, row => Assert.Equal("scraper:local", row.SourceKey));

        var urls = Assert.Single(rows, row => row.FieldKey == "urls");
        Assert.True(urls.Value.HasValue);
        Assert.Contains(urls.Value.Value.EnumerateArray(), value => value.GetString() == "https://site.example/models/updated-name");

        var tag = await context.Tags.SingleAsync();
        var application = await context.TagApplications.SingleAsync();
        Assert.Equal(AffinityHostType.Performer, application.HostType);
        Assert.Equal(performer.Id, application.HostId);
        Assert.Equal(tag.Id, application.TagId);
        Assert.Equal("scraper:local", application.SourceKey);
    }

    [Fact]
    public async Task ApplyAsync_DownloadsAndReplacesPerformerImage()
    {
        await using var context = CreateContext();
        var performer = new Performer { Name = "Original Name", ImageBlobId = "old-blob" };
        context.Performers.Add(performer);
        await context.SaveChangesAsync();

        var blobService = new FakeBlobService();
        var httpClientFactory = new FakeHttpClientFactory(new HttpClient(new StubHttpMessageHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3, 4]),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            return response;
        })));

        var service = new PerformerScrapeService(context, null!, blobService, httpClientFactory);
        var scraped = new ScrapedPerformerDto
        {
            ImageUrl = "https://site.example/images/updated.jpg",
        };

        await service.ApplyAsync(performer, scraped, createMissingTags: false);

        Assert.Equal("blob-1", performer.ImageBlobId);
        Assert.Contains("old-blob", blobService.DeletedBlobIds);
        Assert.Equal("image/jpeg", blobService.StoredContentType);
        Assert.Equal([1, 2, 3, 4], blobService.StoredBytes);
    }

    [Fact]
    public void ConvertScrapeResult_ResolvesRelativeUrlsAndImage()
    {
        var scraped = PerformerScrapeService.ConvertScrapeResult(
            new Dictionary<string, object>
            {
                ["Name"] = "Jane Doe",
                ["URL"] = "/performer/jane-doe",
                ["Image"] = "/images/jane.jpg",
            },
            "https://example.com/performer/jane-doe");

        Assert.NotNull(scraped);
        Assert.Equal("https://example.com/images/jane.jpg", scraped!.ImageUrl);
        Assert.Contains("https://example.com/performer/jane-doe", scraped.Urls);
    }

    [Fact]
    public void ConvertScrapeResult_PreservesJsonElementCollectionsFromExtensionDtos()
    {
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var extensionResult = JsonSerializer.Deserialize<Dictionary<string, object>>(
            JsonSerializer.Serialize(
                new ScrapedPerformerDto
                {
                    Name = "Extension Performer",
                    Urls = ["https://example.com/performers/extension,Doe"],
                    Aliases = ["Extension Alias, Preferred"],
                    TagNames = ["Extension Tag, Featured"],
                },
                jsonOptions),
            jsonOptions);

        var scraped = PerformerScrapeService.ConvertScrapeResult(extensionResult!, string.Empty, "extension.scraper");

        Assert.NotNull(scraped);
        Assert.Equal(["https://example.com/performers/extension,Doe"], scraped!.Urls);
        Assert.Equal(["Extension Alias, Preferred"], scraped.Aliases);
        Assert.Equal(["Extension Tag, Featured"], scraped.TagNames);
    }

    [Fact]
    public void ConvertScrapeResult_SelectsContextualValuesFromJsonAndClrCollectionObjects()
    {
        using var jsonDocument = JsonDocument.Parse("""
            {
              "aliases": [{ "name": "JSON Alias, Preferred", "url": "https://example.com/not-an-alias" }],
              "tagNames": [{ "title": "JSON Tag, Featured", "url": "https://example.com/not-a-tag" }],
              "urls": [{ "name": "Not a URL", "url": "https://example.com/json,Doe" }]
            }
            """);
        var result = jsonDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
        result["aliases"] = new object[]
        {
            result["aliases"],
            new Dictionary<string, string>
            {
                ["Name"] = "CLR Alias, Preferred",
                ["Url"] = "https://example.com/not-a-clr-alias",
            },
        };

        var scraped = PerformerScrapeService.ConvertScrapeResult(result, string.Empty, "extension.scraper");

        Assert.NotNull(scraped);
        Assert.Equal(["https://example.com/json,Doe"], scraped!.Urls);
        Assert.Equal(["JSON Alias, Preferred", "CLR Alias, Preferred"], scraped.Aliases);
        Assert.Equal(["JSON Tag, Featured"], scraped.TagNames);
    }

    [Fact]
    public void ConvertScrapeResult_FallsBackFromBlankPreferredObjectValues()
    {
        using var jsonDocument = JsonDocument.Parse("""
            {
              "aliases": [{ "name": null, "title": "JSON title fallback" }]
            }
            """);
        var result = jsonDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());
        result["tagNames"] = new object[]
        {
            new Dictionary<string, string>
            {
                ["Name"] = "   ",
                ["Title"] = "CLR title fallback",
            },
        };
        result["name"] = "Fallback performer";

        var scraped = PerformerScrapeService.ConvertScrapeResult(result, string.Empty, "extension.scraper");

        Assert.NotNull(scraped);
        Assert.Equal(["JSON title fallback"], scraped!.Aliases);
        Assert.Equal(["CLR title fallback"], scraped.TagNames);
    }

    [Fact]
    public void CandidateUrlExtraction_ProvidesBaseForRelativeStructuredValues()
    {
        using var jsonDocument = JsonDocument.Parse("""
            {
              "name": "Candidate performer",
              "imageUrl": "../images/candidate.jpg",
              "urls": [
                { "name": "Not the candidate URL", "url": "https://example.com/performers/candidate" },
                { "url": "related-profile" }
              ]
            }
            """);
        var result = jsonDocument.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => (object)property.Value.Clone());

        var candidateUrl = PerformerScrapeService.ExtractCandidateUrl(result);
        var scraped = PerformerScrapeService.ConvertScrapeResult(result, candidateUrl!, "extension.scraper");

        Assert.Equal("https://example.com/performers/candidate", candidateUrl);
        Assert.NotNull(scraped);
        Assert.Equal("https://example.com/images/candidate.jpg", scraped!.ImageUrl);
        Assert.Contains("https://example.com/performers/related-profile", scraped.Urls);
    }

    private static CoveContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase($"performer-scrape-service-{Guid.NewGuid():N}")
            .Options;

        return new TestCoveContext(options);
    }

    private sealed class TestCoveContext(DbContextOptions<CoveContext> options) : CoveContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

        }
    }

    private sealed class FakeBlobService : IBlobService
    {
        public byte[] StoredBytes { get; private set; } = [];
        public string StoredContentType { get; private set; } = string.Empty;
        public List<string> DeletedBlobIds { get; } = [];

        public async Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
        {
            using var buffer = new MemoryStream();
            await data.CopyToAsync(buffer, ct);
            StoredBytes = buffer.ToArray();
            StoredContentType = contentType;
            return "blob-1";
        }

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream Stream, string ContentType)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            DeletedBlobIds.Add(blobId);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responseFactory());
    }
}
