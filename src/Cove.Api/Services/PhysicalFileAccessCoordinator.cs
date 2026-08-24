namespace Cove.Api.Services;

using System.Collections.Concurrent;
using System.Threading.Channels;
using Cove.Core.Common;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Lets file-producing scans/imports overlap while making the final reference-check-and-delete phase
/// exclusive. This closes the gap where a writer could attach a path after it was checked but before
/// the filesystem delete ran.
/// </summary>
public sealed class PhysicalFileAccessCoordinator
{
    private readonly SemaphoreSlim _turnstile = new(1, 1);
    private readonly SemaphoreSlim _readerMutex = new(1, 1);
    private readonly SemaphoreSlim _resource = new(1, 1);
    private int _readerCount;

    public static PhysicalFileAccessCoordinator Shared { get; } = new();

    public async ValueTask<IDisposable> AcquireReadAsync(CancellationToken ct)
    {
        // Passing through the writer-held turnstile prevents a steady stream of imports from starving
        // a deletion that is already waiting.
        await _turnstile.WaitAsync(ct);
        _turnstile.Release();

        await _readerMutex.WaitAsync(ct);
        try
        {
            if (_readerCount == 0)
                await _resource.WaitAsync(ct);
            _readerCount++;
            return new ReadLease(this);
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    public async ValueTask<IDisposable> AcquireWriteAsync(CancellationToken ct)
    {
        await _turnstile.WaitAsync(ct);
        try
        {
            await _resource.WaitAsync(ct);
            return new WriteLease(this);
        }
        catch
        {
            _turnstile.Release();
            throw;
        }
    }

    private void ReleaseRead()
    {
        _readerMutex.Wait();
        try
        {
            _readerCount--;
            if (_readerCount == 0)
                _resource.Release();
        }
        finally
        {
            _readerMutex.Release();
        }
    }

    private void ReleaseWrite()
    {
        _resource.Release();
        _turnstile.Release();
    }

    private sealed class ReadLease(PhysicalFileAccessCoordinator owner) : IDisposable
    {
        private PhysicalFileAccessCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseRead();
    }

    private sealed class WriteLease(PhysicalFileAccessCoordinator owner) : IDisposable
    {
        private PhysicalFileAccessCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseWrite();
    }
}

/// <summary>
/// Wakes the durable physical-deletion worker after a request commits new outbox rows. The bounded
/// channel deliberately coalesces bursts because one recovery pass drains every pending batch.
/// </summary>
public sealed class PhysicalFileDeletionRecoverySignal
{
    private readonly Channel<byte> _signals = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Notify() => _signals.Writer.TryWrite(0);

    internal async Task WaitAsync(TimeSpan maximumDelay, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(maximumDelay);
        try
        {
            await _signals.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The periodic pass is the crash/restart fallback when no in-process signal exists.
        }

        while (_signals.Reader.TryRead(out _))
        {
        }
    }
}

internal readonly record struct PhysicalFileIdentitySnapshot(
    bool Captured,
    bool Exists,
    long? Length,
    long? LastWriteTimeUtcTicks,
    long? CreationTimeUtcTicks)
{
    public static PhysicalFileIdentitySnapshot Capture(string path)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            return file.Exists
                ? new(true, true, file.Length, file.LastWriteTimeUtc.Ticks, file.CreationTimeUtc.Ticks)
                : new(true, false, null, null, null);
        }
        catch
        {
            // An unknown identity must never be treated as permission to unlink a future pathname.
            return new(false, false, null, null, null);
        }
    }

    public static PhysicalFileIdentitySnapshot FromPending(PendingPhysicalFileDeletion item)
        => new(
            item.IdentityCaptured,
            item.ExpectedExists,
            item.ExpectedLength,
            item.ExpectedLastWriteTimeUtcTicks,
            item.ExpectedCreationTimeUtcTicks);
}

/// <summary>
/// Performs the final database reference check and physical delete while holding the writer side of
/// <see cref="PhysicalFileAccessCoordinator"/>. Every metadata deletion path uses this primitive so
/// scans, imports, moves, and deletes agree on one safety boundary.
/// </summary>
public sealed class PhysicalFileDeletionService(
    CoveContext db,
    PhysicalFileAccessCoordinator? coordinator = null,
    ILogger<PhysicalFileDeletionService>? logger = null)
{
    private readonly PhysicalFileAccessCoordinator _coordinator = coordinator ?? PhysicalFileAccessCoordinator.Shared;

    public async Task<BulkPhysicalDeletionResult> DeleteUnreferencedAsync(
        IEnumerable<string> candidatePaths,
        int maxParallelism,
        CancellationToken ct)
    {
        var candidates = candidatePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(FilesystemPaths.PathComparer)
            .Select(path => new PhysicalPathDeletionCandidate(path, [PhysicalFileIdentitySnapshot.Capture(path)]))
            .ToArray();
        var result = await DeletePathsAsync(candidates, maxParallelism, ct);
        return new BulkPhysicalDeletionResult(result.Deleted, result.Failures.Count);
    }

    public async Task<BulkPhysicalDeletionResult> ProcessPendingAsync(
        Guid? batchId,
        int maxParallelism,
        CancellationToken ct)
    {
        long cursor = 0;
        var deleted = 0;
        var failed = 0;
        while (true)
        {
            // Serialize the complete fetch/delete/ack cycle. The recovery worker and an originating
            // job may otherwise materialize the same outbox row in different DbContexts, after which
            // the second successful acknowledgement would fail optimistic concurrency.
            using var lease = await _coordinator.AcquireWriteAsync(ct);
            var batchQuery = db.PendingPhysicalFileDeletions.AsQueryable();
            if (batchId.HasValue)
                batchQuery = batchQuery.Where(item => item.BatchId == batchId.Value);
            var seed = await batchQuery
                .Where(item => item.Id > cursor)
                .OrderBy(item => item.Id)
                .Take(2_000)
                .ToListAsync(ct);
            if (seed.Count == 0)
                break;

            cursor = seed[^1].Id;
            var seedPaths = seed.Select(item => item.Path).Distinct(FilesystemPaths.PathComparer).ToArray();
            // Load every outbox entry for the selected paths together. If a path was staged twice and
            // the identities disagree, the conservative result is to preserve the current file.
            var pending = await batchQuery
                .Where(item => seedPaths.Contains(item.Path))
                .ToListAsync(ct);
            var candidates = pending
                .GroupBy(item => item.Path, FilesystemPaths.PathComparer)
                .Select(group => new PhysicalPathDeletionCandidate(
                    group.Key,
                    group.Select(PhysicalFileIdentitySnapshot.FromPending).Distinct().ToArray()))
                .ToArray();
            var result = await DeletePathsWithinWriterLeaseAsync(candidates, maxParallelism, ct);
            deleted += result.Deleted;
            failed += result.Failures.Count;
            var attemptedAt = DateTime.UtcNow;
            foreach (var item in pending)
            {
                if (result.Failures.TryGetValue(item.Path, out var error))
                {
                    item.AttemptCount++;
                    item.LastAttemptAt = attemptedAt;
                    item.LastError = error[..Math.Min(error.Length, 2_000)];
                }
                else
                {
                    db.PendingPhysicalFileDeletions.Remove(item);
                }
            }
            await db.SaveChangesAsync(ct);
        }
        return new BulkPhysicalDeletionResult(deleted, failed);
    }

    private async Task<PhysicalPathDeletionResult> DeletePathsAsync(
        IEnumerable<PhysicalPathDeletionCandidate> candidatePaths,
        int maxParallelism,
        CancellationToken ct)
    {
        var candidates = candidatePaths
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .GroupBy(candidate => candidate.Path, FilesystemPaths.PathComparer)
            .Select(group => new PhysicalPathDeletionCandidate(
                group.Key,
                group.SelectMany(candidate => candidate.ExpectedIdentities).Distinct().ToArray()))
            .ToArray();
        if (candidates.Length == 0)
            return new PhysicalPathDeletionResult(0, new Dictionary<string, string>(FilesystemPaths.PathComparer));

        var deleted = 0;
        var failures = new ConcurrentDictionary<string, string>(FilesystemPaths.PathComparer);
        var lockBatchSize = Math.Max(1, Math.Min(512, Math.Max(1, maxParallelism) * 16));
        foreach (var candidateChunk in candidates.Chunk(lockBatchSize))
        {
            using var lease = await _coordinator.AcquireWriteAsync(ct);
            deleted += await DeleteCandidateChunkWithinWriterLeaseAsync(candidateChunk, maxParallelism, failures, ct);
        }
        return new PhysicalPathDeletionResult(deleted, failures);
    }

    private async Task<PhysicalPathDeletionResult> DeletePathsWithinWriterLeaseAsync(
        IEnumerable<PhysicalPathDeletionCandidate> candidatePaths,
        int maxParallelism,
        CancellationToken ct)
    {
        var candidates = candidatePaths
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Path))
            .GroupBy(candidate => candidate.Path, FilesystemPaths.PathComparer)
            .Select(group => new PhysicalPathDeletionCandidate(
                group.Key,
                group.SelectMany(candidate => candidate.ExpectedIdentities).Distinct().ToArray()))
            .ToArray();
        var deleted = 0;
        var failures = new ConcurrentDictionary<string, string>(FilesystemPaths.PathComparer);
        var batchSize = Math.Max(1, Math.Min(512, Math.Max(1, maxParallelism) * 16));
        foreach (var candidateChunk in candidates.Chunk(batchSize))
            deleted += await DeleteCandidateChunkWithinWriterLeaseAsync(candidateChunk, maxParallelism, failures, ct);
        return new PhysicalPathDeletionResult(deleted, failures);
    }

    private async Task<int> DeleteCandidateChunkWithinWriterLeaseAsync(
        PhysicalPathDeletionCandidate[] candidates,
        int maxParallelism,
        ConcurrentDictionary<string, string> failures,
        CancellationToken ct)
    {
        var deleted = 0;
        var referencedPaths = await FindReferencedPathsAsync(candidates.Select(candidate => candidate.Path).ToArray(), ct);
        var pathsToDelete = candidates.Where(candidate => !referencedPaths.Contains(candidate.Path)).ToArray();
        await Parallel.ForEachAsync(
            pathsToDelete,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, maxParallelism),
                CancellationToken = ct,
            },
            (candidate, _) =>
            {
                try
                {
                    var expected = candidate.ExpectedIdentities;
                    if (expected.Count == 0 || expected.Any(identity => !identity.Captured))
                    {
                        failures[candidate.Path] = "The file identity could not be captured when deletion was staged.";
                        return ValueTask.CompletedTask;
                    }

                    var current = PhysicalFileIdentitySnapshot.Capture(candidate.Path);
                    if (!current.Captured)
                    {
                        failures[candidate.Path] = "The current file identity could not be read.";
                        return ValueTask.CompletedTask;
                    }

                    if (expected.Count != 1 || expected[0] != current)
                    {
                        if (current.Exists)
                        {
                            logger?.LogWarning(
                                "Skipped physical deletion for {Path} because the file changed after deletion was staged.",
                                candidate.Path);
                        }
                        return ValueTask.CompletedTask;
                    }

                    if (current.Exists)
                    {
                        File.Delete(candidate.Path);
                        Interlocked.Increment(ref deleted);
                    }
                }
                catch (Exception ex)
                {
                    failures[candidate.Path] = ex.Message;
                    logger?.LogWarning(ex, "Entity metadata was deleted, but physical file {Path} could not be removed.", candidate.Path);
                }
                return ValueTask.CompletedTask;
            });
        return deleted;
    }

    private async Task<HashSet<string>> FindReferencedPathsAsync(string[] candidatePaths, CancellationToken ct)
    {
        var query = db.Set<BaseFileEntity>().AsNoTracking();
        List<string> referenced;
        if (FilesystemPaths.PathComparison == StringComparison.OrdinalIgnoreCase)
        {
            var normalizedPaths = candidatePaths
                .Select(NormalizeCaseInsensitivePath)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            referenced = await query
                .Where(file => normalizedPaths.Contains(file.Path.ToUpper()))
                .Select(file => file.Path)
                .ToListAsync(ct);
        }
        else
        {
            referenced = await query
                .Where(file => candidatePaths.Contains(file.Path))
                .Select(file => file.Path)
                .ToListAsync(ct);
        }
        return new HashSet<string>(referenced, FilesystemPaths.PathComparer);
    }

    internal static string NormalizeCaseInsensitivePath(string path) => path.ToUpperInvariant();

    private sealed record PhysicalPathDeletionResult(
        int Deleted,
        IReadOnlyDictionary<string, string> Failures);

    private sealed record PhysicalPathDeletionCandidate(
        string Path,
        IReadOnlyList<PhysicalFileIdentitySnapshot> ExpectedIdentities);
}

public sealed class PhysicalFileDeletionRecoveryService(
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    PhysicalFileDeletionRecoverySignal signal,
    ILogger<PhysicalFileDeletionRecoveryService> logger) : BackgroundService
{
    internal const string MigrationId = "20260824161540_AddPhysicalDeletionIdentity";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var migrationChecked = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                if (!migrationChecked)
                {
                    var appliedMigrations = await db.Database.GetAppliedMigrationsAsync(stoppingToken);
                    if (!appliedMigrations.Contains(MigrationId, StringComparer.Ordinal))
                    {
                        // Hosted services can start before a fresh database has been migrated. Stay
                        // alive so the schema-init pass or the first deletion signal can wake us.
                        logger.LogDebug("Physical deletion recovery is waiting for migration {MigrationId}.", MigrationId);
                    }
                    else
                    {
                        migrationChecked = true;
                    }
                }

                if (migrationChecked)
                {
                    var deletionService = scope.ServiceProvider.GetRequiredService<PhysicalFileDeletionService>();
                    var result = await deletionService.ProcessPendingAsync(
                        batchId: null,
                        BulkDeletionJobService.ResolveMaxParallelism(config, Environment.ProcessorCount),
                        stoppingToken);
                    if (result.Deleted > 0 || result.Failed > 0)
                    {
                        logger.LogInformation(
                            "Recovered pending physical deletions: {Deleted} deleted and {Failed} still pending.",
                            result.Deleted,
                            result.Failed);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Recovery is a permanent safety net, not a one-shot startup task. A transient
                // database/filesystem failure must leave it alive to retry the durable outbox.
                logger.LogError(ex, "Failed to recover pending physical file deletions; retrying later.");
            }

            try
            {
                await signal.WaitAsync(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
