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
    [CoversEndpoint("POST", "/api/videos/{id:int}/generate-screenshot")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/cover/from-frame")]
    [CoversEndpoint("POST", "/api/videos/{id:int}/rescan")]
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

    [Fact]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/transcode")]
    [CoversEndpoint("GET", "/api/stream/video/{videoid:int}/hls/{profile}.m3u8")]
    public async Task GivenDecodableVideo_WhenLiveTranscodeAndHlsAreRead_ThenEncodedMediaAndSegmentsAreReturned()
    {
        var ffmpegCapabilities = await AsUser().GetFfmpegCapabilitiesAsync();
        ffmpegCapabilities.FfmpegFound.Should().BeTrue();
        ffmpegCapabilities.FfmpegPath.Should().NotBeNullOrWhiteSpace();
        var sourcePath = await AsTestFileSystem().CreateSyntheticVideoAsync(
            ffmpegCapabilities.FfmpegPath!,
            $"stream-transcode-{Guid.NewGuid():N}.mp4",
            width: 160,
            height: 120,
            durationSeconds: 2,
            color: "green");
        var video = await AsUser(ApiTestUsers.Eva).CreateVideoFromFileAsync(sourcePath);
        var metadataOnly = await AsUser().CreateVideoAsync($"Transcode no-file {Guid.NewGuid():N}");
        var missingId = int.MaxValue - video.Id;

        var noRoleUsername = $"stream-transcode-no-role-{Guid.NewGuid():N}";
        await AsUser().CreateUserAsync(new CreateUserRequest(
            noRoleUsername,
            ApiTestUsers.Password,
            Roles: []));
        using var noRoleSession = await AsUser().CreateAuthSessionAsync(
            noRoleUsername,
            ApiTestUsers.Password);
        var forbiddenTranscode = () => noRoleSession.Client.TranscodeVideoAsync(video.Id, resolution: null, start: null);
        var forbiddenHls = () => noRoleSession.Client.GetHlsProfileAsync(video.Id, "original", propagateAccessToken: false);
        await forbiddenTranscode.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");
        await forbiddenHls.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 403 (Forbidden)*");

        var missingTranscode = () => AsUser().TranscodeVideoAsync(missingId, resolution: null, start: null);
        var noFileHls = () => AsUser().GetHlsProfileAsync(metadataOnly.Id, "original", propagateAccessToken: false);
        await missingTranscode.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");
        await noFileHls.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*returned 404 (NotFound)*");

        var transcoded = await AsUser(ApiTestUsers.Eva).TranscodeVideoAsync(
            video.Id,
            resolution: "240p",
            start: 0.25);
        transcoded.MediaType.Should().Be("video/mp4");
        transcoded.CacheControl.Should().BeNull();
        transcoded.AcceptRanges.Should().Equal("none");
        transcoded.Bytes.Should().HaveCountGreaterThan(1_024);
        global::System.Text.Encoding.ASCII.GetString(transcoded.Bytes, 4, 4).Should().Be("ftyp");
        ContainsAscii(transcoded.Bytes, "moov").Should().BeTrue();
        ContainsAscii(transcoded.Bytes, "moof").Should().BeTrue();
        ContainsAscii(transcoded.Bytes, "mdat").Should().BeTrue();

        var hls = await AsUser(ApiTestUsers.Eva).GetHlsProfileAsync(
            video.Id,
            "original",
            propagateAccessToken: true);
        hls.MediaType.Should().Be("application/vnd.apple.mpegurl");
        hls.CacheControl.Should().Be("no-cache");
        hls.Text.Contains(Uri.EscapeDataString(AsUser(ApiTestUsers.Eva).AccessToken), StringComparison.Ordinal)
            .Should().BeTrue();
        var sanitizedPlaylist = hls.Text.Replace(
            Uri.EscapeDataString(AsUser(ApiTestUsers.Eva).AccessToken),
            "<access-token>");
        sanitizedPlaylist.Should().StartWith("#EXTM3U\n");
        sanitizedPlaylist.Should().Contain("#EXT-X-ENDLIST");
        sanitizedPlaylist.Should().NotContain("ignored=");
        var segmentUrl = sanitizedPlaylist
            .Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith($"/api/stream/video/{video.Id}/hls/segment/", StringComparison.Ordinal));
        segmentUrl.Should().EndWith("?access_token=<access-token>");
        var segmentName = segmentUrl.Split('?', 2)[0].Split('/').Last();
        segmentName.Should().Be("original_0000.ts");

        var segment = await AsUser().GetHlsSegmentAsync(video.Id, segmentName);
        segment.MediaType.Should().Be("video/mp2t");
        segment.CacheControl.Should().Be("public, max-age=86400");
        segment.Bytes.Should().NotBeEmpty();
        segment.Bytes[0].Should().Be(0x47);
    }

    private static bool ContainsAscii(byte[] bytes, string value)
        => bytes.AsSpan().IndexOf(global::System.Text.Encoding.ASCII.GetBytes(value)) >= 0;
}
