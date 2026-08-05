using System.Net;
using System.Net.Http.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Tests.Integration;

public sealed class GlobalSearchSmokeTests
{
    [Fact]
    public async Task ReturnsLightweightGroupedResultsFromOneEndpoint()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        await factory.WithDbContextAsync(async db =>
        {
            db.Videos.Add(new Video { Title = "Unique searchable title", SearchText = "Unique searchable title" });
            db.Performers.Add(new Performer { Name = "Unique searchable performer", SearchText = "Unique searchable performer" });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=unique+searchable&perType=8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>();
        Assert.NotNull(result);
        Assert.Empty(result.FailedTypes);
        Assert.Contains(result.Groups, group => group.Type == "video" && group.Items.Any(item => item.Title == "Unique searchable title"));
        Assert.Contains(result.Groups, group => group.Type == "performer" && group.Items.Any(item => item.Title == "Unique searchable performer"));
    }

    [Fact]
    public async Task ShortTermsDoNotRunASearch()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=a&perType=8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>();
        Assert.NotNull(result);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task GlobalSearchUsesIndexedDocumentsInsteadOfRelationshipFallbackScans()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        await factory.WithDbContextAsync(async db =>
        {
            var performer = new Performer { Name = "Relationship-only phrase" };
            var video = new Video { Title = "Unrelated title" };
            db.VideoPerformers.Add(new VideoPerformer { Video = video, Performer = performer });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=relationship-only&perType=8");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>();
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Groups, group => group.Type == "video");
        Assert.Contains(result.Groups, group => group.Type == "performer");
    }
}
