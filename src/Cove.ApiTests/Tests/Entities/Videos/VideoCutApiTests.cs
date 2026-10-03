using System.Diagnostics;
using System.Net;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities.Auth;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Tests.Entities.Videos;

/// <summary>
/// Every test cuts the same shape out of a 12-second video with a keyframe each second: 4.5–7.2 s.
/// A lossless cut keeps 0–4.5 and 7–12 (the second part moves back to its keyframe), leaving 9.5 s;
/// an exact cut keeps 0–4.5 and 7.2–12, leaving 9.3 s.
/// </summary>
public sealed class VideoCutApiTests(
    ITestOutputHelper output,
    CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    private static readonly TimeRangeDto Removed = new(4.5, 7.2);

    [Fact]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/cut/preview")]
    public async Task GivenTimedItems_WhenACutIsPreviewed_ThenItSaysWhereTheCutLandsAndWhatHappensToEachItem()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await CreateKeyframedVideoAsync("preview", ct);
        var before = await owner.CreateVideoSegmentAsync(video, Segment(1, 3, "before"), ct);
        var after = await owner.CreateVideoSegmentAsync(video, Segment(8, 10, "after"), ct);
        var inside = await owner.CreateVideoSegmentAsync(video, Segment(5, 6, "inside"), ct);
        var spanning = await owner.CreateVideoSegmentAsync(video, Segment(3, 9, "spanning"), ct);
        var clip = await owner.CreateVideoAsync(new VideoBuilder().WithTitle("Clip to the end").Build() with
        { ParentVideoId = video.Id, ClipStartSec = 10, ClipEndSec = null }, ct);

        var lossless = await owner.PreviewVideoCutAsync(video.Id, new VideoCutPreviewRequestDto { Remove = [Removed] }, ct);

        lossless.FileId.Should().Be(video.PrimaryFileId);
        lossless.Exact.Should().BeFalse();
        lossless.SourceDuration.Should().BeApproximately(12, 0.05);
        lossless.Kept.Should().HaveCount(2);
        lossless.Kept[0].Start.Should().Be(0);
        lossless.Kept[0].End.Should().BeApproximately(4.5, 0.001);
        lossless.Kept[1].Start.Should().BeApproximately(7, 0.001, "the kept part starts on the keyframe at or before 7.2");
        lossless.OutputDuration.Should().BeApproximately(9.5, 0.05);
        lossless.RemovedSeconds.Should().BeApproximately(2.5, 0.05);
        lossless.EstimatedBytes.Should().BeLessThan(lossless.SourceBytes);

        Item(lossless, "segment", before.Id).Should().Match<VideoCutItemPreview>(item => item.Outcome == "kept" && item.NewStart == 1 && item.NewEnd == 3);
        Item(lossless, "segment", inside.Id).Should().Match<VideoCutItemPreview>(item => item.Outcome == "removed" && item.NewStart == null && item.NewEnd == null);
        var movedAfter = Item(lossless, "segment", after.Id);
        movedAfter.Outcome.Should().Be("moved");
        movedAfter.NewStart.Should().BeApproximately(5.5, 0.001);
        movedAfter.NewEnd.Should().BeApproximately(7.5, 0.001);
        var joined = Item(lossless, "segment", spanning.Id);
        joined.Outcome.Should().Be("joined");
        joined.NewStart.Should().BeApproximately(3, 0.001);
        joined.NewEnd.Should().BeApproximately(6.5, 0.001);
        var movedClip = Item(lossless, "clip", clip.Id);
        movedClip.Outcome.Should().Be("moved");
        movedClip.NewStart.Should().BeApproximately(7.5, 0.001);
        movedClip.NewEnd.Should().BeApproximately(9.5, 0.05, "a clip that ran to the end of the video still does after the cut");

        var exact = await owner.PreviewVideoCutAsync(video.Id, new VideoCutPreviewRequestDto { Remove = [Removed], Exact = true }, ct);
        exact.Kept[1].Start.Should().BeApproximately(7.2, 0.001);
        exact.OutputDuration.Should().BeApproximately(9.3, 0.05);
        Item(exact, "segment", after.Id).NewStart.Should().BeApproximately(5.3, 0.001);

        await owner.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{video.Id}/cut/preview", HttpStatusCode.BadRequest,
            new VideoCutPreviewRequestDto { Remove = [new TimeRangeDto(0, 12)] }, ct);
        await owner.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{video.Id}/cut/preview", HttpStatusCode.BadRequest,
            new VideoCutPreviewRequestDto { Remove = [] }, ct);
        await AsAnonymous().AssertResponseAsync(HttpMethod.Post, $"/api/videos/{video.Id}/cut/preview", HttpStatusCode.Unauthorized,
            new VideoCutPreviewRequestDto { Remove = [Removed] }, ct);
    }

    /// <summary>
    /// A lossless cut that replaces the original: the cut file becomes the video's only file, and every
    /// timed item on the video lands where the preview said it would.
    /// </summary>
    [Fact]
    [CoversEndpoint("POST", "/api/videos/cut")]
    public async Task GivenTimedItems_WhenALosslessCutReplacesTheOriginal_ThenTheItemsMoveWithTheCut()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var video = await CreateKeyframedVideoAsync("replace", ct);
        var original = video.Files.Should().ContainSingle().Which;
        var after = await owner.CreateVideoSegmentAsync(video, Segment(8, 10, "after"), ct);
        var inside = await owner.CreateVideoSegmentAsync(video, Segment(5, 6, "inside"), ct);
        var spanning = await owner.CreateVideoSegmentAsync(video, Segment(3, 9, "spanning"), ct);
        var clip = await owner.CreateVideoAsync(new VideoBuilder().WithTitle("Clip to the end").Build() with
        { ParentVideoId = video.Id, ClipStartSec = 10, ClipEndSec = null }, ct);

        var started = await owner.StartVideoCutAsync(new VideoCutRequestDto
        {
            Videos = [new VideoCutItemDto { VideoId = video.Id, FileId = original.Id, Remove = [Removed] }],
            ReplaceOriginal = true,
        }, ct);
        started.ItemCount.Should().Be(1);
        var job = await owner.WaitForTerminalJobAsync(started.JobId, ct);

        job.Status.Should().Be(JobStatus.Completed, job.Error ?? job.Summary);
        job.UnitsSucceeded.Should().Be(1, job.Summary);
        var cut = await owner.GetVideoByIdAsync(video.Id, ct);
        var file = cut.Files.Should().ContainSingle().Which;
        file.Id.Should().NotBe(original.Id);
        cut.PrimaryFileId.Should().Be(file.Id);
        file.Duration.Should().BeApproximately(9.5, 0.2);
        file.VideoCodec.Should().Be(original.VideoCodec, "a lossless cut copies the video stream");

        var movedAfter = await owner.GetVideoSegmentAsync(cut, after.Id, ct);
        movedAfter.StartSec.Should().BeApproximately(5.5, 0.001);
        movedAfter.EndSec.Should().BeApproximately(7.5, 0.001);
        var joined = await owner.GetVideoSegmentAsync(cut, spanning.Id, ct);
        joined.StartSec.Should().BeApproximately(3, 0.001);
        joined.EndSec.Should().BeApproximately(6.5, 0.001);
        var removed = () => owner.GetVideoSegmentAsync(cut, inside.Id, ct);
        await removed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404*");
        var movedClip = await owner.GetVideoByIdAsync(clip.Id, ct);
        movedClip.ClipStartSec.Should().BeApproximately(7.5, 0.001);
        movedClip.ClipEndSec.Should().BeApproximately(9.5, 0.05);
    }

    /// <summary>
    /// Cutting while re-encoding lands exactly on the marks. It measures quality on samples of the kept
    /// footage first, which needs ffmpeg's libvmaf; without it the job must fail and say so.
    /// </summary>
    [Fact]
    [CoversEndpoint("POST", "/api/videos/cut")]
    public async Task GivenAVideo_WhenItIsCutWhileReencoding_ThenTheCutIsExactAndTheOriginalIsKept()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var capabilities = await owner.GetFfmpegCapabilitiesAsync(ct);
        var canMeasure = FfmpegHwAccel.HasQualityMeasurement(capabilities.FfmpegPath!);
        var video = await CreateKeyframedVideoAsync("exact", ct);
        var original = video.Files.Should().ContainSingle().Which;
        var segment = await owner.CreateVideoSegmentAsync(video, Segment(8, 10, "after"), ct);

        var job = await owner.WaitForTerminalJobAsync((await owner.StartVideoCutAsync(new VideoCutRequestDto
        {
            Videos = [new VideoCutItemDto { VideoId = video.Id, FileId = original.Id, Remove = [Removed] }],
            Codec = "h264",
            Effort = "balancedSoftware",
        }, ct)).JobId, ct);
        var files = (await owner.GetVideoByIdAsync(video.Id, ct)).Files;

        if (canMeasure)
        {
            job.Status.Should().Be(JobStatus.Completed, job.Error ?? job.Summary);
            job.UnitsSucceeded.Should().Be(1, job.Summary);
            var cut = files.Should().HaveCount(2).And.ContainSingle(file => file.Id != original.Id).Which;
            cut.Basename.Should().Contain("trimmed");
            cut.Duration.Should().BeApproximately(9.3, 0.2);
            (await owner.GetVideoByIdAsync(video.Id, ct)).PrimaryFileId.Should().Be(original.Id);
            (await owner.GetVideoSegmentAsync(video, segment.Id, ct)).StartSec.Should().Be(8,
                "items follow the primary file, which is still the original");
        }
        else
        {
            job.Status.Should().Be(JobStatus.Failed);
            job.Error.Should().Contain("libvmaf");
            files.Should().ContainSingle();
        }
    }

    [Fact]
    [CoversEndpoint("POST", "/api/videos/cut")]
    public async Task GivenAnInvalidOrUnauthorisedCut_WhenItIsStarted_ThenItIsRefusedAndNothingIsQueued()
    {
        var ct = TestContext.Current.CancellationToken;
        var video = await CreateKeyframedVideoAsync("refused", ct);
        var fileId = video.PrimaryFileId!.Value;
        var historyBefore = await JobIdsAsync(ct);

        VideoCutRequestDto Request(Action<VideoCutRequestDto>? change = null, params VideoCutItemDto[] videos)
        {
            var request = new VideoCutRequestDto
            {
                Videos = videos.Length > 0 ? [.. videos] : [new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [Removed] }],
            };
            change?.Invoke(request);
            return request;
        }

        using var viewer = await CreateViewerSessionAsync(ct);
        var viewerStart = () => viewer.Client.StartVideoCutAsync(Request(), ct);
        await viewerStart.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 403 (Forbidden)*");

        var badRequests = new[]
        {
            Request(request => request.Videos = []),
            Request(request => request.Codec = "vp8"),
            Request(request => request.Container = "avi"),
            Request(request => request.OutputFrameRate = 30),
            Request(null,
                new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [new TimeRangeDto(1, 2)] },
                new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [new TimeRangeDto(3, 4)] }),
            Request(null, new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [] }),
            Request(null, new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [new TimeRangeDto(0, 30)] }),
            Request(null, new VideoCutItemDto { VideoId = video.Id, FileId = fileId, Remove = [new TimeRangeDto(5, 3)] }),
        };
        foreach (var request in badRequests)
        {
            var start = () => AsUser().StartVideoCutAsync(request, ct);
            await start.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 400 (BadRequest)*");
        }

        var stale = () => AsUser().StartVideoCutAsync(
            Request(null, new VideoCutItemDto { VideoId = video.Id, FileId = fileId + 100_000, Remove = [Removed] }), ct);
        await stale.Should().ThrowAsync<InvalidOperationException>().WithMessage("*returned 409 (Conflict)*");

        (await JobIdsAsync(ct)).Should().Equal(historyBefore);
        (await AsUser().GetVideoByIdAsync(video.Id, ct)).Files.Should().ContainSingle();
    }

    private static VideoCutItemPreview Item(VideoCutPreview preview, string kind, int id)
        => preview.Items.Should().ContainSingle(item => item.Kind == kind && item.Id == id).Which;

    private static SegmentCreateDto Segment(double start, double end, string title) => new(
        StartSec: start,
        EndSec: end,
        TagId: null,
        Kind: "chapter",
        RefId: null,
        Payload: null,
        SourceKey: "api-test",
        SourceRunId: null,
        Confidence: null,
        Title: title,
        ColorHint: null);

    /// <summary>A 12-second 10 fps test pattern with a keyframe every second, so lossless cuts land predictably.</summary>
    private async Task<VideoDto> CreateKeyframedVideoAsync(string label, CancellationToken ct)
    {
        var capabilities = await AsUser().GetFfmpegCapabilitiesAsync(ct);
        capabilities.FfmpegFound.Should().BeTrue();
        var ffmpeg = capabilities.FfmpegPath!;
        var path = Path.Combine(AsTestFileSystem().LibraryPath, $"cut-{label}-{Guid.NewGuid():N}.mp4");
        var startInfo = new ProcessStartInfo { FileName = ffmpeg, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsLinux())
        {
            var ffmpegDirectory = Path.GetDirectoryName(ffmpeg)!;
            startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var inherited);
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(inherited) ? ffmpegDirectory : $"{ffmpegDirectory}{Path.PathSeparator}{inherited}";
        }
        foreach (var argument in new[]
        {
            "-nostdin", "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=10:duration=12",
            "-threads", "1", "-c:v", "libx264", "-preset", "ultrafast",
            "-g", "10", "-keyint_min", "10", "-sc_threshold", "0",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", path,
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg could not be started."))
        {
            var error = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            if (process.ExitCode != 0)
                throw new InvalidOperationException(await error);
        }
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return await AsUser().CreateVideoFromFileAsync(path, ct);
    }

    private async Task<string[]> JobIdsAsync(CancellationToken ct)
        => [.. (await AsUser().GetJobHistoryAsync(ct)).Select(job => job.Id)
            .Concat((await AsUser().GetJobsAsync(ct)).Select(job => job.Id))
            .Distinct()
            .Order(StringComparer.Ordinal)];

    private async Task<CoveAuthSession> CreateViewerSessionAsync(CancellationToken ct)
    {
        var username = $"cut-viewer-{Guid.NewGuid():N}";
        await AsUser().CreateUserAsync(new CreateUserRequest(username, ApiTestUsers.Password, Roles: [BuiltinRoles.Viewer]), ct);
        return await AsUser().CreateAuthSessionAsync(username, ApiTestUsers.Password, ct);
    }
}
