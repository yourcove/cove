using System.Reflection;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// Shot boundaries belong to the file that was analysed. When Cove deletes that file in favour of
/// another copy of the same footage on the same timeline, the shots move to the copy instead of being
/// deleted with the file they were measured on.
/// </summary>
public sealed class VideoShotCarryOverTests
{
    private const string Phash = "00ff00ff00ff00ff";

    [Fact]
    public async Task AConversionKeepsTheShotsWhenItDeletesTheOriginal()
    {
        await using var harness = await Harness.CreateAsync();
        var (videoId, original, converted) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25);
        await harness.WriteShotsAsync(original);

        var swap = await harness.Maintenance().MakePrimaryWhenSameContentAsync(videoId, converted, CancellationToken.None);
        var afterSwap = await harness.GetShotsAsync(original);
        var removal = await harness.Maintenance().DeleteFileAsync(original, deleteFromDisk: false, CancellationToken.None);

        Assert.True(swap.Applied, swap.Reason);
        Assert.NotNull(afterSwap);
        Assert.True(removal.Applied, removal.Reason);
        var set = await harness.GetShotsAsync(converted);
        Assert.NotNull(set);
        Assert.Equal((2, 2, 25d), (set.Revision, set.ShotCount, set.Fps!.Value));
        Assert.Equal([(0, 125), (125, 250)], set.Shots!.Select(shot => (shot.StartFrame!.Value, shot.EndFrame!.Value)));
        Assert.False(set.IsStale);
    }

    [Fact]
    public async Task ADifferentFrameRateKeepsTheTimesAndDropsTheFrames()
    {
        await using var harness = await Harness.CreateAsync();
        var (videoId, original, converted) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 30);
        await harness.WriteShotsAsync(original);

        await harness.Maintenance().MakePrimaryWhenSameContentAsync(videoId, converted, CancellationToken.None);
        await harness.Maintenance().DeleteFileAsync(original, deleteFromDisk: false, CancellationToken.None);

        var set = await harness.GetShotsAsync(converted);
        Assert.Null(set!.Fps);
        Assert.Null(set.FrameCount);
        Assert.Equal([(0d, 5d, (int?)null), (5d, 10d, null)], set.Shots!.Select(shot => (shot.StartSec, shot.EndSec, shot.StartFrame)));
    }

    [Fact]
    public async Task AReplacementWithItsOwnShotsKeepsThem()
    {
        await using var harness = await Harness.CreateAsync();
        var (videoId, original, converted) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25);
        await harness.WriteShotsAsync(original);
        await harness.WriteShotsAsync(converted, cut: 3);

        await harness.Maintenance().MakePrimaryWhenSameContentAsync(videoId, converted, CancellationToken.None);
        await harness.Maintenance().DeleteFileAsync(original, deleteFromDisk: false, CancellationToken.None);

        Assert.Equal(3d, (await harness.GetShotsAsync(converted))!.Shots![1].StartSec);
        Assert.Equal(1, await harness.CountSetsAsync());
    }

    [Fact]
    public async Task HandEditedShotsReplaceAnUneditedAnalysisAndKeepTheirEditTime()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, primary, duplicate) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25);
        var editedAt = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        await harness.WriteShotsAsync(primary, cut: 3);
        await harness.WriteShotsAsync(duplicate, editedAt: editedAt);

        await harness.Maintenance().DeleteFileAsync(duplicate, deleteFromDisk: false, CancellationToken.None);

        var set = await harness.GetShotsAsync(primary);
        Assert.Equal((5d, editedAt, 2), (set!.Shots![1].StartSec, set.EditedAt!.Value, set.Revision));
        Assert.Equal(1, await harness.CountSetsAsync());
    }

    [Fact]
    public async Task DeletingAnEquivalentDuplicateMovesItsShotsToThePrimary()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, primary, duplicate) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25);
        await harness.WriteShotsAsync(duplicate);

        var result = await harness.Maintenance().DeleteFileAsync(duplicate, deleteFromDisk: false, CancellationToken.None);

        Assert.True(result.Applied, result.Reason);
        Assert.Equal(2, (await harness.GetShotsAsync(primary))!.ShotCount);
    }

    // The same footage needs both stored perceptual hashes to match and the running times to agree to
    // within two frames or 0.1 s; anything looser would shift every cut.
    [Theory]
    [InlineData(10.05, Phash, true)]
    [InlineData(10.9, Phash, false)]
    [InlineData(10, null, false)]
    [InlineData(10, "ff00ff00ff00ff00", false)]
    public async Task ShotsMoveOnlyOntoTheSameTimeline(double duplicateDuration, string? duplicatePhash, bool moves)
    {
        await using var harness = await Harness.CreateAsync();
        var (_, primary, duplicate) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25, convertedDuration: duplicateDuration, convertedPhash: duplicatePhash);
        await harness.WriteShotsAsync(duplicate, duration: duplicateDuration);

        await harness.Maintenance().DeleteFileAsync(duplicate, deleteFromDisk: false, CancellationToken.None);

        Assert.Equal(moves, await harness.GetShotsAsync(primary) is not null);
        Assert.Equal(moves ? 1 : 0, await harness.CountSetsAsync());
    }

    [Fact]
    public async Task ShotsMeasuredOnOlderContentsStayWithTheirFile()
    {
        await using var harness = await Harness.CreateAsync();
        var (_, primary, duplicate) = await harness.SeedVideoAsync(originalFrameRate: 25, convertedFrameRate: 25);
        await harness.WriteShotsAsync(duplicate);
        await harness.SetFileSizeAsync(duplicate, 900);

        await harness.Maintenance().DeleteFileAsync(duplicate, deleteFromDisk: false, CancellationToken.None);

        Assert.Null(await harness.GetShotsAsync(primary));
        Assert.Equal(0, await harness.CountSetsAsync());
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly SqliteConnection _anchor;
        private readonly DbContextOptions<CoveContext> _options;

        private Harness(SqliteConnection anchor, DbContextOptions<CoveContext> options)
        {
            _anchor = anchor;
            _options = options;
        }

        public static async Task<Harness> CreateAsync()
        {
            var connectionString = $"Data Source=shot-carry-over-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Foreign Keys=True";
            var anchor = new SqliteConnection(connectionString);
            await anchor.OpenAsync();
            var harness = new Harness(anchor, new DbContextOptionsBuilder<CoveContext>().UseSqlite(connectionString).Options);
            await using var db = harness.CreateContext();
            await db.Database.EnsureCreatedAsync();
            return harness;
        }

        public CoveContext CreateContext() => new(_options);

        public VideoFileMaintenanceService Maintenance()
            => new(
                CreateContext(),
                NoOp<IFingerprintService>.Create(),
                NoOp<ISegmentSpanCacheInvalidator>.Create(),
                new EventBus(),
                new VideoGeneratedAssetCoordinator(),
                NullLogger<VideoFileMaintenanceService>.Instance);

        /// <summary>A video whose primary file is the original, plus a second file of the same footage.</summary>
        public async Task<(int VideoId, int Original, int Converted)> SeedVideoAsync(
            double originalFrameRate,
            double convertedFrameRate,
            double convertedDuration = 10,
            string? convertedPhash = Phash)
        {
            await using var db = CreateContext();
            var folder = new Folder { Path = $"/library/carry-over/{Guid.NewGuid():N}" };
            var original = new VideoFile { ParentFolder = folder, Basename = "original.mp4", Duration = 10, FrameRate = originalFrameRate, Size = 1_000 };
            var video = new Video { Title = "Carry-over", Files = [original] };
            db.Videos.Add(video);
            await db.SaveChangesAsync();
            var converted = new VideoFile { VideoId = video.Id, ParentFolder = folder, Basename = "converted.mp4", Duration = convertedDuration, FrameRate = convertedFrameRate, Size = 800 };
            db.VideoFiles.Add(converted);
            await db.SaveChangesAsync();
            db.FileFingerprints.Add(new FileFingerprint { FileId = original.Id, Type = "phash", Value = Phash });
            if (convertedPhash is not null)
                db.FileFingerprints.Add(new FileFingerprint { FileId = converted.Id, Type = "phash", Value = convertedPhash });
            await db.SaveChangesAsync();
            return (video.Id, original.Id, converted.Id);
        }

        public async Task WriteShotsAsync(int fileId, double cut = 5, double duration = 10, DateTime? editedAt = null)
        {
            await using var db = CreateContext();
            var result = await new VideoShotService(db).WriteSetAsync(new VideoShotSetWrite
            {
                FileId = fileId,
                SourceKey = "ext:ai.shots",
                Fps = 25,
                DurationSec = duration,
                EditedAt = editedAt,
                Shots =
                [
                    new VideoShotInput { StartSec = 0, EndSec = cut, StartFrame = 0, EndFrame = (int)(cut * 25) },
                    new VideoShotInput { StartSec = cut, EndSec = duration, StartFrame = (int)(cut * 25), EndFrame = (int)(duration * 25) },
                ],
            });
            Assert.Equal(VideoShotWriteOutcome.Written, result.Outcome);
        }

        public async Task<VideoShotSetDto?> GetShotsAsync(int fileId)
        {
            await using var db = CreateContext();
            return await new VideoShotService(db).GetForFileAsync(fileId);
        }

        public async Task<int> CountSetsAsync()
        {
            await using var db = CreateContext();
            return await db.VideoShotSets.CountAsync();
        }

        /// <summary>What a rescan does when a file's contents change in place.</summary>
        public async Task SetFileSizeAsync(int fileId, long size)
        {
            await using var db = CreateContext();
            await db.VideoFiles.Where(file => file.Id == fileId).ExecuteUpdateAsync(update => update.SetProperty(file => file.Size, size));
        }

        public async ValueTask DisposeAsync() => await _anchor.DisposeAsync();
    }

    private class NoOp<T> : DispatchProxy where T : class
    {
        public static T Create() => DispatchProxy.Create<T, NoOp<T>>();

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType;
            if (returnType is null || returnType == typeof(void))
                return null;
            if (returnType == typeof(Task))
                return Task.CompletedTask;
            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var result = returnType.GetGenericArguments()[0];
                var value = result.IsValueType ? Activator.CreateInstance(result) : null;
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(result).Invoke(null, [value]);
            }
            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}
