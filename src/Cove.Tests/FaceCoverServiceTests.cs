using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

[RequiresPostgres]
public class FaceCoverServiceTests
{
    [Fact]
    public async Task SetFromDetection_StoresCropAndRecordsSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "old-cover");
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        fixture.WriteFrame(video.Id, 120);

        var outcome = await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken);

        Assert.Equal(FaceCoverChangeOutcome.Changed, outcome);
        var stored = await fixture.ReloadFaceAsync(face.Id);
        Assert.Equal(fixture.Blobs.StoredBlobIds.Single(), stored.CoverBlobId);
        Assert.Equal(detection.Id, stored.CoverDetectionId);
        Assert.Equal(["old-cover"], fixture.Blobs.DeletedBlobIds);
    }

    [Fact]
    public async Task SetFromDetection_ChosenAgain_KeepsTheSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        fixture.WriteFrame(video.Id, 120);

        await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken);
        await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken);

        var stored = await fixture.ReloadFaceAsync(face.Id);
        Assert.Equal(fixture.Blobs.StoredBlobIds.Last(), stored.CoverBlobId);
        Assert.Equal(detection.Id, stored.CoverDetectionId);
    }

    [Fact]
    public async Task SetFromDetection_RejectsAnotherFacesDetection()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "old-cover");
        var other = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(other.Id, video.Id, 120);
        fixture.WriteFrame(video.Id, 120);

        var outcome = await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken);

        Assert.Equal(FaceCoverChangeOutcome.DetectionNotOnFace, outcome);
        Assert.Equal("old-cover", (await fixture.ReloadFaceAsync(face.Id)).CoverBlobId);
        Assert.Empty(fixture.Blobs.StoredBlobIds);
    }

    [Fact]
    public async Task SetFromDetection_WithoutItsFrame_LeavesTheCoverUnchanged()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "old-cover");
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);

        var outcome = await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken);

        Assert.Equal(FaceCoverChangeOutcome.SourceUnavailable, outcome);
        Assert.Equal("old-cover", (await fixture.ReloadFaceAsync(face.Id)).CoverBlobId);
        Assert.Empty(fixture.Blobs.DeletedBlobIds);
    }

    [Fact]
    public async Task ReplacingTheCoverWithoutASource_ClearsTheRecordedSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "chosen-cover");
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        face.CoverDetectionId = detection.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        Assert.Equal(detection.Id, (await fixture.ReloadFaceAsync(face.Id)).CoverDetectionId);

        // What an upload, or an extension writing its own cover, does: only the blob changes.
        face.CoverBlobId = "replacement-cover";
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var stored = await fixture.ReloadFaceAsync(face.Id);
        Assert.Equal("replacement-cover", stored.CoverBlobId);
        Assert.Null(stored.CoverDetectionId);
    }

    [Fact]
    public async Task ReleaseIfSourceLeft_RemovesACoverWhoseDetectionMovedToAnotherFace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "chosen-cover");
        var other = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        face.CoverDetectionId = detection.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.False(await fixture.Service.ReleaseIfSourceLeftAsync(face.Id, TestContext.Current.CancellationToken));
        Assert.Equal("chosen-cover", (await fixture.ReloadFaceAsync(face.Id)).CoverBlobId);

        detection.RefId = other.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.True(await fixture.Service.ReleaseIfSourceLeftAsync(face.Id, TestContext.Current.CancellationToken));
        var stored = await fixture.ReloadFaceAsync(face.Id);
        Assert.Null(stored.CoverBlobId);
        Assert.Null(stored.CoverDetectionId);
        Assert.Equal(["chosen-cover"], fixture.Blobs.DeletedBlobIds);
    }

    [Fact]
    public async Task DeletingTheSourceDetection_KeepsTheCoverAndForgetsItsSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "chosen-cover");
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        face.CoverDetectionId = detection.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        // As a re-analysis does: in the database, without loading the face.
        await fixture.Context.Detections.Where(item => item.Id == detection.Id).ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        fixture.Context.ChangeTracker.Clear();

        Assert.False(await fixture.Service.ReleaseIfSourceLeftAsync(face.Id, TestContext.Current.CancellationToken));
        var stored = await fixture.ReloadFaceAsync(face.Id);
        Assert.Equal("chosen-cover", stored.CoverBlobId);
        Assert.Null(stored.CoverDetectionId);
    }

    [Fact]
    public async Task ReleaseIfSourceLeft_KeepsACoverWithNoRecordedSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        var face = await fixture.AddFaceAsync(coverBlobId: "uploaded-cover");

        Assert.False(await fixture.Service.ReleaseIfSourceLeftAsync(face.Id, TestContext.Current.CancellationToken));

        Assert.Equal("uploaded-cover", (await fixture.ReloadFaceAsync(face.Id)).CoverBlobId);
        Assert.Empty(fixture.Blobs.DeletedBlobIds);
    }

    [Fact]
    public async Task Describe_ReportsEachDetectionsVideoMomentAndFaceSegment()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "chosen-cover");
        var other = await fixture.AddFaceAsync();
        var inSegment = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        var outsideSegments = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 300);
        var foreign = await fixture.AddVideoDetectionAsync(other.Id, video.Id, 121);
        face.CoverDetectionId = inSegment.Id;
        var wide = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 100, EndSec = 200, Kind = "face", RefId = face.Id, SourceKey = "ext" };
        var narrow = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 118, EndSec = 122.5, Kind = "face", RefId = face.Id, SourceKey = "ext" };
        var anotherFaces = new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 119, EndSec = 121, Kind = "face", RefId = other.Id, SourceKey = "ext" };
        fixture.Context.Segments.AddRange(wide, narrow, anotherFaces);
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var described = await fixture.Service.DescribeAsync(face.Id, [outsideSegments.Id, foreign.Id], TestContext.Current.CancellationToken);

        Assert.NotNull(described);
        Assert.Equal(new FaceCoverStateDto(true, inSegment.Id, true), described.Cover);
        Assert.Equal(
            [
                new FaceImageSourceDto(inSegment.Id, "video", video.Id, "Source video", 120, narrow.Id),
                new FaceImageSourceDto(outsideSegments.Id, "video", video.Id, "Source video", 300, null),
            ],
            described.Sources);
    }

    [Fact]
    public async Task Describe_FlagsACoverWhoseSourceLeftTheFace()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync(coverBlobId: "chosen-cover");
        var other = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        face.CoverDetectionId = detection.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        detection.RefId = other.Id;
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);

        var described = await fixture.Service.DescribeAsync(face.Id, [], TestContext.Current.CancellationToken);

        Assert.NotNull(described);
        Assert.Equal(new FaceCoverStateDto(true, detection.Id, false), described.Cover);
        Assert.Empty(described.Sources);
    }

    [Fact]
    public async Task DetectionsOfAFaceMergedIntoThisOne_CountAsItsOwn()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync();
        var merged = await fixture.AddFaceAsync();
        var mergedTwice = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(mergedTwice.Id, video.Id, 120);
        merged.MergedIntoFaceId = face.Id;
        mergedTwice.MergedIntoFaceId = merged.Id;
        fixture.Context.Segments.Add(new Segment { HostType = SegmentHostType.Video, HostId = video.Id, StartSec = 118, EndSec = 122, Kind = "face", RefId = mergedTwice.Id, SourceKey = "ext" });
        await fixture.Context.SaveChangesAsync(TestContext.Current.CancellationToken);
        fixture.WriteFrame(video.Id, 120);

        Assert.Equal(FaceCoverChangeOutcome.Changed, await fixture.Service.SetFromDetectionAsync(face.Id, detection.Id, TestContext.Current.CancellationToken));
        var described = await fixture.Service.DescribeAsync(face.Id, [], TestContext.Current.CancellationToken);

        Assert.NotNull(described);
        Assert.Equal(new FaceCoverStateDto(true, detection.Id, true), described.Cover);
        Assert.NotNull(Assert.Single(described.Sources).SegmentId);
        Assert.False(await fixture.Service.ReleaseIfSourceLeftAsync(face.Id, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Controller_MapsOutcomesToResponses()
    {
        await using var fixture = await Fixture.CreateAsync();
        var video = await fixture.AddVideoAsync("Source video");
        var face = await fixture.AddFaceAsync();
        var detection = await fixture.AddVideoDetectionAsync(face.Id, video.Id, 120);
        var controller = new FaceCoverController(fixture.Service);

        Assert.IsType<NotFoundResult>((await controller.GetImageSources(face.Id + 1000, null, TestContext.Current.CancellationToken)).Result);
        Assert.IsType<BadRequestObjectResult>((await controller.GetImageSources(face.Id, Enumerable.Range(1, 51).ToArray(), TestContext.Current.CancellationToken)).Result);
        Assert.IsType<NotFoundObjectResult>(await controller.SetCoverFromDetection(face.Id + 1000, new FaceCoverFromDetectionDto(detection.Id), TestContext.Current.CancellationToken));
        Assert.IsType<BadRequestObjectResult>(await controller.SetCoverFromDetection(face.Id, new FaceCoverFromDetectionDto(detection.Id + 1000), TestContext.Current.CancellationToken));
        Assert.IsType<ConflictObjectResult>(await controller.SetCoverFromDetection(face.Id, new FaceCoverFromDetectionDto(detection.Id), TestContext.Current.CancellationToken));

        fixture.WriteFrame(video.Id, 120);
        Assert.IsType<NoContentResult>(await controller.SetCoverFromDetection(face.Id, new FaceCoverFromDetectionDto(detection.Id), TestContext.Current.CancellationToken));
    }

    private sealed class Fixture(CoveContext context, CovePostgresDatabase database) : IAsyncDisposable
    {
        private readonly string _frameDirectory = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"cove-face-cover-frames-{Guid.NewGuid():N}")).FullName;

        public CoveContext Context { get; } = context;
        public RecordingBlobService Blobs { get; } = new();
        public FaceCoverService Service => field ??= new FaceCoverService(
            Context,
            Blobs,
            new FrameThumbnailService(_frameDirectory),
            NullLogger<FaceCoverService>.Instance);

        public static async Task<Fixture> CreateAsync()
        {
            var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
            return new Fixture(database.CreateContext(), database);
        }

        public async Task<Video> AddVideoAsync(string title)
        {
            var video = new Video { Title = title };
            Context.Videos.Add(video);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return video;
        }

        public async Task<Face> AddFaceAsync(string? coverBlobId = null)
        {
            var face = new Face { CoverBlobId = coverBlobId };
            Context.Faces.Add(face);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return face;
        }

        public async Task<Detection> AddVideoDetectionAsync(int faceId, int videoId, double observedAtSec)
        {
            var detection = new Detection
            {
                HostType = DetectionHostType.Video,
                HostId = videoId,
                ObservedAtSec = observedAtSec,
                FrameWidth = 640,
                FrameHeight = 360,
                Class = "face",
                Score = 0.9f,
                X = 200,
                Y = 100,
                W = 100,
                H = 100,
                RefKind = "face",
                RefId = faceId,
                SourceKey = "ext",
            };
            Context.Detections.Add(detection);
            await Context.SaveChangesAsync(TestContext.Current.CancellationToken);
            return detection;
        }

        public void WriteFrame(int videoId, double seconds)
            => File.WriteAllBytes(
                FrameThumbnailService.FramePath(_frameDirectory, videoId, seconds),
                TestImages.Jpeg(640, 360, new Cove.Api.Imaging.Rgba32(0, 0, 0)));

        public Task<Face> ReloadFaceAsync(int faceId)
            => Context.Faces.AsNoTracking().SingleAsync(face => face.Id == faceId, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await database.DisposeAsync();
            try { Directory.Delete(_frameDirectory, recursive: true); } catch (IOException) { }
        }
    }

    private sealed class RecordingBlobService : IBlobService
    {
        public List<string> StoredBlobIds { get; } = [];
        public List<string> DeletedBlobIds { get; } = [];

        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
        {
            var blobId = $"stored-{StoredBlobIds.Count + 1}";
            StoredBlobIds.Add(blobId);
            return Task.FromResult(blobId);
        }

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
        {
            DeletedBlobIds.Add(blobId);
            return Task.CompletedTask;
        }
    }

    // Serves only frames that already exist: a frame that would have to be made reports as unavailable.
    private sealed class FrameThumbnailService(string frameDirectory) : IThumbnailService
    {
        public static string FramePath(string frameDirectory, int videoId, double seconds)
            => Path.Combine(frameDirectory, $"{videoId}_t{(long)Math.Round(seconds * 1000)}ms.jpg");

        public string GetTimestampedThumbnailPath(int videoId, double seconds) => FramePath(frameDirectory, videoId, seconds);
        public Task GenerateVideoThumbnailAsync(int videoId, double? atSeconds = null, CancellationToken ct = default) => throw new IOException("No video file.");
        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(int imageId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(int imageId, int maxDimension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(string blobId, int maxDimension, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> GenerateImageThumbnailAsync(int imageId, int maxDimension = 640, bool overwrite = false, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public string GetThumbnailPathForVideo(int videoId) => throw new NotSupportedException();
        public string GetPreviewPath(int videoId) => throw new NotSupportedException();
        public string GetSpritePath(int videoId) => throw new NotSupportedException();
        public string GetSpriteVttPath(int videoId) => throw new NotSupportedException();
        public string StartGenerateAllThumbnails() => throw new NotSupportedException();
    }
}
