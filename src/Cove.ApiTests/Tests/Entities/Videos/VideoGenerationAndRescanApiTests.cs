using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;
using SixLabors.ImageSharp;
using Xunit.Abstractions;

namespace Cove.ApiTests.Tests.Entities.Videos;

[Collection(ApiTestLane2Collection.Name)]
public sealed class VideoGenerationAndRescanApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    public async Task GivenDecodableVideo_WhenFramesAndRescanAreRequested_ThenGeneratedCoverAndFileMetricsPersist()
    {
        var ffmpegCapabilities = await AsUser().GetFfmpegCapabilitiesAsync();
        ffmpegCapabilities.FfmpegFound.Should().BeTrue();
        ffmpegCapabilities.FfmpegPath.Should().NotBeNullOrWhiteSpace();
        var ffmpegPath = ffmpegCapabilities.FfmpegPath!;
        var fileName = $"generated-video-{Guid.NewGuid():N}.mp4";
        var sourcePath = await AsTestFileSystem().CreateSyntheticVideoAsync(
            ffmpegPath,
            fileName,
            width: 160,
            height: 120,
            durationSeconds: 2,
            color: "red");
        var video = await AsUser(ApiTestUsers.Eva).CreateVideoFromFileAsync(sourcePath);
        var originalFile = video.Files.Should().ContainSingle().Which;
        originalFile.Width.Should().Be(160);
        originalFile.Height.Should().Be(120);
        originalFile.Duration.Should().BeApproximately(2, 0.1);
        var metadataOnly = await AsUser().CreateVideoAsync($"Screenshot no-file {Guid.NewGuid():N}");
        var missingId = int.MaxValue - video.Id;

        var viewerUsername = $"video-generation-viewer-{Guid.NewGuid():N}";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            viewerUsername,
            ApiTestUsers.Password,
            Roles: [BuiltinRoles.Viewer]));
        using var viewerSession = await AsUser().CreateAuthSessionAsync(
            viewerUsername,
            ApiTestUsers.Password);
        var historyBeforeForbidden = (await AsUser().GetJobHistoryAsync()).Select(job => job.Id).ToArray();
        var forbiddenScreenshot = () => viewerSession.Client.GenerateVideoScreenshotAsync(video.Id, 0.5);
        var forbiddenCover = () => viewerSession.Client.SetVideoCoverFromFrameAsync(video.Id, 0.5);
        var forbiddenRescan = () => viewerSession.Client.RescanVideoAsync(video.Id);
        await forbiddenScreenshot.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await forbiddenCover.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await forbiddenRescan.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        (await AsUser().GetJobHistoryAsync()).Select(job => job.Id).Should().Equal(historyBeforeForbidden);
        (await AsUser().GetVideoByIdAsync(video.Id)).ImagePath.Should().BeNull();

        var missingScreenshot = () => AsUser().GenerateVideoScreenshotAsync(missingId, 0.5);
        var missingCover = () => AsUser().SetVideoCoverFromFrameAsync(missingId, 0.5);
        var missingRescan = () => AsUser().RescanVideoAsync(missingId);
        var noFileRescan = () => AsUser().RescanVideoAsync(metadataOnly.Id);
        await missingScreenshot.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await missingCover.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await missingRescan.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await noFileRescan.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 400 (BadRequest)*");
        (await AsUser().GetJobHistoryAsync()).Select(job => job.Id).Should().Equal(historyBeforeForbidden);

        (await AsUser(ApiTestUsers.Eva).GenerateVideoScreenshotAsync(video.Id, 0.5)).Success
            .Should().BeTrue();
        var generated = await AsUser().GetGeneratedVideoScreenshotAsync(video.Id, 0.5);
        generated.MediaType.Should().Be("image/jpeg");
        generated.CacheControl.Should().Be("public, max-age=86400");
        generated.Bytes.Should().NotBeEmpty();
        using (var frame = Image.Load(generated.Bytes))
        {
            frame.Width.Should().Be(160);
            frame.Height.Should().Be(120);
        }

        (await AsUser(ApiTestUsers.Eva).SetVideoCoverFromFrameAsync(video.Id, 0.5)).Success
            .Should().BeTrue();
        var covered = await AsUser().GetVideoByIdAsync(video.Id);
        covered.ImagePath.Should().StartWith($"/api/videos/{video.Id}/image?max=1280&v=");
        var cover = await AsUser().GetGeneratedVideoScreenshotAsync(video.Id, seconds: null);
        var timestampedAfterCover = await AsUser().GetGeneratedVideoScreenshotAsync(video.Id, 0.5);
        cover.MediaType.Should().Be("image/jpeg");
        cover.CacheControl.Should().Contain("no-cache");
        cover.Bytes.Should().Equal(timestampedAfterCover.Bytes);

        var originalSize = new FileInfo(sourcePath).Length;
        await AsTestFileSystem().CreateSyntheticVideoAsync(
            ffmpegPath,
            fileName,
            width: 320,
            height: 180,
            durationSeconds: 3,
            color: "blue");
        new FileInfo(sourcePath).Length.Should().NotBe(originalSize);
        var jobId = await AsUser(ApiTestUsers.Eva).RescanVideoAsync(video.Id);
        var completed = await AsUser().WaitForTerminalJobAsync(jobId);
        completed.Id.Should().Be(jobId);
        completed.Type.Should().Be("scan");
        completed.Status.Should().Be(JobStatus.Completed);
        completed.Error.Should().BeNull();

        var rescanned = await AsUser().GetVideoByIdAsync(video.Id);
        var rescannedFile = rescanned.Files.Should().ContainSingle().Which;
        rescannedFile.Id.Should().Be(originalFile.Id);
        rescannedFile.Path.Should().Be(sourcePath);
        rescannedFile.Width.Should().Be(320);
        rescannedFile.Height.Should().Be(180);
        rescannedFile.Duration.Should().BeApproximately(3, 0.1);
        rescannedFile.Size.Should().Be(new FileInfo(sourcePath).Length).And.NotBe(originalFile.Size);
        rescanned.ImagePath.Should().Be(covered.ImagePath);
    }
}
