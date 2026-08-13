using System.Text.Json;
using Cove.ApiTests.Builders;
using Cove.ApiTests.ExampleData;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Xunit.Abstractions;

namespace Cove.ApiTests;

[Collection(ApiTestLane1Collection.Name)]
public sealed class VideoCreationApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenVideo_WhenMemberReadsVideos_ThenVideoIsReturned()
    {
        var video = await AsUser().CreateVideoAsync(TestCatalog.Movies.RaidersOfTheLostCorset.Title);

        var videos = await AsUser(ApiTestUsers.Eva).GetVideosAsync();

        videos.Should().ContainSingle(candidate => candidate.Id == video.Id);
    }

    [Fact]
    public async Task GivenVideoMetadata_WhenVideoIsCreated_ThenAllMetadataCanBeRetrieved()
    {
        const string customFieldKey = "prop_budget";
        var movie = TestCatalog.Movies.RaidersOfTheLostCorset;
        var studio = await AsUser().CreateStudioAsync(TestCatalog.Studio.Name);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().WithName(movie.Cast[0].Name).Build());
        var tag = await AsUser().CreateTagAsync(movie.Tags[0].Name);
        var gallery = await AsUser().CreateGalleryAsync(new GalleryBuilder().WithTitle("Wardrobe Vault Stills").Build());
        var group = await AsUser().CreateGroupAsync("Adventure Double Feature");
        await AsUser().CreateCustomFieldDefinitionAsync(new CustomFieldDefinitionCreateDto
        {
            Key = customFieldKey,
            Label = "Prop budget",
            Type = "text",
            EntityTypes = ["video"]
        });
        var request = new VideoBuilder()
            .WithTitle(movie.Title)
            .WithCode("BDP-RAIDERS-001")
            .WithDetails(movie.Premise)
            .WithDirector("Penny Farthing")
            .WithDate("2026-07-18")
            .WithRating(93)
            .WithStudio(studio)
            .WithCaptions("The vault is this way.")
            .WithUrl("https://barely-dressed.example/raiders")
            .WithTags([tag])
            .WithPerformers([performer])
            .WithGallery(gallery)
            .WithGroup(group, 4)
            .WithRemoteId("https://metadata.example/graphql", "video-raiders")
            .WithCustomField(customFieldKey, "Mostly candles")
            .AsOrganized()
            .AsVr()
            .Build();

        var video = await AsUser().CreateVideoAsync(request);

        var videoAfter = await AsUser().GetVideoByIdAsync(video.Id);
        var engagement = await AsUser().GetVideoEngagementAsync(videoAfter);
        videoAfter.Title.Should().Be(request.Title);
        videoAfter.Code.Should().Be(request.Code);
        videoAfter.Details.Should().Be(request.Details);
        videoAfter.Director.Should().Be(request.Director);
        videoAfter.Date.Should().Be(request.Date);
        videoAfter.Organized.Should().BeTrue();
        videoAfter.IsVr.Should().BeTrue();
        videoAfter.StudioId.Should().Be(studio.Id);
        videoAfter.StudioName.Should().Be(studio.Name);
        videoAfter.Captions.Should().Be(request.Captions);
        videoAfter.Urls.Should().Equal(request.Urls!);
        videoAfter.Tags.Should().ContainSingle(candidate => candidate.Id == tag.Id);
        videoAfter.Performers.Should().ContainSingle(candidate => candidate.Id == performer.Id);
        videoAfter.Galleries.Should().ContainSingle(candidate => candidate.Id == gallery.Id);
        var videoGroup = videoAfter.Groups.Should().ContainSingle(candidate => candidate.Id == group.Id).Which;
        videoGroup.VideoIndex.Should().Be(4);
        videoAfter.RemoteIds.Should().Equal(request.RemoteIds!);
        videoAfter.CustomFields.Should().ContainKey(customFieldKey)
            .WhoseValue.Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("Mostly candles");
        engagement.Rating.Should().Be(request.Rating);
    }
}
