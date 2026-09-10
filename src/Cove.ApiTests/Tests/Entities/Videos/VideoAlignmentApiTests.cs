using System.Diagnostics;
using System.Globalization;
using System.Net;
using Cove.Api.Controllers;
using Cove.ApiTests.Builders;
using Cove.ApiTests.Infrastructure;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Tests.Entities.Videos;

public sealed class VideoAlignmentApiTests(ITestOutputHelper output, CoveApiTestFixture fixture) : ApiTest(output, fixture)
{
    [Fact]
    [CoversEndpoint("GET", "/api/videos/{videoId:int}/alignments")]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/alignments/assess")]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/alignments/analyze")]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/alignments/preview")]
    [CoversEndpoint("POST", "/api/videos/{videoId:int}/alignments/apply")]
    public async Task GivenEquivalentFiles_WhenPrimaryChanges_ThenNoAlignmentStateIsPersisted()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = AsUser();
        var ffmpeg = (await owner.GetFfmpegCapabilitiesAsync(ct)).FfmpegPath!;
        var sourcePath = await CreateTestPatternVideoAsync(ffmpeg, AsTestFileSystem().LibraryPath, "primary-source.mp4", 18, ct);
        var targetPath = await CreateTestPatternVideoAsync(ffmpeg, AsTestFileSystem().LibraryPath, "primary-target.mp4", 32, ct);
        var source = await owner.CreateVideoFromFileAsync(sourcePath, ct);
        var target = await owner.CreateVideoFromFileAsync(targetPath, ct);
        await owner.MergeVideosAsync(source, ct, target);
        var merged = await owner.GetVideoByIdAsync(source.Id, ct);
        var sourceFileId = merged.PrimaryFileId!.Value;
        var targetFileId = merged.Files.Single(file => file.Id != sourceFileId).Id;
        var child = await owner.CreateVideoAsync(new VideoBuilder().WithTitle("Timed child").Build() with
        { ParentVideoId = source.Id, ClipStartSec = 2, ClipEndSec = 5 }, ct);

        var assessment = await owner.AssessVideoAlignmentAsync(source.Id, targetFileId, ct);
        assessment.GetProperty("equivalent").GetBoolean().Should().BeTrue();
        assessment.GetProperty("dependencyCount").GetInt32().Should().Be(1);
        var analysis = await owner.AnalyzeVideoAlignmentAsync(source.Id, new AnalyzeVideoAlignment(sourceFileId, targetFileId), ct);
        analysis.TryGetProperty("source", out _).Should().BeFalse();
        analysis.TryGetProperty("target", out _).Should().BeFalse();
        var anchors = analysis.GetProperty("anchors").EnumerateArray()
            .Select(anchor => new Cove.Core.Services.AlignmentAnchor(anchor.GetProperty("sourceSec").GetDouble(), anchor.GetProperty("targetSec").GetDouble(), anchor.GetProperty("section").GetInt32()))
            .ToList();
        anchors.Should().HaveCountGreaterThanOrEqualTo(2);
        var sourceDuration = merged.Files.Single(file => file.Id == sourceFileId).Duration;
        var targetDuration = merged.Files.Single(file => file.Id == targetFileId).Duration;
        var preview = await owner.PreviewVideoAlignmentAsync(source.Id, new ReviewVideoAlignment(sourceFileId, targetFileId, sourceDuration, targetDuration, anchors), ct);
        var comparison = preview.GetProperty("comparisons").EnumerateArray().Should().ContainSingle().Which;
        comparison.GetProperty("kind").GetString().Should().Be("clip");
        comparison.GetProperty("sourceThumbnail").GetString().Should().NotBeNullOrWhiteSpace();
        comparison.GetProperty("targetThumbnail").GetString().Should().NotBeNullOrWhiteSpace();
        await owner.ApplyVideoAlignmentAsync(source.Id, new VideoSetPrimaryFileDto(targetFileId, ExpectedPrimaryFileId: sourceFileId), ct);

        var updated = await owner.GetVideoByIdAsync(source.Id, ct);
        updated.PrimaryFileId.Should().Be(targetFileId);
        updated.Files.Select(file => file.Id).Should().Equal(targetFileId, sourceFileId);
        var updatedChild = await owner.GetVideoByIdAsync(child.Id, ct);
        updatedChild.ClipStartSec.Should().Be(2);
        updatedChild.Files.Select(file => file.Id).Should().Equal(targetFileId, sourceFileId);
        var state = await owner.GetVideoAlignmentsAsync(source.Id, ct);
        state.TryGetProperty("reviews", out _).Should().BeFalse();
        state.TryGetProperty("references", out _).Should().BeFalse();
        await owner.AssertResponseAsync(HttpMethod.Post, $"/api/videos/{source.Id}/alignments/apply", HttpStatusCode.Conflict,
            new VideoSetPrimaryFileDto(sourceFileId, ExpectedPrimaryFileId: sourceFileId), ct);
        await AsAnonymous().AssertResponseAsync($"/api/videos/{source.Id}/alignments", HttpStatusCode.Unauthorized, ct);
    }

    private static async Task<string> CreateTestPatternVideoAsync(string ffmpegPath, string directory, string fileName, int crf, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, fileName);
        var startInfo = new ProcessStartInfo { FileName = ffmpegPath, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsLinux())
        {
            var ffmpegDirectory = Path.GetDirectoryName(ffmpegPath)!;
            startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out var inherited);
            startInfo.Environment["LD_LIBRARY_PATH"] = string.IsNullOrWhiteSpace(inherited) ? ffmpegDirectory : $"{ffmpegDirectory}{Path.PathSeparator}{inherited}";
        }
        foreach (var argument in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=2:duration=12", "-threads", "1", "-c:v", "libx264", "-preset", "ultrafast", "-crf", crf.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an", path }) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("FFmpeg could not be started.");
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException(await error);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-1));
        return path;
    }
}
