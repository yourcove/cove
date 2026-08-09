using System.Text.Json;

using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Cove.Plugins;

using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class AiDataPurgeService(
    CoveContext db,
    IEnumerable<IFaceLifecycleParticipant> faceLifecycleParticipants,
    IBlobService blobService,
    ILogger<AiDataPurgeService> logger,
    SegmentSpanResolver? segmentSpanResolver = null,
    IExtensionServiceExchange? serviceExchange = null)
{
    private const int PurgeBatchSize = 5_000;

    private readonly CoveContext _db = db;
    // Host registrations plus extension-published participants. Since the extensions-runtime redesign
    // extensions surface IFaceLifecycleParticipant through the cross-extension exchange rather than the
    // host container, so merge both sources (deduplicated) before notifying on delete.
    private readonly IReadOnlyList<IFaceLifecycleParticipant> _faceLifecycleParticipants =
        faceLifecycleParticipants
            .Concat(serviceExchange?.GetAll<IFaceLifecycleParticipant>() ?? [])
            .Distinct()
            .ToList();
    private readonly IBlobService _blobService = blobService;
    private readonly ILogger<AiDataPurgeService> _logger = logger;
    private readonly SegmentSpanResolver? _segmentSpanResolver = segmentSpanResolver;

    public async Task<AiDataSummaryDto> GetSummaryAsync(AiDataSelectorDto selectorDto, CancellationToken cancellationToken = default)
    {
        var selector = Normalize(selectorDto);
        var runModels = await LoadAiRunModelLookupAsync(selector, cancellationToken);
        var records = new List<AiDataSummaryRecord>();

        if (selector.IncludesKind("embedding"))
            records.AddRange(await GetEmbeddingSummaryRecordsAsync(selector, runModels, cancellationToken));

        if (selector.IncludesKind("detection"))
            records.AddRange(await GetDetectionSummaryRecordsAsync(selector, runModels, cancellationToken));

        if (selector.IncludesKind("segment"))
            records.AddRange(await GetSegmentSummaryRecordsAsync(selector, runModels, cancellationToken));

        if (selector.IncludesKind("tagapplication"))
            records.AddRange(await GetTagApplicationSummaryRecordsAsync(selector, cancellationToken));

        if (selector.IncludesKind("face"))
            records.AddRange(await GetFaceSummaryRecordsAsync(selector, runModels, cancellationToken));

        var items = records
            .GroupBy(record => new
            {
                record.Kind,
                record.Detail,
                record.SourceKey,
                record.SourceRunId,
                record.Model,
                record.HostType,
            })
            .Select(group => new AiDataSummaryItemDto(
                group.Key.Kind,
                group.Key.Detail,
                group.Key.SourceKey,
                group.Key.SourceRunId,
                group.Key.Model,
                group.Key.HostType,
                group.Count()))
            .OrderBy(item => item.Kind)
            .ThenBy(item => item.Detail)
            .ThenBy(item => item.SourceKey)
            .ThenBy(item => item.Model)
            .ThenBy(item => item.HostType)
            .ToList();

        var totals = items
            .GroupBy(item => item.Kind)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Count), StringComparer.OrdinalIgnoreCase);

        return new AiDataSummaryDto(items, totals, items.Sum(item => item.Count));
    }

    public async Task<int> DeleteEmbeddingsAsync(AiDataSelectorDto selectorDto, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var selector = Normalize(selectorDto);
        var runModels = await LoadAiRunModelLookupAsync(selector, cancellationToken);
        return await PurgeEmbeddingsCoreAsync(selector, runModels, dryRun, cancellationToken);
    }

    public async Task<AiDataPurgeResultDto> PurgeAsync(AiDataSelectorDto selectorDto, bool dryRun = false, CancellationToken cancellationToken = default)
    {
        var selector = Normalize(selectorDto);
        var runModels = await LoadAiRunModelLookupAsync(selector, cancellationToken);
        var removed = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var faceIds = selector.IncludesKind("face")
            ? await ResolveFaceIdsAsync(selector, runModels, cancellationToken)
            : [];
        IReadOnlyCollection<int>? excludedFaceIds = faceIds.Count > 0 ? faceIds : null;
        var affectedRunKeys = await CollectAffectedRunKeysAsync(selector, runModels, faceIds, cancellationToken);
        var affectedVideoIds = dryRun
            ? []
            : await CollectAffectedVideoIdsAsync(selector, runModels, faceIds, cancellationToken);
        affectedRunKeys.UnionWith(await CollectSelectedAiRunKeysAsync(selector, cancellationToken));

        if (faceIds.Count > 0)
            MergeRemovedCounts(removed, await PurgeFacesByIdsAsync(faceIds, dryRun, cancellationToken));

        // Let face providers clean up derived state not represented by a Face row (e.g. an extension's
        // provisional identity graph). Fires even when no Cove faces matched, so an "entire source" clear
        // still drops provisional identities. The per-face OnDeletingAsync path above handles promoted
        // identities tied to a Face.
        if (!dryRun && selector.IncludesKind("face") && _faceLifecycleParticipants.Count > 0)
        {
            var purgeScope = new FacePurgeScope(
                selector.SourceKey,
                selector.HostType,
                selector.HostId,
                selector.HostId is null && string.IsNullOrWhiteSpace(selector.SourceRunId),
                faceIds);
            foreach (var participant in _faceLifecycleParticipants)
                await participant.OnFacesPurgedAsync(purgeScope, cancellationToken);
        }

        if (selector.IncludesKind("embedding"))
            AddRemovedCount(removed, "embedding", await PurgeEmbeddingsCoreAsync(selector, runModels, dryRun, cancellationToken, excludedFaceIds));

        if (selector.IncludesKind("detection"))
            AddRemovedCount(removed, "detection", await PurgeDetectionsCoreAsync(selector, runModels, dryRun, cancellationToken, excludedFaceIds));

        if (selector.IncludesKind("segment"))
            AddRemovedCount(removed, "segment", await PurgeSegmentsCoreAsync(selector, runModels, dryRun, cancellationToken, excludedFaceIds));

        if (selector.IncludesKind("tagapplication"))
            MergeRemovedCounts(removed, await PurgeTagApplicationsCoreAsync(selector, dryRun, cancellationToken));

        if (affectedRunKeys.Count > 0)
            AddRemovedCount(removed, "aiRun", await PurgeUnreferencedAiRunsAsync(affectedRunKeys, dryRun, cancellationToken));

        if (!dryRun && affectedVideoIds.Count > 0 && _segmentSpanResolver is not null)
        {
            foreach (var videoId in affectedVideoIds)
            {
                _segmentSpanResolver.EvictVideo(videoId);
            }
        }

        return new AiDataPurgeResultDto(removed);
    }

    private async Task<HashSet<int>> CollectAffectedVideoIdsAsync(
        AiDataSelector selector,
        IReadOnlyDictionary<string, string?> runModels,
        IReadOnlyCollection<int> faceIds,
        CancellationToken cancellationToken)
    {
        var videoIds = new HashSet<int>();

        if (selector.IncludesKind("segment"))
        {
            var segmentCandidates = await QueryVideoSegmentCandidatesAsync(selector, runModels, cancellationToken);
            videoIds.UnionWith(segmentCandidates.Select(candidate => candidate.HostId));
        }

        if (faceIds.Count > 0)
        {
            var faceIdArray = faceIds.ToArray();
            var faceSegmentVideoIds = await _db.Segments
                .AsNoTracking()
                .Where(segment => segment.HostType == SegmentHostType.Video
                    && segment.RefId.HasValue
                    && segment.Kind != null
                    && segment.Kind.ToLower() == "face"
                    && faceIdArray.Contains((int)segment.RefId.Value))
                .Select(segment => segment.HostId)
                .Distinct()
                .ToListAsync(cancellationToken);
            videoIds.UnionWith(faceSegmentVideoIds);
        }

        return videoIds;
    }

    private async Task<List<VideoSegmentCandidate>> QueryVideoSegmentCandidatesAsync(
        AiDataSelector selector,
        IReadOnlyDictionary<string, string?> runModels,
        CancellationToken cancellationToken)
    {
        if (TryParseSegmentHostType(selector.HostType, out var hostType) && hostType != SegmentHostType.Video)
        {
            return [];
        }

        var query = _db.ReadSet<Segment>()
            .AsNoTracking()
            .Where(segment => segment.HostType == SegmentHostType.Video);

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(segment => segment.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(segment => segment.SourceRunId == selector.SourceRunId);

        if (selector.HostId.HasValue)
            query = query.Where(segment => segment.HostId == selector.HostId.Value);

        var rows = await query
            .Select(segment => new VideoSegmentCandidate(
                segment.HostId,
                segment.SourceRunId,
                ExtractModelKey(segment.Payload)))
            .ToListAsync(cancellationToken);

        return rows
            .Where(candidate => MatchesOptional(ResolveArtifactModel(candidate.Model, candidate.SourceRunId, runModels), selector.Model))
            .ToList();
    }

    private async Task<HashSet<string>> CollectAffectedRunKeysAsync(
        AiDataSelector selector,
        IReadOnlyDictionary<string, string?> runModels,
        IReadOnlyCollection<int> faceIds,
        CancellationToken cancellationToken)
    {
        var runKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (selector.IncludesKind("embedding"))
            AddRunKeys(runKeys, (await QueryEmbeddingCandidatesAsync(selector, runModels, cancellationToken)).Select(candidate => candidate.SourceRunId));

        if (selector.IncludesKind("detection"))
            AddRunKeys(runKeys, (await QueryDetectionCandidatesAsync(selector, runModels, cancellationToken)).Select(candidate => candidate.SourceRunId));

        if (selector.IncludesKind("segment"))
            AddRunKeys(runKeys, (await QuerySegmentCandidatesAsync(selector, runModels, cancellationToken)).Select(candidate => candidate.SourceRunId));

        if (selector.IncludesKind("tagapplication"))
            AddRunKeys(runKeys, (await QueryTagApplicationCandidatesAsync(selector, cancellationToken)).Select(candidate => candidate.SourceRunId));

        if (faceIds.Count > 0)
        {
            var faceIdSet = faceIds.ToHashSet();

            AddRunKeys(runKeys, await _db.FaceAppearances
                .AsNoTracking()
                .Where(appearance => faceIdSet.Contains(appearance.FaceId))
                .Select(appearance => appearance.SourceRunId)
                .ToListAsync(cancellationToken));

            AddRunKeys(runKeys, await _db.Embeddings
                .AsNoTracking()
                .Where(embedding => embedding.HostType == EmbeddingHostType.Face && faceIdSet.Contains(embedding.HostId))
                .Select(embedding => embedding.SourceRunId)
                .ToListAsync(cancellationToken));

            AddRunKeys(runKeys, await _db.Set<Detection>()
                .AsNoTracking()
                .Where(detection => detection.RefId.HasValue
                    && detection.RefKind != null
                    && detection.RefKind.ToLower() == "face"
                    && faceIdSet.Contains((int)detection.RefId.Value))
                .Select(detection => detection.SourceRunId)
                .ToListAsync(cancellationToken));

            AddRunKeys(runKeys, await _db.Segments
                .AsNoTracking()
                .Where(segment => segment.RefId.HasValue
                    && segment.Kind != null
                    && segment.Kind.ToLower() == "face"
                    && faceIdSet.Contains((int)segment.RefId.Value))
                .Select(segment => segment.SourceRunId)
                .ToListAsync(cancellationToken));
        }

        return runKeys;
    }

    private async Task<HashSet<string>> CollectSelectedAiRunKeysAsync(AiDataSelector selector, CancellationToken cancellationToken)
    {
        var runKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!ShouldSelectAiRunsForSource(selector.SourceKey))
        {
            return runKeys;
        }

        var query = _db.ReadSet<AiRun>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey) && await AiRunSourceExistsAsync(selector.SourceKey, cancellationToken))
            query = query.Where(run => run.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(run => run.RunKey == selector.SourceRunId);

        if (TryParseAiRunTargetType(selector.HostType, out var targetType))
            query = query.Where(run => run.TargetType == targetType);

        if (selector.HostId.HasValue)
            query = query.Where(run => run.TargetId == selector.HostId.Value);

        var rows = await query
            .Select(run => new { run.RunKey, run.Models })
            .ToListAsync(cancellationToken);

        AddRunKeys(runKeys, rows
            .Where(run => MatchesOptional(ExtractAiRunModel(run.Models), selector.Model))
            .Select(run => run.RunKey));

        return runKeys;
    }

    private async Task<List<AiDataSummaryRecord>> GetEmbeddingSummaryRecordsAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<Embedding>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(embedding => embedding.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(embedding => embedding.SourceRunId == selector.SourceRunId);

        if (!string.IsNullOrWhiteSpace(selector.Modality) && TryParseEmbeddingModality(selector.Modality, out var modality))
            query = query.Where(embedding => embedding.Modality == modality);

        if (TryParseEmbeddingHostType(selector.HostType, out var hostType))
            query = query.Where(embedding => embedding.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(embedding => embedding.HostId == selector.HostId.Value);

        var rows = await query
            .Select(embedding => new
            {
                embedding.SourceKey,
                embedding.SourceRunId,
                embedding.HostType,
                embedding.Modality,
                embedding.Meta,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AiDataSummaryRecord(
                "embedding",
                NormalizeEnumName(row.Modality),
                row.SourceKey,
                Clean(row.SourceRunId),
                ResolveArtifactModel(ExtractModelKey(row.Meta), row.SourceRunId, runModels),
                NormalizeEnumName(row.HostType)))
            .Where(record => MatchesOptional(record.Model, selector.Model))
            .ToList();
    }

    private async Task<List<AiDataSummaryRecord>> GetDetectionSummaryRecordsAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<Detection>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(detection => detection.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(detection => detection.SourceRunId == selector.SourceRunId);

        if (TryParseDetectionHostType(selector.HostType, out var hostType))
            query = query.Where(detection => detection.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(detection => detection.HostId == selector.HostId.Value);

        var rows = await query
            .Select(detection => new
            {
                detection.SourceKey,
                detection.SourceRunId,
                detection.HostType,
                detection.Class,
                detection.Extra,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AiDataSummaryRecord(
                "detection",
                Clean(row.Class),
                row.SourceKey,
                Clean(row.SourceRunId),
                ResolveArtifactModel(ExtractModelKey(row.Extra), row.SourceRunId, runModels),
                NormalizeEnumName(row.HostType)))
            .Where(record => MatchesOptional(record.Model, selector.Model))
            .ToList();
    }

    private async Task<List<AiDataSummaryRecord>> GetSegmentSummaryRecordsAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<Segment>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(segment => segment.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(segment => segment.SourceRunId == selector.SourceRunId);

        if (TryParseSegmentHostType(selector.HostType, out var hostType))
            query = query.Where(segment => segment.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(segment => segment.HostId == selector.HostId.Value);

        var rows = await query
            .Select(segment => new
            {
                segment.SourceKey,
                segment.SourceRunId,
                segment.HostType,
                segment.Kind,
                segment.Payload,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AiDataSummaryRecord(
                "segment",
                Clean(row.Kind),
                row.SourceKey,
                Clean(row.SourceRunId),
                ResolveArtifactModel(ExtractModelKey(row.Payload), row.SourceRunId, runModels),
                NormalizeEnumName(row.HostType)))
            .Where(record => MatchesOptional(record.Model, selector.Model))
            .ToList();
    }

    private async Task<List<AiDataSummaryRecord>> GetTagApplicationSummaryRecordsAsync(AiDataSelector selector, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<TagApplication>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(application => application.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(application => application.SourceRunId == selector.SourceRunId);

        if (!string.IsNullOrWhiteSpace(selector.Model))
            query = query.Where(application => application.ModelKey == selector.Model);

        if (TryParseAffinityHostType(selector.HostType, out var hostType))
            query = query.Where(application => application.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(application => application.HostId == selector.HostId.Value);

        var rows = await query
            .Select(application => new
            {
                application.SourceKey,
                application.SourceRunId,
                application.ModelKey,
                application.HostType,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new AiDataSummaryRecord(
                "tagApplication",
                null,
                row.SourceKey,
                Clean(row.SourceRunId),
                Clean(row.ModelKey),
                NormalizeEnumName(row.HostType)))
            .ToList();
    }

    private async Task<List<AiDataSummaryRecord>> GetFaceSummaryRecordsAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<Face>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey) || !string.IsNullOrWhiteSpace(selector.SourceRunId) || !string.IsNullOrWhiteSpace(selector.Model) || selector.HasHostFilter)
        {
            var faceIds = await ResolveFaceIdsAsync(selector, runModels, cancellationToken);
            if (faceIds.Count == 0)
            {
                return [];
            }

            query = query.Where(face => faceIds.Contains(face.Id));
        }

        var faces = await query
            .Select(face => new { face.PrimarySourceKey })
            .ToListAsync(cancellationToken);

        return faces
            .Select(face => new AiDataSummaryRecord(
                "face",
                null,
                Clean(face.PrimarySourceKey) ?? "unknown",
                null,
                null,
                "face"))
            .Where(record => MatchesOptional(record.Model, selector.Model))
            .ToList();
    }

    private async Task<int> PurgeEmbeddingsCoreAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, bool dryRun, CancellationToken cancellationToken, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var candidates = await QueryEmbeddingCandidatesAsync(selector, runModels, cancellationToken, excludedFaceIds: excludedFaceIds);
        if (candidates.Count == 0)
        {
            return 0;
        }

        if (dryRun)
        {
            return candidates.Count;
        }

        return await RemoveByIdsInBatchesAsync(_db.Embeddings, candidates.Select(candidate => candidate.Id), cancellationToken);
    }

    private async Task<int> PurgeDetectionsCoreAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, bool dryRun, CancellationToken cancellationToken, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var candidates = await QueryDetectionCandidatesAsync(selector, runModels, cancellationToken, excludedFaceIds: excludedFaceIds);
        if (candidates.Count == 0)
        {
            return 0;
        }

        if (dryRun)
        {
            return candidates.Count;
        }

        return await RemoveByIdsInBatchesAsync(_db.Set<Detection>(), candidates.Select(candidate => candidate.Id), cancellationToken);
    }

    private async Task<int> PurgeSegmentsCoreAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, bool dryRun, CancellationToken cancellationToken, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var candidates = await QuerySegmentCandidatesAsync(selector, runModels, cancellationToken, excludedFaceIds: excludedFaceIds);
        if (candidates.Count == 0)
        {
            return 0;
        }

        if (dryRun)
        {
            return candidates.Count;
        }

        return await RemoveByIdsInBatchesAsync(_db.Segments, candidates.Select(candidate => candidate.Id), cancellationToken);
    }

    private async Task<Dictionary<string, int>> PurgeTagApplicationsCoreAsync(AiDataSelector selector, bool dryRun, CancellationToken cancellationToken)
    {
        var candidates = await QueryTagApplicationCandidatesAsync(selector, cancellationToken);

        if (candidates.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        if (dryRun)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["tagApplication"] = candidates.Count,
            };
        }

        var removedCount = await RemoveByIdsInBatchesAsync(_db.TagApplications, candidates.Select(candidate => candidate.Id), cancellationToken);

        var affectedPairs = candidates
            .Select(candidate => new TagHostPair(candidate.HostType, candidate.HostId, candidate.TagId))
            .Distinct()
            .ToArray();
        await RemoveOrphanedTagLinksAsync(affectedPairs, cancellationToken);

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["tagApplication"] = removedCount,
        };
    }

    private async Task<List<TagApplicationCandidate>> QueryTagApplicationCandidatesAsync(AiDataSelector selector, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<TagApplication>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(application => application.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(application => application.SourceRunId == selector.SourceRunId);

        if (!string.IsNullOrWhiteSpace(selector.Model))
            query = query.Where(application => application.ModelKey == selector.Model);

        if (TryParseAffinityHostType(selector.HostType, out var hostType))
            query = query.Where(application => application.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(application => application.HostId == selector.HostId.Value);

        return await query
            .Select(application => new TagApplicationCandidate(application.Id, application.HostType, application.HostId, application.TagId, application.SourceRunId))
            .ToListAsync(cancellationToken);
    }

    private async Task<int> PurgeUnreferencedAiRunsAsync(IReadOnlyCollection<string> runKeys, bool dryRun, CancellationToken cancellationToken)
    {
        if (runKeys.Count == 0)
        {
            return 0;
        }

        var runKeyArray = runKeys
            .Where(static runKey => !string.IsNullOrWhiteSpace(runKey))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (runKeyArray.Length == 0)
        {
            return 0;
        }

        var candidateRuns = await _db.AiRuns
            .Where(run => runKeyArray.Contains(run.RunKey) && run.Status != AiRunStatus.Pending && run.Status != AiRunStatus.Running)
            .ToListAsync(cancellationToken);
        if (candidateRuns.Count == 0)
        {
            return 0;
        }

        var referencedRunKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRunKeys(referencedRunKeys, await _db.Embeddings
            .AsNoTracking()
            .Where(embedding => embedding.SourceRunId != null && runKeyArray.Contains(embedding.SourceRunId))
            .Select(embedding => embedding.SourceRunId)
            .Distinct()
            .ToListAsync(cancellationToken));

        AddRunKeys(referencedRunKeys, await _db.Set<Detection>()
            .AsNoTracking()
            .Where(detection => detection.SourceRunId != null && runKeyArray.Contains(detection.SourceRunId))
            .Select(detection => detection.SourceRunId)
            .Distinct()
            .ToListAsync(cancellationToken));

        AddRunKeys(referencedRunKeys, await _db.Segments
            .AsNoTracking()
            .Where(segment => segment.SourceRunId != null && runKeyArray.Contains(segment.SourceRunId))
            .Select(segment => segment.SourceRunId)
            .Distinct()
            .ToListAsync(cancellationToken));

        AddRunKeys(referencedRunKeys, await _db.TagApplications
            .AsNoTracking()
            .Where(application => runKeyArray.Contains(application.SourceRunId))
            .Select(application => application.SourceRunId)
            .Distinct()
            .ToListAsync(cancellationToken));

        AddRunKeys(referencedRunKeys, await _db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.SourceRunId != null && runKeyArray.Contains(appearance.SourceRunId))
            .Select(appearance => appearance.SourceRunId)
            .Distinct()
            .ToListAsync(cancellationToken));

        var removableRuns = candidateRuns
            .Where(run => !referencedRunKeys.Contains(run.RunKey))
            .ToArray();
        if (removableRuns.Length == 0)
        {
            return 0;
        }

        if (dryRun)
        {
            return removableRuns.Length;
        }

        _db.AiRuns.RemoveRange(removableRuns);
        await _db.SaveChangesAsync(cancellationToken);
        return removableRuns.Length;
    }

    private async Task<int> RemoveByIdsInBatchesAsync<TEntity>(DbSet<TEntity> set, IEnumerable<int> ids, CancellationToken cancellationToken)
        where TEntity : class
    {
        var removed = 0;

        foreach (var batchIds in ids.Distinct().Chunk(PurgeBatchSize))
        {
            var idBatch = batchIds.ToArray();
            var entities = await set
                .Where(entity => idBatch.Contains(EF.Property<int>(entity, "Id")))
                .ToListAsync(cancellationToken);

            if (entities.Count == 0)
            {
                continue;
            }

            set.RemoveRange(entities);
            removed += entities.Count;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return removed;
    }

    private async Task<HashSet<int>> ResolveFaceIdsAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var faceIds = new HashSet<int>();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey) && !selector.HasRunModelOrHostFilter)
        {
            var directIds = await _db.ReadSet<Face>()
                .AsNoTracking()
                .Where(face => face.PrimarySourceKey == selector.SourceKey)
                .Select(face => face.Id)
                .ToListAsync(cancellationToken);
            faceIds.UnionWith(directIds);
        }

        var detectionCandidates = await QueryDetectionCandidatesAsync(selector, runModels, cancellationToken, requireFaceReference: true);
        faceIds.UnionWith(detectionCandidates.Where(candidate => candidate.FaceId.HasValue).Select(candidate => candidate.FaceId!.Value));

        var segmentCandidates = await QuerySegmentCandidatesAsync(selector, runModels, cancellationToken, requireFaceReference: true);
        faceIds.UnionWith(segmentCandidates.Where(candidate => candidate.FaceId.HasValue).Select(candidate => candidate.FaceId!.Value));

        var appearanceCandidates = await QueryFaceAppearanceCandidatesAsync(selector, runModels, cancellationToken);
        faceIds.UnionWith(appearanceCandidates.Select(candidate => candidate.FaceId));

        if (!selector.HasHostFilter)
        {
            var embeddingCandidates = await QueryEmbeddingCandidatesAsync(selector, runModels, cancellationToken, EmbeddingHostType.Face);
            faceIds.UnionWith(embeddingCandidates.Select(candidate => candidate.HostId));
        }

        return faceIds;
    }

    private async Task<Dictionary<string, int>> PurgeFacesByIdsAsync(IReadOnlyCollection<int> faceIds, bool dryRun, CancellationToken cancellationToken)
    {
        if (faceIds.Count == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var faceIdSet = faceIds.ToHashSet();
        var facesQuery = _db.Faces.Where(face => faceIdSet.Contains(face.Id));
        var faceCount = await facesQuery.CountAsync(cancellationToken);
        if (faceCount == 0)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        var detectionsQuery = _db.Set<Detection>()
            .Where(detection => detection.RefId.HasValue && faceIdSet.Contains((int)detection.RefId.Value) && detection.RefKind != null && detection.RefKind.ToLower() == "face");
        var embeddingsQuery = _db.Embeddings
            .Where(embedding => embedding.HostType == EmbeddingHostType.Face && faceIdSet.Contains(embedding.HostId));
        var segmentsQuery = _db.Segments
            .Where(segment => segment.RefId.HasValue && faceIdSet.Contains((int)segment.RefId.Value) && segment.Kind != null && segment.Kind.ToLower() == "face");

        if (dryRun)
        {
            var removedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["face"] = faceCount,
            };
            AddRemovedCount(removedCounts, "detection", await detectionsQuery.CountAsync(cancellationToken));
            AddRemovedCount(removedCounts, "embedding", await embeddingsQuery.CountAsync(cancellationToken));
            AddRemovedCount(removedCounts, "segment", await segmentsQuery.CountAsync(cancellationToken));
            return removedCounts;
        }

        var faces = await facesQuery.ToListAsync(cancellationToken);

        var mergedFaces = await _db.Faces.Where(face => face.MergedIntoFaceId.HasValue && faceIdSet.Contains(face.MergedIntoFaceId.Value)).ToListAsync(cancellationToken);
        var detections = await detectionsQuery.ToListAsync(cancellationToken);
        var embeddings = await embeddingsQuery.ToListAsync(cancellationToken);
        var segments = await segmentsQuery.ToListAsync(cancellationToken);
        var coverBlobIds = faces
            .Select(face => Clean(face.CoverBlobId))
            .Where(static blobId => !string.IsNullOrWhiteSpace(blobId))
            .Cast<string>()
            .ToArray();

        foreach (var face in faces)
        {
            foreach (var participant in _faceLifecycleParticipants)
            {
                await participant.OnDeletingAsync(face, cancellationToken);
            }
        }

        foreach (var mergedFace in mergedFaces)
        {
            mergedFace.MergedIntoFaceId = null;
        }

        if (detections.Count > 0)
            _db.Set<Detection>().RemoveRange(detections);

        if (embeddings.Count > 0)
            _db.Embeddings.RemoveRange(embeddings);

        if (segments.Count > 0)
            _db.Segments.RemoveRange(segments);

        _db.Faces.RemoveRange(faces);
        await _db.SaveChangesAsync(cancellationToken);

        foreach (var coverBlobId in coverBlobIds)
        {
            try
            {
                await _blobService.DeleteBlobIfUnreferencedAsync(coverBlobId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete face cover blob {BlobId} while purging AI data.", coverBlobId);
            }
        }

        return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["face"] = faces.Count,
            ["detection"] = detections.Count,
            ["embedding"] = embeddings.Count,
            ["segment"] = segments.Count,
        };
    }

    private async Task RemoveOrphanedTagLinksAsync(IReadOnlyCollection<TagHostPair> affectedPairs, CancellationToken cancellationToken)
    {
        if (affectedPairs.Count == 0)
        {
            return;
        }

        var videoPairs = affectedPairs.Where(pair => pair.HostType == AffinityHostType.Video).ToArray();
        if (videoPairs.Length > 0)
        {
            var videoIds = videoPairs.Select(pair => pair.HostId).Distinct().ToArray();
            var videoTags = await _db.Set<VideoTag>()
                .Where(videoTag => videoIds.Contains(videoTag.VideoId))
                .ToListAsync(cancellationToken);
            var remaining = await _db.TagApplications
                .Where(application => application.HostType == AffinityHostType.Video && videoIds.Contains(application.HostId))
                .Select(application => new TagHostPair(application.HostType, application.HostId, application.TagId))
                .Distinct()
                .ToListAsync(cancellationToken);
            var remainingSet = remaining.ToHashSet();
            var orphaned = videoPairs.Where(pair => !remainingSet.Contains(pair)).ToHashSet();
            var toRemove = videoTags.Where(videoTag => orphaned.Contains(new TagHostPair(AffinityHostType.Video, videoTag.VideoId, videoTag.TagId))).ToArray();
            if (toRemove.Length > 0)
            {
                _db.Set<VideoTag>().RemoveRange(toRemove);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        var imagePairs = affectedPairs.Where(pair => pair.HostType == AffinityHostType.Image).ToArray();
        if (imagePairs.Length > 0)
        {
            var imageIds = imagePairs.Select(pair => pair.HostId).Distinct().ToArray();
            var imageTags = await _db.Set<ImageTag>()
                .Where(imageTag => imageIds.Contains(imageTag.ImageId))
                .ToListAsync(cancellationToken);
            var remaining = await _db.TagApplications
                .Where(application => application.HostType == AffinityHostType.Image && imageIds.Contains(application.HostId))
                .Select(application => new TagHostPair(application.HostType, application.HostId, application.TagId))
                .Distinct()
                .ToListAsync(cancellationToken);
            var remainingSet = remaining.ToHashSet();
            var orphaned = imagePairs.Where(pair => !remainingSet.Contains(pair)).ToHashSet();
            var toRemove = imageTags.Where(imageTag => orphaned.Contains(new TagHostPair(AffinityHostType.Image, imageTag.ImageId, imageTag.TagId))).ToArray();
            if (toRemove.Length > 0)
            {
                _db.Set<ImageTag>().RemoveRange(toRemove);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }
    }

    private async Task<List<EmbeddingCandidate>> QueryEmbeddingCandidatesAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken, EmbeddingHostType? forceHostType = null, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var query = _db.ReadSet<Embedding>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(embedding => embedding.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(embedding => embedding.SourceRunId == selector.SourceRunId);

        if (!string.IsNullOrWhiteSpace(selector.Modality) && TryParseEmbeddingModality(selector.Modality, out var modality))
            query = query.Where(embedding => embedding.Modality == modality);

        if (forceHostType.HasValue)
        {
            query = query.Where(embedding => embedding.HostType == forceHostType.Value);
        }
        else if (TryParseEmbeddingHostType(selector.HostType, out var hostType))
        {
            query = query.Where(embedding => embedding.HostType == hostType);
        }

        if (selector.HostId.HasValue)
            query = query.Where(embedding => embedding.HostId == selector.HostId.Value);

        if (excludedFaceIds is { Count: > 0 })
        {
            var excludedIds = excludedFaceIds.ToArray();
            query = query.Where(embedding => embedding.HostType != EmbeddingHostType.Face || !excludedIds.Contains(embedding.HostId));
        }

        var rows = await query
            .Select(embedding => new EmbeddingCandidate(
                embedding.Id,
                embedding.HostId,
                embedding.SourceRunId,
                ExtractModelKey(embedding.Meta)))
            .ToListAsync(cancellationToken);

        return rows
            .Where(candidate => MatchesOptional(ResolveArtifactModel(candidate.Model, candidate.SourceRunId, runModels), selector.Model))
            .ToList();
    }

    private async Task<List<DetectionCandidate>> QueryDetectionCandidatesAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken, bool requireFaceReference = false, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var query = _db.ReadSet<Detection>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(detection => detection.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(detection => detection.SourceRunId == selector.SourceRunId);

        if (TryParseDetectionHostType(selector.HostType, out var hostType))
            query = query.Where(detection => detection.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(detection => detection.HostId == selector.HostId.Value);

        if (excludedFaceIds is { Count: > 0 })
        {
            var excludedIds = excludedFaceIds.ToArray();
            query = query.Where(detection => !(detection.RefId.HasValue && detection.RefKind != null && detection.RefKind.ToLower() == "face" && excludedIds.Contains((int)detection.RefId.Value)));
        }

        if (requireFaceReference)
            query = query.Where(detection => detection.RefId.HasValue && detection.RefKind != null && detection.RefKind.ToLower() == "face");

        var rows = await query
            .Select(detection => new DetectionCandidate(
                detection.Id,
                detection.RefId.HasValue && detection.RefKind != null && detection.RefKind.ToLower() == "face" ? (int?)detection.RefId.Value : null,
                detection.SourceRunId,
                ExtractModelKey(detection.Extra)))
            .ToListAsync(cancellationToken);

        return rows
            .Where(candidate => MatchesOptional(ResolveArtifactModel(candidate.Model, candidate.SourceRunId, runModels), selector.Model))
            .ToList();
    }

    private async Task<List<SegmentCandidate>> QuerySegmentCandidatesAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken, bool requireFaceReference = false, IReadOnlyCollection<int>? excludedFaceIds = null)
    {
        var query = _db.ReadSet<Segment>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(segment => segment.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(segment => segment.SourceRunId == selector.SourceRunId);

        if (TryParseSegmentHostType(selector.HostType, out var hostType))
            query = query.Where(segment => segment.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(segment => segment.HostId == selector.HostId.Value);

        if (excludedFaceIds is { Count: > 0 })
        {
            var excludedIds = excludedFaceIds.ToArray();
            query = query.Where(segment => !(segment.RefId.HasValue && segment.Kind != null && segment.Kind.ToLower() == "face" && excludedIds.Contains((int)segment.RefId.Value)));
        }

        if (requireFaceReference)
            query = query.Where(segment => segment.RefId.HasValue && segment.Kind != null && segment.Kind.ToLower() == "face");

        var rows = await query
            .Select(segment => new SegmentCandidate(
                segment.Id,
                segment.RefId.HasValue && segment.Kind != null && segment.Kind.ToLower() == "face" ? (int?)segment.RefId.Value : null,
                segment.SourceRunId,
                ExtractModelKey(segment.Payload)))
            .ToListAsync(cancellationToken);

        return rows
            .Where(candidate => MatchesOptional(ResolveArtifactModel(candidate.Model, candidate.SourceRunId, runModels), selector.Model))
            .ToList();
    }

    private async Task<List<FaceAppearanceCandidate>> QueryFaceAppearanceCandidatesAsync(AiDataSelector selector, IReadOnlyDictionary<string, string?> runModels, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<FaceAppearance>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(appearance => appearance.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(appearance => appearance.SourceRunId == selector.SourceRunId);

        if (TryParseFaceAppearanceHostType(selector.HostType, out var hostType))
            query = query.Where(appearance => appearance.HostType == hostType);

        if (selector.HostId.HasValue)
            query = query.Where(appearance => appearance.HostId == selector.HostId.Value);

        var rows = await query
            .Select(appearance => new FaceAppearanceCandidate(appearance.FaceId, appearance.SourceRunId))
            .ToListAsync(cancellationToken);

        return rows
            .Where(candidate => MatchesOptional(ResolveArtifactModel(null, candidate.SourceRunId, runModels), selector.Model))
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, string?>> LoadAiRunModelLookupAsync(AiDataSelector selector, CancellationToken cancellationToken)
    {
        var query = _db.ReadSet<AiRun>().AsNoTracking();

        if (!string.IsNullOrWhiteSpace(selector.SourceKey))
            query = query.Where(run => run.SourceKey == selector.SourceKey);

        if (!string.IsNullOrWhiteSpace(selector.SourceRunId))
            query = query.Where(run => run.RunKey == selector.SourceRunId);

        var runs = await query
            .Select(run => new { run.RunKey, run.Models })
            .ToListAsync(cancellationToken);

        return runs
            .Select(run => new KeyValuePair<string, string?>(run.RunKey, ExtractAiRunModel(run.Models)))
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<bool> AiRunSourceExistsAsync(string sourceKey, CancellationToken cancellationToken)
        => await _db.ReadSet<AiRun>()
            .AsNoTracking()
            .AnyAsync(run => run.SourceKey == sourceKey, cancellationToken);

    private static AiDataSelector Normalize(AiDataSelectorDto selector)
    {
        var kinds = (selector.Kinds ?? [])
            .Select(kind => Clean(kind)?.ToLowerInvariant())
            .Where(static kind => !string.IsNullOrWhiteSpace(kind))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new AiDataSelector(
            Clean(selector.SourceKey),
            Clean(selector.SourceRunId),
            Clean(selector.Model),
            Clean(selector.Modality),
            Clean(selector.HostType)?.ToLowerInvariant(),
            selector.HostId,
            kinds);
    }

    private static string? ExtractModelKey(JsonDocument? document)
        => ExtractJsonString(document, "modelKey", "model", "configName", "ConfigName", "name", "Name");

    private static string? ExtractAiRunModel(JsonDocument? document)
    {
        if (document is null)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectAiRunModels(document.RootElement, names);
        return names.Count == 0 ? null : string.Join(", ", names.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
    }

    private static void CollectAiRunModels(JsonElement element, ISet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectAiRunModels(item, names);
                }
                break;
            case JsonValueKind.Object:
                var name = ExtractJsonString(element, "configName", "ConfigName", "config_name", "name", "Name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }

                foreach (var property in element.EnumerateObject())
                {
                    CollectAiRunModels(property.Value, names);
                }
                break;
        }
    }

    private static string? ExtractJsonString(JsonDocument? document, params string[] propertyNames)
        => document is null ? null : ExtractJsonString(document.RootElement, propertyNames);

    private static string? ExtractJsonString(JsonElement element, params string[] propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (propertyNames.Any(name => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) && property.Value.ValueKind == JsonValueKind.String)
            {
                return Clean(property.Value.GetString());
            }
        }

        return null;
    }

    private static string? ResolveArtifactModel(string? directModel, string? sourceRunId, IReadOnlyDictionary<string, string?> runModels)
    {
        var normalizedDirect = Clean(directModel);
        if (!string.IsNullOrWhiteSpace(normalizedDirect))
        {
            return normalizedDirect;
        }

        if (!string.IsNullOrWhiteSpace(sourceRunId) && runModels.TryGetValue(sourceRunId, out var runModel))
        {
            return Clean(runModel);
        }

        return null;
    }

    private static bool MatchesOptional(string? value, string? expected)
        => string.IsNullOrWhiteSpace(expected) || string.Equals(Clean(value), expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEnumName<TEnum>(TEnum value) where TEnum : struct, Enum
        => value.ToString().ToLowerInvariant();

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void AddRemovedCount(IDictionary<string, int> removedCounts, string kind, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (removedCounts.TryGetValue(kind, out var existing))
        {
            removedCounts[kind] = existing + count;
        }
        else
        {
            removedCounts[kind] = count;
        }
    }

    private static void MergeRemovedCounts(IDictionary<string, int> removedCounts, IReadOnlyDictionary<string, int> counts)
    {
        foreach (var (kind, count) in counts)
        {
            AddRemovedCount(removedCounts, kind, count);
        }
    }

    private static void AddRunKeys(ISet<string> runKeys, IEnumerable<string?> values)
    {
        foreach (var value in values)
        {
            var cleaned = Clean(value);
            if (!string.IsNullOrWhiteSpace(cleaned))
            {
                runKeys.Add(cleaned);
            }
        }
    }

    private static bool TryParseEmbeddingModality(string? value, out EmbeddingModality modality)
        => Enum.TryParse(value, true, out modality);

    private static bool TryParseEmbeddingHostType(string? value, out EmbeddingHostType hostType)
        => Enum.TryParse(value, true, out hostType);

    private static bool TryParseDetectionHostType(string? value, out DetectionHostType hostType)
        => Enum.TryParse(value, true, out hostType);

    private static bool TryParseSegmentHostType(string? value, out SegmentHostType hostType)
        => Enum.TryParse(value, true, out hostType);

    private static bool TryParseFaceAppearanceHostType(string? value, out FaceAppearanceHostType hostType)
        => Enum.TryParse(value, true, out hostType);

    private static bool TryParseAiRunTargetType(string? value, out AiRunTargetType targetType)
        => Enum.TryParse(value, true, out targetType);

    private static bool TryParseAffinityHostType(string? value, out AffinityHostType hostType)
        => Enum.TryParse(value, true, out hostType);

    private static bool ShouldSelectAiRunsForSource(string? sourceKey)
        => string.IsNullOrWhiteSpace(sourceKey)
           || SourceKeyConventions.IsExtensionSource(sourceKey);

    private sealed record AiDataSelector(
        string? SourceKey,
        string? SourceRunId,
        string? Model,
        string? Modality,
        string? HostType,
        int? HostId,
        HashSet<string> Kinds)
    {
        public bool IncludesKind(string kind) => Kinds.Count == 0 || Kinds.Contains(kind);

        public bool HasHostFilter => !string.IsNullOrWhiteSpace(HostType) || HostId.HasValue;

        public bool HasRunModelOrHostFilter => !string.IsNullOrWhiteSpace(SourceRunId) || !string.IsNullOrWhiteSpace(Model) || HasHostFilter;
    }

    private sealed record AiDataSummaryRecord(string Kind, string? Detail, string SourceKey, string? SourceRunId, string? Model, string HostType);

    private sealed record EmbeddingCandidate(int Id, int HostId, string? SourceRunId, string? Model);

    private sealed record DetectionCandidate(int Id, int? FaceId, string? SourceRunId, string? Model);

    private sealed record SegmentCandidate(int Id, int? FaceId, string? SourceRunId, string? Model);

    private sealed record VideoSegmentCandidate(int HostId, string? SourceRunId, string? Model);

    private sealed record FaceAppearanceCandidate(int FaceId, string? SourceRunId);

    private sealed record TagApplicationCandidate(int Id, AffinityHostType HostType, int HostId, int TagId, string? SourceRunId);

    private readonly record struct TagHostPair(AffinityHostType HostType, int HostId, int TagId);
}
