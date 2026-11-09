using System.Collections;
using System.Data.Common;
using System.Text.RegularExpressions;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Tests.Postgres;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

[RequiresPostgres]
public sealed class CleanServiceMemoryTests
{
    [Theory]
    [InlineData("dry-run")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task InspectsBoundedPagesAndDefersDeletionUntilInspectionCompletes(string outcome)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var guard = new InspectionGuard();
        var options = database.CreateOptions(builder => builder.AddInterceptors(guard));
        var services = new ServiceCollection();
        services.AddScoped(_ => new CoveContext(options));
        AddDeletionServices(services);
        await using var provider = services.BuildServiceProvider();
        await using (var db = new CoveContext(options))
        {
            db.Videos.AddRange(Enumerable.Range(0, 601).Select(index => new Video { Title = $"orphan {index}" }));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        guard.Enabled = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (outcome == "cancel") guard.AfterInspect = cancellation.Cancel;
        var jobs = new CapturingJobs(guard, cancellation.Token);
        var service = new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance);
        service.StartClean(dryRun: outcome == "dry-run");
        if (outcome == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => jobs.Completion);
        else
        {
            await jobs.Completion;
            Assert.True(guard.Inspected >= 601);
        }
        guard.Enabled = false;
        await using var verify = new CoveContext(options);
        Assert.Equal(outcome == "delete" ? 0 : 601, await verify.Videos.CountAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanPreservesClipsAndScopeWhileRemovingMissingArchiveContentAcrossBatches(bool scoped)
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var guard = new InspectionGuard { RequiredInspectionCount = 1203 };
            var options = database.CreateOptions(builder => builder.AddInterceptors(guard));
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services);
            await using var provider = services.BuildServiceProvider();
            var selected = Path.Combine(root, "selected");
            int clipId;
            await using (var db = new CoveContext(options))
            {
                var physical = new Folder { Path = selected };
                var virtualFolder = new Folder { Path = "/virtual/archive-content" };
                var outside = new Folder { Path = root };
                db.Folders.AddRange(physical, virtualFolder, outside);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                var archive = new GalleryFile { ParentFolder = physical, Basename = "missing.zip" };
                db.Galleries.Add(new Gallery { Title = "archive", Files = [archive] });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                db.Images.AddRange(Enumerable.Range(0, 601).Select(index => new Image
                {
                    Title = $"virtual image {index}",
                    Files = [new ImageFile { ParentFolder = virtualFolder, Basename = $"{index}.jpg", ZipFileId = archive.Id }],
                }));
                db.Images.AddRange(Enumerable.Range(0, 600).Select(index => new Image
                {
                    Title = $"outside image {index}", Files = [new ImageFile { ParentFolder = outside, Basename = $"{index}.jpg" }],
                }));
                await File.WriteAllBytesAsync(Path.Combine(root, "live.mp4"), [1], TestContext.Current.CancellationToken);
                var parent = new Video { Title = "live parent", Files = [new VideoFile { ParentFolder = outside, Basename = "live.mp4" }] };
                var clip = new Video { Title = "clip", ParentVideo = parent };
                db.Videos.AddRange(parent, clip);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                clipId = clip.Id;
                await db.Images.ExecuteUpdateAsync(setters => setters.SetProperty(image => image.FileCount, 99), TestContext.Current.CancellationToken);
            }
            guard.Enabled = true;
            var jobs = new CapturingJobs(guard, TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance)
                .StartClean(paths: scoped ? [selected] : null);
            await jobs.Completion;
            guard.Enabled = false;
            await using var verify = new CoveContext(options);
            Assert.Equal(scoped ? 600 : 0, await verify.Images.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(scoped ? 600 : 0, await verify.Images.CountAsync(image => image.FileCount == 1, TestContext.Current.CancellationToken));
            Assert.Equal(scoped ? 600 : 0, await verify.ImageFiles.CountAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.Galleries.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Empty(await verify.GalleryFiles.ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(2, await verify.Videos.CountAsync(TestContext.Current.CancellationToken));
            Assert.True(await verify.Videos.AnyAsync(video => video.Id == clipId, TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CleanRemovesOrphansThroughTheUserDeletionPath()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-deps-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var options = database.CreateOptions();
            var eventBus = new EventBus();
            var thumbnails = new DeletionSafetyTests.RecordingThumbnailService();
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services, eventBus, thumbnails);
            await using var provider = services.BuildServiceProvider();
            var deletedEventIds = new List<int>();
            using var subscription = eventBus.Subscribe<EntityEvent>(evt =>
            {
                if (evt.Type == EventType.VideoDeleted)
                    deletedEventIds.Add(evt.EntityId);
            });
            int orphanId, liveId, clipId;
            await using (var db = new CoveContext(options))
            {
                await File.WriteAllBytesAsync(Path.Combine(root, "live.mp4"), [1], TestContext.Current.CancellationToken);
                var folder = new Folder { Path = root };
                var orphan = new Video { Title = "orphan", Files = [new VideoFile { ParentFolder = folder, Basename = "gone.mp4" }] };
                var clip = new Video { Title = "orphan clip", ParentVideo = orphan };
                var live = new Video { Title = "live", Files = [new VideoFile { ParentFolder = folder, Basename = "live.mp4" }] };
                var tag = new Tag { Name = "tag" };
                var definition = new CustomFieldDefinition { Key = "clean_fixture", Label = "Clean fixture", EntityTypes = [CustomFieldEntityTypes.Video] };
                db.AddRange(orphan, clip, live, tag, definition);
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
                (orphanId, liveId, clipId) = (orphan.Id, live.Id, clip.Id);
                foreach (var hostId in new[] { orphanId, clipId, liveId })
                {
                    db.AddRange(
                        new TagApplication { HostType = AffinityHostType.Video, HostId = hostId, TagId = tag.Id, SourceKey = "test" },
                        new Segment { HostType = SegmentHostType.Video, HostId = hostId, StartSec = 1, SourceKey = "test" },
                        new CustomFieldValue { DefinitionId = definition.Id, EntityType = CustomFieldEntityTypes.Video, EntityId = hostId, TextValue = "x" });
                }
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var jobs = new CapturingJobs(new InspectionGuard(), TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance).StartClean();
            await jobs.Completion;

            await using var verify = new CoveContext(options);
            Assert.Equal([liveId], await verify.Videos.Select(video => video.Id).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.TagApplications.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.Segments.Select(item => item.HostId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal([liveId], await verify.CustomFieldValues.Select(item => item.EntityId).ToListAsync(TestContext.Current.CancellationToken));
            Assert.Equal(new[] { orphanId, clipId }.Order(), deletedEventIds.Order());
            Assert.Equal(new[] { orphanId, clipId }.Order(), thumbnails.DeletedVideoIds.Order());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task CleanFailureDoesNotLeakIntoTheNextDeletionInTheBatch()
    {
        await using var database = await CovePostgresDatabase.CreateAsync(TestContext.Current.CancellationToken);
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-failure-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var failure = new FirstImageDeleteFailure();
            var options = database.CreateOptions(builder => builder.AddInterceptors(failure));
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            AddDeletionServices(services);
            await using var provider = services.BuildServiceProvider();
            await using (var db = new CoveContext(options))
            {
                var folder = new Folder { Path = root };
                db.AddRange(
                    new Image { Title = "first", Files = [new ImageFile { ParentFolder = folder, Basename = "first.jpg" }] },
                    new Image { Title = "second", Files = [new ImageFile { ParentFolder = folder, Basename = "second.jpg" }] });
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            failure.Armed = true;
            var jobs = new CapturingJobs(new InspectionGuard(), TestContext.Current.CancellationToken);
            new CleanService(jobs, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<CleanService>.Instance).StartClean();
            await jobs.Completion;

            // The failed image stays for the next clean; the second deletion must not replay its staged delete.
            await using var verify = new CoveContext(options);
            Assert.NotNull(failure.FailedImageId);
            Assert.Equal([failure.FailedImageId.Value], await verify.Images.Select(image => image.Id).ToListAsync(TestContext.Current.CancellationToken));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class FirstImageDeleteFailure : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public int? FailedImageId { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && FailedImageId is null
                && eventData.Context!.ChangeTracker.Entries<Image>().FirstOrDefault(entry => entry.State == EntityState.Deleted) is { } deleted)
            {
                FailedImageId = deleted.Entity.Id;
                throw new DbUpdateException("Injected save failure");
            }
            return ValueTask.FromResult(result);
        }
    }

    private static void AddDeletionServices(IServiceCollection services, IEventBus? eventBus = null,
        IThumbnailService? thumbnails = null)
    {
        services.AddScoped<CustomFieldService>();
        services.AddScoped<IThumbnailService>(_ => thumbnails ?? new DeletionSafetyTests.RecordingThumbnailService());
        services.AddScoped<IBlobService>(provider => new DeletionSafetyTests.ReferenceAwareBlobService(provider.GetRequiredService<CoveContext>()));
        services.AddScoped<ImageDeletionService>(provider => new ImageDeletionService(provider.GetRequiredService<CoveContext>(),
            provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<IThumbnailService>(),
            blobService: provider.GetRequiredService<IBlobService>()));
        services.AddSingleton(eventBus ?? new EventBus());
        services.AddScoped(provider => new BulkEntityDeletionService(provider.GetRequiredService<CoveContext>(),
            provider.GetRequiredService<CustomFieldService>(), provider.GetRequiredService<ImageDeletionService>(),
            provider.GetRequiredService<IThumbnailService>(), provider.GetRequiredService<IBlobService>(),
            provider.GetRequiredService<IEventBus>()));
    }

    private sealed class CapturingJobs(InspectionGuard guard, CancellationToken ct) : IJobService
    {
        public Task Completion { get; private set; } = Task.CompletedTask;
        public string Enqueue(string type, string description, Func<IJobProgress, CancellationToken, Task> work, bool exclusive = true)
        {
            Completion = work(new Progress(guard), ct);
            return "clean-test";
        }
        public bool Cancel(string id) => false;
        public bool ReorderQueued(string id, string? before) => false;
        public JobInfo? GetJob(string id) => null;
        public IReadOnlyList<JobInfo> GetAllJobs() => [];
        public IReadOnlyList<JobInfo> GetJobHistory() => [];
    }
    private sealed class Progress(InspectionGuard guard) : IJobProgress
    {
        public void Report(double progress, string? subTask = null) => guard.Inspect();
    }
    private sealed class InspectionGuard : DbCommandInterceptor
    {
        public bool Enabled { get; set; }
        public int Inspected { get; private set; }
        private int unreadInspection;
        public int RequiredInspectionCount { get; init; } = 601;
        private bool deleting;
        public Action? AfterInspect { get; set; }
        public void Inspect() { Inspected += unreadInspection; unreadInspection = 0; AfterInspect?.Invoke(); }
        public void ReadRow()
        {
            unreadInspection++;
            Assert.True(unreadInspection <= 256, "Clean read more than one page of videos before inspecting them.");
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            CheckDeletion(command);
            return ValueTask.FromResult(result);
        }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            // Npgsql sends a SaveChanges batch, deletions included, through a reader.
            CheckDeletion(command);
            return ValueTask.FromResult(result);
        }
        private void CheckDeletion(DbCommand command)
        {
            if (Enabled && DeleteStatement.IsMatch(command.CommandText))
            {
                Assert.True(Inspected >= RequiredInspectionCount, "Destination deletion began before inspection finished.");
                deleting = true;
            }
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DbDataReader>(Enabled && !deleting && !command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase)
                && EntityTable.IsMatch(command.CommandText) && !FilesCollection.IsMatch(command.CommandText)
                ? new GuardedReader(result, this) : result);

        // A batch can start with another statement, so a deletion is any statement in it that starts with DELETE.
        private static readonly Regex DeleteStatement = new(@"(^|;)\s*DELETE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // Npgsql quotes an identifier only when it must, so these lower-case table names appear bare.
        private static readonly Regex EntityTable = new(@"\bFROM ""?(videos|images|galleries|audios|text_documents)""?\s", RegexOptions.CultureInvariant);
        // Split queries load an entity's files in a second query joined to the page. Its rows are the page's
        // files, not more entities, so only the page query itself is counted.
        private static readonly Regex FilesCollection = new(@"\bJOIN \(\s*SELECT\b[^()]*\bFROM ""?files""?\s", RegexOptions.CultureInvariant);
    }
    private sealed class GuardedReader(DbDataReader inner, InspectionGuard guard) : DbDataReader
    {
        public override bool Read() { var result = inner.Read(); if (result) guard.ReadRow(); return result; }
        public override async Task<bool> ReadAsync(CancellationToken cancellationToken)
        { var result = await inner.ReadAsync(cancellationToken); if (result) guard.ReadRow(); return result; }
        public override bool NextResult() => inner.NextResult();
        public override Task<bool> NextResultAsync(CancellationToken cancellationToken) => inner.NextResultAsync(cancellationToken);
        public override void Close() => inner.Close();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
        public override int Depth => inner.Depth;
        public override int FieldCount => inner.FieldCount;
        public override bool HasRows => inner.HasRows;
        public override bool IsClosed => inner.IsClosed;
        public override int RecordsAffected => inner.RecordsAffected;
        public override object this[int ordinal] => inner[ordinal];
        public override object this[string name] => inner[name];
        public override string GetName(int ordinal) => inner.GetName(ordinal);
        public override string GetDataTypeName(int ordinal) => inner.GetDataTypeName(ordinal);
        public override Type GetFieldType(int ordinal) => inner.GetFieldType(ordinal);
        public override int GetOrdinal(string name) => inner.GetOrdinal(name);
        public override object GetValue(int ordinal) => inner.GetValue(ordinal);
        // Npgsql reads most columns through these; the base versions would cast GetValue and lose provider conversions.
        public override T GetFieldValue<T>(int ordinal) => inner.GetFieldValue<T>(ordinal);
        public override Task<T> GetFieldValueAsync<T>(int ordinal, CancellationToken cancellationToken) => inner.GetFieldValueAsync<T>(ordinal, cancellationToken);
        public override Task<bool> IsDBNullAsync(int ordinal, CancellationToken cancellationToken) => inner.IsDBNullAsync(ordinal, cancellationToken);
        public override int GetValues(object[] values) => inner.GetValues(values);
        public override bool IsDBNull(int ordinal) => inner.IsDBNull(ordinal);
        public override bool GetBoolean(int ordinal) => inner.GetBoolean(ordinal);
        public override byte GetByte(int ordinal) => inner.GetByte(ordinal);
        public override long GetBytes(int ordinal, long offset, byte[]? buffer, int bufferOffset, int length) => inner.GetBytes(ordinal, offset, buffer, bufferOffset, length);
        public override char GetChar(int ordinal) => inner.GetChar(ordinal);
        public override long GetChars(int ordinal, long offset, char[]? buffer, int bufferOffset, int length) => inner.GetChars(ordinal, offset, buffer, bufferOffset, length);
        public override Guid GetGuid(int ordinal) => inner.GetGuid(ordinal);
        public override short GetInt16(int ordinal) => inner.GetInt16(ordinal);
        public override int GetInt32(int ordinal) => inner.GetInt32(ordinal);
        public override long GetInt64(int ordinal) => inner.GetInt64(ordinal);
        public override float GetFloat(int ordinal) => inner.GetFloat(ordinal);
        public override double GetDouble(int ordinal) => inner.GetDouble(ordinal);
        public override string GetString(int ordinal) => inner.GetString(ordinal);
        public override decimal GetDecimal(int ordinal) => inner.GetDecimal(ordinal);
        public override DateTime GetDateTime(int ordinal) => inner.GetDateTime(ordinal);
        public override IEnumerator GetEnumerator() => ((IEnumerable)inner).GetEnumerator();
    }
}
