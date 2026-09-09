using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;

namespace Cove.Tests;

public sealed class ImageDeletionJobTests
{
    [Fact]
    public async Task FailedEntityCommitDoesNotMakeItsPhysicalPathEligibleForDeletion()
    {
        var mediaDirectory = Path.Combine(Path.GetTempPath(), $"cove-image-delete-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaDirectory);
        var mediaPath = Path.Combine(mediaDirectory, "must-survive.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4]);
        var interceptor = new FailRequestedSaveInterceptor();

        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source=ImageDeletionCommitFailureTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
                .AddInterceptors(interceptor)
                .Options;
            await using var db = new CoveContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            var image = new Image
            {
                Title = "Survivor",
                Files =
                [
                    new ImageFile
                    {
                        Basename = Path.GetFileName(mediaPath),
                        ParentFolder = new Folder { Path = mediaDirectory, ModTime = DateTime.UtcNow },
                        Path = mediaPath,
                        Size = 4,
                        ModTime = DateTime.UtcNow,
                    },
                ],
            };
            db.Images.Add(image);
            await db.SaveChangesAsync();
            var deletionContext = new BulkDeletionExecutionContext();
            var service = new ImageDeletionService(db, new CustomFieldService(db), null!);
            interceptor.FailNextSave = true;

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(
                image.Id,
                deleteFile: true,
                deleteGenerated: false,
                deletionContext,
                CancellationToken.None));

            db.ChangeTracker.Clear();
            Assert.True(await db.Images.AnyAsync(item => item.Id == image.Id));
            Assert.True(File.Exists(mediaPath));
            Assert.Empty(deletionContext.GetPhysicalFiles());
        }
        finally
        {
            Directory.Delete(mediaDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task JobDeletesThePhysicalFileFromAnIsolatedLibraryPath()
    {
        var mediaDirectory = Path.Combine(Path.GetTempPath(), $"cove-image-delete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(mediaDirectory);
        var mediaPath = Path.Combine(mediaDirectory, "delete-me.jpg");
        await File.WriteAllBytesAsync(mediaPath, [1, 2, 3, 4]);

        try
        {
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source=ImageDeletionPhysicalFileTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
                .Options;
            await using var db = new CoveContext(options);
            await db.Database.OpenConnectionAsync();
            await db.Database.EnsureCreatedAsync();
            var folder = new Folder { Path = mediaDirectory, ModTime = DateTime.UtcNow };
            var image = new Image
            {
                Title = "Disposable",
                Files =
                [
                    new ImageFile
                    {
                        Basename = Path.GetFileName(mediaPath),
                        ParentFolder = folder,
                        Path = mediaPath,
                        Size = 4,
                        ModTime = DateTime.UtcNow,
                    },
                ],
            };
            db.Images.Add(image);
            await db.SaveChangesAsync();

            var jobs = new CapturingJobService();
            var eventBus = new EventBus();
            using var services = new ServiceCollection()
                .AddScoped(_ => new CoveContext(options))
                .AddSingleton<IEventBus>(eventBus)
                .AddScoped<IImageRepository, ImageRepository>()
                .AddScoped<CustomFieldService>()
                .AddScoped<ImageDeletionService>(provider => new ImageDeletionService(
                    provider.GetRequiredService<CoveContext>(),
                    provider.GetRequiredService<CustomFieldService>(),
                    null!))
                .AddScoped<BulkEntityDeletionService>(provider => new BulkEntityDeletionService(
                    provider.GetRequiredService<CoveContext>(),
                    provider.GetRequiredService<CustomFieldService>(),
                    provider.GetRequiredService<ImageDeletionService>(),
                    null!,
                    null!,
                    provider.GetRequiredService<IEventBus>()))
                .BuildServiceProvider();
            var deletionJobs = new BulkDeletionJobService(
                jobs,
                services.GetRequiredService<IServiceScopeFactory>(),
                new CoveConfiguration { MaxParallelTasks = 2 });

            deletionJobs.Start(null, BulkDeletionEntityKind.Image, [image.Id], deleteFiles: true);
            await jobs.RunAsync();

            Assert.False(File.Exists(mediaPath));
            Assert.False(await db.Images.AnyAsync());
        }
        finally
        {
            Directory.Delete(mediaDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task JobDeletesImagesAsObservableUnitsAndSkipsMissingIds()
    {
        var options = new DbContextOptionsBuilder<CoveContext>()
            .UseSqlite($"Data Source=ImageDeletionJobTests-{Guid.NewGuid():N};Mode=Memory;Cache=Shared")
            .Options;
        await using var db = new CoveContext(options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var first = new Image { Title = "First" };
        var second = new Image { Title = "Second" };
        db.Images.AddRange(first, second);
        await db.SaveChangesAsync();

        var eventBus = new EventBus();
        var deletedIds = new ConcurrentBag<int>();
        using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
        {
            if (evt.Type == EventType.ImageDeleted)
                deletedIds.Add(evt.EntityId);
        });
        var jobs = new CapturingJobService();
        var services = new ServiceCollection()
            .AddScoped(_ => new CoveContext(options))
            .AddSingleton<IEventBus>(eventBus)
            .AddScoped<IImageRepository, ImageRepository>()
            .AddScoped<CustomFieldService>()
            .AddScoped<ImageDeletionService>(provider => new ImageDeletionService(
                provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(),
                null!))
            .AddScoped<BulkEntityDeletionService>(provider => new BulkEntityDeletionService(
                provider.GetRequiredService<CoveContext>(),
                provider.GetRequiredService<CustomFieldService>(),
                provider.GetRequiredService<ImageDeletionService>(),
                null!,
                null!,
                provider.GetRequiredService<IEventBus>()))
            .BuildServiceProvider();
        var deletionJobs = new BulkDeletionJobService(
            jobs,
            services.GetRequiredService<IServiceScopeFactory>(),
            // SQLite serializes writes; production PostgreSQL parallelism is covered separately by
            // the configured-worker tests.
            new CoveConfiguration { MaxParallelTasks = 1 });

        var queued = deletionJobs.Start(null, BulkDeletionEntityKind.Image, [first.Id, first.Id, -1, 999, second.Id], deleteFiles: false, deleteGenerated: false);
        await jobs.RunAsync();

        Assert.Equal("image-delete-job", queued.JobId);
        Assert.Equal("image-bulk-delete", jobs.Type);
        Assert.Equal([first.Id, second.Id], deletedIds.Order());
        Assert.False(await db.Images.AnyAsync());
        Assert.Equal(2, jobs.Progress.Units.Count(unit => unit.Outcome == JobUnitOutcome.Succeeded));
        Assert.Single(jobs.Progress.Units, unit => unit.Outcome == JobUnitOutcome.Skipped);
        Assert.Contains("skipped IDs: 999", jobs.Progress.Summary, StringComparison.Ordinal);
    }

    private sealed class CapturingJobService : IJobService
    {
        private Func<IJobProgress, CancellationToken, Task>? _work;
        public string? Type { get; private set; }
        public CapturingProgress Progress { get; } = new();

        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Type = type;
            _work = work;
            return "image-delete-job";
        }

        public Task RunAsync() => _work!(Progress, CancellationToken.None);
        public bool Cancel(string jobId) => false;
        public bool ReorderQueued(string jobId, string? beforeJobId) => false;
        public JobInfo? GetJob(string jobId) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }

    private sealed class FailRequestedSaveInterceptor : SaveChangesInterceptor
    {
        public bool FailNextSave { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new InvalidOperationException("Injected save failure");
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CapturingProgress : IJobProgress
    {
        public List<CapturingUnit> Units { get; } = [];
        public string Summary { get; private set; } = string.Empty;
        public void Report(double progress, string? subTask = null) { }
        public void SetSummary(string summary) => Summary = summary;
        public IJobUnit StartUnit(string unitId, string? label = null)
        {
            var unit = new CapturingUnit();
            Units.Add(unit);
            return unit;
        }
    }

    private sealed class CapturingUnit : IJobUnit
    {
        public JobUnitOutcome? Outcome { get; private set; }
        public void Report(double progress, string? message = null) { }
        public void Complete(JobUnitOutcome outcome, string? message = null) => Outcome ??= outcome;
        public void Dispose() { }
    }
}
