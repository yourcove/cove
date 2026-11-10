using JobInfo = Cove.Core.Interfaces.JobInfo;
using System.Net;
using System.Text;
using Cove.Api.Controllers;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

// Runs the bulk Identify job end to end against two metadata servers, one of which answers with an
// HTML page instead of GraphQL (issue #270).
public sealed class IdentifyJobTests
{
    private const string BrokenEndpoint = "https://broken.example/graphql";
    private const string GoodEndpoint = "https://good.example/graphql";

    [Fact]
    [RequiresPostgres]
    public async Task OneSourceFailing_StillTriesTheNextSourceAndReportsTheError()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var run = await RunIdentifyAsync(database, ["Broken Box", "Good Box"]);

        var video = await run.LoadVideoAsync();
        Assert.Equal("Remote Video", video.Title);
        var remoteId = Assert.Single(video.RemoteIds);
        Assert.Equal(GoodEndpoint, remoteId.Endpoint);
        Assert.Equal("remote-video-1", remoteId.RemoteId);

        var unit = Assert.Single(run.Progress.Units);
        Assert.Equal(JobUnitOutcome.Succeeded, unit.Outcome);
        Assert.StartsWith("Identified 1 of 1 videos, 0 unmatched.", run.Progress.Summary);
        Assert.Contains(
            $"Source errors: Broken Box: Invalid endpoint: {BrokenEndpoint} returned an HTML page (HTTP 404 Not Found) instead of a GraphQL response",
            run.Progress.Summary);
    }

    [Fact]
    [RequiresPostgres]
    public async Task EverySourceFailing_ReportsTheVideoAsFailedRatherThanUnmatched()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        await using var run = await RunIdentifyAsync(database, ["Broken Box"]);

        var video = await run.LoadVideoAsync();
        Assert.Equal("Local Video", video.Title);
        Assert.Empty(video.RemoteIds);

        var unit = Assert.Single(run.Progress.Units);
        Assert.Equal(JobUnitOutcome.Failed, unit.Outcome);
        Assert.Contains("Broken Box: Invalid endpoint", unit.Message);
        Assert.Contains("HTTP 404", unit.Message);
        Assert.StartsWith("Identified 0 of 1 videos, 1 failed, 0 unmatched.", run.Progress.Summary);
        Assert.Contains($"Broken Box: Invalid endpoint: {BrokenEndpoint}", run.Progress.Summary);
    }

    [Fact]
    public void BuildIdentifySummary_ListsEachSourceErrorOnceAndCapsTheRest()
    {
        var summary = MetadataController.BuildIdentifySummary(2, 1, 3, 6, 0, new Dictionary<string, List<string>>
        {
            ["Box A"] = ["one", "two", "three", "four", "five"],
            ["Box B"] = ["boom"],
        });

        Assert.Equal("Identified 2 of 6 videos, 1 failed, 3 unmatched. Source errors: Box A: one | two | three (+2 more); Box B: boom", summary);
    }

    private static async Task<IdentifyRun> RunIdentifyAsync(CovePostgresDatabase database, List<string> sources)
    {
        var config = new CoveConfiguration
        {
            Scraping = new ScrapingConfig
            {
                MetadataServers =
                [
                    new MetadataServerInstance { Endpoint = BrokenEndpoint, ApiKey = "key", Name = "Broken Box" },
                    new MetadataServerInstance { Endpoint = GoodEndpoint, ApiKey = "key", Name = "Good Box" },
                ],
                IdentifyDefaults = new IdentifyDefaultsConfig
                {
                    AutoApplyMinFingerprintMatches = null,
                    AutoApplyMaxDurationDifferenceSeconds = null,
                },
            },
        };

        var httpClient = new HttpClient(new TwoServerHandler());
        var services = new ServiceCollection();
        services.AddDbContext<CoveContext>(options => database.Configure(options));
        services.AddScoped(provider =>
        {
            var db = provider.GetRequiredService<CoveContext>();
            return new MetadataServerService(httpClient, config, db, new NullBlobService(), new NullVideoCoverService(),
                new TagProvenanceService(db), NullLogger<MetadataServerService>.Instance);
        });
        var provider = services.BuildServiceProvider();

        int videoId;
        using (var scope = provider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var video = new Video { Title = "Local Video" };
            var file = new VideoFile
            {
                Basename = "local-video.mp4",
                Path = "/library/local-video.mp4",
                ParentFolder = new Folder { Path = "/library" },
                Duration = 120,
            };
            file.Fingerprints.Add(new FileFingerprint { Type = "oshash", Value = "1a2b" });
            video.Files.Add(file);
            db.Videos.Add(video);
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            videoId = video.Id;
        }

        var jobs = new CapturingJobService();
        var controller = new MetadataController(null!, jobs, null!, null!,
            provider.GetRequiredService<IServiceScopeFactory>(), null!, config, new EventBus(),
            NullLogger<MetadataController>.Instance);
        controller.StartIdentify(new IdentifyOptionsDto
        {
            VideoIds = [videoId],
            Sources = sources,
            SetCoverImage = false,
        });

        var progress = new RecordingProgress();
        await jobs.Work!(progress, TestContext.Current.CancellationToken);
        return new IdentifyRun(provider, videoId, progress);
    }

    private sealed record IdentifyRun(ServiceProvider Provider, int VideoId, RecordingProgress Progress) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Provider.DisposeAsync();

        public async Task<Video> LoadVideoAsync()
        {
            using var scope = Provider.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<CoveContext>().Videos
                .Include(video => video.RemoteIds)
                .SingleAsync(video => video.Id == VideoId, TestContext.Current.CancellationToken);
        }
    }

    private sealed class TwoServerHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.Host == "broken.example")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("<!DOCTYPE html><html><body>Not here</body></html>", Encoding.UTF8, "text/html"),
                };
            }

            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var data = body.Contains("FindVideosByVideoFingerprints", StringComparison.Ordinal)
                ? $$"""{ "findVideosByVideoFingerprints": [[{{RemoteVideoJson}}]] }"""
                : $$"""{ "findVideo": {{RemoteVideoJson}} }""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{ "data": {{data}} }""", Encoding.UTF8, "application/json"),
            };
        }
    }

    private const string RemoteVideoJson = """
        {
          "id": "remote-video-1",
          "title": "Remote Video",
          "code": null,
          "details": null,
          "director": null,
          "duration": 120,
          "date": null,
          "urls": [],
          "images": [],
          "studio": null,
          "tags": [],
          "performers": [],
          "fingerprints": [
            { "algorithm": "OSHASH", "hash": "0000000000001a2b", "duration": 120 }
          ]
        }
        """;

    private sealed class RecordingProgress : IJobProgress
    {
        public List<RecordingUnit> Units { get; } = [];
        public string? Summary { get; private set; }

        public void Report(double progress, string? subTask = null)
        {
        }

        public void SetSummary(string summary) => Summary = summary;

        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            var unit = new RecordingUnit();
            Units.Add(unit);
            return unit;
        }
    }

    private sealed class RecordingUnit : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }
        public string? Message { get; private set; }

        public void Report(double progress, string? message = null)
        {
        }

        public void Complete(JobUnitOutcome outcome, string? message = null)
        {
            Outcome ??= outcome;
            Message ??= message;
        }

        public void Dispose()
        {
        }
    }

    private sealed class CapturingJobService : IJobService
    {
        public Func<IJobProgress, CancellationToken, Task>? Work { get; private set; }
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Work = work;
            return "identify-test";
        }
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullBlobService : IBlobService
    {
        public Task<string> StoreBlobAsync(Stream data, string contentType, CancellationToken ct = default)
            => Task.FromResult("blob-fixture");

        public Task<(Stream Stream, string ContentType)?> GetBlobAsync(string blobId, CancellationToken ct = default)
            => Task.FromResult<(Stream Stream, string ContentType)?>(null);

        public Task DeleteBlobAsync(string blobId, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class NullVideoCoverService : IVideoCoverService
    {
        public Task<bool> TryApplyRemoteCoverAsync(Video video, string? imageUrl, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<FetchedImage?> TryFetchImageAsync(string? imageUrl, CancellationToken ct = default)
            => Task.FromResult<FetchedImage?>(null);
    }
}
