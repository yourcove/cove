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

        var response = await client.GetAsync("/api/search/global?q=unique+searchable&perType=8", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
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

        var response = await client.GetAsync("/api/search/global?q=a&perType=8", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
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
            video.VideoPerformers.Add(new VideoPerformer { Performer = performer });
            db.Videos.Add(video);
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=relationship-only&perType=8", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.DoesNotContain(result.Groups, group => group.Type == "video");
        Assert.Contains(result.Groups, group => group.Type == "performer");
    }

    [Fact]
    public async Task ExactVideoTitleRanksAheadOfBroaderMatches()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        await factory.WithDbContextAsync(async db =>
        {
            db.Videos.AddRange(
                new Video { Title = "Remembered Scene", SearchText = "Remembered Scene" },
                new Video { Title = "Remembered Scene With Many More Words", SearchText = "Remembered Scene Remembered Scene" });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=remembered+scene&perType=8", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
        var videos = Assert.Single(result!.Groups, group => group.Type == "video").Items;
        Assert.Equal("Remembered Scene", videos[0].Title);
    }

    [Fact]
    public async Task ExactGalleryTitleRanksAheadOfBroaderMatches()
    {
        using var factory = new CoveWebApplicationFactory();
        await factory.ResetDatabaseAsync();
        await factory.WithDbContextAsync(async db =>
        {
            db.Galleries.AddRange(
                new Gallery { Title = "Remembered Gallery", SearchText = "Remembered Gallery" },
                new Gallery { Title = "Remembered Gallery With Many More Words", SearchText = "Remembered Gallery Remembered Gallery" });
            await db.SaveChangesAsync();
        });
        using var client = factory.CreateAuthenticatedClient();

        var response = await client.GetAsync("/api/search/global?q=remembered+gallery&perType=8", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<GlobalSearchResponseDto>(cancellationToken: TestContext.Current.CancellationToken);
        var galleries = Assert.Single(result!.Groups, group => group.Type == "gallery").Items;
        Assert.Equal("Remembered Gallery", galleries[0].Title);
    }
}
