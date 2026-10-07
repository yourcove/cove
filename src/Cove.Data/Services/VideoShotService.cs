using System.Linq.Expressions;
using System.Text;
using System.Text.Json;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Cove.Data.Services;

/// <summary>
/// The only writer of <see cref="VideoShotSet"/>; see <see cref="IVideoShotService"/> for the contract.
/// </summary>
/// <remarks>
/// A set's shots live in its own row as arrays of cuts and labels, so every write is one statement on
/// that row: an insert that does nothing when the file already has a set, or an update or delete that
/// matches only the set and revision the write started from. A write that loses a race therefore
/// changes nothing and reports a conflict, without row locks, savepoints or the change tracker. Only
/// <see cref="MoveSetToFileAsync"/>, which changes two sets for a caller deleting a file, locks them
/// and works inside a savepoint.
/// </remarks>
public sealed class VideoShotService(CoveContext db) : IVideoShotService
{
    private const int SummaryChunkSize = 2_000;

    // A replace without an expected version retries when another write gets in between its read and
    // its own statement; more attempts than this means the set is being rewritten continuously.
    private const int ReplaceAttempts = 3;

    // Every column but the shots and the payload, which can be large.
    private static readonly Expression<Func<VideoShotSet, VideoShotSet>> Summary = set => new VideoShotSet
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
        var set = await db.VideoShotSets
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.FileId == fileId, cancellationToken);
        return set is null ? null : ToDto(set, await LoadFileStateAsync(fileId, cancellationToken), withShots: true);
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
                .Select(Summary)
                .ToListAsync(cancellationToken);
            if (sets.Count == 0)
                continue;

            var files = await LoadFileStatesAsync(sets.Select(set => set.FileId).ToArray(), cancellationToken);
            foreach (var set in sets)
                result[set.FileId] = ToDto(set, files.GetValueOrDefault(set.FileId), withShots: false);
        }

        return result;
    }

    public async Task<VideoShotWriteResult> WriteSetAsync(VideoShotSetWrite write, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(write);
        if (ValidateProvenance(write) is { } provenanceError)
            return Rejected(provenanceError);
        if (write.Expected is not null && write.WriteMode != VideoShotWriteMode.Replace)
            return Rejected("An expected set can only be given when replacing.");

        var partition = VideoShotPartition.Normalize(write.Shots, write.DurationSec, write.Fps, write.FrameCount);
        if (!partition.IsValid)
            return Rejected(partition.Error!);

        var file = await LoadFileStateAsync(write.FileId, cancellationToken);
        if (file is null)
            return new VideoShotWriteResult { Outcome = VideoShotWriteOutcome.FileNotFound, Reason = $"File {write.FileId} is not a video file." };
        if (!VideoShotPartition.DurationMatchesFile(write.DurationSec, file.DurationSec))
        {
            return Rejected($"The shots cover {VideoShotPartition.DescribeDurationMismatch(write.DurationSec, file.DurationSec)}; " +
                "they were probably analysed from a different file.");
        }

        var cuts = partition.ToCuts();
        var values = new VideoShotSet
        {
            FileId = write.FileId,
            SourceKey = write.SourceKey.Trim(),
            SourceRunId = NullIfBlank(write.SourceRunId),
            Model = NullIfBlank(write.Model),
            ModelVersion = NullIfBlank(write.ModelVersion),
            Mode = NullIfBlank(write.Mode),
            DecodeBackend = NullIfBlank(write.DecodeBackend),
            Fps = write.Fps,
            FrameCount = partition.FrameCount,
            DurationSec = write.DurationSec,
            EditedAt = ToUtc(write.EditedAt),
            FileSize = file.Size,
            Payload = ToDocument(write.Payload),
        };
        cuts.ApplyTo(values);

        for (var attempt = 0; attempt < ReplaceAttempts; attempt++)
        {
            var existing = await LoadSetAsync(write.FileId, Summary, cancellationToken);
            if (existing is not null && write.WriteMode == VideoShotWriteMode.SkipIfExists)
            {
                return new VideoShotWriteResult
                {
                    Outcome = VideoShotWriteOutcome.SkippedExisting,
                    Reason = "The file already has shot boundaries.",
                    Set = ToDto(existing, file, withShots: false),
                };
            }

            if (write.Expected is { } expected && !Matches(existing, expected))
                return WriteConflict(existing, file);

            if (existing is null)
            {
                (values.Id, values.Revision, values.CreatedAt, values.UpdatedAt) = (0, 1, default, default);
                if (await TryInsertAsync(values, cancellationToken))
                {
                    return new VideoShotWriteResult
                    {
                        Outcome = VideoShotWriteOutcome.Written,
                        Set = ToDto(values, file, withShots: false),
                    };
                }

                if (await LoadFileStateAsync(write.FileId, cancellationToken) is null)
                    return new VideoShotWriteResult { Outcome = VideoShotWriteOutcome.FileNotFound, Reason = $"File {write.FileId} is not a video file." };
                continue;
            }

            if (await TryReplaceAsync(existing, values, cancellationToken))
            {
                return new VideoShotWriteResult
                {
                    Outcome = VideoShotWriteOutcome.Replaced,
                    ReplacedEditedSet = existing.EditedAt is not null,
                    Set = ToDto(values, file, withShots: false),
                };
            }

            if (write.Expected is not null)
                return WriteConflict(await LoadSetAsync(write.FileId, Summary, cancellationToken), file);
        }

        return WriteConflict(await LoadSetAsync(write.FileId, Summary, cancellationToken), file);
    }

    public Task<VideoShotEditResult> SplitAsync(VideoShotSplitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!double.IsFinite(request.AtSec))
            return Task.FromResult(EditRejected("The split time must be a finite number."));
        if (ValidateSourceKey(request.SourceKey) is { } keyError)
            return Task.FromResult(EditRejected(keyError));

        return RunEditAsync(request.FileId, request.Expected, allowCreate: true, request.SourceKey, (context, cuts) =>
        {
            if (cuts is null)
            {
                if (!double.IsFinite(context.File.DurationSec) || context.File.DurationSec <= 0)
                    return EditRejected("The file has no known duration to draw shots over.");
                cuts = context.CreateManualSet(context.File.DurationSec, ShotCuts.Single(context.File.DurationSec));
            }

            var at = request.AtSec;
            var index = cuts.FindShot(at);
            if (index < 0)
                return EditRejected($"{request.AtSec:0.###}s is not inside any shot.");
            var start = cuts.StartOf(index);
            var end = cuts.EndOf(index);

            int? cutFrame = null;
            if (cuts.HasFrames)
            {
                var startFrame = cuts.StartFrameOf(index);
                var endFrame = cuts.EndFrameOf(index);
                (var frame, at) = SnapToFrame(at, start, end, startFrame, endFrame);
                if (frame <= startFrame || frame >= endFrame)
                    return EditRejected($"{request.AtSec:0.###}s is within a frame of an existing cut.");
                cutFrame = frame;
            }

            if (at - start < VideoShotRules.MinShotDurationSec || end - at < VideoShotRules.MinShotDurationSec)
                return EditRejected($"{request.AtSec:0.###}s is too close to an existing cut.");

            cuts.Split(index, at, cutFrame);
            return context.Save(cuts);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> MergeAsync(VideoShotMergeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Expected);
        return RunEditAsync(request.FileId, request.Expected, allowCreate: false, sourceKey: null, (context, cuts) =>
        {
            var cut = cuts!.FindCut(request.CutSec);
            if (cut < 0)
                return EditRejected($"There is no cut at {request.CutSec:0.###}s.");

            cuts.Merge(cut);
            return context.Save(cuts);
        }, cancellationToken);
    }

    public Task<VideoShotEditResult> MoveCutAsync(VideoShotMoveCutRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Expected);
        if (!double.IsFinite(request.ToSec))
            return Task.FromResult(EditRejected("The new cut time must be a finite number."));

        return RunEditAsync(request.FileId, request.Expected, allowCreate: false, sourceKey: null, (context, cuts) =>
        {
            var cut = cuts!.FindCut(request.FromSec);
            if (cut < 0)
                return EditRejected($"There is no cut at {request.FromSec:0.###}s.");

            // The cut separates shot `cut` from shot `cut + 1`, and may move anywhere between them.
            var start = cuts.StartOf(cut);
            var end = cuts.EndOf(cut + 1);
            var to = request.ToSec;
            if (to <= start || to >= end)
                return EditRejected($"{request.ToSec:0.###}s is not between the neighbouring cuts.");

            int? cutFrame = null;
            if (cuts.HasFrames)
            {
                var startFrame = cuts.StartFrameOf(cut);
                var endFrame = cuts.EndFrameOf(cut + 1);
                (var frame, to) = SnapToFrame(to, start, end, startFrame, endFrame);
                if (frame <= startFrame || frame >= endFrame)
                    return EditRejected($"{request.ToSec:0.###}s is within a frame of a neighbouring cut.");
                cutFrame = frame;
            }

            if (Math.Abs(to - cuts.Times[cut]) < 1e-9)
                return context.Unchanged();
            if (to - start < VideoShotRules.MinShotDurationSec || end - to < VideoShotRules.MinShotDurationSec)
                return EditRejected($"{request.ToSec:0.###}s is not between the neighbouring cuts.");

            cuts.Move(cut, to, cutFrame);
            return context.Save(cuts);
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

        return RunEditAsync(request.FileId, request.Expected, allowCreate: true, request.SourceKey, (context, cuts) =>
        {
            PartitionResult partition;
            if (cuts is null)
            {
                var duration = request.Shots.Max(shot => shot.EndSec);
                if (!double.IsFinite(duration))
                    return EditRejected("The shots do not cover the file's duration.");
                if (!VideoShotPartition.DurationMatchesFile(duration, context.File.DurationSec))
                    return EditRejected($"The shots end at {VideoShotPartition.DescribeDurationMismatch(duration, context.File.DurationSec)}.");
                partition = VideoShotPartition.Normalize(request.Shots, duration, null, null);
                if (!partition.IsValid)
                    return EditRejected(partition.Error!);
                return context.Save(context.CreateManualSet(duration, partition.ToCuts()));
            }

            // The frame count follows the replacement's frames, and there is none without them. The
            // frame rate stays, so shots with frames can be put back later, for example by an undo.
            var set = context.Set!;
            partition = VideoShotPartition.Normalize(request.Shots, set.DurationSec, set.Fps, null);
            if (!partition.IsValid)
                return EditRejected(partition.Error!);
            set.FrameCount = partition.FrameCount;
            return context.Save(partition.ToCuts());
        }, cancellationToken);
    }

    public async Task<VideoShotEditResult> DeleteAsync(VideoShotDeleteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Expected is not { } expected)
        {
            var count = await db.VideoShotSets.Where(set => set.FileId == request.FileId).ExecuteDeleteAsync(cancellationToken);
            return new VideoShotEditResult { Status = count > 0 ? VideoShotEditStatus.Deleted : VideoShotEditStatus.NotFound };
        }

        var deleted = await db.VideoShotSets
            .Where(set => set.FileId == request.FileId && set.Id == expected.SetId && set.Revision == expected.Revision)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted > 0)
            return new VideoShotEditResult { Status = VideoShotEditStatus.Deleted };

        var current = await GetForFileAsync(request.FileId, cancellationToken);
        return new VideoShotEditResult
        {
            Status = VideoShotEditStatus.Conflict,
            Error = current is null ? "The file's shot boundaries were deleted." : "The file's shot boundaries changed since they were read.",
            Set = current,
        };
    }

    /// <summary>
    /// Moves a file's set onto another file, for a caller that is about to delete the first file in
    /// favour of the second and has established that both show the same footage on the same timeline.
    /// The set stays behind, to be deleted with its file, when it no longer describes that file's
    /// contents, or when the target has a set of its own that is not an unedited analysis losing to a
    /// hand-edited set, or when either set changes while the move runs. Frames are dropped when the frame
    /// rates differ. Runs in the caller's transaction, inside a savepoint, and leaves the change tracker
    /// alone.
    /// </summary>
    /// <returns>Whether the set moved.</returns>
    internal static async Task<bool> MoveSetToFileAsync(CoveContext db, int fromFileId, int toFileId, CancellationToken cancellationToken)
    {
        if (fromFileId == toFileId)
            return false;
        if (db.Database.IsNpgsql())
        {
            // Lock both sets, in id order so that moves between the same two files cannot deadlock. An
            // edit that started from either set then waits, and conflicts once this transaction commits.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM video_shot_sets WHERE \"FileId\" IN ({fromFileId}, {toFileId}) ORDER BY \"Id\" FOR UPDATE", cancellationToken);
        }

        var sets = await db.VideoShotSets
            .AsNoTracking()
            .Where(set => set.FileId == fromFileId || set.FileId == toFileId)
            .Select(set => new { set.Id, set.FileId, set.Fps, set.FileSize, set.EditedAt, set.Revision })
            .ToListAsync(cancellationToken);
        var moving = sets.FirstOrDefault(set => set.FileId == fromFileId);
        var existing = sets.FirstOrDefault(set => set.FileId == toFileId);
        if (moving is null || existing is not null && (moving.EditedAt is null || existing.EditedAt is not null))
            return false;

        var files = await db.VideoFiles
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(file => file.Id == fromFileId || file.Id == toFileId)
            .Select(file => new { file.Id, file.Size, file.FrameRate })
            .ToListAsync(cancellationToken);
        var source = files.FirstOrDefault(file => file.Id == fromFileId);
        var target = files.FirstOrDefault(file => file.Id == toFileId);
        // A set measured on older contents of its file must not look current on the target.
        if (source is null || target is null || source.Size != moving.FileSize)
            return false;

        var dropFrames = moving.Fps is { } fps && (target.FrameRate <= 0 || Math.Abs(target.FrameRate - fps) > FrameRateTolerance);
        var transaction = db.Database.CurrentTransaction;
        var savepoint = "video_shots_move_" + Guid.NewGuid().ToString("n")[..12];
        if (transaction is not null)
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);
        try
        {
            // Both statements match only the sets as they were read: a set edited since then stays where it is.
            var replaced = existing is null || await db.VideoShotSets
                .Where(set => set.Id == existing.Id && set.Revision == existing.Revision && set.EditedAt == null)
                .ExecuteDeleteAsync(cancellationToken) == 1;

            var now = DateTime.UtcNow;
            var moved = replaced && await db.VideoShotSets
                .Where(set => set.Id == moving.Id && set.Revision == moving.Revision)
                .ExecuteUpdateAsync(update =>
                {
                    update.SetProperty(set => set.FileId, toFileId)
                        .SetProperty(set => set.FileSize, target.Size)
                        .SetProperty(set => set.Revision, set => set.Revision + 1)
                        .SetProperty(set => set.UpdatedAt, now);
                    if (dropFrames)
                    {
                        update.SetProperty(set => set.Fps, (double?)null)
                            .SetProperty(set => set.FrameCount, (int?)null)
                            .SetProperty(set => set.CutFrames, (int[]?)null);
                    }
                }, cancellationToken) == 1;
            if (transaction is not null)
            {
                if (moved)
                    await transaction.ReleaseSavepointAsync(savepoint, cancellationToken);
                else
                    await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
            }

            return moved;
        }
        catch (Exception exception) when (transaction is not null && IsUniqueViolation(exception))
        {
            // The target gained a set that this transaction's snapshot could not see: it keeps it.
            await transaction.RollbackToSavepointAsync(savepoint, CancellationToken.None);
            return false;
        }
    }

    // Frame rates within this of each other keep a set's frame numbers valid on the other file.
    private const double FrameRateTolerance = 0.01;

    private static bool IsUniqueViolation(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
                return true;
        }

        return false;
    }

    // ── edits ────────────────────────────────────────────────────────────

    /// <summary>An edit of the loaded cuts, or of null when the file has no set yet and the edit may create one.</summary>
    private delegate VideoShotEditResult EditOperation(EditContext context, ShotCuts? cuts);

    private async Task<VideoShotEditResult> RunEditAsync(
        int fileId,
        VideoShotSetVersion? expected,
        bool allowCreate,
        string? sourceKey,
        EditOperation operation,
        CancellationToken cancellationToken)
    {
        var file = await LoadFileStateAsync(fileId, cancellationToken);
        if (file is null)
            return new VideoShotEditResult { Status = VideoShotEditStatus.NotFound, Error = $"File {fileId} is not a video file." };

        var set = await LoadSetAsync(fileId, columns: null, cancellationToken);
        if (set is null)
        {
            if (expected is not null)
                return new VideoShotEditResult { Status = VideoShotEditStatus.Conflict, Error = "The file's shot boundaries were deleted." };
            if (!allowCreate)
                return new VideoShotEditResult { Status = VideoShotEditStatus.NotFound, Error = "The file has no shot boundaries." };
        }
        else if (expected is null || !Matches(set, expected))
        {
            return EditConflict(
                ToDto(set, file, withShots: true),
                expected is null ? "The file already has shot boundaries." : "The file's shot boundaries changed since they were read.");
        }

        var context = new EditContext(file, set, sourceKey);
        var result = operation(context, set is null ? null : ShotCuts.Of(set));
        if (context.Pending is not { } pending)
            return result;

        var saved = context.Creating
            ? await TryInsertAsync(pending, cancellationToken)
            : await TryUpdateShotsAsync(pending, expected!.Revision, cancellationToken);
        if (saved)
        {
            // A created set has its id only once it is inserted.
            return context.Creating ? result with { Set = ToDto(pending, file, withShots: true) } : result;
        }

        if (context.Creating && await LoadFileStateAsync(fileId, cancellationToken) is null)
            return new VideoShotEditResult { Status = VideoShotEditStatus.NotFound, Error = $"File {fileId} is not a video file." };

        var current = await GetForFileAsync(fileId, cancellationToken);
        return EditConflict(current, current is null
            ? "The file's shot boundaries were deleted."
            : context.Creating ? "The file already has shot boundaries." : "The file's shot boundaries changed since they were read.");
    }

    /// <summary>The file and set an edit works on, and the set row it leaves to be written.</summary>
    private sealed class EditContext(FileState file, VideoShotSet? set, string? sourceKey)
    {
        public FileState File { get; } = file;
        public VideoShotSet? Set { get; private set; } = set;
        public bool Creating { get; private set; }

        /// <summary>The set with the edit applied, once the edit has changed it.</summary>
        public VideoShotSet? Pending { get; private set; }

        public ShotCuts CreateManualSet(double durationSec, ShotCuts cuts)
        {
            Set = new VideoShotSet
            {
                FileId = File.FileId,
                SourceKey = string.IsNullOrWhiteSpace(sourceKey) ? VideoShotRules.ManualSourceKey : sourceKey.Trim(),
                DurationSec = durationSec,
                FileSize = File.Size,
                Revision = 0,
            };
            Creating = true;
            return cuts;
        }

        public VideoShotEditResult Unchanged() => new()
        {
            Status = VideoShotEditStatus.Updated,
            Set = ToDto(Set!, File, withShots: true),
        };

        public VideoShotEditResult Save(ShotCuts cuts)
        {
            var now = DateTime.UtcNow;
            var current = Set!;
            cuts.ApplyTo(current);
            current.Revision++;
            current.EditedAt = now;
            current.UpdatedAt = now;
            if (Creating)
                current.CreatedAt = now;
            Pending = current;
            return new VideoShotEditResult
            {
                Status = VideoShotEditStatus.Updated,
                Set = ToDto(current, File, withShots: true),
            };
        }
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

    // ── statements ───────────────────────────────────────────────────────

    /// <summary>The file's set, every column or only <paramref name="columns"/>.</summary>
    private Task<VideoShotSet?> LoadSetAsync(int fileId, Expression<Func<VideoShotSet, VideoShotSet>>? columns, CancellationToken cancellationToken)
    {
        var query = db.VideoShotSets.AsNoTracking().Where(set => set.FileId == fileId);
        return (columns is null ? query : query.Select(columns)).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Inserts <paramref name="set"/> unless its file already has a set or no longer exists, and fills in its id. The insert
    /// is written out rather than saved through the context, so that it neither saves the caller's
    /// pending changes nor fails, and with it the caller's transaction, when another write got there first.
    /// </summary>
    private async Task<bool> TryInsertAsync(VideoShotSet set, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        if (set.CreatedAt == default)
            set.CreatedAt = now;
        if (set.UpdatedAt == default)
            set.UpdatedAt = set.CreatedAt;

        var entityType = db.Model.FindEntityType(typeof(VideoShotSet))!;
        var table = StoreObjectIdentifier.Table(entityType.GetTableName()!, entityType.GetSchema());
        var sql = db.GetService<ISqlGenerationHelper>();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        var columns = new List<string>();
        var placeholders = new List<string>();
        var parameters = new List<object>();
        foreach (var property in entityType.GetProperties().Where(property => !property.IsPrimaryKey()))
        {
            var name = "p" + parameters.Count;
            columns.Add(sql.DelimitIdentifier(property.GetColumnName(table)!));
            placeholders.Add(sql.GenerateParameterNamePlaceholder(name));
            parameters.Add(property.GetRelationalTypeMapping().CreateParameter(
                command, sql.GenerateParameterName(name), property.GetGetter().GetClrValueUsingContainingEntity(set), property.IsNullable));
        }

        // The file row is share-locked in the same statement, so a file deleted meanwhile makes the insert
        // add nothing rather than fail on the foreign key, which would abort the caller's transaction.
        var fileParameter = "p" + parameters.Count;
        parameters.Add(entityType.FindProperty(nameof(VideoShotSet.FileId))!.GetRelationalTypeMapping()
            .CreateParameter(command, sql.GenerateParameterName(fileParameter), set.FileId, nullable: false));
        var id = sql.DelimitIdentifier(entityType.FindPrimaryKey()!.Properties[0].GetColumnName(table)!);
        var fileId = sql.DelimitIdentifier(entityType.FindProperty(nameof(VideoShotSet.FileId))!.GetColumnName(table)!);
        var lockFile = db.Database.IsNpgsql() ? " FOR KEY SHARE" : string.Empty;
        var statement = $"INSERT INTO {sql.DelimitIdentifier(table.Name, table.Schema)} ({string.Join(", ", columns)}) " +
            $"SELECT {string.Join(", ", placeholders)} " +
            $"WHERE EXISTS (SELECT 1 FROM files WHERE \"Id\" = {sql.GenerateParameterNamePlaceholder(fileParameter)}{lockFile}) " +
            $"ON CONFLICT ({fileId}) DO NOTHING RETURNING {id} AS {sql.DelimitIdentifier("Value")}";
        var inserted = await db.Database.SqlQueryRaw<int>(statement, parameters.ToArray()).ToListAsync(cancellationToken);
        if (inserted.Count == 0)
            return false;
        set.Id = inserted[0];
        return true;
    }

    /// <summary>Replaces every column of <paramref name="existing"/> with <paramref name="values"/>, if it is still at the revision read.</summary>
    private async Task<bool> TryReplaceAsync(VideoShotSet existing, VideoShotSet values, CancellationToken cancellationToken)
    {
        values.Id = existing.Id;
        values.CreatedAt = existing.CreatedAt;
        values.UpdatedAt = DateTime.UtcNow;
        values.Revision = existing.Revision + 1;
        var updated = await db.VideoShotSets
            .Where(set => set.Id == existing.Id && set.Revision == existing.Revision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(set => set.SourceKey, values.SourceKey)
                .SetProperty(set => set.SourceRunId, values.SourceRunId)
                .SetProperty(set => set.Model, values.Model)
                .SetProperty(set => set.ModelVersion, values.ModelVersion)
                .SetProperty(set => set.Mode, values.Mode)
                .SetProperty(set => set.DecodeBackend, values.DecodeBackend)
                .SetProperty(set => set.Fps, values.Fps)
                .SetProperty(set => set.FrameCount, values.FrameCount)
                .SetProperty(set => set.DurationSec, values.DurationSec)
                .SetProperty(set => set.ShotCount, values.ShotCount)
                .SetProperty(set => set.CutTimes, values.CutTimes)
                .SetProperty(set => set.CutFrames, values.CutFrames)
                .SetProperty(set => set.ShotTypes, values.ShotTypes)
                .SetProperty(set => set.Transitions, values.Transitions)
                .SetProperty(set => set.EditedAt, values.EditedAt)
                .SetProperty(set => set.Revision, values.Revision)
                .SetProperty(set => set.FileSize, values.FileSize)
                .SetProperty(set => set.Payload, values.Payload)
                .SetProperty(set => set.UpdatedAt, values.UpdatedAt), cancellationToken);
        return updated == 1;
    }

    /// <summary>Writes an edit's shots, if the set is still at the revision the edit started from.</summary>
    private async Task<bool> TryUpdateShotsAsync(VideoShotSet set, int expectedRevision, CancellationToken cancellationToken)
    {
        var updated = await db.VideoShotSets
            .Where(candidate => candidate.Id == set.Id && candidate.Revision == expectedRevision)
            .ExecuteUpdateAsync(update => update
                .SetProperty(candidate => candidate.FrameCount, set.FrameCount)
                .SetProperty(candidate => candidate.ShotCount, set.ShotCount)
                .SetProperty(candidate => candidate.CutTimes, set.CutTimes)
                .SetProperty(candidate => candidate.CutFrames, set.CutFrames)
                .SetProperty(candidate => candidate.ShotTypes, set.ShotTypes)
                .SetProperty(candidate => candidate.Transitions, set.Transitions)
                .SetProperty(candidate => candidate.EditedAt, set.EditedAt)
                .SetProperty(candidate => candidate.Revision, set.Revision)
                .SetProperty(candidate => candidate.UpdatedAt, set.UpdatedAt), cancellationToken);
        return updated == 1;
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

    private static bool Matches(VideoShotSet? set, VideoShotSetVersion expected)
        => set is not null && set.Id == expected.SetId && set.Revision == expected.Revision;

    /// <summary>A summary, or with <paramref name="withShots"/> the whole set, shots and payload included.</summary>
    private static VideoShotSetDto ToDto(VideoShotSet set, FileState? file, bool withShots) => new()
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
        Payload = withShots ? set.Payload?.RootElement.Clone() : null,
        Shots = withShots ? ShotCuts.Of(set).ToDtos() : null,
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

    private static VideoShotWriteResult WriteConflict(VideoShotSet? current, FileState file) => new()
    {
        Outcome = VideoShotWriteOutcome.Conflict,
        Reason = "The file's shot boundaries changed since they were read.",
        Set = current is null ? null : ToDto(current, file, withShots: false),
    };

    private static VideoShotEditResult EditRejected(string error)
        => new() { Status = VideoShotEditStatus.Invalid, Error = error };

    private static VideoShotEditResult EditConflict(VideoShotSetDto? current, string error)
        => new() { Status = VideoShotEditStatus.Conflict, Error = error, Set = current };
}
