using System.Data;
using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cove.Data.Services;

/// <summary>
/// The only writer of <see cref="VideoShotSet"/> and <see cref="VideoShot"/>; see
/// <see cref="IVideoShotService"/> for the contract.
/// </summary>
/// <remarks>
/// Writes lock the file row and then the file's set row (PostgreSQL only), in the same order as
/// deleting a file cascades, so a write never races the "no set yet" check or a file deletion. The
/// scoped context may belong to an extension that has its own transaction, pending changes and tracked
/// entities, so the service never clears the change tracker; see <see cref="WriteScope"/> for how a
/// write keeps out of the caller's tracked entities.
/// </remarks>
public sealed class VideoShotService(CoveContext db) : IVideoShotService
{
    private const int SummaryChunkSize = 2_000;

    // Reads of the set, its shots and its payload that can race a write before they run in one snapshot.
    private const int SeparateReadAttempts = 2;

    // Every column but the payload, which can be large: summaries leave it out, and a whole set reads
    // it once instead of on every shot row.
    private static readonly Expression<Func<VideoShotSet, VideoShotSet>> WithoutPayload = set => new VideoShotSet
    {
        Id = set.Id,
        CreatedAt = set.CreatedAt,
        UpdatedAt = set.UpdatedAt,
        FileId = set.FileId,
        SourceKey = set.SourceKey,
        SourceRunId = set.SourceRunId,
        Model = set.Model,
        ModelVersion = set.ModelVersion,
        Mode = set.Mode,
        DecodeBackend = set.DecodeBackend,
        Fps = set.Fps,
        FrameCount = set.FrameCount,
        DurationSec = set.DurationSec,
        ShotCount = set.ShotCount,
        EditedAt = set.EditedAt,
        Revision = set.Revision,
        FileSize = set.FileSize,
    };

    public async Task<VideoShotSetDto?> GetForFileAsync(int fileId, CancellationToken cancellationToken = default)
    {
        for (var attempt = 0; attempt < SeparateReadAttempts; attempt++)
        {
            var read = await ReadWholeSetAsync(fileId, sameRevision: true, cancellationToken);
            if (!read.Changed)
                return read.Set;
        }

        // A write got in between both times. Inside the caller's transaction, its isolation level decides
        // what the reads see; otherwise they run again in one REPEATABLE READ snapshot, where none can.
        if (db.Database.CurrentTransaction is not null)
            return (await ReadWholeSetAsync(fileId, sameRevision: false, cancellationToken)).Set;
        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
            var read = await ReadWholeSetAsync(fileId, sameRevision: false, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return read.Set;
        });
    }

    public async Task<VideoShotSetDto?> GetForVideoAsync(int videoId, CancellationToken cancellationToken = default)
    {
        var primaryFileId = await db.Videos
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(video => video.Id == videoId)
            .Select(video => video.PrimaryFileId)
            .FirstOrDefaultAsync(cancellationToken);
        return primaryFileId is int fileId ? await GetForFileAsync(fileId, cancellationToken) : null;
    }

    public async Task<IReadOnlyList<VideoShotSetDto>> ListForVideoAsync(int videoId, CancellationToken cancellationToken = default)
    {
        var fileIds = await db.VideoFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(file => file.VideoId == videoId)
            .Select(file => file.Id)
            .ToListAsync(cancellationToken);
        var summaries = await GetSummariesForFilesAsync(fileIds, cancellationToken);
        return summaries.Values.OrderBy(summary => summary.FileId).ToList();
    }

    public async Task<IReadOnlyDictionary<int, VideoShotSetDto>> GetSummariesForFilesAsync(
        IReadOnlyCollection<int> fileIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        var result = new Dictionary<int, VideoShotSetDto>();
        foreach (var chunk in fileIds.Distinct().Chunk(SummaryChunkSize))
        {
            var sets = await db.VideoShotSets
                .AsNoTracking()
                .Where(set => chunk.Contains(set.FileId))
                .Select(WithoutPayload)
                .ToListAsync(cancellationToken);
            if (sets.Count == 0)
                continue;

            var files = await LoadFileStatesAsync(sets.Select(set => set.FileId).ToArray(), cancellationToken);
            foreach (var set in sets)
                result[set.FileId] = ToDto(set, files.GetValueOrDefault(set.FileId), null);
        }

        return result;
    }

    public Task<VideoShotWriteResult> WriteSetAsync(VideoShotSetWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (ValidateProvenance(write) is { } provenanceError)
            return Task.FromResult(Rejected(provenanceError));
        if (write.Expected is not null && write.WriteMode != VideoShotWriteMode.Replace)
            return Task.FromResult(Rejected("An expected set can only be given when replacing."));

        var partition = VideoShotPartition.Normalize(write.Shots, write.DurationSec, write.Fps, write.FrameCount);
        if (!partition.IsValid)
            return Task.FromResult(Rejected(partition.Error!));

        return RunWriteAsync(write.FileId, async scope =>
        {
            var file = await LoadFileStateAsync(write.FileId, cancellationToken);
            if (file is null)
                return new VideoShotWriteResult { Outcome = VideoShotWriteOutcome.FileNotFound, Reason = $"File {write.FileId} is not a video file." };
            if (!VideoShotPartition.DurationMatchesFile(write.DurationSec, file.DurationSec))
            {
                return Rejected($"The shots cover {VideoShotPartition.DescribeDurationMismatch(write.DurationSec, file.DurationSec)}; " +
                    "they were probably analysed from a different file.");
            }

            var existing = (await scope.LoadSetAsync(withShots: false, cancellationToken))?.Set;
            if (existing is not null && write.WriteMode == VideoShotWriteMode.SkipIfExists)
            {
                return new VideoShotWriteResult
                {
                    Outcome = VideoShotWriteOutcome.SkippedExisting,
                    Reason = "The file already has shot boundaries.",
                    Set = ToDto(existing, file, null),
                };
            }

            if (write.Expected is { } expected && !Matches(existing, expected))
            {
                return new VideoShotWriteResult
                {
                    Outcome = VideoShotWriteOutcome.Conflict,
                    Reason = "The file's shot boundaries changed since they were read.",
                    Set = existing is null ? null : ToDto(existing, file, null),
                };
            }

            var replacedEdited = existing?.EditedAt is not null;
            var set = existing;
            if (set is null)
            {
                set = new VideoShotSet { FileId = write.FileId, Revision = 1 };
                scope.Add(set);
            }
            else
            {
                await scope.DeleteShotsAsync(set, cancellationToken);
                set.Revision++;
            }

            set.SourceKey = write.SourceKey.Trim();
            set.SourceRunId = NullIfBlank(write.SourceRunId);
            set.Model = NullIfBlank(write.Model);
            set.ModelVersion = NullIfBlank(write.ModelVersion);
            set.Mode = NullIfBlank(write.Mode);
            set.DecodeBackend = NullIfBlank(write.DecodeBackend);
            set.Fps = write.Fps;
            set.FrameCount = partition.FrameCount;
            set.DurationSec = write.DurationSec;
            set.ShotCount = partition.Shots!.Count;
            set.EditedAt = ToUtc(write.EditedAt);
            set.FileSize = file.Size;
            set.Payload = ToDocument(write.Payload);
            foreach (var shot in partition.Shots)
                scope.Add(ToEntity(set, shot));

            await db.SaveChangesAsync(cancellationToken);
            return new VideoShotWriteResult
            {
                Outcome = existing is null ? VideoShotWriteOutcome.Written : VideoShotWriteOutcome.Replaced,
                ReplacedEditedSet = existing is not null && replacedEdited,
                Set = ToDto(set, file, null),
            };
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> SplitAsync(VideoShotSplitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.AtSec))
            return Task.FromResult(EditRejected("The split time must be a finite number."));
        if (ValidateSourceKey(request.SourceKey) is { } keyError)
            return Task.FromResult(EditRejected(keyError));

        return RunEditAsync(request.FileId, request.Expected, allowCreate: true, async (context, cancellation) =>
        {
            if (context.Set is null)
            {
                if (!double.IsFinite(context.File.DurationSec) || context.File.DurationSec <= 0)
                    return EditRejected("The file has no known duration to draw shots over.");
                context.CreateManualSet(request.SourceKey, context.File.DurationSec,
                    [new PartitionShot(0, context.File.DurationSec, null, null, null, null)]);
            }

            var shots = context.OrderedShots;
            var at = request.AtSec;
            var index = shots.FindIndex(shot => shot.StartSec < at && at < shot.EndSec);
            if (index < 0)
                return EditRejected($"{request.AtSec:0.###}s is not inside any shot.");
            var shot = shots[index];

            int? cutFrame = null;
            if (shot.StartFrame is { } startFrame && shot.EndFrame is { } endFrame)
            {
                (var frame, at) = SnapToFrame(at, shot.StartSec, shot.EndSec, startFrame, endFrame);
                if (frame <= startFrame || frame >= endFrame)
                    return EditRejected($"{request.AtSec:0.###}s is within a frame of an existing cut.");
                cutFrame = frame;
            }

            if (at - shot.StartSec < VideoShotRules.MinShotDurationSec || shot.EndSec - at < VideoShotRules.MinShotDurationSec)
                return EditRejected($"{request.AtSec:0.###}s is too close to an existing cut.");

            var right = new VideoShot
            {
                StartSec = at,
                EndSec = shot.EndSec,
                StartFrame = cutFrame,
                EndFrame = cutFrame is null ? null : shot.EndFrame,
                ShotType = shot.ShotType,
            };
            shot.EndSec = at;
            if (cutFrame is not null)
                shot.EndFrame = cutFrame;
            context.AddShot(right);
            return await context.SaveEditAsync(cancellation);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> MergeAsync(VideoShotMergeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Expected);
        return RunEditAsync(request.FileId, request.Expected, allowCreate: false, async (context, cancellation) =>
        {
            var shots = context.OrderedShots;
            var index = FindCut(shots, request.CutSec);
            if (index < 0)
                return EditRejected($"There is no cut at {request.CutSec:0.###}s.");

            var left = shots[index - 1];
            var right = shots[index];
            left.EndSec = right.EndSec;
            left.EndFrame = right.EndFrame;
            context.RemoveShot(right);
            return await context.SaveEditAsync(cancellation);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> MoveCutAsync(VideoShotMoveCutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Expected);
        if (!double.IsFinite(request.ToSec))
            return Task.FromResult(EditRejected("The new cut time must be a finite number."));

        return RunEditAsync(request.FileId, request.Expected, allowCreate: false, async (context, cancellation) =>
        {
            var shots = context.OrderedShots;
            var index = FindCut(shots, request.FromSec);
            if (index < 0)
                return EditRejected($"There is no cut at {request.FromSec:0.###}s.");

            var left = shots[index - 1];
            var right = shots[index];
            var to = request.ToSec;
            if (to <= left.StartSec || to >= right.EndSec)
                return EditRejected($"{request.ToSec:0.###}s is not between the neighbouring cuts.");

            int? cutFrame = null;
            if (left.StartFrame is { } leftStart && right.EndFrame is { } rightEnd)
            {
                (var frame, to) = SnapToFrame(to, left.StartSec, right.EndSec, leftStart, rightEnd);
                if (frame <= leftStart || frame >= rightEnd)
                    return EditRejected($"{request.ToSec:0.###}s is within a frame of a neighbouring cut.");
                cutFrame = frame;
            }

            if (Math.Abs(to - right.StartSec) < 1e-9)
                return context.Unchanged();
            if (to - left.StartSec < VideoShotRules.MinShotDurationSec || right.EndSec - to < VideoShotRules.MinShotDurationSec)
                return EditRejected($"{request.ToSec:0.###}s is not between the neighbouring cuts.");

            if (cutFrame is not null)
            {
                left.EndFrame = cutFrame;
                right.StartFrame = cutFrame;
            }

            left.EndSec = to;
            right.StartSec = to;
            return await context.SaveEditAsync(cancellation);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> ReplaceShotsAsync(VideoShotReplaceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (ValidateSourceKey(request.SourceKey) is { } keyError)
            return Task.FromResult(EditRejected(keyError));
        if (request.Shots is null || request.Shots.Count == 0)
            return Task.FromResult(EditRejected("A set needs at least one shot."));
        if (request.Shots.Any(shot => shot is null))
            return Task.FromResult(EditRejected("A shot is missing."));

        return RunEditAsync(request.FileId, request.Expected, allowCreate: true, async (context, cancellation) =>
        {
            PartitionResult partition;
            if (context.Set is null)
            {
                var duration = request.Shots.Max(shot => shot.EndSec);
                if (!double.IsFinite(duration))
                    return EditRejected("The shots do not cover the file's duration.");
                if (!VideoShotPartition.DurationMatchesFile(duration, context.File.DurationSec))
                    return EditRejected($"The shots end at {VideoShotPartition.DescribeDurationMismatch(duration, context.File.DurationSec)}.");
                partition = VideoShotPartition.Normalize(request.Shots, duration, null, null);
                if (!partition.IsValid)
                    return EditRejected(partition.Error!);
                context.CreateManualSet(request.SourceKey, duration, partition.Shots!);
                return await context.SaveEditAsync(cancellation);
            }

            // The frame count follows the replacement's frames, and there is none without them. The
            // frame rate stays, so shots with frames can be put back later, for example by an undo.
            var set = context.Set;
            partition = VideoShotPartition.Normalize(request.Shots, set.DurationSec, set.Fps, null);
            if (!partition.IsValid)
                return EditRejected(partition.Error!);
            set.FrameCount = partition.FrameCount;

            var byStart = context.OrderedShots.ToDictionary(shot => shot.StartSec);
            var kept = new HashSet<double>();
            foreach (var shot in partition.Shots!)
            {
                if (byStart.TryGetValue(shot.StartSec, out var existing))
                {
                    existing.EndSec = shot.EndSec;
                    existing.StartFrame = shot.StartFrame;
                    existing.EndFrame = shot.EndFrame;
                    existing.ShotType = shot.ShotType;
                    existing.TransitionIn = shot.TransitionIn;
                }
                else
                {
                    context.AddShot(ToEntity(set, shot));
                }

                kept.Add(shot.StartSec);
            }

            foreach (var stale in byStart.Values.Where(shot => !kept.Contains(shot.StartSec)).ToList())
                context.RemoveShot(stale);
            return await context.SaveEditAsync(cancellation);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> DeleteAsync(VideoShotDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return RunWriteAsync(request.FileId, async scope =>
        {
            var set = (await scope.LoadSetAsync(withShots: false, cancellationToken))?.Set;
            if (set is null)
                return new VideoShotEditResult { Status = request.Expected is null ? VideoShotEditStatus.NotFound : VideoShotEditStatus.Conflict };
            if (request.Expected is { } expected && !Matches(set, expected))
            {
                return new VideoShotEditResult
                {
                    Status = VideoShotEditStatus.Conflict,
                    Error = "The file's shot boundaries changed since they were read.",
                    Set = ToDto(set, await LoadFileStateAsync(request.FileId, cancellationToken), await LoadShotsAsync(set.Id, cancellationToken)),
                };
            }

            await scope.DeleteSetAsync(set, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return new VideoShotEditResult { Status = VideoShotEditStatus.Deleted };
        }, cancellationToken);
    }

    // ── edits ────────────────────────────────────────────────────────────

    private delegate Task<VideoShotEditResult> EditOperation(EditContext context, CancellationToken cancellationToken);

    private Task<VideoShotEditResult> RunEditAsync(
        int fileId,
        VideoShotSetVersion? expected,
        bool allowCreate,
        EditOperation operation,
        CancellationToken cancellationToken)
    {
        return RunWriteAsync(fileId, async scope =>
        {
            var file = await LoadFileStateAsync(fileId, cancellationToken);
            if (file is null)
                return new VideoShotEditResult { Status = VideoShotEditStatus.NotFound, Error = $"File {fileId} is not a video file." };

            var loaded = await scope.LoadSetAsync(withShots: true, cancellationToken);
            if (loaded is null)
            {
                if (expected is not null)
                    return new VideoShotEditResult { Status = VideoShotEditStatus.Conflict, Error = "The file's shot boundaries were deleted." };
                if (!allowCreate)
                    return new VideoShotEditResult { Status = VideoShotEditStatus.NotFound, Error = "The file has no shot boundaries." };
            }
            else if (expected is null || !Matches(loaded.Set, expected))
            {
                return new VideoShotEditResult
                {
                    Status = VideoShotEditStatus.Conflict,
                    Error = expected is null
                        ? "The file already has shot boundaries."
                        : "The file's shot boundaries changed since they were read.",
                    Set = ToDto(loaded.Set, file, loaded.Shots),
                };
            }

            return await operation(new EditContext(db, scope, file, loaded), cancellationToken);
        }, cancellationToken);
    }

    private sealed class EditContext(CoveContext db, WriteScope scope, FileState file, LoadedSet? loaded)
    {
        private readonly List<VideoShot> _shots = loaded?.Shots.ToList() ?? [];
        private bool _created;

        public FileState File { get; } = file;
        public VideoShotSet? Set { get; private set; } = loaded?.Set;

        /// <summary>The set's shots in time order, including shots added by this edit.</summary>
        public List<VideoShot> OrderedShots => _shots;

        public void CreateManualSet(string? sourceKey, double durationSec, IReadOnlyList<PartitionShot> shots)
        {
            var created = new VideoShotSet
            {
                FileId = File.FileId,
                SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? VideoShotRules.ManualSourceKey : sourceKey.Trim(),
                DurationSec = durationSec,
                FileSize = File.Size,
                Revision = 0,
            };
            scope.Add(created);
            Set = created;
            _created = true;
            foreach (var shot in shots)
                AddShot(ToEntity(created, shot));
        }

        public void AddShot(VideoShot shot)
        {
            shot.Set = Set;
            scope.Add(shot);
            _shots.Add(shot);
            _shots.Sort((left, right) => left.StartSec.CompareTo(right.StartSec));
        }

        public void RemoveShot(VideoShot shot)
        {
            _shots.Remove(shot);
            scope.Remove(shot);
        }

        public VideoShotEditResult Unchanged() => new()
        {
            Status = VideoShotEditStatus.Updated,
            Set = ToDto(Set!, File, _shots),
        };

        public async Task<VideoShotEditResult> SaveEditAsync(CancellationToken cancellationToken)
        {
            var current = Set!;
            current.ShotCount = _shots.Count;
            current.Revision = _created ? 1 : current.Revision + 1;
            current.EditedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new VideoShotEditResult
            {
                Status = VideoShotEditStatus.Updated,
                Set = ToDto(current, File, _shots),
            };
        }
    }

    private static int FindCut(List<VideoShot> shots, double cutSec)
    {
        if (!double.IsFinite(cutSec))
            return -1;
        for (var index = 1; index < shots.Count; index++)
        {
            if (Math.Abs(shots[index].StartSec - cutSec) <= VideoShotRules.BoundaryToleranceSec)
                return index;
        }

        return -1;
    }

    /// <summary>
    /// The frame boundary nearest <paramref name="timeSec"/> in a span whose seconds and frames are
    /// known at both ends, and that boundary's time on the span's own scale, so that a cut's seconds
    /// and frame agree.
    /// </summary>
    private static (int Frame, double TimeSec) SnapToFrame(double timeSec, double startSec, double endSec, int startFrame, int endFrame)
    {
        var frames = endFrame - startFrame;
        var frame = startFrame + (int)Math.Round((timeSec - startSec) * frames / (endSec - startSec), MidpointRounding.AwayFromZero);
        return (frame, startSec + (frame - startFrame) * (endSec - startSec) / frames);
    }

    // ── transactions, locks and tracking ─────────────────────────────────

    /// <summary>
    /// Runs a write on one file atomically. Inside a caller's transaction it uses a savepoint, so a
    /// failure undoes only this write; otherwise it opens its own transaction under the retrying
    /// execution strategy.
    /// </summary>
    private async Task<T> RunWriteAsync<T>(int fileId, Func<WriteScope, Task<T>> operation, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is { } outer)
        {
            var savepoint = "video_shots_" + Guid.NewGuid().ToString("n")[..12];
            await outer.CreateSavepointAsync(savepoint, cancellationToken);
            var scope = new WriteScope(db, fileId);
            var completed = false;
            try
            {
                var result = await RunLockedAsync(scope, operation, cancellationToken);
                await outer.ReleaseSavepointAsync(savepoint, cancellationToken);
                completed = true;
                return result;
            }
            catch
            {
                await outer.RollbackToSavepointAsync(savepoint, CancellationToken.None);
                throw;
            }
            finally
            {
                scope.End(completed);
            }
        }

        return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            var scope = new WriteScope(db, fileId);
            var completed = false;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var result = await RunLockedAsync(scope, operation, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                completed = true;
                return result;
            }
            finally
            {
                scope.End(completed);
            }
        });
    }

    private async Task<T> RunLockedAsync<T>(WriteScope scope, Func<WriteScope, Task<T>> operation, CancellationToken cancellationToken)
    {
        await LockFileAsync(scope.FileId, cancellationToken);
        await scope.SetAsideCallerEntitiesAsync(cancellationToken);
        return await operation(scope);
    }

    private async Task LockFileAsync(int fileId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql())
            return;

        // The file row is the per-file mutex: it serialises writers even before the file has a set,
        // and pins the file against deletion until commit. NO KEY UPDATE does not block the KEY SHARE
        // locks that foreign-key inserts take. The set row, when there is one, is locked second, in
        // the same order as a file deletion cascades.
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM files WHERE \"Id\" = {fileId} FOR NO KEY UPDATE", cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM video_shot_sets WHERE \"FileId\" = {fileId} FOR UPDATE", cancellationToken);
    }

    private sealed record LoadedSet(VideoShotSet Set, List<VideoShot> Shots);

    /// <summary>
    /// One write's use of the change tracker. The scoped context may belong to an extension that tracks
    /// the same rows, possibly in an older state, so the write first sets the caller's instances of the
    /// file's set and shots aside and works on instances it loads itself. When it ends, it detaches
    /// everything it tracked and puts the caller's instances back as they were, except those of rows
    /// it deleted. The caller's instances are not refreshed: like any entity another writer changed,
    /// they can be stale afterwards.
    /// </summary>
    private sealed class WriteScope(CoveContext db, int fileId)
    {
        private readonly List<object> _own = [];
        private readonly List<(object Entity, EntityState State)> _setAside = [];
        private readonly HashSet<int> _deletedSets = [];
        private readonly HashSet<int> _setsWithoutShots = [];
        private readonly HashSet<long> _deletedShots = [];

        public int FileId => fileId;

        public async Task SetAsideCallerEntitiesAsync(CancellationToken cancellationToken)
        {
            var currentSetId = await db.VideoShotSets
                .AsNoTracking()
                .Where(set => set.FileId == fileId)
                .Select(set => (int?)set.Id)
                .FirstOrDefaultAsync(cancellationToken);
            var sets = db.ChangeTracker.Entries<VideoShotSet>()
                .Where(entry => entry.Entity.FileId == fileId || entry.Entity.Id == currentSetId)
                .ToList();
            var setIds = sets.Select(entry => entry.Entity.Id).ToHashSet();
            if (currentSetId is int id)
                setIds.Add(id);
            var shots = db.ChangeTracker.Entries<VideoShot>()
                .Where(entry => setIds.Contains(entry.Entity.SetId))
                .ToList();

            // Record every state before detaching anything: detaching a set also detaches its shots.
            var entries = sets.Cast<EntityEntry>().Concat(shots).ToList();
            _setAside.AddRange(entries.Select(entry => (entry.Entity, entry.State)));
            foreach (var entry in entries)
                entry.State = EntityState.Detached;
        }

        public async Task<LoadedSet?> LoadSetAsync(bool withShots, CancellationToken cancellationToken)
        {
            var set = await db.VideoShotSets.FirstOrDefaultAsync(candidate => candidate.FileId == fileId, cancellationToken);
            if (set is null)
                return null;

            _own.Add(set);
            List<VideoShot> shots = [];
            if (withShots)
            {
                shots = await db.VideoShots
                    .Where(shot => shot.SetId == set.Id)
                    .OrderBy(shot => shot.StartSec)
                    .ToListAsync(cancellationToken);
                _own.AddRange(shots);
            }

            return new LoadedSet(set, shots);
        }

        public void Add(object entity)
        {
            db.Add(entity);
            _own.Add(entity);
        }

        public void Remove(VideoShot shot)
        {
            db.VideoShots.Remove(shot);
            _deletedShots.Add(shot.Id);
        }

        public async Task DeleteShotsAsync(VideoShotSet set, CancellationToken cancellationToken)
        {
            await db.VideoShots.Where(shot => shot.SetId == set.Id).ExecuteDeleteAsync(cancellationToken);
            _setsWithoutShots.Add(set.Id);
        }

        public async Task DeleteSetAsync(VideoShotSet set, CancellationToken cancellationToken)
        {
            // The database cascade removes the shots.
            await db.VideoShotSets.Where(candidate => candidate.Id == set.Id).ExecuteDeleteAsync(cancellationToken);
            _deletedSets.Add(set.Id);
        }

        /// <summary>Detaches this write's entities and puts the caller's back; after a failure, all of them.</summary>
        public void End(bool completed)
        {
            foreach (var entity in _own)
            {
                var entry = db.Entry(entity);
                if (entry.State != EntityState.Detached)
                    entry.State = EntityState.Detached;
            }

            var restored = completed ? _setAside.Where(item => !IsDeleted(item.Entity)).ToList() : _setAside;
            if (completed)
            {
                // A deleted shot left in a tracked set's collection would be tracked again, and saved,
                // by the caller's next DetectChanges.
                foreach (var set in restored.Select(item => item.Entity).OfType<VideoShotSet>())
                {
                    foreach (var shot in set.Shots.Where(IsDeleted).ToList())
                        set.Shots.Remove(shot);
                }
            }

            foreach (var (entity, state) in restored)
                db.Entry(entity).State = state;
        }

        private bool IsDeleted(object entity) => entity switch
        {
            VideoShotSet set => _deletedSets.Contains(set.Id),
            VideoShot shot => _deletedShots.Contains(shot.Id) || _deletedSets.Contains(shot.SetId) || _setsWithoutShots.Contains(shot.SetId),
            _ => false,
        };
    }

    // ── loading and mapping ──────────────────────────────────────────────

    private sealed record FileState(int FileId, long Size, double DurationSec, int? VideoId, bool IsPrimary);

    private async Task<FileState?> LoadFileStateAsync(int fileId, CancellationToken cancellationToken)
        => (await LoadFileStatesAsync([fileId], cancellationToken)).GetValueOrDefault(fileId);

    private async Task<Dictionary<int, FileState>> LoadFileStatesAsync(int[] fileIds, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters on the root also lifts the video authorization filter in the subquery:
        // the service authorizes nothing.
        var files = await db.VideoFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(file => fileIds.Contains(file.Id))
            .Select(file => new
            {
                file.Id,
                file.Size,
                file.Duration,
                file.VideoId,
                IsPrimary = db.Videos.Any(video => video.PrimaryFileId == file.Id),
            })
            .ToListAsync(cancellationToken);
        return files.ToDictionary(file => file.Id, file => new FileState(file.Id, file.Size, file.Duration, file.VideoId, file.IsPrimary));
    }

    private sealed record WholeSetRead(bool Changed, VideoShotSetDto? Set);

    /// <summary>
    /// The file's whole set in three reads: the set without its payload, its shots, and then the
    /// payload, once rather than on every shot row. With <paramref name="sameRevision"/>, the payload is
    /// read only for the revision read first. Every change to a set increments its revision or replaces
    /// the set, so finding it shows that the shots read in between belong to that revision, and not
    /// finding it reports a change.
    /// </summary>
    private async Task<WholeSetRead> ReadWholeSetAsync(int fileId, bool sameRevision, CancellationToken cancellationToken)
    {
        var set = await db.VideoShotSets
            .AsNoTracking()
            .Where(candidate => candidate.FileId == fileId)
            .Select(WithoutPayload)
            .FirstOrDefaultAsync(cancellationToken);
        if (set is null)
            return new WholeSetRead(false, null);

        var shots = await LoadShotsAsync(set.Id, cancellationToken);
        var payload = await db.VideoShotSets
            .AsNoTracking()
            .Where(candidate => candidate.Id == set.Id && (!sameRevision || candidate.Revision == set.Revision))
            .Select(candidate => new { candidate.Payload })
            .FirstOrDefaultAsync(cancellationToken);
        if (payload is null)
            return new WholeSetRead(true, null);

        set.Payload = payload.Payload;
        return new WholeSetRead(false, ToDto(set, await LoadFileStateAsync(fileId, cancellationToken), shots));
    }

    private Task<List<VideoShot>> LoadShotsAsync(int setId, CancellationToken cancellationToken)
        => db.VideoShots
            .AsNoTracking()
            .Where(shot => shot.SetId == setId)
            .OrderBy(shot => shot.StartSec)
            .ToListAsync(cancellationToken);

    private static VideoShot ToEntity(VideoShotSet set, PartitionShot shot) => new()
    {
        Set = set,
        StartSec = shot.StartSec,
        EndSec = shot.EndSec,
        StartFrame = shot.StartFrame,
        EndFrame = shot.EndFrame,
        ShotType = shot.ShotType,
        TransitionIn = shot.TransitionIn,
    };

    private static bool Matches(VideoShotSet? set, VideoShotSetVersion expected)
        => set is not null && set.Id == expected.SetId && set.Revision == expected.Revision;

    /// <summary>A summary without <paramref name="shots"/>, or the whole set, payload included, with them.</summary>
    private static VideoShotSetDto ToDto(VideoShotSet set, FileState? file, IEnumerable<VideoShot>? shots) => new()
    {
        Id = set.Id,
        FileId = set.FileId,
        VideoId = file?.VideoId,
        IsPrimaryFile = file?.IsPrimary ?? false,
        SourceKey = set.SourceKey,
        SourceRunId = set.SourceRunId,
        Model = set.Model,
        ModelVersion = set.ModelVersion,
        Mode = set.Mode,
        DecodeBackend = set.DecodeBackend,
        Fps = set.Fps,
        FrameCount = set.FrameCount,
        DurationSec = set.DurationSec,
        ShotCount = set.ShotCount,
        Revision = set.Revision,
        EditedAt = set.EditedAt,
        IsStale = file is not null && file.Size != set.FileSize,
        CreatedAt = set.CreatedAt,
        UpdatedAt = set.UpdatedAt,
        Payload = shots is null ? null : set.Payload?.RootElement.Clone(),
        Shots = shots?.Select(shot => new VideoShotDto
        {
            Id = shot.Id,
            StartSec = shot.StartSec,
            EndSec = shot.EndSec,
            StartFrame = shot.StartFrame,
            EndFrame = shot.EndFrame,
            ShotType = shot.ShotType,
            TransitionIn = shot.TransitionIn,
        }).ToList(),
    };

    private static JsonDocument? ToDocument(JsonElement? payload)
        => payload is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } element
            ? JsonDocument.Parse(element.GetRawText())
            : null;

    private static DateTime? ToUtc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Unspecified } unspecified => DateTime.SpecifyKind(unspecified, DateTimeKind.Utc),
        { } known => known.ToUniversalTime(),
    };

    // ── validation ───────────────────────────────────────────────────────

    private static string? ValidateProvenance(VideoShotSetWrite write)
    {
        if (string.IsNullOrWhiteSpace(write.SourceKey))
            return "A source key is required.";
        if (write.SourceKey.Trim().Length > VideoShotRules.MaxKeyLength
            || write.SourceRunId?.Length > VideoShotRules.MaxKeyLength
            || write.Model?.Length > VideoShotRules.MaxKeyLength)
            return $"Source keys, run keys and model names may be at most {VideoShotRules.MaxKeyLength} characters.";
        if (write.ModelVersion?.Length > VideoShotRules.MaxLabelLength
            || write.Mode?.Length > VideoShotRules.MaxLabelLength
            || write.DecodeBackend?.Length > VideoShotRules.MaxLabelLength)
            return $"Model versions, modes and decode backends may be at most {VideoShotRules.MaxLabelLength} characters.";
        if (write.Payload is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } payload)
        {
            if (Encoding.UTF8.GetByteCount(payload.GetRawText()) > VideoShotRules.MaxPayloadBytes)
                return $"The payload may be at most {VideoShotRules.MaxPayloadBytes} bytes of UTF-8.";
            if (ContainsNul(payload))
                return "The payload may not contain the character U+0000.";
        }

        return null;
    }

    // PostgreSQL's jsonb cannot store U+0000 in a string or a property name.
    private static bool ContainsNul(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString()!.Contains('\0'),
        JsonValueKind.Array => element.EnumerateArray().Any(ContainsNul),
        JsonValueKind.Object => element.EnumerateObject().Any(property => property.Name.Contains('\0') || ContainsNul(property.Value)),
        _ => false,
    };

    private static string? ValidateSourceKey(string? sourceKey)
        => sourceKey is not null && sourceKey.Trim().Length > VideoShotRules.MaxKeyLength
            ? $"Source keys may be at most {VideoShotRules.MaxKeyLength} characters."
            : null;

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static VideoShotWriteResult Rejected(string reason)
        => new() { Outcome = VideoShotWriteOutcome.Invalid, Reason = reason };

    private static VideoShotEditResult EditRejected(string error)
        => new() { Status = VideoShotEditStatus.Invalid, Error = error };
}
