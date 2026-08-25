using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Cove.Tests;

public sealed class BulkDeletionJobServiceTests
{
    [Fact]
    public void StartQueuesAnOwnedJobForDistinctPositiveIds()
    {
        var jobs = new CapturingJobService();
        var service = new BulkDeletionJobService(
            jobs,
            null!,
            new CoveConfiguration { MaxParallelTasks = 3 });
        var principal = new CovePrincipal
        {
            UserId = 17,
            Username = "deletion-test-user",
            Kind = PrincipalKind.User,
            Roles = new HashSet<string>(),
            Permissions = new HashSet<string> { Permissions.VideosDelete },
        };
        var owner = JobOwner.FromPrincipal(principal);

        var result = service.Start(
            principal,
            BulkDeletionEntityKind.Video,
            [4, 4, -1, 9],
            deleteFiles: true,
            deleteGenerated: false);

        Assert.Equal("bulk-delete-job", result.JobId);
        Assert.Equal(2, result.ItemCount);
        Assert.Equal(owner, jobs.Owner);
        Assert.Equal("video-bulk-delete", jobs.Type);
    }

    [Theory]
    [InlineData(-1, 12, 12)]
    [InlineData(0, 12, 1)]
    [InlineData(1, 12, 1)]
    [InlineData(3, 12, 3)]
    public void ResolveMaxParallelismUsesConfiguration(int configured, int processors, int expected)
    {
        var config = new CoveConfiguration { MaxParallelTasks = configured };
        Assert.Equal(expected, BulkDeletionJobService.ResolveMaxParallelism(config, processors));
    }

    [Fact]
    public async Task CancellationDuringVideoNormalizationStillReleasesKeeperReservations()
    {
        var connectionString = $"Data Source=file:bulk-delete-cancel-{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite(connectionString)
            .Options;
        await using var anchor = new CoveContext(options);
        await anchor.Database.OpenConnectionAsync();
        await anchor.Database.EnsureCreatedAsync();
        var first = new Video { Title = "First selected video" };
        var second = new Video { Title = "Second selected video" };
        var search = new DuplicateSearch
        {
            Status = DuplicateSearchStatus.Completed,
            MatchType = "phash",
        };
        anchor.AddRange(first, second, search);
        await anchor.SaveChangesAsync();
        anchor.DuplicateDeletionKeeperReservations.Add(new DuplicateDeletionKeeperReservation
        {
            SearchId = search.Id,
            VideoId = first.Id,
        });
        await anchor.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        await using var provider = services.BuildServiceProvider();
        var jobs = new CapturingJobService();
        var service = new BulkDeletionJobService(
            jobs,
            provider.GetRequiredService<IServiceScopeFactory>(),
            new CoveConfiguration { MaxParallelTasks = 1 });
        service.Start(
            null,
            BulkDeletionEntityKind.Video,
            [first.Id, second.Id],
            duplicateSearchId: search.Id);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.RunAsync(cancellation.Token));

        anchor.ChangeTracker.Clear();
        Assert.False(await anchor.DuplicateDeletionKeeperReservations.AnyAsync(item => item.SearchId == search.Id));
    }

    private sealed class CapturingJobService : IJobService
    {
        private Func<IJobProgress, CancellationToken, Task>? _work;
        public JobOwner? Owner { get; private set; }
        public string? Type { get; private set; }

        public string EnqueueOwned(
            JobOwner owner,
            string type,
            string description,
            Func<IJobProgress, CancellationToken, Task> work,
            string? resultUrl = null,
            bool exclusive = true)
        {
            Owner = owner;
            Type = type;
            _work = work;
            return "bulk-delete-job";
        }

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Type = type;
            _work = work;
            return "global-bulk-delete-job";
        }
        public Task RunAsync(CancellationToken ct) => _work!(new NullProgress(), ct);
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class NullProgress : IJobProgress
    {
        public void Report(double progress, string? subTask = null)
        {
        }
    }
}
