using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.EntityImages;

public sealed class SourceEntityImageApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("PUT", "/api/performers/{id:int}/image/source")]
    [CoversEndpoint("PUT", "/api/studios/{id:int}/image/source")]
    [CoversEndpoint("PUT", "/api/tags/{id:int}/image/source")]
    [CoversEndpoint("PUT", "/api/groups/{id:int}/image/front/source")]
    [CoversEndpoint("PUT", "/api/galleries/{id:int}/image/source")]
    [CoversEndpoint("PUT", "/api/galleries/{id:int}/image/back/source")]
    public async Task GivenCustomVideoCover_WhenMembersCopyItToEntitySlots_ThenCopiesSurviveSourceReplacementAndDeletion()
    {
        var sourceVideo = await AsUser().CreateVideoAsync($"Image source {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var performer = await AsUser().CreatePerformerAsync(new PerformerBuilder().Build(), TestContext.Current.CancellationToken);
        var studio = await AsUser().CreateStudioAsync($"Image source studio {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var tag = await AsUser().CreateTagAsync($"Image source tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var group = await AsUser().CreateGroupAsync($"Image source group {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var gallery = await AsUser().CreateGalleryAsync(NewGallery($"Image source gallery {Guid.NewGuid():N}"), TestContext.Current.CancellationToken);
        var copiedImage = ApiTestImages.RedPixelPng();

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(sourceVideo, copiedImage, cancellationToken: TestContext.Current.CancellationToken);
        var source = new EntityImageCoverSourceDto(VideoId: sourceVideo.Id);
        await AsUser(ApiTestUsers.Eva).SetPerformerImageFromSourceAsync(performer, source, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetStudioImageFromSourceAsync(studio, source, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetTagImageFromSourceAsync(tag, source, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetGroupFrontImageFromSourceAsync(group, source, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetGalleryImageFromSourceAsync(gallery, source, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetGalleryBackImageFromSourceAsync(gallery, source, TestContext.Current.CancellationToken);
        await AssertCopiedImages(performer, studio, tag, group, gallery, copiedImage);

        var emptySource = () => AsUser(ApiTestUsers.Eva).SetPerformerImageFromSourceAsync(performer, new EntityImageCoverSourceDto());
        var ambiguousSource = () => AsUser(ApiTestUsers.Eva).SetPerformerImageFromSourceAsync(performer, new EntityImageCoverSourceDto(ImageId: 1, VideoId: sourceVideo.Id));
        await emptySource.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        await ambiguousSource.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetPerformerImageAsync(performer, TestContext.Current.CancellationToken)).ShouldMatch(copiedImage);

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(sourceVideo, ApiTestImages.BluePixelPng(), cancellationToken: TestContext.Current.CancellationToken);
        await AssertCopiedImages(performer, studio, tag, group, gallery, copiedImage);
        await AsUser(ApiTestUsers.Eva).DeleteVideoImageAsync(sourceVideo, TestContext.Current.CancellationToken);
        await AssertCopiedImages(performer, studio, tag, group, gallery, copiedImage);
    }

    [Fact]
    [CoversEndpoint("POST", "/api/segments/{id:int}/image/from-frame")]
    public async Task GivenVideoBackedSegment_WhenFrameCoverUsesACustomVideoImage_ThenTheFallbackIsPersisted()
    {
        var video = await AsUser().CreateVideoAsync($"Frame source {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var segment = await AsUser().CreateVideoSegmentAsync(video, "Fallback segment", TestContext.Current.CancellationToken);
        var image = ApiTestImages.RedPixelPng();

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(video, image, cancellationToken: TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetSegmentImageFromFrameAsync(segment, cancellationToken: TestContext.Current.CancellationToken);
        (await AsUser().GetSegmentImageAsync(segment, cancellationToken: TestContext.Current.CancellationToken)).ShouldMatch(image);

        await AsUser(ApiTestUsers.Eva).UploadVideoImageAsync(video, ApiTestImages.BluePixelPng(), cancellationToken: TestContext.Current.CancellationToken);
        (await AsUser().GetSegmentImageAsync(segment, cancellationToken: TestContext.Current.CancellationToken)).ShouldMatch(image);
        await AsUser(ApiTestUsers.Eva).DeleteVideoImageAsync(video, TestContext.Current.CancellationToken);
        (await AsUser().GetSegmentImageAsync(segment, cancellationToken: TestContext.Current.CancellationToken)).ShouldMatch(image);
    }

    [Fact]
    [CoversEndpoint("GET", "/api/galleries/{id:int}/cover")]
    [CoversEndpoint("PUT", "/api/galleries/{id:int}/cover")]
    [CoversEndpoint("DELETE", "/api/galleries/{id:int}/cover")]
    public async Task GivenGalleryImageSources_WhenSourceAndCoverAreSelected_ThenReferencesAndFallbackReadsStayCurrent()
    {
        var gallery = await AsUser().CreateGalleryAsync(NewGallery($"Image reference gallery {Guid.NewGuid():N}"), TestContext.Current.CancellationToken);
        var fallbackPath = Path.Combine(AsTestFileSystem().LibraryPath, $"gallery-fallback-{Guid.NewGuid():N}.png");
        var selectedPath = Path.Combine(AsTestFileSystem().LibraryPath, $"gallery-selected-{Guid.NewGuid():N}.png");
        var copySourcePath = Path.Combine(AsTestFileSystem().LibraryPath, $"entity-copy-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(fallbackPath, ApiTestImages.RedPixelPng());
        File.WriteAllBytes(selectedPath, ApiTestImages.BluePixelPng());
        File.WriteAllBytes(copySourcePath, ApiTestImages.OnePixelPng());
        foreach (var path in new[] { fallbackPath, selectedPath, copySourcePath })
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        var scanJob = await AsUser().StartMetadataScanAsync(new ScanOptionsDto { Paths = [fallbackPath, selectedPath, copySourcePath] }, TestContext.Current.CancellationToken);
        (await AsUser().WaitForTerminalJobAsync(scanJob, TestContext.Current.CancellationToken)).Status.Should().Be(JobStatus.Completed);
        var scannedImages = await AsUser().GetImagesAsync(TestContext.Current.CancellationToken);
        var galleryImages = new[]
        {
            FindImageByPath(scannedImages, fallbackPath),
            FindImageByPath(scannedImages, selectedPath),
        }.OrderBy(image => image.Id).ToArray();
        var fallbackImage = galleryImages[0];
        var selectedImage = galleryImages[1];
        var copySource = FindImageByPath(scannedImages, copySourcePath);
        var unrelatedImage = await AsUser().CreateImageAsync($"Unrelated source {Guid.NewGuid():N}", TestContext.Current.CancellationToken);
        var copiedTag = await AsUser().CreateTagAsync($"Copied image tag {Guid.NewGuid():N}", TestContext.Current.CancellationToken);

        await AsUser(ApiTestUsers.Eva).SetTagImageFromSourceAsync(copiedTag, new EntityImageCoverSourceDto(ImageId: copySource.Id), TestContext.Current.CancellationToken);
        (await AsUser().GetTagImageAsync(copiedTag, TestContext.Current.CancellationToken)).ShouldMatch(ApiTestImages.OnePixelPng());
        File.Delete(copySourcePath);
        (await AsUser().GetTagImageAsync(copiedTag, TestContext.Current.CancellationToken)).ShouldMatch(ApiTestImages.OnePixelPng());

        await AsUser(ApiTestUsers.Eva).AddGalleryImagesAsync(gallery, galleryImages, TestContext.Current.CancellationToken);
        await AsUser(ApiTestUsers.Eva).SetGalleryImageFromSourceAsync(gallery, new EntityImageCoverSourceDto(ImageId: selectedImage.Id), TestContext.Current.CancellationToken);
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).CoverImageId.Should().Be(selectedImage.Id);
        (await AsUser().GetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken)).ShouldRedirectTo(selectedImage);

        var unrelatedSource = () => AsUser(ApiTestUsers.Eva).SetGalleryImageFromSourceAsync(gallery, new EntityImageCoverSourceDto(ImageId: unrelatedImage.Id));
        await unrelatedSource.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).CoverImageId.Should().Be(selectedImage.Id);

        await AsUser(ApiTestUsers.Eva).ResetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken);
        (await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken)).CoverImageId.Should().BeNull();
        (await AsUser().GetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken)).ShouldRedirectTo(fallbackImage);

        await AsUser(ApiTestUsers.Eva).SetGalleryCoverAsync(gallery, selectedImage, TestContext.Current.CancellationToken);
        var selected = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        selected.CoverImageId.Should().Be(selectedImage.Id);
        selected.CoverPath.Should().Contain($"/api/galleries/{gallery.Id}/cover");
        (await AsUser().GetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken)).ShouldRedirectTo(selectedImage);

        await AsUser(ApiTestUsers.Eva).ResetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken);
        var reset = await AsUser().GetGalleryByIdAsync(gallery.Id, TestContext.Current.CancellationToken);
        reset.CoverImageId.Should().BeNull();
        (await AsUser().GetGalleryCoverAsync(gallery, TestContext.Current.CancellationToken)).ShouldRedirectTo(fallbackImage);
    }

    private async Task AssertCopiedImages(PerformerDto performer, StudioDto studio, TagDetailDto tag, GroupDto group, GalleryDto gallery, byte[] expected)
    {
        (await AsUser().GetPerformerImageAsync(performer)).ShouldMatch(expected);
        (await AsUser().GetStudioImageAsync(studio)).ShouldMatch(expected);
        (await AsUser().GetTagImageAsync(tag)).ShouldMatch(expected);
        (await AsUser().GetGroupFrontImageAsync(group)).ShouldMatch(expected);
        (await AsUser().GetGalleryImageAsync(gallery)).ShouldMatch(expected);
        (await AsUser().GetGalleryBackImageAsync(gallery)).ShouldMatch(expected);
    }

    private static GalleryCreateDto NewGallery(string title)
        => new(title, null, null, null, null, null, false, null, [], [], [], []);

    private static ImageDto FindImageByPath(IEnumerable<ImageDto> images, string path)
        => images.Single(candidate => candidate.Files.Any(file => Path.GetFullPath(file.Path) == Path.GetFullPath(path)));
}

internal static class RedirectedEntityImageAssertions
{
    public static void ShouldRedirectTo(this ApiBinaryContent actual, ImageDto image)
    {
        actual.MediaType.Should().Be("image/png");
        actual.Content.Should().NotBeEmpty();
        actual.RedirectTarget.Should().NotBeNull();
        actual.RedirectTarget!.AbsolutePath.Should().Be($"/api/stream/image/{image.Id}/thumbnail");
    }
}
