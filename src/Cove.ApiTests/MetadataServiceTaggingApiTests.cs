using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
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
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title);
        var taggedVideo = await AsUser().ImportVideoFromMetadataServiceAsync(video, metadataScene);
        taggedVideo.Tags.Should().ContainSingle();
        var scrapedTag = taggedVideo.Tags.Single();
        scrapedTag.CanRemove.Should().BeTrue();
        scrapedTag.Provenance.Should().Contain(
            provenance => provenance.SourceKey == $"metadata:{metadataScene.Endpoint.AbsoluteUri}");

        await AsUser().RemoveTagFromVideoAsync(taggedVideo, scrapedTag);

        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id);
        videoAfter.Tags.Should().NotContain(tag => tag.Id == scrapedTag.Id);
        (await AsUser().TagExistsAsync(scrapedTag.Id)).Should().BeTrue();
    }
}
