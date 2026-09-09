using JobInfo = Cove.Core.Interfaces.JobInfo;
using System.Text.Json;
using Cove.Api.Controllers;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class MetadataExportTests
{
    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    [InlineData(601)]
    public async Task Export_WritesBeforeMaterializingTheWholeLibrary_AndPreservesVideoRelationships(int count)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-export-test-{Guid.NewGuid():N}");
        using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        var guard = new ExportReadGuard(directory);
        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(options => options.UseSqlite(connection).AddInterceptors(guard));
        await using var provider = services.BuildServiceProvider();
        try
        {
            using (var scope = provider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                var folder = new Folder { Path = "/export-test" };
                var studio = new Studio { Name = "Export studio" };
                var tag = new Tag { Name = "Export tag" };
                var performer = new Performer { Name = "Export performer" };
                db.Videos.AddRange(Enumerable.Range(1, count).Select(index => new Video
                {
                    Id = index * 3,
                    Title = $"Export video {index}",
                    Studio = studio,
                    VideoTags = [new VideoTag { Tag = tag }],
                    VideoPerformers = [new VideoPerformer { Performer = performer }],
                    Files = [new VideoFile
                    {
                        ParentFolder = folder,
                        Basename = $"video-{index}.mp4",
                        Fingerprints = [new FileFingerprint { Type = "md5", Value = "export-fingerprint" }],
                    }],
                }));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            guard.Enabled = true;
            var jobs = new CapturingJobService();
            var controller = new MetadataController(null!, jobs, null!, null!,
                provider.GetRequiredService<IServiceScopeFactory>(), null!,
                new CoveConfiguration { GeneratedPath = directory }, new EventBus(),
                NullLogger<MetadataController>.Instance);
            controller.StartExport(null);
            await jobs.Work!(new Progress(), TestContext.Current.CancellationToken);

            var file = Assert.Single(Directory.GetFiles(Path.Combine(directory, "export"), "*.json"));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            var root = document.RootElement;
            Assert.Equal(6, root.EnumerateObject().Count());
            var videos = root.GetProperty("videos").EnumerateArray().ToArray();
            Assert.Equal(count, videos.Length);
            Assert.Equal(Enumerable.Range(1, count).Select(index => index * 3), videos.Select(video => video.GetProperty("id").GetInt32()));
            foreach (var video in videos)
            {
                Assert.Equal("Export studio", video.GetProperty("studio").GetProperty("name").GetString());
                Assert.Equal("Export tag", video.GetProperty("videoTags")[0].GetProperty("tag").GetProperty("name").GetString());
                Assert.Equal("Export performer", video.GetProperty("videoPerformers")[0].GetProperty("performer").GetProperty("name").GetString());
                Assert.Single(video.GetProperty("files").EnumerateArray());
                Assert.Equal("export-fingerprint", video.GetProperty("files")[0].GetProperty("fingerprints")[0].GetProperty("value").GetString());
            }
            Assert.Empty(root.GetProperty("galleries").EnumerateArray());
            Assert.Empty(root.GetProperty("groups").EnumerateArray());
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, "success")]
    [InlineData(true, "success")]
    [InlineData(true, "replace")]
    [InlineData(true, "cancel")]
    [InlineData(true, "fail")]
    public async Task Export_HonorsSelection_AndPublishesOnlyCompleteFiles(bool includeTags, string outcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-export-test-{Guid.NewGuid():N}");
        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(options => options.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        await using var provider = services.BuildServiceProvider();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            var jobs = new CapturingJobService();
            var controller = new MetadataController(null!, jobs, null!, null!,
                provider.GetRequiredService<IServiceScopeFactory>(), null!,
                new CoveConfiguration { GeneratedPath = directory }, new EventBus(),
                NullLogger<MetadataController>.Instance);
            controller.StartExport(new ExportOptionsDto
            {
                IncludeVideos = false, IncludePerformers = false, IncludeStudios = false,
                IncludeTags = includeTags, IncludeGalleries = false, IncludeGroups = false,
            });
            var progress = new Progress((_, message) =>
            {
                if (message != "Finalizing export file...") return;
                if (outcome == "replace")
                {
                    // Exports retain their second-resolution names. A same-second export
                    // must replace the old result atomically rather than fail at publication.
                    var temporaryFile = Assert.Single(Directory.GetFiles(Path.Combine(directory, "export")));
                    var destination = temporaryFile[..(temporaryFile.IndexOf(".json", StringComparison.Ordinal) + 5)];
                    File.WriteAllText(destination, "previous export");
                }
                if (outcome == "cancel") cancellation.Cancel();
                if (outcome == "fail") throw new IOException("Injected export failure");
            });
            var run = () => jobs.Work!(progress, cancellation.Token);
            if (outcome == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(run);
            else if (outcome == "fail")
                await Assert.ThrowsAsync<IOException>(run);
            else
                await run();

            var files = Directory.GetFiles(Path.Combine(directory, "export"));
            if (outcome is "cancel" or "fail")
                Assert.Empty(files);
            else
            {
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Assert.Single(files), TestContext.Current.CancellationToken));
                Assert.Equal(includeTags ? ["tags"] : Array.Empty<string>(), document.RootElement.EnumerateObject().Select(property => property.Name));
                if (includeTags) Assert.Empty(document.RootElement.GetProperty("tags").EnumerateArray());
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    // Detect eager library loading without depending on GC timing or machine memory limits.
    private sealed class ExportReadGuard(string directory) : IMaterializationInterceptor
    {
        public bool Enabled { get; set; }
        private int _videosSinceWrite;
        private long _lastLength;

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            if (!Enabled || entity is not Video) return entity;
            var exportDirectory = Path.Combine(directory, "export");
            var length = Directory.Exists(exportDirectory)
                ? Directory.GetFiles(exportDirectory).Sum(path => new FileInfo(path).Length)
                : 0;
            if (length > _lastLength) _videosSinceWrite = 0;
            _lastLength = length;
            Assert.True(++_videosSinceWrite <= 256, "Export materialized more than 256 videos without writing output.");
            return entity;
        }
    }

    private sealed class Progress(Action<double, string?>? report = null) : IJobProgress
    {
        public void Report(double progress, string? subTask = null) => report?.Invoke(progress, subTask);
    }

    private sealed class CapturingJobService : IJobService
    {
        public Func<IJobProgress, CancellationToken, Task>? Work { get; private set; }
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Work = work;
            return "export-test";
        }
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
}
