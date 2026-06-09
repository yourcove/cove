using System.Globalization;
using System.Text.Json;

using Cove.Core.Entities;
using Cove.Core.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

public sealed class FacePerformerPropagationService(CoveContext db, IFieldProvenanceService? fieldProvenanceService = null) : IFacePerformerPropagationService
{
    private const string ExtensionDataOwner = "cove.face-performer-propagation";
    private const string SourceKey = "face-performer-propagation";
    private const string AssignmentKeyPrefix = "performer-assignment:";

    private readonly CoveContext _db = db;
    private readonly IFieldProvenanceService? _fieldProvenanceService = fieldProvenanceService;

    public async Task ApplyLinkChangeAsync(int faceId, int? oldPerformerId, int? newPerformerId, CancellationToken cancellationToken = default)
    {
        if (oldPerformerId == newPerformerId)
        {
            return;
        }

        if (oldPerformerId.HasValue)
        {
            await RemoveAssignmentsAsync(faceId, oldPerformerId.Value, cancellationToken);
        }

        if (newPerformerId.HasValue)
        {
            await AddAssignmentsAsync(faceId, newPerformerId.Value, cancellationToken);
        }
    }

    public async Task ReconcileHostAsync(FaceAppearanceHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        var kind = hostType == FaceAppearanceHostType.Video ? FaceHostKind.Video : FaceHostKind.Image;

        // Linked faces currently appearing on this host, with appearance metadata for provenance.
        var appearanceRows = await _db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.HostType == hostType && appearance.HostId == hostId)
            .Join(
                _db.Faces.AsNoTracking().Where(face => face.PerformerId != null),
                appearance => appearance.FaceId,
                face => face.Id,
                (appearance, face) => new
                {
                    FaceId = face.Id,
                    // Guaranteed non-null by the Where(face.PerformerId != null) above; COALESCE keeps the
                    // projection translatable.
                    PerformerId = face.PerformerId ?? 0,
                    appearance.SourceKey,
                    appearance.SourceRunId,
                    appearance.FirstSeenAtSec,
                    appearance.LastSeenAtSec,
                    appearance.TopConfidence,
                })
            .ToListAsync(cancellationToken);

        // Best appearance row per face (one assignment per face), and the set of desired (face, performer) pairs.
        var desiredByFace = appearanceRows
            .GroupBy(row => row.FaceId)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(row => row.TopConfidence ?? -1f).First());
        var desiredPairs = desiredByFace.Values
            .Select(row => (row.FaceId, row.PerformerId))
            .ToHashSet();

        // Ensure each desired performer is present on the host (added once per performer), and decide
        // whether face propagation owns it — never adopt a performer placed by other means (manual, scraper).
        var faceOwnedPerformers = new HashSet<int>();
        foreach (var performerId in desiredByFace.Values.Select(row => row.PerformerId).Distinct())
        {
            var hostRef = new FaceHostRef(kind, hostId);
            var alreadyOwned = await HasOwnedHostAssignmentAsync(performerId, hostRef, cancellationToken);
            var added = kind == FaceHostKind.Video
                ? await AddVideoPerformerAsync(hostId, performerId, cancellationToken)
                : await AddImagePerformerAsync(hostId, performerId, cancellationToken);
            if (added || alreadyOwned)
                faceOwnedPerformers.Add(performerId);
        }

        foreach (var row in desiredByFace.Values)
        {
            if (!faceOwnedPerformers.Contains(row.PerformerId))
                continue;

            var host = new FaceHostRef(kind, hostId, row.FirstSeenAtSec, row.LastSeenAtSec, row.SourceKey, row.SourceRunId, row.TopConfidence);
            await UpsertAssignmentAsync(row.FaceId, row.PerformerId, host, cancellationToken);
        }

        // Drop assignments for faces that no longer appear on the host (or were unlinked/relinked),
        // removing the host performer only when no remaining linked face keeps it here.
        var assignmentRows = await _db.ExtensionData
            .Where(item => item.Key.StartsWith(AssignmentKeyPrefix))
            .ToListAsync(cancellationToken);
        var hostAssignments = assignmentRows
            .Select(row => (Row: row, Assignment: TryParseAssignment(row.Key)))
            .Where(item => item.Assignment is not null && item.Assignment.Value.Kind == kind && item.Assignment.Value.HostId == hostId)
            .Select(item => (item.Row, Assignment: item.Assignment!.Value))
            .ToArray();

        foreach (var (row, assignment) in hostAssignments)
        {
            if (desiredPairs.Contains((assignment.FaceId, assignment.PerformerId)))
                continue;

            var performerStillKeptHere = desiredPairs.Any(pair => pair.PerformerId == assignment.PerformerId);
            if (!performerStillKeptHere)
                await RemoveHostPerformerAsync(assignment.Kind, assignment.HostId, assignment.PerformerId, cancellationToken);

            _db.ExtensionData.Remove(row);
        }

        // Re-record the host's performer provenance from the resulting face-propagation assignment set.
        var representativeSourceKey = desiredByFace.Values
            .Select(row => row.SourceKey)
            .FirstOrDefault(sourceKey => !string.IsNullOrWhiteSpace(sourceKey));
        await RecordHostPerformerProvenanceAsync(
            null,
            new FaceHostRef(kind, hostId),
            ResolveSourceKey(new FaceHostRef(kind, hostId, SourceKey: representativeSourceKey), null),
            cancellationToken);
    }

    public async Task<IReadOnlyList<FaceHostRef>> LoadFaceHostsAsync(int faceId, CancellationToken cancellationToken = default)
    {
        var appearances = await _db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => appearance.FaceId == faceId)
            .Select(appearance => new FaceHostRef(
                appearance.HostType == FaceAppearanceHostType.Video ? FaceHostKind.Video : FaceHostKind.Image,
                appearance.HostId,
                appearance.FirstSeenAtSec,
                appearance.LastSeenAtSec,
                appearance.SourceKey,
                appearance.SourceRunId,
                appearance.TopConfidence))
            .ToListAsync(cancellationToken);

        if (appearances.Count > 0)
        {
            return CollapseHosts(appearances);
        }

        var detections = await _db.Detections
            .AsNoTracking()
            .Where(detection =>
                detection.RefId == faceId
                && detection.RefKind != null
                && detection.RefKind.ToLower() == "face")
            .Select(detection => new FaceHostRef(
                detection.HostType == DetectionHostType.Video ? FaceHostKind.Video : FaceHostKind.Image,
                detection.HostId,
                detection.ObservedAtSec,
                detection.ObservedAtSec,
                detection.SourceKey,
                detection.SourceRunId,
                detection.Score))
            .ToListAsync(cancellationToken);

        return CollapseHosts(detections);
    }

    private async Task AddAssignmentsAsync(int faceId, int performerId, CancellationToken cancellationToken)
    {
        var hosts = await LoadFaceHostsAsync(faceId, cancellationToken);
        var faceSourceKey = await _db.Faces
            .AsNoTracking()
            .Where(face => face.Id == faceId)
            .Select(face => face.PrimarySourceKey)
            .FirstOrDefaultAsync(cancellationToken);

        foreach (var host in hosts)
        {
            var added = host.Kind switch
            {
                FaceHostKind.Video => await AddVideoPerformerAsync(host.HostId, performerId, cancellationToken),
                FaceHostKind.Image => await AddImagePerformerAsync(host.HostId, performerId, cancellationToken),
                _ => false,
            };

            if (added || await HasOwnedHostAssignmentAsync(performerId, host, cancellationToken))
            {
                await UpsertAssignmentAsync(faceId, performerId, host, cancellationToken);
                await RecordHostPerformerProvenanceAsync(performerId, host, ResolveSourceKey(host, faceSourceKey), cancellationToken);
            }
        }
    }

    private async Task RemoveAssignmentsAsync(int faceId, int performerId, CancellationToken cancellationToken)
    {
        var assignments = await _db.ExtensionData
            .Where(item => item.Key.StartsWith(AssignmentKeyPrefix))
            .ToListAsync(cancellationToken);

        var parsedAssignments = assignments
            .Select(item => (Row: item, Assignment: TryParseAssignment(item.Key)))
            .Where(item => item.Assignment is not null)
            .Select(item => (item.Row, Assignment: item.Assignment!.Value))
            .ToArray();

        var ownedAssignments = parsedAssignments
            .Where(item => item.Assignment.FaceId == faceId && item.Assignment.PerformerId == performerId)
            .ToArray();

        foreach (var owned in ownedAssignments)
        {
            var hasOtherAssignment = parsedAssignments.Any(item =>
                item.Row != owned.Row
                && item.Assignment.FaceId != faceId
                && item.Assignment.PerformerId == performerId
                && item.Assignment.Kind == owned.Assignment.Kind
                && item.Assignment.HostId == owned.Assignment.HostId);

            if (!hasOtherAssignment)
            {
                await RemoveHostPerformerAsync(owned.Assignment.Kind, owned.Assignment.HostId, performerId, cancellationToken);
            }

            _db.ExtensionData.Remove(owned.Row);
            await RecordHostPerformerProvenanceAsync(null, new FaceHostRef(owned.Assignment.Kind, owned.Assignment.HostId), SourceKey, cancellationToken);
        }
    }

    private async Task RecordHostPerformerProvenanceAsync(int? currentPerformerId, FaceHostRef host, string sourceKey, CancellationToken cancellationToken)
    {
        if (_fieldProvenanceService == null)
            return;

        var performerIds = new HashSet<int>();
        var assignmentKeys = await _db.ExtensionData
            .AsNoTracking()
            .Where(item => item.Key.StartsWith(AssignmentKeyPrefix))
            .Select(item => item.Key)
            .ToListAsync(cancellationToken);

        assignmentKeys.AddRange(_db.ExtensionData.Local
            .Where(item => item.Key.StartsWith(AssignmentKeyPrefix)
                && _db.Entry(item).State != EntityState.Deleted)
            .Select(item => item.Key));

        var deletedAssignmentKeys = _db.ExtensionData.Local
            .Where(item => item.Key.StartsWith(AssignmentKeyPrefix)
                && _db.Entry(item).State == EntityState.Deleted)
            .Select(item => item.Key)
            .ToHashSet(StringComparer.Ordinal);
        assignmentKeys.RemoveAll(deletedAssignmentKeys.Contains);

        foreach (var assignment in assignmentKeys.Select(TryParseAssignment).OfType<FaceAssignment>())
        {
            if (assignment.Kind == host.Kind && assignment.HostId == host.HostId)
                performerIds.Add(assignment.PerformerId);
        }

        if (currentPerformerId.HasValue)
            performerIds.Add(currentPerformerId.Value);

        List<string> performerNames = performerIds.Count == 0
            ? []
            : await _db.Performers
                .AsNoTracking()
                .Where(performer => performerIds.Contains(performer.Id))
                .OrderBy(performer => performer.Name)
                .Select(performer => string.IsNullOrWhiteSpace(performer.Name) ? performer.Id.ToString() : performer.Name.Trim())
                .ToListAsync(cancellationToken);

        await _fieldProvenanceService.RecordAsync(
            ToAffinityHostType(host.Kind),
            host.HostId,
            "performers",
            performerNames,
            sourceKey,
            sourceRunId: host.SourceRunId,
            confidence: host.Confidence,
            cancellationToken: cancellationToken);
    }

    private async Task<bool> AddVideoPerformerAsync(int videoId, int performerId, CancellationToken cancellationToken)
    {
        var exists = await _db.Set<VideoPerformer>()
            .AnyAsync(item => item.VideoId == videoId && item.PerformerId == performerId, cancellationToken);
        if (exists)
        {
            return false;
        }

        _db.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = videoId, PerformerId = performerId });
        return true;
    }

    private async Task<bool> AddImagePerformerAsync(int imageId, int performerId, CancellationToken cancellationToken)
    {
        var exists = await _db.Set<ImagePerformer>()
            .AnyAsync(item => item.ImageId == imageId && item.PerformerId == performerId, cancellationToken);
        if (exists)
        {
            return false;
        }

        _db.Set<ImagePerformer>().Add(new ImagePerformer { ImageId = imageId, PerformerId = performerId });
        return true;
    }

    private async Task RemoveHostPerformerAsync(FaceHostKind kind, int hostId, int performerId, CancellationToken cancellationToken)
    {
        if (kind == FaceHostKind.Video)
        {
            var link = await _db.Set<VideoPerformer>()
                .FirstOrDefaultAsync(item => item.VideoId == hostId && item.PerformerId == performerId, cancellationToken);
            if (link is not null)
            {
                _db.Set<VideoPerformer>().Remove(link);
            }
            return;
        }

        var imageLink = await _db.Set<ImagePerformer>()
            .FirstOrDefaultAsync(item => item.ImageId == hostId && item.PerformerId == performerId, cancellationToken);
        if (imageLink is not null)
        {
            _db.Set<ImagePerformer>().Remove(imageLink);
        }
    }

    private async Task UpsertAssignmentAsync(int faceId, int performerId, FaceHostRef host, CancellationToken cancellationToken)
    {
        var key = BuildAssignmentKey(faceId, performerId, host.Kind, host.HostId);
        var value = JsonSerializer.Serialize(new
        {
            faceId,
            performerId,
            hostType = FormatHostKind(host.Kind),
            hostId = host.HostId,
            assignedAt = DateTime.UtcNow,
        });

        var existing = await _db.ExtensionData
            .FirstOrDefaultAsync(item => item.Key == key, cancellationToken);
        if (existing is null)
        {
            _db.ExtensionData.Add(new ExtensionData
            {
                ExtensionId = ExtensionDataOwner,
                Key = key,
                Value = value,
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<bool> HasOwnedHostAssignmentAsync(int performerId, FaceHostRef host, CancellationToken cancellationToken)
    {
        var hostKind = FormatHostKind(host.Kind);
        var suffix = string.Create(CultureInfo.InvariantCulture, $":{performerId}:{hostKind}:{host.HostId}");
        return await _db.ExtensionData.AnyAsync(
            item => item.Key.StartsWith(AssignmentKeyPrefix)
                    && item.Key.EndsWith(suffix),
            cancellationToken);
    }

    private static string BuildAssignmentKey(int faceId, int performerId, FaceHostKind kind, int hostId)
        => string.Create(CultureInfo.InvariantCulture, $"{AssignmentKeyPrefix}{faceId}:{performerId}:{FormatHostKind(kind)}:{hostId}");

    private static FaceAssignment? TryParseAssignment(string key)
    {
        if (!key.StartsWith(AssignmentKeyPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = key[AssignmentKeyPrefix.Length..].Split(':');
        if (parts.Length != 4
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var faceId)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var performerId)
            || !TryParseHostKind(parts[2], out var kind)
            || !int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hostId))
        {
            return null;
        }

        return new FaceAssignment(faceId, performerId, kind, hostId);
    }

    private static string FormatHostKind(FaceHostKind kind) => kind == FaceHostKind.Video ? "video" : "image";

    private static IReadOnlyList<FaceHostRef> CollapseHosts(IEnumerable<FaceHostRef> hosts)
        => hosts
            .GroupBy(static item => (item.Kind, item.HostId))
            .Select(static group =>
            {
                var best = group.OrderByDescending(static item => item.Confidence ?? -1f).First();
                return best with
                {
                    FirstSeenAtSec = group.Min(static item => item.FirstSeenAtSec),
                    LastSeenAtSec = group.Max(static item => item.LastSeenAtSec),
                };
            })
            .ToArray();

    private static string ResolveSourceKey(FaceHostRef host, string? faceSourceKey)
    {
        var sourceKey = !string.IsNullOrWhiteSpace(host.SourceKey) ? host.SourceKey : faceSourceKey;
        if (string.IsNullOrWhiteSpace(sourceKey))
            return SourceKey;

        return sourceKey.Trim();
    }

    private static AffinityHostType ToAffinityHostType(FaceHostKind kind)
        => kind == FaceHostKind.Video ? AffinityHostType.Video : AffinityHostType.Image;

    private static bool TryParseHostKind(string value, out FaceHostKind kind)
    {
        if (string.Equals(value, "video", StringComparison.OrdinalIgnoreCase))
        {
            kind = FaceHostKind.Video;
            return true;
        }

        if (string.Equals(value, "image", StringComparison.OrdinalIgnoreCase))
        {
            kind = FaceHostKind.Image;
            return true;
        }

        kind = default;
        return false;
    }

    private readonly record struct FaceAssignment(int FaceId, int PerformerId, FaceHostKind Kind, int HostId);
}

public readonly record struct FaceHostRef(
    FaceHostKind Kind,
    int HostId,
    double? FirstSeenAtSec = null,
    double? LastSeenAtSec = null,
    string? SourceKey = null,
    string? SourceRunId = null,
    float? Confidence = null);

public enum FaceHostKind
{
    Video,
    Image,
}
