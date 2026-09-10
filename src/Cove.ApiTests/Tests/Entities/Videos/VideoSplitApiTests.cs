using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoSplitApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task SplitPersistsAssociationsAndCustomFieldsAndFailedSplitLeavesFileWithSource()
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N");
        var source = await AsUser().CreateVideoAsync($"Split source {suffix}", ct);
        await AsDbUser().AttachVideoFileAsync(source.Id, 30, 100, cancellationToken: ct);
        await AsDbUser().AttachVideoFileAsync(source.Id, 40, 200, cancellationToken: ct);
        source = await AsUser().GetVideoByIdAsync(source.Id, ct);
        var fileId = source.Files.Last().Id;
        var tag = await AsUser().CreateTagAsync($"Split tag {suffix}", ct);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName($"Split performer {suffix}").Build(), ct);
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle($"Split gallery {suffix}").Build(), ct);
        var key = $"split_{suffix}";
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto { Key = key, Label = "Split field", Type = "text", EntityTypes = ["video"] }, ct);
        using var http = AsUser().CreateHttpClient();
        // PostgreSQL rejects NUL in text after the scene has been saved, exercising the outer transaction.
        using var invalid = await http.PostAsJsonAsync($"/api/videos/{source.Id}/split-file",
            new VideoSplitFileDto(fileId, Metadata: new VideoSplitMetadataDto(Title: $"Failed split {suffix}", CustomFields: new() { [key] = "invalid\0text" })), ct);
        Assert.False(invalid.IsSuccessStatusCode);
        var afterFailure = await AsUser().GetVideoByIdAsync(source.Id, ct);
        Assert.Equal(2, afterFailure.Files.Count);
        Assert.DoesNotContain(await AsUser().GetVideosAsync(ct), video => video.Title == $"Failed split {suffix}");

        using var response = await http.PostAsJsonAsync($"/api/videos/{source.Id}/split-file", new VideoSplitFileDto(fileId,
            Metadata: new VideoSplitMetadataDto(Title: $"Split result {suffix}", Date: "2024-03", TagIds: [tag.Id], PerformerIds: [performer.Id], GalleryIds: [gallery.Id], CustomFields: new() { [key] = "Copied value" })), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var created = await AsUser().GetVideoByIdAsync(payload.GetProperty("videoId").GetInt32(), ct);
        Assert.Equal(fileId, created.PrimaryFileId);
        Assert.Single(created.Files);
        Assert.Equal("2024-03", created.Date);
        Assert.Equal(tag.Id, Assert.Single(created.Tags).Id);
        Assert.Equal(performer.Id, Assert.Single(created.Performers).Id);
        Assert.Equal(gallery.Id, Assert.Single(created.Galleries).Id);
        Assert.Equal("Copied value", created.CustomFields![key].ToString());
        Assert.Single((await AsUser().GetVideoByIdAsync(source.Id, ct)).Files);
    }
}
