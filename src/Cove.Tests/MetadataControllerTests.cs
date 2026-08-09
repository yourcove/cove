using Cove.Api.Controllers;

namespace Cove.Tests;

public class MetadataControllerTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("imported-cover", false)]
    public void ShouldGenerateDefaultVideoThumbnail_SkipsExplicitBlobCovers(
        string? imageBlobId,
        bool expected)
    {
        Assert.Equal(
            expected,
            MetadataController.ShouldGenerateDefaultVideoThumbnail(
                requested: true,
                imageBlobId));
    }

    [Fact]
    public void ShouldGenerateDefaultVideoThumbnail_RequiresThumbnailRequest()
    {
        Assert.False(MetadataController.ShouldGenerateDefaultVideoThumbnail(
            requested: false,
            imageBlobId: null));
    }
}
