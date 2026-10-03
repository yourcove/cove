using System.Text.Json;

using Cove.Core.Entities;
using Cove.Core.Interfaces;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Cove.Data.Services;

/// <summary>
/// Applies a linked face's performer to the videos and images the face appears on, and records which of those
/// performer links propagation owns so they can be removed again. A face can appear on thousands of hosts, so every
/// operation works on all of its hosts at once in a fixed number of queries.
/// </summary>
public sealed class FacePerformerPropagationService(CoveContext db, IFieldProvenanceBatchRecorder? fieldProvenance = null) : IFacePerformerPropagationService
{
    private const string ExtensionDataOwner = FacePerformerAssignmentData.ExtensionId;
    private const string SourceKey = "face-performer-propagation";
    private const string AssignmentKeyPrefix = FacePerformerAssignmentData.KeyPrefix;
    private const string PerformersFieldKey = "performers";

    private readonly CoveContext _db = db;
    private readonly IFieldProvenanceBatchRecorder? _fieldProvenance = fieldProvenance;

    public async Task ApplyLinkChangeAsync(int faceId, int? oldPerformerId, int? newPerformerId, CancellationToken cancellationToken = default)
    {
        if (oldPerformerId == newPerformerId)
        {
            return;
        }

        using var changeDetection = PauseChangeDetection();
        var assignments = await LoadAssignmentsAsync(cancellationToken);
        var provenance = new List<PendingProvenance>();

        if (oldPerformerId.HasValue)
        {
            await RemoveAssignmentsAsync(faceId, oldPerformerId.Value, assignments, provenance, cancellationToken);
        }

        if (newPerformerId.HasValue)
        {
            await AddAssignmentsAsync(faceId, newPerformerId.Value, assignments, provenance, cancellationToken);
        }

        await RecordHostPerformerProvenanceAsync(provenance, cancellationToken);
    }

    public async Task ReconcileHostAsync(FaceAppearanceHostType hostType, int hostId, CancellationToken cancellationToken = default)
        => await ReconcileHostsCoreAsync([(hostType, hostId)], cancellationToken);

    /// <summary>
    /// Reconciles every given host as <see cref="ReconcileHostAsync"/> does for one, bypassing authorization filters.
    /// Face deletes and merges touch every host a face appears on.
    /// </summary>
    public async Task ReconcileHostsUnscopedAsync(
        IEnumerable<(FaceAppearanceHostType HostType, int HostId)> hosts,
        CancellationToken cancellationToken = default)
    {
        using var authorizationScope = _db.SuppressAuthorizationFilters();
        await ReconcileHostsCoreAsync(hosts, cancellationToken);
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

    /// <summary>
    /// How many hosts linking each face would touch: its distinct appearance hosts, or its detection hosts when it
    /// has no appearances, as <see cref="LoadFaceHostsAsync"/> resolves them.
    /// </summary>
    public async Task<Dictionary<int, int>> CountFaceHostsAsync(IReadOnlyCollection<int> faceIds, CancellationToken cancellationToken = default)
    {
        if (faceIds.Count == 0)
            return [];

        var ids = faceIds.ToArray();
        var counts = await _db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => ids.Contains(appearance.FaceId))
            .Select(appearance => new { appearance.FaceId, appearance.HostType, appearance.HostId })
            .Distinct()
            .GroupBy(host => host.FaceId)
            .Select(group => new { FaceId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(row => row.FaceId, row => row.Count, cancellationToken);

        var withoutAppearances = ids.Where(id => !counts.ContainsKey(id)).Select(id => (long)id).ToArray();
        if (withoutAppearances.Length > 0)
        {
            var detectionCounts = await _db.Detections
                .AsNoTracking()
                .Where(detection => detection.RefId != null
                    && withoutAppearances.Contains(detection.RefId.Value)
                    && detection.RefKind != null
                    && detection.RefKind.ToLower() == "face")
                .Select(detection => new { FaceId = detection.RefId!.Value, detection.HostType, detection.HostId })
                .Distinct()
                .GroupBy(host => host.FaceId)
                .Select(group => new { FaceId = group.Key, Count = group.Count() })
                .ToListAsync(cancellationToken);
            foreach (var row in detectionCounts)
                counts[(int)row.FaceId] = row.Count;
        }

        return counts;
    }

    private async Task ReconcileHostsCoreAsync(
        IEnumerable<(FaceAppearanceHostType HostType, int HostId)> hosts,
        CancellationToken cancellationToken)
    {
        var hostRefs = hosts
            .Select(host => (Kind: ToHostKind(host.HostType), host.HostId))
            .Distinct()
            .Select(host => new FaceHostRef(host.Kind, host.HostId))
            .ToArray();
        if (hostRefs.Length == 0)
            return;

        using var changeDetection = PauseChangeDetection();
        var videoIds = hostRefs.Where(host => host.Kind == FaceHostKind.Video).Select(host => host.HostId).ToArray();
        var imageIds = hostRefs.Where(host => host.Kind == FaceHostKind.Image).Select(host => host.HostId).ToArray();

        // Linked faces currently appearing on these hosts, with appearance metadata for provenance.
        var appearanceRows = await _db.FaceAppearances
            .AsNoTracking()
            .Where(appearance => (appearance.HostType == FaceAppearanceHostType.Video && videoIds.Contains(appearance.HostId))
                || (appearance.HostType == FaceAppearanceHostType.Image && imageIds.Contains(appearance.HostId)))
            .Join(
                _db.Faces.AsNoTracking().Where(face => face.PerformerId != null && face.MergedIntoFaceId == null),
                appearance => appearance.FaceId,
                face => face.Id,
                (appearance, face) => new
                {
                    appearance.HostType,
                    appearance.HostId,
                    FaceId = face.Id,
                    // Guaranteed non-null by the Where(face.PerformerId != null) above; COALESCE keeps the
                    // projection translatable.
                    PerformerId = face.PerformerId ?? 0,
                    appearance.SourceKey,
                    appearance.TopConfidence,
                })
            .ToListAsync(cancellationToken);

        // Per host, the best appearance row per face (one assignment per face).
        var desiredByHost = appearanceRows
            .GroupBy(row => (Kind: ToHostKind(row.HostType), row.HostId))
            .ToDictionary(
                group => group.Key,
                group => group.GroupBy(row => row.FaceId)
                    .Select(faceRows => faceRows.OrderByDescending(row => row.TopConfidence ?? -1f).First())
                    .ToArray());

        var assignments = await LoadAssignmentsAsync(cancellationToken);
        var desiredPerformerIds = appearanceRows.Select(row => row.PerformerId).ToHashSet();
        var presentLinks = await LoadPresentLinksAsync(hostRefs, desiredPerformerIds, cancellationToken);
        var pendingDeletes = PendingHostLinkDeletes(desiredPerformerIds);
        var rows = await LoadAssignmentRowsAsync(
            appearanceRows
                .Select(row => BuildAssignmentKey(row.FaceId, row.PerformerId, ToHostKind(row.HostType), row.HostId))
                .Concat(hostRefs.SelectMany(assignments.OnHost).Select(BuildAssignmentKey)),
            cancellationToken);

        var releasedLinks = new List<(int PerformerId, FaceHostRef Host)>();
        var provenance = new List<PendingProvenance>(hostRefs.Length);
        foreach (var host in hostRefs)
        {
            var desired = desiredByHost.GetValueOrDefault((host.Kind, host.HostId)) ?? [];

            // Ensure each desired performer is on the host and assign the faces whose performer propagation owns.
            var ownedPerformers = desired
                .Select(row => row.PerformerId)
                .Distinct()
                .Where(performerId => TryClaimHostPerformer(host, performerId, presentLinks, pendingDeletes, assignments))
                .ToHashSet();
            foreach (var row in desired.Where(row => ownedPerformers.Contains(row.PerformerId)))
            {
                var assignment = new FaceAssignment(row.FaceId, row.PerformerId, host.Kind, host.HostId);
                UpsertAssignment(assignment, rows);
                assignments.Add(assignment);
            }

            // Drop assignments for faces that no longer appear on the host (or were unlinked or relinked), releasing
            // the host performer only when no remaining linked face keeps it there.
            var desiredPairs = desired.Select(row => (row.FaceId, row.PerformerId)).ToHashSet();
            var keptPerformers = desired.Select(row => row.PerformerId).ToHashSet();
            foreach (var stale in assignments.OnHost(host).Where(assignment => !desiredPairs.Contains((assignment.FaceId, assignment.PerformerId))))
            {
                if (!keptPerformers.Contains(stale.PerformerId))
                    releasedLinks.Add((stale.PerformerId, host));
                RemoveAssignmentRow(stale, rows);
                assignments.Remove(stale);
            }

            var representativeSourceKey = desired
                .Select(row => row.SourceKey)
                .FirstOrDefault(sourceKey => !string.IsNullOrWhiteSpace(sourceKey));
            provenance.Add(new PendingProvenance(host, ResolveSourceKey(representativeSourceKey, null), assignments.PerformersOnHost(host)));
        }

        foreach (var performerHosts in releasedLinks.Distinct().GroupBy(link => link.PerformerId))
            await RemoveHostPerformersAsync(performerHosts.Key, [.. performerHosts.Select(link => link.Host)], cancellationToken);

        await RecordHostPerformerProvenanceAsync(provenance, cancellationToken);
    }

    private async Task AddAssignmentsAsync(
        int faceId,
        int performerId,
        AssignmentSet assignments,
        List<PendingProvenance> provenance,
        CancellationToken cancellationToken)
    {
        var hosts = await LoadFaceHostsAsync(faceId, cancellationToken);
        if (hosts.Count == 0)
        {
            return;
        }

        var faceSourceKey = await _db.Faces
            .AsNoTracking()
            .Where(face => face.Id == faceId)
            .Select(face => face.PrimarySourceKey)
            .FirstOrDefaultAsync(cancellationToken);
        var performerIds = new HashSet<int> { performerId };
        var presentLinks = await LoadPresentLinksAsync(hosts, performerIds, cancellationToken);
        var pendingDeletes = PendingHostLinkDeletes(performerIds);
        var rows = await LoadAssignmentRowsAsync(
            hosts.Select(host => BuildAssignmentKey(faceId, performerId, host.Kind, host.HostId)),
            cancellationToken);

        foreach (var host in hosts)
        {
            if (!TryClaimHostPerformer(host, performerId, presentLinks, pendingDeletes, assignments))
                continue;

            var assignment = new FaceAssignment(faceId, performerId, host.Kind, host.HostId);
            UpsertAssignment(assignment, rows);
            assignments.Add(assignment);
            provenance.Add(new PendingProvenance(
                host,
                ResolveSourceKey(host.SourceKey, faceSourceKey),
                assignments.PerformersOnHost(host),
                host.SourceRunId,
                host.Confidence));
        }
    }

    private async Task RemoveAssignmentsAsync(
        int faceId,
        int performerId,
        AssignmentSet assignments,
        List<PendingProvenance> provenance,
        CancellationToken cancellationToken)
    {
        var owned = assignments.ForFace(faceId, performerId);
        if (owned.Count == 0)
        {
            return;
        }

        var rows = await LoadAssignmentRowsAsync(owned.Select(BuildAssignmentKey), cancellationToken);
        var releasedHosts = new List<FaceHostRef>();
        foreach (var assignment in owned)
        {
            var host = new FaceHostRef(assignment.Kind, assignment.HostId);
            // Keep the host performer wherever another face's propagation still holds it.
            if (!assignments.OnHost(host).Any(other => other.FaceId != faceId && other.PerformerId == performerId))
                releasedHosts.Add(host);
            RemoveAssignmentRow(assignment, rows);
            assignments.Remove(assignment);
            provenance.Add(new PendingProvenance(host, SourceKey, assignments.PerformersOnHost(host)));
        }

        await RemoveHostPerformersAsync(performerId, releasedHosts, cancellationToken);
    }

    /// <summary>
    /// Adds the performer to the host when it is missing, so propagation owns it there, and otherwise reports whether
    /// propagation already owns it through another face. A performer placed by other means (manual, scraper) is never
    /// adopted.
    /// </summary>
    private bool TryClaimHostPerformer(
        FaceHostRef host,
        int performerId,
        HashSet<(FaceHostKind Kind, int HostId, int PerformerId)> presentLinks,
        IReadOnlyDictionary<(FaceHostKind Kind, int HostId, int PerformerId), EntityEntry> pendingDeletes,
        AssignmentSet assignments)
    {
        if (!presentLinks.Add((host.Kind, host.HostId, performerId)))
            return assignments.HasPerformerOnHost(performerId, host);

        // Restoring a link deleted earlier in this unit of work keeps the saved row; adding a second instance with
        // the same key would conflict with the tracked one.
        if (pendingDeletes.TryGetValue((host.Kind, host.HostId, performerId), out var pendingDelete))
            pendingDelete.State = EntityState.Unchanged;
        else if (host.Kind == FaceHostKind.Video)
            _db.Set<VideoPerformer>().Add(new VideoPerformer { VideoId = host.HostId, PerformerId = performerId });
        else
            _db.Set<ImagePerformer>().Add(new ImagePerformer { ImageId = host.HostId, PerformerId = performerId });
        return true;
    }

    private async Task RecordHostPerformerProvenanceAsync(IReadOnlyList<PendingProvenance> pending, CancellationToken cancellationToken)
    {
        if (_fieldProvenance == null || pending.Count == 0)
            return;

        var performerIds = pending.SelectMany(item => item.PerformerIds).ToHashSet();
        var performersInNameOrder = performerIds.Count == 0
            ? []
            : await _db.Performers
                .AsNoTracking()
                .Where(performer => performerIds.Contains(performer.Id))
                .OrderBy(performer => performer.Name)
                .Select(performer => new
                {
                    performer.Id,
                    Name = string.IsNullOrWhiteSpace(performer.Name) ? performer.Id.ToString() : performer.Name.Trim(),
                })
                .ToListAsync(cancellationToken);

        var entries = pending
            .Select(item => new FieldProvenanceHostValue(
                ToAffinityHostType(item.Host.Kind),
                item.Host.HostId,
                performersInNameOrder
                    .Where(performer => item.PerformerIds.Contains(performer.Id))
                    .Select(performer => performer.Name)
                    .ToList(),
                item.SourceKey,
                item.SourceRunId,
                item.Confidence))
            .ToArray();

        await _fieldProvenance.RecordForHostsAsync(PerformersFieldKey, entries, cancellationToken);
    }

    /// <summary>
    /// Loads the face-propagation assignments as this unit of work sees them: persisted rows, plus rows added
    /// and minus rows removed but not yet saved.
    /// </summary>
    private async Task<AssignmentSet> LoadAssignmentsAsync(CancellationToken cancellationToken)
    {
        var keys = await _db.ExtensionData
            .AsNoTracking()
            .Where(item => item.ExtensionId == ExtensionDataOwner && item.Key.StartsWith(AssignmentKeyPrefix))
            .Select(item => item.Key)
            .ToListAsync(cancellationToken);
        var assignments = new AssignmentSet();
        foreach (var key in keys)
        {
            if (TryParseAssignment(key) is { } assignment)
                assignments.Add(assignment);
        }

        foreach (var entry in _db.ChangeTracker.Entries<ExtensionData>())
        {
            if (entry.Entity.ExtensionId != ExtensionDataOwner || TryParseAssignment(entry.Entity.Key) is not { } assignment)
                continue;

            if (entry.State == EntityState.Deleted)
                assignments.Remove(assignment);
            else if (entry.State != EntityState.Detached)
                assignments.Add(assignment);
        }

        return assignments;
    }

    /// <summary>Tracked assignment rows by key, including rows added or removed in this unit of work.</summary>
    private async Task<Dictionary<string, ExtensionData>> LoadAssignmentRowsAsync(IEnumerable<string> keys, CancellationToken cancellationToken)
    {
        var wanted = keys.ToHashSet(StringComparer.Ordinal);
        var rows = new Dictionary<string, ExtensionData>(StringComparer.Ordinal);
        if (wanted.Count == 0)
            return rows;

        foreach (var entry in _db.ChangeTracker.Entries<ExtensionData>())
        {
            if (entry.State != EntityState.Detached && entry.Entity.ExtensionId == ExtensionDataOwner && wanted.Contains(entry.Entity.Key))
                rows[entry.Entity.Key] = entry.Entity;
        }

        var missing = wanted.Where(key => !rows.ContainsKey(key)).ToArray();
        if (missing.Length > 0)
        {
            var persisted = await _db.ExtensionData
                .Where(item => item.ExtensionId == ExtensionDataOwner && missing.Contains(item.Key))
                .ToListAsync(cancellationToken);
            foreach (var row in persisted)
                rows[row.Key] = row;
        }

        return rows;
    }

    private void UpsertAssignment(FaceAssignment assignment, Dictionary<string, ExtensionData> rows)
    {
        var key = BuildAssignmentKey(assignment);
        var value = JsonSerializer.Serialize(new
        {
            faceId = assignment.FaceId,
            performerId = assignment.PerformerId,
            hostType = FormatHostKind(assignment.Kind),
            hostId = assignment.HostId,
            assignedAt = DateTime.UtcNow,
        });

        if (!rows.TryGetValue(key, out var existing))
        {
            existing = new ExtensionData
            {
                ExtensionId = ExtensionDataOwner,
                Key = key,
                Value = value,
            };
            _db.ExtensionData.Add(existing);
            rows[key] = existing;
            return;
        }

        existing.Value = value;
        existing.UpdatedAt = DateTime.UtcNow;
        var entry = _db.Entry(existing);
        if (entry.State == EntityState.Deleted)
            entry.State = EntityState.Modified;
    }

    private void RemoveAssignmentRow(FaceAssignment assignment, Dictionary<string, ExtensionData> rows)
    {
        if (rows.TryGetValue(BuildAssignmentKey(assignment), out var row) && _db.Entry(row).State != EntityState.Deleted)
            _db.ExtensionData.Remove(row);
    }

    /// <summary>
    /// The (host, performer) links among <paramref name="hosts"/> and <paramref name="performerIds"/> that exist,
    /// saved or pending: a pending add counts as present and a pending delete as absent.
    /// </summary>
    private async Task<HashSet<(FaceHostKind Kind, int HostId, int PerformerId)>> LoadPresentLinksAsync(
        IReadOnlyCollection<FaceHostRef> hosts,
        IReadOnlySet<int> performerIds,
        CancellationToken cancellationToken)
    {
        var present = new HashSet<(FaceHostKind Kind, int HostId, int PerformerId)>();
        if (performerIds.Count == 0)
            return present;

        var videoIds = hosts.Where(host => host.Kind == FaceHostKind.Video).Select(host => host.HostId).ToHashSet();
        var imageIds = hosts.Where(host => host.Kind == FaceHostKind.Image).Select(host => host.HostId).ToHashSet();
        var performers = performerIds.ToArray();
        if (videoIds.Count > 0)
        {
            var linked = await _db.Set<VideoPerformer>()
                .AsNoTracking()
                .Where(item => performers.Contains(item.PerformerId) && videoIds.Contains(item.VideoId))
                .Select(item => new { item.VideoId, item.PerformerId })
                .ToListAsync(cancellationToken);
            present.UnionWith(linked.Select(link => (FaceHostKind.Video, link.VideoId, link.PerformerId)));
        }

        if (imageIds.Count > 0)
        {
            var linked = await _db.Set<ImagePerformer>()
                .AsNoTracking()
                .Where(item => performers.Contains(item.PerformerId) && imageIds.Contains(item.ImageId))
                .Select(item => new { item.ImageId, item.PerformerId })
                .ToListAsync(cancellationToken);
            present.UnionWith(linked.Select(link => (FaceHostKind.Image, link.ImageId, link.PerformerId)));
        }

        // Apply pending deletes before pending adds: replacing a whole link collection can leave a Deleted link and
        // an Added replacement with the same key tracked together, and the link exists once the save completes.
        var pending = _db.ChangeTracker.Entries<VideoPerformer>()
            .Where(entry => videoIds.Contains(entry.Entity.VideoId) && performerIds.Contains(entry.Entity.PerformerId))
            .Select(entry => (entry.State, Link: (FaceHostKind.Video, entry.Entity.VideoId, entry.Entity.PerformerId)))
            .Concat(_db.ChangeTracker.Entries<ImagePerformer>()
                .Where(entry => imageIds.Contains(entry.Entity.ImageId) && performerIds.Contains(entry.Entity.PerformerId))
                .Select(entry => (entry.State, Link: (FaceHostKind.Image, entry.Entity.ImageId, entry.Entity.PerformerId))))
            .ToArray();
        present.ExceptWith(pending.Where(item => item.State == EntityState.Deleted).Select(item => item.Link));
        present.UnionWith(pending.Where(item => item.State == EntityState.Added).Select(item => item.Link));

        return present;
    }

    /// <summary>Host links of the given performers deleted earlier in this unit of work but not yet saved.</summary>
    private Dictionary<(FaceHostKind Kind, int HostId, int PerformerId), EntityEntry> PendingHostLinkDeletes(IReadOnlySet<int> performerIds)
    {
        var pending = new Dictionary<(FaceHostKind Kind, int HostId, int PerformerId), EntityEntry>();
        foreach (var entry in _db.ChangeTracker.Entries<VideoPerformer>())
        {
            if (entry.State == EntityState.Deleted && performerIds.Contains(entry.Entity.PerformerId))
                pending[(FaceHostKind.Video, entry.Entity.VideoId, entry.Entity.PerformerId)] = entry;
        }

        foreach (var entry in _db.ChangeTracker.Entries<ImagePerformer>())
        {
            if (entry.State == EntityState.Deleted && performerIds.Contains(entry.Entity.PerformerId))
                pending[(FaceHostKind.Image, entry.Entity.ImageId, entry.Entity.PerformerId)] = entry;
        }

        return pending;
    }

    private async Task RemoveHostPerformersAsync(int performerId, IReadOnlyCollection<FaceHostRef> hosts, CancellationToken cancellationToken)
    {
        var videoIds = hosts.Where(host => host.Kind == FaceHostKind.Video).Select(host => host.HostId).ToHashSet();
        var imageIds = hosts.Where(host => host.Kind == FaceHostKind.Image).Select(host => host.HostId).ToHashSet();

        // Links added earlier in this unit of work are not in the database yet; removing them detaches them.
        if (videoIds.Count > 0)
        {
            var links = await _db.Set<VideoPerformer>()
                .Where(item => item.PerformerId == performerId && videoIds.Contains(item.VideoId))
                .ToListAsync(cancellationToken);
            links.AddRange(_db.ChangeTracker.Entries<VideoPerformer>()
                .Where(entry => entry.State == EntityState.Added
                    && entry.Entity.PerformerId == performerId
                    && videoIds.Contains(entry.Entity.VideoId))
                .Select(entry => entry.Entity));
            _db.Set<VideoPerformer>().RemoveRange(links);
        }

        if (imageIds.Count > 0)
        {
            var links = await _db.Set<ImagePerformer>()
                .Where(item => item.PerformerId == performerId && imageIds.Contains(item.ImageId))
                .ToListAsync(cancellationToken);
            links.AddRange(_db.ChangeTracker.Entries<ImagePerformer>()
                .Where(entry => entry.State == EntityState.Added
                    && entry.Entity.PerformerId == performerId
                    && imageIds.Contains(entry.Entity.ImageId))
                .Select(entry => entry.Entity));
            _db.Set<ImagePerformer>().RemoveRange(links);
        }
    }

    /// <summary>
    /// Pauses automatic change detection while staging changes across many hosts, since each tracker lookup would
    /// otherwise rescan every entity added so far. SaveChanges still detects changes once the setting is restored.
    /// </summary>
    private ChangeDetectionPause PauseChangeDetection()
    {
        var previous = _db.ChangeTracker.AutoDetectChangesEnabled;
        _db.ChangeTracker.DetectChanges();
        _db.ChangeTracker.AutoDetectChangesEnabled = false;
        return new ChangeDetectionPause(_db, previous);
    }

    private static string BuildAssignmentKey(FaceAssignment assignment)
        => BuildAssignmentKey(assignment.FaceId, assignment.PerformerId, assignment.Kind, assignment.HostId);

    private static string BuildAssignmentKey(int faceId, int performerId, FaceHostKind kind, int hostId)
        => FacePerformerAssignmentData.BuildKey(new(
            faceId,
            performerId,
            FormatHostKind(kind),
            hostId));

    private static FaceAssignment? TryParseAssignment(string key)
    {
        if (!FacePerformerAssignmentData.TryParseKey(key, out var assignment)
            || !TryParseHostKind(assignment.HostType, out var kind))
            return null;

        return new FaceAssignment(assignment.FaceId, assignment.PerformerId, kind, assignment.HostId);
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

    private static string ResolveSourceKey(string? preferredSourceKey, string? fallbackSourceKey)
    {
        var sourceKey = !string.IsNullOrWhiteSpace(preferredSourceKey) ? preferredSourceKey : fallbackSourceKey;
        if (string.IsNullOrWhiteSpace(sourceKey))
            return SourceKey;

        return sourceKey.Trim();
    }

    private static FaceHostKind ToHostKind(FaceAppearanceHostType hostType)
        => hostType == FaceAppearanceHostType.Video ? FaceHostKind.Video : FaceHostKind.Image;

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

    private readonly struct ChangeDetectionPause(CoveContext db, bool previous) : IDisposable
    {
        public void Dispose() => db.ChangeTracker.AutoDetectChangesEnabled = previous;
    }

    private readonly record struct FaceAssignment(int FaceId, int PerformerId, FaceHostKind Kind, int HostId);

    private readonly record struct PendingProvenance(
        FaceHostRef Host,
        string SourceKey,
        IReadOnlySet<int> PerformerIds,
        string? SourceRunId = null,
        float? Confidence = null);

    /// <summary>In-memory view of propagation assignments, indexed by host so each lookup avoids a query.</summary>
    private sealed class AssignmentSet
    {
        private readonly Dictionary<(FaceHostKind Kind, int HostId), HashSet<FaceAssignment>> _byHost = [];

        public void Add(FaceAssignment assignment)
        {
            if (!_byHost.TryGetValue((assignment.Kind, assignment.HostId), out var onHost))
            {
                onHost = [];
                _byHost[(assignment.Kind, assignment.HostId)] = onHost;
            }

            onHost.Add(assignment);
        }

        public void Remove(FaceAssignment assignment)
        {
            if (_byHost.TryGetValue((assignment.Kind, assignment.HostId), out var onHost))
                onHost.Remove(assignment);
        }

        public FaceAssignment[] OnHost(FaceHostRef host)
            => _byHost.TryGetValue((host.Kind, host.HostId), out var onHost) ? [.. onHost] : [];

        public List<FaceAssignment> ForFace(int faceId, int performerId)
            => _byHost.Values
                .SelectMany(onHost => onHost)
                .Where(assignment => assignment.FaceId == faceId && assignment.PerformerId == performerId)
                .ToList();

        public bool HasPerformerOnHost(int performerId, FaceHostRef host)
            => _byHost.TryGetValue((host.Kind, host.HostId), out var onHost)
                && onHost.Any(assignment => assignment.PerformerId == performerId);

        public IReadOnlySet<int> PerformersOnHost(FaceHostRef host)
            => _byHost.TryGetValue((host.Kind, host.HostId), out var onHost)
                ? onHost.Select(assignment => assignment.PerformerId).ToHashSet()
                : new HashSet<int>();
    }
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
