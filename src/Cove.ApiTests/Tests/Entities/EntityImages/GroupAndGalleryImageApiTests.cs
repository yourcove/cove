using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using System.Net;
using System.Net.Http.Headers;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.EntityImages;

[Collection(ApiTestLane2Collection.Name)]
public sealed class GroupAndGalleryImageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/groups/{id:int}/image/front")]
    [CoversEndpoint("GET", "/api/groups/{id:int}/image/front")]
    [CoversEndpoint("DELETE", "/api/groups/{id:int}/image/front")]
    [CoversEndpoint("POST", "/api/groups/{id:int}/image/back")]
    [CoversEndpoint("GET", "/api/groups/{id:int}/image/back")]
    [CoversEndpoint("DELETE", "/api/groups/{id:int}/image/back")]
    public async Task GivenGroup_WhenMemberManagesDistinctFrontAndBackImages_ThenSlotsRemainIndependent()
    {
        var group = await AsUser().CreateGroupAsync($"Entity image group {Guid.NewGuid():N}");
        var front = ApiTestImages.RedPixelPng();
        var replacementFront = ApiTestImages.OnePixelPng();
        var back = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, front);
        await AsUser(ApiTestUsers.Eva).UploadGroupBackImageAsync(group, back);
        (await AsUser().GetGroupFrontImageAsync(group)).ShouldMatch(front);
        (await AsUser().GetGroupBackImageAsync(group)).ShouldMatch(back);
        var withImages = (await AsUser().GetGroupsAsync()).Single(candidate => candidate.Id == group.Id);
        withImages.FrontImagePath.Should().Contain($"/api/groups/{group.Id}/image/front");
        withImages.BackImagePath.Should().Contain($"/api/groups/{group.Id}/image/back");

        await AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, replacementFront);
        (await AsUser().GetGroupFrontImageAsync(group)).ShouldMatch(replacementFront);
        (await AsUser().GetGroupBackImageAsync(group)).ShouldMatch(back);

        await AsUser(ApiTestUsers.Eva).DeleteGroupFrontImageAsync(group);
        (await AsUser().GetGroupBackImageAsync(group)).ShouldMatch(back);
        var withoutFront = (await AsUser().GetGroupsAsync()).Single(candidate => candidate.Id == group.Id);
        withoutFront.FrontImagePath.Should().BeNull();
        withoutFront.BackImagePath.Should().Contain($"/api/groups/{group.Id}/image/back");
        var missingFront = () => AsUser().GetGroupFrontImageAsync(group);
        await missingFront.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        await AsUser(ApiTestUsers.Eva).DeleteGroupBackImageAsync(group);
        var withoutImages = (await AsUser().GetGroupsAsync()).Single(candidate => candidate.Id == group.Id);
        withoutImages.FrontImagePath.Should().BeNull();
        withoutImages.BackImagePath.Should().BeNull();
        var missingBack = () => AsUser().GetGroupBackImageAsync(group);
        await missingBack.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    [CoversEndpoint("POST", "/api/galleries/{id:int}/image")]
    [CoversEndpoint("GET", "/api/galleries/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/galleries/{id:int}/image")]
    [CoversEndpoint("POST", "/api/galleries/{id:int}/image/back")]
    [CoversEndpoint("GET", "/api/galleries/{id:int}/image/back")]
    [CoversEndpoint("DELETE", "/api/galleries/{id:int}/image/back")]
    public async Task GivenGallery_WhenMemberManagesDistinctCoverAndBackImages_ThenDtoPathsAndSlotsRemainIndependent()
    {
        var gallery = await AsUser().CreateGalleryAsync(new GalleryCreateDto($"Entity image gallery {Guid.NewGuid():N}", null, null, null, null, null, false, null, [], [], [], []));
        var cover = ApiTestImages.RedPixelPng();
        var back = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadGalleryImageAsync(gallery, cover);
        await AsUser(ApiTestUsers.Eva).UploadGalleryBackImageAsync(gallery, back);
        (await AsUser().GetGalleryImageAsync(gallery)).ShouldMatch(cover);
        (await AsUser().GetGalleryBackImageAsync(gallery)).ShouldMatch(back);
        var withImages = await AsUser().GetGalleryByIdAsync(gallery.Id);
        withImages.CoverPath.Should().Contain($"/api/galleries/{gallery.Id}/cover");
        withImages.BackCoverPath.Should().Contain($"/api/galleries/{gallery.Id}/image/back");

        await AsUser(ApiTestUsers.Eva).DeleteGalleryImageAsync(gallery);
        (await AsUser().GetGalleryBackImageAsync(gallery)).ShouldMatch(back);
        (await AsUser().GetGalleryByIdAsync(gallery.Id)).CoverPath.Should().BeNull();
        var missingCover = () => AsUser().GetGalleryImageAsync(gallery);
        await missingCover.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        await AsUser(ApiTestUsers.Eva).DeleteGalleryBackImageAsync(gallery);
        var withoutImages = await AsUser().GetGalleryByIdAsync(gallery.Id);
        withoutImages.BackCoverPath.Should().BeNull();
        var missingBack = () => AsUser().GetGalleryBackImageAsync(gallery);
        await missingBack.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenGroup_WhenImageUploadIsInvalid_ThenNoSlotIsCreated()
    {
        var group = await AsUser().CreateGroupAsync($"Invalid entity image group {Guid.NewGuid():N}");
        var invalidUpload = () => AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, "not an image"u8.ToArray(), "text/plain");

        await invalidUpload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var missing = () => AsUser().GetGroupFrontImageAsync(group);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        using var client = new HttpClient { BaseAddress = ApiUri };
        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(ApiTestImages.OnePixelPng());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(image, "file", "unauthenticated.png");
        using var unauthenticated = await client.PostAsync($"/api/groups/{group.Id}/image/front", content);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }
}

internal static class EntityImageAssertions
{
    public static void ShouldMatch(this ApiBinaryContent actual, byte[] expected)
    {
        actual.MediaType.Should().Be("image/png");
        actual.Content.Should().Equal(expected);
    }
}
