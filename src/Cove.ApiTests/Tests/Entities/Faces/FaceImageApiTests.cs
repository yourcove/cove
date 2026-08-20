using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Faces;

[Collection(ApiTestLane1Collection.Name)]
public sealed class FaceImageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("POST", "/api/faces/{id:int}/image")]
    [CoversEndpoint("GET", "/api/faces/{id:int}/image")]
    [CoversEndpoint("DELETE", "/api/faces/{id:int}/image")]
    public async Task GivenFace_WhenImageIsUploadedReadAndDeleted_ThenPublicImageLifecycleIsObservable()
    {
        // Arrange
        var face = await AsUser().CreateFaceAsync(new FaceCreateDto("Image candidate", null, false, null));
        var image = ApiTestImages.OnePixelPng();

        // Act
        await AsUser(ApiTestUsers.Eva).UploadFaceImageAsync(face, image);
        var uploaded = await AsUser(ApiTestUsers.Eva).GetFaceImageAsync(face);
        var faceWithImage = await AsUser().GetFaceByIdAsync(face.Id);
        await AsUser(ApiTestUsers.Eva).DeleteFaceImageAsync(face);
        var faceWithoutImage = await AsUser().GetFaceByIdAsync(face.Id);
        var readAfterDelete = () => AsUser().GetFaceImageAsync(face);

        // Assert
        uploaded.MediaType.Should().Be("image/png");
        uploaded.Content.Should().Equal(image);
        faceWithImage.CoverImageUrl.Should().Contain($"/api/faces/{face.Id}/image");
        faceWithoutImage.CoverImageUrl.Should().BeNull();
        await readAfterDelete.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
    }
}
