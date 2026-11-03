using Microsoft.EntityFrameworkCore.Diagnostics;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public class GenerateJobServiceTests
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
            GenerateJobService.ShouldGenerateDefaultVideoThumbnail(
                requested: true,
                imageBlobId));
    }

    [Fact]
    public void ShouldGenerateDefaultVideoThumbnail_RequiresThumbnailRequest()
    {
        Assert.False(GenerateJobService.ShouldGenerateDefaultVideoThumbnail(
            requested: false,
            imageBlobId: null));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, JobUnitOutcome.Failed)]
    public async Task ReportGenerateResultAsync_RecordsFailureOnlyWhenGenerationFails(
        bool result,
        JobUnitOutcome? expected)
    {
        var unit = new NullJobUnit("video");

        await GenerateJobService.ReportGenerateResultAsync(unit, Task.FromResult(result), "failed");

        Assert.Equal(expected, unit.Outcome);
    }

    [Fact]
    public void RequiresPathsForExplicitNonVideoWork_RejectsMixedIdOnlyRequest()
    {
        var options = new GenerateOptionsDto
        {
            VideoIds = [1],
            ImagePhashes = true,
        };

        Assert.True(GenerateJobService.RequiresPathsForExplicitNonVideoWork(options));
        Assert.False(GenerateJobService.RequiresPathsForExplicitNonVideoWork(options with { Paths = ["/library"] }));
    }

    [Fact]
    public void SelectVideoFile_UsesThePrimaryFileInsideTheRequestedPath()
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "first.mp4"),
            (2, "/library/private", "selected.mp4"));
        video.PrimaryFileId = 2;

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.NotNull(selected);
        Assert.Equal(2, selected.Id);
    }

    [Fact]
    public void SelectVideoFile_ReturnsNullWhenNoFileMatchesTheRequestedPath()
    {
        var video = CreateVideoWithFiles((1, "/library/original", "first.mp4"));
        video.PrimaryFileId = 1;

        var selected = GenerateJobService.SelectVideoFile(video, ["/library/private"]);

        Assert.Null(selected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(99)]
    public void SelectVideoFile_DoesNotFallBackToMatchingSecondaryFile(int? primaryFileId)
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "primary.mp4"),
            (2, "/library/private", "secondary.mp4"));
        video.PrimaryFileId = primaryFileId;

        Assert.Null(GenerateJobService.SelectVideoFile(video, ["/library/private"]));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(2, 2)]
    [InlineData(99, null)]
    public void SelectVideoFile_WithoutPathFilterRequiresAnAvailablePrimaryFile(int? primaryFileId, int? expectedFileId)
    {
        var video = CreateVideoWithFiles(
            (1, "/library/original", "first.mp4"),
            (2, "/library/private", "primary.mp4"));
        video.PrimaryFileId = primaryFileId;

        Assert.Equal(expectedFileId, GenerateJobService.SelectVideoFile(video, [])?.Id);
    }

    [Theory]
    [InlineData("/library/video.mp4", "/library", true)]
    [InlineData("/library/video.mp4", "/library/", true)]
    [InlineData("/library", "/library", true)]
    [InlineData("/library-other/video.mp4", "/library", false)]
    [InlineData("/LIBRARY/video.mp4", "/library", true)]
    [InlineData("\\library\\video.mp4", "/library", true)]
    [InlineData("C:\\library\\video.mp4", "C:/library", true)]
    [InlineData("C:/library/video.mp4", "C:\\library", true)]
    [InlineData("C:/library-other/video.mp4", "C:/library", false)]
    [InlineData("/library/nested/video.mp4", "/library/nested", true)]
    [InlineData("/library/nested-other/video.mp4", "/library/nested", false)]
    [InlineData("/library/video.mp4", "/", true)]
    [InlineData("relative/library/video.mp4", "relative/library", true)]
    [InlineData("relative/library-other/video.mp4", "relative/library", false)]
    public void IsUnderAnyPath_UsesDirectorySegmentBoundaries(
        string candidate,
        string filter,
        bool expected)
    {
        Assert.Equal(expected, GeneratePathFilter.Contains(candidate, [filter]));
    }

    [Theory]
    [InlineData("/canonical/video.mp4", "/stale", "wrong.mp4")]
    [InlineData("C:/canonical/video.mp4", "C:/stale", "wrong.mp4")]
    [InlineData("//server/share/video.mp4", "//other/share", "wrong.mp4")]
    public void Resolve_UsesTheCanonicalFilePathInsteadOfNavigationData(string storedPath, string folderPath, string basename)
    {
        var file = new VideoFile
        {
            Path = storedPath,
            Basename = basename,
            ParentFolder = new Folder { Path = folderPath },
        };

        var expected = OperatingSystem.IsWindows() ? storedPath.Replace('/', '\\') : storedPath;
        Assert.Equal(expected, GeneratePathFilter.Resolve(file));
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(true, true, 0)]
    [InlineData(false, false, 0)]
    public async Task Start_MakesStereoscopicVrAssetsOnlyWhenAskedAndOnlyForVrVideos(
        bool vrStereo,
        bool flatVideo,
        int expectedRequests)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(tempRoot, "vr.mp4"), [1], TestContext.Current.CancellationToken);

            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(dbOptions));
            await using var provider = services.BuildServiceProvider();

            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = CreateVideoWithFiles((1, tempRoot, "vr.mp4"));
                video.IsVr = !flatVideo;
                db.Videos.Add(video);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                video.PrimaryFileId = video.Files.Single().Id;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var jobs = new CapturingJobService();
            var thumbnails = new CapturingThumbnailService(tempRoot);
            var fingerprints = new NullFingerprintService();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var fingerprintWriter = new FileFingerprintWriter(scopeFactory);
            var nonVideoGeneration = new NonVideoGenerationService(
                thumbnails,
                fingerprints,
                fingerprintWriter,
                NullLogger<NonVideoGenerationService>.Instance);
            var service = new GenerateJobService(
                jobs,
                thumbnails,
                thumbnails,
                fingerprints,
                new AlwaysReadableSourceHealthProbe(),
                fingerprintWriter,
                nonVideoGeneration,
                scopeFactory,
                new CoveConfiguration { MaxParallelTasks = 1 },
                NullLogger<GenerateJobService>.Instance);

            // The 2D cover and preview are asked for either way; they must not bring the 3D ones along.
            service.Start(new GenerateOptionsDto
            {
                Thumbnails = true,
                Previews = true,
                VrStereo = vrStereo,
            });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.NotNull(thumbnails.PreviewSourceFileId);
            Assert.Equal(expectedRequests, thumbnails.VrCardRequests);
            Assert.Equal(expectedRequests, thumbnails.VrPreviewRequests);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, null, false, false, false, new double[0])]
    [InlineData(true, null, false, false, false, new[] { 10d, 100d })]
    [InlineData(true, null, true, false, false, new[] { 10d, 30d, 100d })]
    // Selecting only the clip makes its segments' frames from its source, and nothing else for the source.
    [InlineData(true, null, false, true, false, new[] { 100d })]
    [InlineData(true, null, false, false, true, new[] { 10d, 100d })]
    // Face segments are made only when chosen; "" stands for segments without a kind.
    [InlineData(true, "FACE", false, false, false, new[] { 120d })]
    [InlineData(true, ",face", false, false, false, new[] { 10d, 100d, 120d })]
    public async Task Start_MakesStartFramesForSegmentsWithoutChosenCovers(
        bool segmentCovers,
        string? kinds,
        bool overwrite,
        bool selectClipOnly,
        bool framesFail,
        double[] expectedSeconds)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var library = await SeedSegmentCoverLibraryAsync(tempRoot);
            var (sourceId, sourceFileId, clipId) = (library.SourceId, library.SourceFileId, library.ClipId);
            var jobs = new CapturingJobService();
            var thumbnails = new CapturingThumbnailService(tempRoot) { OnThumbnailFromFile = seconds => seconds == null || !framesFail };
            await File.WriteAllBytesAsync(thumbnails.GetTimestampedThumbnailPath(sourceId, 30), [1], TestContext.Current.CancellationToken);
            var service = CreateService(library.Provider, jobs, thumbnails);

            // The video cover is asked for too, so the run has work even without segment covers.
            service.Start(new GenerateOptionsDto
            {
                Thumbnails = true,
                SegmentCovers = segmentCovers,
                SegmentCoverKinds = kinds?.Split(',').ToList(),
                Overwrite = overwrite,
                VideoIds = selectClipOnly ? [clipId] : null,
            });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.All(thumbnails.ThumbnailRequests, request => Assert.Equal((sourceId, sourceFileId), (request.VideoId, request.SourceFileId)));
            // Compared by whole second: which of two segments sharing a frame supplies its time is unordered.
            Assert.Equal(expectedSeconds, thumbnails.ThumbnailRequests.Where(request => request.AtSeconds != null).Select(request => Math.Floor(request.AtSeconds!.Value)).Order());
            Assert.Equal(selectClipOnly ? 0 : 1, thumbnails.ThumbnailRequests.Count(request => request.AtSeconds == null));
            var unit = Assert.Single(jobs.Progress.Units);
            Assert.Equal(framesFail, unit.Outcome == JobUnitOutcome.Failed);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstimateAsync_CountsWhatAScopeHoldsAndWhatIsStillMissing(bool selectClipOnly)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var library = await SeedSegmentCoverLibraryAsync(tempRoot);
            var thumbnails = new CapturingThumbnailService(tempRoot);
            await File.WriteAllBytesAsync(thumbnails.GetTimestampedThumbnailPath(library.SourceId, 30), [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(thumbnails.GetThumbnailPathForVideo(library.SourceId), [1], TestContext.Current.CancellationToken);
            await using var scope = library.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            {
                // A run decodes nothing from a file it already found unreadable, so only its MD5 counts.
                await File.WriteAllBytesAsync(Path.Combine(tempRoot, "unreadable.mp4"), [1, 2, 3, 4, 5], TestContext.Current.CancellationToken);
                var unreadable = CreateVideoWithFiles((3, tempRoot, "unreadable.mp4"));
                unreadable.Files.Single().Size = 5;
                unreadable.Files.Single().SourceUnreadableAt = DateTime.UtcNow;
                unreadable.Files.Single().SourceUnreadableSize = 5;
                db.Videos.Add(unreadable);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                unreadable.PrimaryFileId = 3;
                db.Segments.Add(new Segment { HostType = SegmentHostType.Video, HostId = unreadable.Id, StartSec = 5, SourceKey = "user" });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var service = new GenerateEstimateService(db, thumbnails, thumbnails);

            // What is ticked does not matter: every option is counted for the scope.
            var estimate = await service.EstimateAsync(
                new GenerateOptionsDto { Thumbnails = false, VideoIds = selectClipOnly ? [library.ClipId] : null },
                TestContext.Current.CancellationToken);

            Assert.Empty(thumbnails.ThumbnailRequests);
            var segmentCovers = Assert.IsType<List<GenerateSegmentKindCountDto>>(estimate.SegmentCovers);
            if (selectClipOnly)
            {
                // Only the clip's own segment, made from its source; the source itself is not in scope.
                Assert.Equal([("", 1, 1)], segmentCovers.Select(kind => (kind.Kind, kind.Missing, kind.Total)));
                Assert.Equal((0, 0), (estimate.Thumbnails!.Missing, estimate.Thumbnails.Total));
                Assert.Equal((0, 0), (estimate.Md5!.Missing, estimate.Md5.Total));
                // Videos were picked without paths, so images and the rest are out of reach.
                Assert.Null(estimate.ImageThumbnails);
                Assert.Null(estimate.AudioPhashes);
                return;
            }

            // Frames 10 (two segments share it), 30 (already made) and 100 (the clip's segment). 250 is past the
            // end, 60 has a chosen cover, the audio segment is not a video's, and the unreadable video's segment
            // cannot be decoded.
            Assert.Equal([("", 2, 3), ("face", 1, 1)], segmentCovers.Select(kind => (kind.Kind, kind.Missing, kind.Total)));
            Assert.All(segmentCovers, kind => Assert.False(kind.Estimated));
            Assert.Equal((0, 1), (estimate.Thumbnails!.Missing, estimate.Thumbnails.Total));
            Assert.Equal((1, 1), (estimate.Previews!.Missing, estimate.Previews.Total));
            Assert.Equal((0, 0), (estimate.VrStereo!.Missing, estimate.VrStereo.Total));
            Assert.Equal((2, 2, 6L), (estimate.Md5!.Missing, estimate.Md5.Total, estimate.Md5.MissingBytes));
            Assert.Equal((0, 0), (estimate.ImageThumbnails!.Missing, estimate.ImageThumbnails.Total));

            // Ids of other media leave the videos out.
            var forImages = await service.EstimateAsync(new GenerateOptionsDto { ImageIds = [999] }, TestContext.Current.CancellationToken);
            Assert.Null(forImages.Thumbnails);
            Assert.Null(forImages.SegmentCovers);
            Assert.Null(forImages.AudioPhashes);
            Assert.Equal((0, 0), (forImages.ImageThumbnails!.Missing, forImages.ImageThumbnails.Total));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public async Task EstimateAsync_ScalesWhatASampleIsMissingToTheWholeScope()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        try
        {
            await using var library = await SeedSegmentCoverLibraryAsync(tempRoot);
            var thumbnails = new CapturingThumbnailService(tempRoot);
            await using var scope = library.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            // Ten times more segments than are looked for on disk; the first half have their frame.
            const int count = GenerateEstimateService.SampleSize * 10;
            db.Segments.AddRange(Enumerable.Range(0, count).Select(index => new Segment
            {
                HostType = SegmentHostType.Video, HostId = library.SourceId, StartSec = index * 0.01, Kind = "many", SourceKey = "user",
            }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            for (var index = 0; index < count / 2; index++)
                await File.WriteAllBytesAsync(thumbnails.GetTimestampedThumbnailPath(library.SourceId, index * 0.01), [1], TestContext.Current.CancellationToken);

            var estimate = await new GenerateEstimateService(db, thumbnails, thumbnails)
                .EstimateAsync(new GenerateOptionsDto(), TestContext.Current.CancellationToken);

            var many = Assert.Single(estimate.SegmentCovers!, kind => kind.Kind == "many");
            Assert.Equal(count, many.Total);
            Assert.True(many.Estimated);
            Assert.InRange(many.Missing, count * 0.4, count * 0.6);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class SegmentCoverLibrary(ServiceProvider provider, int sourceId, int sourceFileId, int clipId) : IAsyncDisposable
    {
        public ServiceProvider Provider => provider;
        public int SourceId => sourceId;
        public int SourceFileId => sourceFileId;
        public int ClipId => clipId;
        public ValueTask DisposeAsync() => provider.DisposeAsync();
    }

    /// <summary>A 200 s source with a clip of it, and segments covering each case segment covers handle.</summary>
    private static async Task<SegmentCoverLibrary> SeedSegmentCoverLibraryAsync(string tempRoot)
    {
        await File.WriteAllBytesAsync(Path.Combine(tempRoot, "source.mp4"), [1], TestContext.Current.CancellationToken);

        var dbOptions = new DbContextOptionsBuilder<CoveContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(dbOptions));
        var provider = services.BuildServiceProvider();

        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var source = CreateVideoWithFiles((1, tempRoot, "source.mp4"));
        source.Files.Single().Duration = 200;
        source.Files.Single().Size = 1;
        db.Videos.Add(source);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        source.PrimaryFileId = source.Files.Single().Id;
        var clip = new Video { Title = "Clip", ParentVideoId = source.Id, ClipStartSec = 90 };
        db.Videos.Add(clip);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        db.Segments.AddRange(
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 10, EndSec = 20, SourceKey = "user" },
            // It starts at the same moment as the segment above, so they share a frame.
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 10, EndSec = 16, SourceKey = "user" },
            // It starts past the end of the file, so it has no frame to make.
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 250, EndSec = 260, SourceKey = "user" },
            // Its frame was made by an earlier run.
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 30, EndSec = 50, SourceKey = "user" },
            // A clip's segment runs on the source's timeline.
            new Segment { HostType = SegmentHostType.Video, HostId = clip.Id, StartSec = 100, SourceKey = "user" },
            // A chosen cover needs no frame.
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 60, EndSec = 70, ImageBlobId = "chosen", SourceKey = "user" },
            // A face segment, made only when face segments are chosen.
            new Segment { HostType = SegmentHostType.Video, HostId = source.Id, StartSec = 120, EndSec = 124, Kind = "Face", SourceKey = "ext:ai.faces" },
            // Not a video segment, despite sharing the host id.
            new Segment { HostType = SegmentHostType.Audio, HostId = source.Id, StartSec = 80, EndSec = 90, SourceKey = "user" });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return new SegmentCoverLibrary(provider, source.Id, source.PrimaryFileId.Value, clip.Id);
    }

    private static GenerateJobService CreateService(ServiceProvider provider, CapturingJobService jobs, CapturingThumbnailService thumbnails)
    {
        var fingerprints = new NullFingerprintService();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var fingerprintWriter = new FileFingerprintWriter(scopeFactory);
        return new GenerateJobService(
            jobs,
            thumbnails,
            thumbnails,
            fingerprints,
            new AlwaysReadableSourceHealthProbe(),
            fingerprintWriter,
            new NonVideoGenerationService(thumbnails, fingerprints, fingerprintWriter, NullLogger<NonVideoGenerationService>.Instance),
            scopeFactory,
            new CoveConfiguration { MaxParallelTasks = 1 },
            NullLogger<GenerateJobService>.Instance);
    }

    [Fact]
    public async Task Start_PathScopedOverwrite_ReportsFailureForTheMatchingPrimaryFile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"cove-generate-{Guid.NewGuid():N}");
        var originalRoot = Path.Combine(tempRoot, "original");
        var selectedRoot = Path.Combine(tempRoot, "selected");
        Directory.CreateDirectory(originalRoot);
        Directory.CreateDirectory(selectedRoot);

        try
        {
            await File.WriteAllBytesAsync(Path.Combine(originalRoot, "first.mp4"), [1], TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(selectedRoot, "selected.mp4"), [2], TestContext.Current.CancellationToken);

            var dbOptions = new DbContextOptionsBuilder<CoveContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(dbOptions));
            await using var provider = services.BuildServiceProvider();

            int selectedFileId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                var video = CreateVideoWithFiles(
                    (1, originalRoot, "first.mp4"),
                    (2, selectedRoot, "selected.mp4"));
                db.Videos.Add(video);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                selectedFileId = video.Files.Single(file => file.Basename == "selected.mp4").Id;
                video.PrimaryFileId = selectedFileId;
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var jobs = new CapturingJobService();
            var thumbnails = new CapturingThumbnailService(tempRoot);
            var fingerprints = new NullFingerprintService();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var fingerprintWriter = new FileFingerprintWriter(scopeFactory);
            var nonVideoGeneration = new NonVideoGenerationService(
                thumbnails,
                fingerprints,
                fingerprintWriter,
                NullLogger<NonVideoGenerationService>.Instance);
            var service = new GenerateJobService(
                jobs,
                thumbnails,
                thumbnails,
                fingerprints,
                new AlwaysReadableSourceHealthProbe(),
                fingerprintWriter,
                nonVideoGeneration,
                scopeFactory,
                new CoveConfiguration { MaxParallelTasks = 1 },
                NullLogger<GenerateJobService>.Instance);

            service.Start(new GenerateOptionsDto
            {
                Thumbnails = false,
                Previews = true,
                Overwrite = true,
                Paths = [selectedRoot],
            });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            Assert.Equal(selectedFileId, thumbnails.PreviewSourceFileId);
            Assert.Equal(1, jobs.Progress.DeclaredTotal);
            var unit = Assert.Single(jobs.Progress.Units);
            Assert.Equal(JobUnitOutcome.Failed, unit.Outcome);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageGenerationBoundsMaterializationAndAppliesSelectionBeforeLoadingFiles(bool scoped)
    {
        var guard = new GenerationMaterializationGuard();
        var dbOptions = new DbContextOptionsBuilder<CoveContext>().UseSqlite("Data Source=:memory:").AddInterceptors(guard).Options;
        await using var db = new CoveContext(dbOptions);
        await db.Database.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        var folder = new Folder { Path = "/generation/memory" };
        var images = Enumerable.Range(0, 601).Select(index => new Image
        {
            Title = $"image {index}",
            Files = [new ImageFile { ParentFolder = folder, Basename = $"{index}.jpg" }],
        }).ToArray();
        db.Images.AddRange(images);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var selectedId = images[^1].Id;
        db.ChangeTracker.Clear();
        guard.Enabled = true;
        await using var provider = new ServiceCollection().BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var trackedDuringWork = false;
        var thumbnails = new CapturingThumbnailService(Path.GetTempPath())
        {
            OnImageThumbnail = _ =>
            {
                guard.Processed++;
                trackedDuringWork |= db.ChangeTracker.Entries().Any();
                return Task.FromResult(true);
            },
        };
        var service = new NonVideoGenerationService(thumbnails, new NullFingerprintService(),
            new FileFingerprintWriter(scopeFactory), NullLogger<NonVideoGenerationService>.Instance);
        await service.GenerateAsync(db, new GenerateOptionsDto
        {
            Thumbnails = false, ImageThumbnails = true,
            ImageIds = scoped ? [selectedId] : null,
        }, 1, new CapturingJobProgress(), TestContext.Current.CancellationToken);
        Assert.Equal(scoped ? 1 : 601, guard.Materialized);
        Assert.Equal(guard.Materialized, guard.Processed);
        Assert.False(trackedDuringWork);
    }

    [Fact]
    public async Task VideoGenerationBatchesGraphsAndKeepsCountedUnitsWhenAssetsChange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-generate-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var guard = new GenerationMaterializationGuard { EntityType = typeof(Video) };
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "library.db")};Pooling=False").AddInterceptors(guard).Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            await using var provider = services.BuildServiceProvider();
            int firstId;
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                var folder = new Folder { Path = Path.Combine(root, "missing-source") };
                var videos = Enumerable.Range(0, 601).Select(index => new Video
                {
                    Title = $"video {index}", Files = [new VideoFile { ParentFolder = folder, Basename = $"{index}.mp4" }],
                }).ToArray();
                db.Videos.AddRange(videos);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                firstId = videos[0].Id;
            }
            var jobs = new CapturingJobService();
            jobs.Progress.OnDeclared = () =>
            {
                // This was selected as missing, then appears before processing begins.
                File.WriteAllBytes(Path.Combine(root, $"{firstId}.jpg"), [0]);
                guard.Enabled = true;
            };
            jobs.Progress.OnCompleted = () => guard.Processed++;
            var thumbnails = new CapturingThumbnailService(root);
            var fingerprints = new NullFingerprintService();
            var scopes = provider.GetRequiredService<IServiceScopeFactory>();
            var writer = new FileFingerprintWriter(scopes);
            var service = new GenerateJobService(jobs, thumbnails, thumbnails, fingerprints,
                new AlwaysReadableSourceHealthProbe(), writer,
                new NonVideoGenerationService(thumbnails, fingerprints, writer, NullLogger<NonVideoGenerationService>.Instance),
                scopes, new CoveConfiguration { MaxParallelTasks = 1 }, NullLogger<GenerateJobService>.Instance);
            service.Start(new GenerateOptionsDto { Thumbnails = true });
            await jobs.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(601, jobs.Progress.DeclaredTotal);
            Assert.Equal(601, jobs.Progress.Units.Count);
            Assert.Equal(601, guard.Processed);
            Assert.Equal(601, guard.Materialized);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class GenerationMaterializationGuard : IMaterializationInterceptor
    {
        public Type EntityType { get; init; } = typeof(ImageFile);
        public bool Enabled { get; set; }
        public int Materialized { get; private set; }
        public int Processed { get; set; }
        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (Enabled && EntityType.IsInstanceOfType(entity))
            {
                Materialized++;
                Assert.True(Materialized - Processed <= GenerationSelection.BatchSize,
                    "Generation materialized more than one batch before processing it.");
            }
            return entity;
        }
    }

    private static Video CreateVideoWithFiles(params (int Id, string Folder, string Basename)[] files)
    {
        var video = new Video();
        foreach (var file in files)
        {
            video.Files.Add(new VideoFile
            {
                Id = file.Id,
                Basename = file.Basename,
                Path = BaseFileEntity.ComputePath(file.Folder, file.Basename),
                ParentFolder = new Folder { Path = file.Folder },
            });
        }

        return video;
    }

    private sealed class CapturingJobService : IJobService
    {
        public CapturingJobProgress Progress { get; } = new();

        public Task Completion { get; private set; } = Task.CompletedTask;

        public string Enqueue(
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            bool exclusive = true)
        {
            Completion = work(Progress, CancellationToken.None);
            return "generate-job";
        }

        public bool Cancel(string jobId) => false;

        public bool ReorderQueued(string jobId, string? beforeJobId) => false;

        public JobInfo? GetJob(string jobId) => null;

        public IReadOnlyList<JobInfo> GetAllJobs() => [];

        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class CapturingJobProgress : IJobProgress
    {
        public List<CapturingJobUnit> Units { get; } = [];
        public int? DeclaredTotal { get; private set; }
        public Action? OnDeclared { get; set; }
        public Action? OnCompleted { get; set; }
        public void DeclareUnitCount(int totalUnits) { DeclaredTotal = totalUnits; OnDeclared?.Invoke(); }

        public void Report(double progress, string? subTask = null)
        {
        }

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            var unit = new CapturingJobUnit(OnCompleted);
            Units.Add(unit);
            return unit;
        }
    }

    private sealed class CapturingJobUnit(Action? onCompleted = null) : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            if (Outcome == null)
            {
                Outcome = outcome;
                onCompleted?.Invoke();
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingThumbnailService(string generatedRoot) : IThumbnailService, IVideoAssetGenerator
    {
        public int? PreviewSourceFileId { get; private set; }
        public Func<int, Task<bool>>? OnImageThumbnail { get; init; }

        public Task<string?> GetVideoThumbnailPathAsync(int videoId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> GetImageFilePathAsync(int imageId, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageStreamAsync(
            int imageId,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetImageThumbnailStreamAsync(
            int imageId,
            int maxDimension = 640,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task<(Stream stream, string contentType, bool supportsRangeRequests)?> GetBlobImageThumbnailStreamAsync(
            string blobId,
            int maxDimension = 640,
            CancellationToken ct = default)
            => Task.FromResult<(Stream, string, bool)?>(null);

        public Task DeleteVideoGeneratedFilesAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteImageGeneratedFilesAsync(int imageId, CancellationToken ct = default) => Task.CompletedTask;

        public Task DeleteBlobGeneratedFilesAsync(string blobId, CancellationToken ct = default) => Task.CompletedTask;

        public Task GenerateVideoThumbnailAsync(
            int videoId,
            double? atSeconds = null,
            CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<bool> GenerateThumbnailFromFileAsync(
            int videoId,
            int sourceFileId,
            double? atSeconds,
            CancellationToken ct = default)
        {
            ThumbnailRequests.Add((videoId, sourceFileId, atSeconds));
            return Task.FromResult(OnThumbnailFromFile?.Invoke(atSeconds) ?? true);
        }

        public List<(int VideoId, int SourceFileId, double? AtSeconds)> ThumbnailRequests { get; } = [];
        public Func<double?, bool>? OnThumbnailFromFile { get; init; }

        public Task<bool> GenerateImageThumbnailAsync(
            int imageId,
            int maxDimension = 640,
            bool overwrite = false,
            CancellationToken ct = default)
            => OnImageThumbnail?.Invoke(imageId) ?? Task.FromResult(true);

        public Task GenerateVideoPreviewAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> GeneratePreviewFromFileAsync(
            int videoId,
            int sourceFileId,
            bool overwrite,
            CancellationToken ct = default)
        {
            PreviewSourceFileId = sourceFileId;
            return Task.FromResult(false);
        }

        public Task GenerateVideoSpriteAsync(int videoId, CancellationToken ct = default) => Task.CompletedTask;

        public Task<bool> GenerateSpriteFromFileAsync(
            int videoId,
            int sourceFileId,
            bool overwrite,
            CancellationToken ct = default)
            => Task.FromResult(true);

        public string GetThumbnailPathForVideo(int videoId) => Path.Combine(generatedRoot, $"{videoId}.jpg");

        public string GetTimestampedThumbnailPath(int videoId, double seconds)
            => Path.Combine(generatedRoot, $"{videoId}_t{(long)Math.Round(seconds * 1000)}ms.jpg");

        public string GetPreviewPath(int videoId) => Path.Combine(generatedRoot, $"{videoId}.mp4");

        public string GetSpritePath(int videoId) => Path.Combine(generatedRoot, $"{videoId}_sprite.jpg");

        public string GetSpriteVttPath(int videoId) => Path.Combine(generatedRoot, $"{videoId}.vtt");


        public string StartGenerateAllThumbnails() => "generate-thumbnails";

        public int VrCardRequests { get; private set; }
        public int VrPreviewRequests { get; private set; }

        public bool HasVrCard(int videoId) => false;

        public bool HasVrPreview(int videoId) => false;

        public Task<bool> GenerateVrCardFromFileAsync(int videoId, int sourceFileId, bool overwrite, CancellationToken ct = default)
        {
            VrCardRequests++;
            return Task.FromResult(true);
        }

        public Task<bool> GenerateVrPreviewFromFileAsync(int videoId, int sourceFileId, bool overwrite, CancellationToken ct = default)
        {
            VrPreviewRequests++;
            return Task.FromResult(true);
        }
    }

    private sealed class NullFingerprintService : IFingerprintService
    {
        public Task<string?> ComputeMd5Async(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeImagePhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public ImageSignature? ComputeImageSignature(byte[] data) => null;

        public Task<string?> ComputeVideoPhashAsync(string path, double duration, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeAudioPhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public Task<string?> ComputeTextPhashAsync(string path, CancellationToken ct = default)
            => Task.FromResult<string?>(null);

        public string StartGenerateVideoPhashes() => "video-phashes";

        public string StartGenerateImagePhashes() => "image-phashes";
    }
}

/// <summary>
/// Source-health probe that never flags anything, so these tests exercise generation flow rather
/// than the truncation check (which has its own tests in <see cref="VideoSourceHealthTests"/>).
/// </summary>
internal sealed class AlwaysReadableSourceHealthProbe : IVideoSourceHealthProbe
{
    public Task<string?> GetUnreadableReasonAsync(string path, CancellationToken ct = default)
        => Task.FromResult<string?>(null);
}
