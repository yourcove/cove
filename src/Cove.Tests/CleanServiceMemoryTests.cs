using System.Collections;
using System.Data.Common;
using Cove.Api.Services;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

public sealed class CleanServiceMemoryTests
{
    [Theory]
    [InlineData("dry-run")]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task InspectsBoundedPagesAndDefersDeletionUntilInspectionCompletes(string outcome)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-clean-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var guard = new InspectionGuard();
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source={Path.Combine(directory, "library.db")};Pooling=False").AddInterceptors(guard).Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            await using var provider = services.BuildServiceProvider();
            await using (var db = new CoveContext(options))
            {
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CleanPreservesClipsAndScopeWhileRemovingMissingArchiveContentAcrossBatches(bool scoped)
    {
        var root = Path.Combine(Path.GetTempPath(), $"cove-clean-mixed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var guard = new InspectionGuard { RequiredInspectionCount = 1203 };
            var options = new DbContextOptionsBuilder<CoveContext>()
                .UseSqlite($"Data Source={Path.Combine(root, "library.db")};Pooling=False").AddInterceptors(guard).Options;
            var services = new ServiceCollection();
            services.AddScoped(_ => new CoveContext(options));
            await using var provider = services.BuildServiceProvider();
            var selected = Path.Combine(root, "selected");
            int clipId;
            await using (var db = new CoveContext(options))
            {
                await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
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
            if (Enabled && command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(Inspected >= RequiredInspectionCount, "Destination deletion began before inspection finished.");
                deleting = true;
            }
            return ValueTask.FromResult(result);
        }
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<DbDataReader>(Enabled && !deleting && !command.CommandText.Contains("COUNT(", StringComparison.OrdinalIgnoreCase) && new[] { "videos", "images", "galleries", "audios", "text_documents" }.Any(table => command.CommandText.Contains($"FROM \"{table}\"", StringComparison.Ordinal))
                ? new GuardedReader(result, this) : result);
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
