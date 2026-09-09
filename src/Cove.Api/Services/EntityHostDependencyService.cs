using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Stages removal of polymorphic rows that cannot have a database foreign key to their host.
/// Callers commit these changes together with removal of the host entity.
/// </summary>
public sealed class EntityHostDependencyService(CoveContext db)
{
    public async Task<EntityHostDependencyCleanup> StageDeleteAsync(
        AffinityHostType hostType,
        int hostId,
        CancellationToken ct)
    {
        var (groupHostType, groupKinds) = GroupHost(hostType);
        // Keep each branch aligned with its composite index. Combining HostType and Kind with OR can
        // make PostgreSQL scan the whole group_items table once for every entity in a large job.
        var groupItems = await db.GroupItems
            .IgnoreQueryFilters()
            .Where(item => item.HostType == groupHostType && item.HostId == hostId)
            .ToListAsync(ct);
        var legacyGroupItems = await db.GroupItems
            .IgnoreQueryFilters()
            .Where(item => groupKinds.Contains(item.Kind)
                && item.HostId == hostId
                && item.HostType != groupHostType)
            .ToListAsync(ct);
        var tagApplications = await db.TagApplications
            .IgnoreQueryFilters()
            .Where(item => item.HostType == hostType && item.HostId == hostId)
            .ToListAsync(ct);
        var contextType = hostType switch
        {
            AffinityHostType.Performer => "performer",
            AffinityHostType.Face => "face",
            _ => null,
        };
        var contextualTagApplications = contextType is not null
            ? await db.TagApplications
                .IgnoreQueryFilters()
                .Where(item => item.ContextType == contextType && item.ContextId == hostId)
                .ToListAsync(ct)
            : [];
        var fieldProvenance = await db.FieldProvenance
            .IgnoreQueryFilters()
            .Where(item => item.HostType == hostType && item.HostId == hostId)
            .ToListAsync(ct);

        var referencedSegments = hostType is AffinityHostType.Performer or AffinityHostType.Face
            ? await db.Segments
                .IgnoreQueryFilters()
                .Where(item => item.Kind != null
                    && item.Kind.ToLower() == (hostType == AffinityHostType.Performer ? "performer" : "face")
                    && item.RefId == hostId)
                .ToListAsync(ct)
            : [];
        var referencedDetections = hostType == AffinityHostType.Performer
            ? await db.Detections
                .IgnoreQueryFilters()
                .Where(item => item.RefKind != null && item.RefKind.ToLower() == "performer" && item.RefId == hostId)
                .ToListAsync(ct)
            : [];

        var segments = (await LoadSegmentsAsync(hostType, hostId, ct))
            .Concat(referencedSegments)
            .DistinctBy(item => item.Id)
            .ToArray();
        var segmentIds = segments.Select(segment => segment.Id).ToArray();
        var segmentGroupItems = segmentIds.Length == 0
            ? []
            : await db.GroupItems
                .IgnoreQueryFilters()
                .Where(item => item.HostType == "segment" && segmentIds.Contains(item.HostId))
                .ToListAsync(ct);
        var segmentTagApplications = segmentIds.Length == 0
            ? []
            : await db.TagApplications
                .IgnoreQueryFilters()
                .Where(item => item.HostType == AffinityHostType.Segment && segmentIds.Contains(item.HostId))
                .ToListAsync(ct);
        var segmentFieldProvenance = segmentIds.Length == 0
            ? []
            : await db.FieldProvenance
                .IgnoreQueryFilters()
                .Where(item => item.HostType == AffinityHostType.Segment && segmentIds.Contains(item.HostId))
                .ToListAsync(ct);
        var embeddings = await LoadEmbeddingsAsync(hostType, hostId, segmentIds, ct);
        var detections = await LoadDetectionsAsync(hostType, hostId, ct);
        var appearances = await LoadFaceAppearancesAsync(hostType, hostId, ct);
        var aiRuns = await LoadAiRunsAsync(hostType, hostId, ct);
        var affectedSegmentVideoIds = segments
            .Where(segment => segment.HostType == SegmentHostType.Video)
            .Select(segment => segment.HostId)
            .Distinct()
            .ToHashSet();
        if (hostType == AffinityHostType.Tag)
        {
            affectedSegmentVideoIds.UnionWith(await db.Segments
                .IgnoreQueryFilters()
                .Where(segment => segment.HostType == SegmentHostType.Video && segment.TagId == hostId)
                .Select(segment => segment.HostId)
                .Distinct()
                .ToListAsync(ct));
        }

        RemoveRange(groupItems.Concat(legacyGroupItems).Concat(segmentGroupItems).DistinctBy(item => item.Id));
        RemoveRange(tagApplications.Concat(contextualTagApplications).Concat(segmentTagApplications).DistinctBy(item => item.Id));
        RemoveRange(fieldProvenance.Concat(segmentFieldProvenance).DistinctBy(item => item.Id));
        RemoveRange(embeddings);
        RemoveRange(detections.Concat(referencedDetections).DistinctBy(item => item.Id));
        RemoveRange(appearances);
        RemoveRange(aiRuns);
        RemoveRange(segments);

        return new EntityHostDependencyCleanup(
            segments.Select(segment => segment.ImageBlobId)
                .Where(blobId => !string.IsNullOrWhiteSpace(blobId))
                .Cast<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            affectedSegmentVideoIds.ToArray());
    }

    private void RemoveRange<TEntity>(IEnumerable<TEntity> entities)
        where TEntity : class
    {
        var items = entities as TEntity[] ?? entities.ToArray();
        if (items.Length > 0)
            db.Set<TEntity>().RemoveRange(items);
    }

    private Task<List<Segment>> LoadSegmentsAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => db.Segments.IgnoreQueryFilters().Where(item => item.HostType == SegmentHostType.Video && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Image => db.Segments.IgnoreQueryFilters().Where(item => item.HostType == SegmentHostType.Image && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Audio => db.Segments.IgnoreQueryFilters().Where(item => item.HostType == SegmentHostType.Audio && item.HostId == hostId).ToListAsync(ct),
            _ => Task.FromResult(new List<Segment>()),
        };

    private async Task<List<Embedding>> LoadEmbeddingsAsync(
        AffinityHostType hostType,
        int hostId,
        int[] segmentIds,
        CancellationToken ct)
    {
        var direct = hostType switch
        {
            AffinityHostType.Video => await db.Embeddings.IgnoreQueryFilters().Where(item => item.HostType == EmbeddingHostType.Video && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Image => await db.Embeddings.IgnoreQueryFilters().Where(item => item.HostType == EmbeddingHostType.Image && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Performer => await db.Embeddings.IgnoreQueryFilters().Where(item => item.HostType == EmbeddingHostType.Performer && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Face => await db.Embeddings.IgnoreQueryFilters().Where(item => item.HostType == EmbeddingHostType.Face && item.HostId == hostId).ToListAsync(ct),
            _ => [],
        };
        if (segmentIds.Length == 0)
            return direct;

        var segmentEmbeddings = await db.Embeddings
            .IgnoreQueryFilters()
            .Where(item => item.HostType == EmbeddingHostType.Segment && segmentIds.Contains(item.HostId))
            .ToListAsync(ct);
        return direct.Concat(segmentEmbeddings).DistinctBy(item => item.Id).ToList();
    }

    private Task<List<Detection>> LoadDetectionsAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => db.Detections.IgnoreQueryFilters().Where(item => item.HostType == DetectionHostType.Video && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Image => db.Detections.IgnoreQueryFilters().Where(item => item.HostType == DetectionHostType.Image && item.HostId == hostId).ToListAsync(ct),
            _ => Task.FromResult(new List<Detection>()),
        };

    private Task<List<FaceAppearance>> LoadFaceAppearancesAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => db.FaceAppearances.IgnoreQueryFilters().Where(item => item.HostType == FaceAppearanceHostType.Video && item.HostId == hostId).ToListAsync(ct),
            AffinityHostType.Image => db.FaceAppearances.IgnoreQueryFilters().Where(item => item.HostType == FaceAppearanceHostType.Image && item.HostId == hostId).ToListAsync(ct),
            _ => Task.FromResult(new List<FaceAppearance>()),
        };

    private Task<List<AiRun>> LoadAiRunsAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => db.AiRuns.IgnoreQueryFilters().Where(item => item.TargetType == AiRunTargetType.Video && item.TargetId == hostId).ToListAsync(ct),
            AffinityHostType.Image => db.AiRuns.IgnoreQueryFilters().Where(item => item.TargetType == AiRunTargetType.Image && item.TargetId == hostId).ToListAsync(ct),
            AffinityHostType.Performer => db.AiRuns.IgnoreQueryFilters().Where(item => item.TargetType == AiRunTargetType.Performer && item.TargetId == hostId).ToListAsync(ct),
            AffinityHostType.Face => db.AiRuns.IgnoreQueryFilters().Where(item => item.TargetType == AiRunTargetType.Face && item.TargetId == hostId).ToListAsync(ct),
            _ => Task.FromResult(new List<AiRun>()),
        };

    private static (string HostType, GroupItemKind[] Kinds) GroupHost(AffinityHostType hostType)
        => hostType switch
        {
            AffinityHostType.Video => ("video", [GroupItemKind.Video, GroupItemKind.VideoRange]),
            AffinityHostType.Image => ("image", [GroupItemKind.Image]),
            AffinityHostType.Audio => ("audio", [GroupItemKind.Audio]),
            AffinityHostType.Text => ("text", [GroupItemKind.Text]),
            AffinityHostType.Group => ("group", [GroupItemKind.Group]),
            AffinityHostType.Performer => ("performer", [GroupItemKind.Performer]),
            AffinityHostType.Studio => ("studio", [GroupItemKind.Studio]),
            AffinityHostType.Tag => ("tag", [GroupItemKind.Tag]),
            AffinityHostType.Gallery => ("gallery", [GroupItemKind.Gallery]),
            AffinityHostType.Face => ("face", [GroupItemKind.Face]),
            AffinityHostType.Segment => ("segment", [GroupItemKind.Segment]),
            _ => throw new ArgumentOutOfRangeException(nameof(hostType), hostType, null),
        };
}

public sealed record EntityHostDependencyCleanup(
    IReadOnlyList<string> BlobIds,
    IReadOnlyList<int> SegmentVideoIds);
