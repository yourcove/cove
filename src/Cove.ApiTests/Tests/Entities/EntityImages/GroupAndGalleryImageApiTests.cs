using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using System.Net;
using System.Net.Http.Headers;

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
        var group = await AsUser().CreateGroupAsync($"Entity image group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var front = ApiTestImages.RedPixelPng();
        var replacementFront = ApiTestImages.OnePixelPng();
        var back = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, front, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).UploadGroupBackImageAsync(group, back, cancellationToken: TestContext.Current.CancellationToken);
        (await AsUser().GetGroupFrontImageAsync(group, TestContext.Current.CancellationToken)).ShouldMatch(front);
        (await AsUser().GetGroupBackImageAsync(group, TestContext.Current.CancellationToken)).ShouldMatch(back);
        var withImages = (await AsUser().GetGroupsAsync(TestContext.Current.CancellationToken)).Single(candidate => candidate.Id == group.Id);
        withImages.FrontImagePath.Should().Contain($"/api/groups/{group.Id}/image/front");
        withImages.BackImagePath.Should().Contain($"/api/groups/{group.Id}/image/back");

        await AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, replacementFront, cancellationToken: TestContext.Current.CancellationToken);
        (await AsUser().GetGroupFrontImageAsync(group, TestContext.Current.CancellationToken)).ShouldMatch(replacementFront);
        (await AsUser().GetGroupBackImageAsync(group, TestContext.Current.CancellationToken)).ShouldMatch(back);

        await AsUser(ApiTestUsers.Eva).DeleteGroupFrontImageAsync(group, TestContext.Current.CancellationToken);
        (await AsUser().GetGroupBackImageAsync(group, TestContext.Current.CancellationToken)).ShouldMatch(back);
        var withoutFront = (await AsUser().GetGroupsAsync(TestContext.Current.CancellationToken)).Single(candidate => candidate.Id == group.Id);
        withoutFront.FrontImagePath.Should().BeNull();
        withoutFront.BackImagePath.Should().Contain($"/api/groups/{group.Id}/image/back");
        var missingFront = () => AsUser().GetGroupFrontImageAsync(group);
        await missingFront.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        await AsUser(ApiTestUsers.Eva).DeleteGroupBackImageAsync(group, TestContext.Current.CancellationToken);
        var withoutImages = (await AsUser().GetGroupsAsync(TestContext.Current.CancellationToken)).Single(candidate => candidate.Id == group.Id);
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
        var gallery = await AsUser().CreateGalleryAsync(new GalleryCreateDto($"Entity image gallery {Guid.NewGuid():N}", null, null, null, null, null, false, null, [], [], [], []), TestContext.Current.CancellationToken);
        var cover = ApiTestImages.RedPixelPng();
        var back = ApiTestImages.BluePixelPng();

        await AsUser(ApiTestUsers.Eva).UploadGalleryImageAsync(gallery, cover, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).UploadGalleryBackImageAsync(gallery, back, cancellationToken: TestContext.Current.CancellationToken);
        (await AsUser().GetGalleryImageAsync(gallery, TestContext.Current.CancellationToken)).ShouldMatch(cover);
        (await AsUser().GetGalleryBackImageAsync(gallery, TestContext.Current.CancellationToken)).ShouldMatch(back);
        var withImages = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        withImages.CoverPath.Should().Contain($"/api/galleries/{gallery.Id}/cover");
        withImages.BackCoverPath.Should().Contain($"/api/galleries/{gallery.Id}/image/back");

        await AsUser(ApiTestUsers.Eva).DeleteGalleryImageAsync(gallery, TestContext.Current.CancellationToken);
        (await AsUser().GetGalleryBackImageAsync(gallery, TestContext.Current.CancellationToken)).ShouldMatch(back);
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).CoverPath.Should().BeNull();
        var missingCover = () => AsUser().GetGalleryImageAsync(gallery);
        await missingCover.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        await AsUser(ApiTestUsers.Eva).DeleteGalleryBackImageAsync(gallery, TestContext.Current.CancellationToken);
        var withoutImages = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        withoutImages.BackCoverPath.Should().BeNull();
        var missingBack = () => AsUser().GetGalleryBackImageAsync(gallery);
        await missingBack.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");
    }

    [Fact]
    public async Task GivenGroup_WhenImageUploadIsInvalid_ThenNoSlotIsCreated()
    {
        var group = await AsUser().CreateGroupAsync($"Invalid entity image group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var invalidUpload = () => AsUser(ApiTestUsers.Eva).UploadGroupFrontImageAsync(group, "not an image"u8.ToArray(), "text/plain");

        await invalidUpload.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        var missing = () => AsUser().GetGroupFrontImageAsync(group);
        await missing.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 404 (NotFound)*");

        using var client = new HttpClient { BaseAddress = ApiUri };
        using var content = new MultipartFormDataContent();
        using var image = new ByteArrayContent(ApiTestImages.OnePixelPng());
        image.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(image, "file", "unauthenticated.png");
        using var unauthenticated = await client.PostAsync($"/api/groups/{group.Id}/image/front", content, TestContext.Current.CancellationToken);
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
