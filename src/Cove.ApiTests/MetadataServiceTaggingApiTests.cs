using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane2Collection.Name)]
public sealed class MetadataServiceTaggingApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenMetadataServiceTag_WhenTagIsRemoved_ThenVideoNoLongerHasTag()
    {
        var metadataScene = AsMetadataService().CreateScene(
            new MetadataServiceSceneBuilder()
                .WithTitle("Metadata scene")
                .WithTag("Metadata tag")
                .Build());
        var video = await AsUser().CreateVideoAsync("Local video");
        var taggedVideo = await AsUser().ImportVideoFromMetadataServiceAsync(video, metadataScene);
        var scrapedTag = Assert.Single(taggedVideo.Tags);
        Assert.True(scrapedTag.CanRemove);
        Assert.Contains(
            scrapedTag.Provenance ?? [],
            provenance => provenance.SourceKey == $"metadata:{metadataScene.Endpoint.AbsoluteUri}");

        await AsUser().RemoveTagFromVideoAsync(taggedVideo, scrapedTag);

        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id);
        Assert.DoesNotContain(videoAfter.Tags, tag => tag.Id == scrapedTag.Id);
        Assert.True(await AsUser().TagExistsAsync(scrapedTag.Id));
    }
}
