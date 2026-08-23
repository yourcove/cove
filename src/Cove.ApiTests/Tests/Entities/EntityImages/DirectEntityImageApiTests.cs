using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.EntityImages;

[Collection(ApiTestLane2Collection.Name)]
public sealed class DirectEntityImageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/audios/{id:int}/image")]
    [CoversEndpoint("GET", "/api/audios/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/audios/{id:int}/image")]
    [CoversEndpoint("POST", "/api/texts/{id:int}/image")]
    [CoversEndpoint("GET", "/api/texts/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/texts/{id:int}/image")]
    public async Task GivenAudioAndText_WhenMembersManageImages_ThenTheirIndependentLifecyclesArePubliclyObservable()
    {
        var audio = await AsUser().CreateAudioAsync($"Image audio {Guid.NewGuid():N}");
        var text = await AsUser().CreateTextAsync($"Image text {Guid.NewGuid():N}");
        var audioImage = ApiTestImages.RedPixelPng();
        var textImage = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadAudioImageAsync(audio, audioImage);
        await AsUser(ApiTestUsers.Eva).UploadTextImageAsync(text, textImage);
        (await AsUser().GetAudioImageAsync(audio)).ShouldMatch(audioImage);
        (await AsUser().GetTextImageAsync(text)).ShouldMatch(textImage);
        (await AsUser().GetAudioByIdAsync(audio.Id)).ImagePath.Should().Contain($"/api/audios/{audio.Id}/image");
        (await AsUser().GetTextByIdAsync(text.Id)).ImagePath.Should().Contain($"/api/texts/{text.Id}/image");

        await AsUser(ApiTestUsers.Eva).DeleteAudioImageAsync(audio);
        await AsUser(ApiTestUsers.Eva).DeleteTextImageAsync(text);
        (await AsUser().GetAudioByIdAsync(audio.Id)).ImagePath.Should().BeNull();
        (await AsUser().GetTextByIdAsync(text.Id)).ImagePath.Should().BeNull();
        await AssertMissing(() => AsUser().GetAudioImageAsync(audio));
        await AssertMissing(() => AsUser().GetTextImageAsync(text));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{id:int}/image")]
    [CoversEndpoint("GET", "/api/videos/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/videos/{id:int}/image")]
    [CoversEndpoint("POST", "/api/segments/{id:int}/image")]
    [CoversEndpoint("GET", "/api/segments/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/segments/{id:int}/image")]
    public async Task GivenVideoAndSegment_WhenImagesAreReplacedAndDeleted_ThenCacheReadsAndSlotsRemainIndependent()
    {
        var video = await AsUser().CreateVideoAsync($"Image video {Guid.NewGuid():N}");
        var segment = await AsUser().CreateVideoSegmentAsync(video, "Image segment");
        var videoImage = ApiTestImages.RedPixelPng();
        var replacement = ApiTestImages.OnePixelPng();
        var segmentImage = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(video, videoImage);
        await AsUser(ApiTestUsers.Eva).UploadSegmentImageAsync(segment, segmentImage);
        (await AsUser().GetVideoImageAsync(video)).ShouldMatch(videoImage);
        (await AsUser().GetSegmentImageAsync(segment)).ShouldMatch(segmentImage);
        var cachedVideoImage = await AsUser().GetVideoImageAsync(video, "?max=64&v=entity-image");
        var cachedSegmentImage = await AsUser().GetSegmentImageAsync(segment, "?max=64&v=entity-image");
        cachedVideoImage.MediaType.Should().Be("image/png");
        cachedVideoImage.Content.Should().NotBeEmpty();
        cachedVideoImage.CacheControl.Should().Be("public, max-age=31536000, immutable");
        cachedSegmentImage.MediaType.Should().Be("image/png");
        cachedSegmentImage.Content.Should().NotBeEmpty();
        cachedSegmentImage.CacheControl.Should().Be("public, max-age=31536000, immutable");
        (await AsUser().GetVideoByIdAsync(video.Id)).ImagePath.Should().Contain($"/api/videos/{video.Id}/image");

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(video, replacement);
        (await AsUser().GetVideoImageAsync(video)).ShouldMatch(replacement);
        (await AsUser().GetSegmentImageAsync(segment)).ShouldMatch(segmentImage);
        await AsUser(ApiTestUsers.Eva).DeleteVideoImageAsync(video);
        await AsUser(ApiTestUsers.Eva).DeleteSegmentImageAsync(segment);
        (await AsUser().GetVideoByIdAsync(video.Id)).ImagePath.Should().BeNull();
        await AssertMissing(() => AsUser().GetVideoImageAsync(video));
        await AssertMissing(() => AsUser().GetSegmentImageAsync(segment));
    }

    [Fact]
    [CoversEndpoint("POST", "/api/studios/{id:int}/image")]
    [CoversEndpoint("GET", "/api/studios/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/studios/{id:int}/image")]
    [CoversEndpoint("POST", "/api/tags/{id:int}/image")]
    [CoversEndpoint("GET", "/api/tags/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/tags/{id:int}/image")]
    public async Task GivenStudioAndTag_WhenMembersManageImages_ThenPathsAndIdempotentDeletesRemainCorrect()
    {
        var studio = await AsUser().CreateStudioAsync($"Image studio {Guid.NewGuid():N}");
        var tag = await AsUser().CreateTagAsync($"Image tag {Guid.NewGuid():N}");
        var studioImage = ApiTestImages.RedPixelPng();
        var tagImage = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadStudioImageAsync(studio, studioImage);
        await AsUser(ApiTestUsers.Eva).UploadTagImageAsync(tag, tagImage);
        (await AsUser().GetStudioImageAsync(studio)).ShouldMatch(studioImage);
        (await AsUser().GetTagImageAsync(tag)).ShouldMatch(tagImage);
        (await AsUser().GetStudioByIdAsync(studio.Id)).ImagePath.Should().Contain($"/api/studios/{studio.Id}/image");
        (await AsUser().GetTagsAsync()).Single(candidate => candidate.Id == tag.Id).ImagePath.Should().Contain($"/api/tags/{tag.Id}/image");

        await AsUser(ApiTestUsers.Eva).DeleteStudioImageAsync(studio);
        await AsUser(ApiTestUsers.Eva).DeleteTagImageAsync(tag);
        await AsUser(ApiTestUsers.Eva).DeleteStudioImageAsync(studio);
        await AsUser(ApiTestUsers.Eva).DeleteTagImageAsync(tag);
        (await AsUser().GetStudioByIdAsync(studio.Id)).ImagePath.Should().BeNull();
        (await AsUser().GetTagsAsync()).Single(candidate => candidate.Id == tag.Id).ImagePath.Should().BeNull();
        await AssertMissing(() => AsUser().GetStudioImageAsync(studio));
        await AssertMissing(() => AsUser().GetTagImageAsync(tag));
    }

    [Fact]
    [CoversEndpoint("DELETE", "/api/performers/{id:int}/image")]
    public async Task GivenPerformerWithPublicImage_WhenMemberDeletesIt_ThenTheImageCannotBeRead()
    {
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build());
        await AsUser().UploadPerformerImageAsync(performer, ApiTestImages.OnePixelPng());

        await AsUser(ApiTestUsers.Eva).DeletePerformerImageAsync(performer);
        await AsUser(ApiTestUsers.Eva).DeletePerformerImageAsync(performer);

        (await AsUser().GetPerformerByIdAsync(performer.Id)).ImagePath.Should().BeNull();
        await AssertMissing(() => AsUser().GetPerformerImageAsync(performer));
    }

    [Fact]
    public async Task GivenText_WhenImageUploadIsNotAnImage_ThenNoImageIsCreated()
    {
        var text = await AsUser().CreateTextAsync($"Invalid image text {Guid.NewGuid():N}");

        var invalidUpload = () => AsUser(ApiTestUsers.Eva).UploadTextImageAsync(text, "not an image"u8.ToArray(), "text/plain");

        await invalidUpload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await AssertMissing(() => AsUser().GetTextImageAsync(text));
    }

    private static async Task AssertMissing(Func<Task<ApiBinaryContent>> read)
        => await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
}
