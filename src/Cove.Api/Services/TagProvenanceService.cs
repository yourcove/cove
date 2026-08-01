using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Api.Services;

public sealed partial class TagProvenanceService(
    CoveContext db,
    IServiceScopeFactory? scopeFactory = null,
    ILogger<TagProvenanceService>? logger = null) : ITagProvenanceService
{
    private readonly CoveContext _db = db;
    private readonly IServiceScopeFactory? _scopeFactory = scopeFactory;
    private readonly ILogger<TagProvenanceService> _logger = logger ?? NullLogger<TagProvenanceService>.Instance;

    [LoggerMessage(
        EventId = 2801,
        Level = LogLevel.Trace,
        Message = "Staged {SourceKey} tag changes for {HostType} {HostId}; added={AddedCount}, removed={RemovedCount}")]
    private partial void TraceTagChangesStaged(
        string sourceKey,
        AffinityHostType hostType,
        int hostId,
        int addedCount,
        int removedCount);

    public Task RecordAsync(
        AffinityHostType hostType,
        int hostId,
        int tagId,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        string? contextType = null,
        int? contextId = null,
        double? totalDurationSec = null,
        double? hostDurationSec = null,
        CancellationToken cancellationToken = default)
    {
        if (tagId <= 0)
        {
            return Task.CompletedTask;
        }

        return EnsureApplicationAsync(hostType, hostId, tagId, null, sourceKey, sourceRunId, modelKey, confidence, contextType, contextId, totalDurationSec, hostDurationSec, cancellationToken);
    }

    public Task RecordAsync(
        AffinityHostType hostType,
        int hostId,
        Tag tag,
        string sourceKey,
        string? sourceRunId = null,
        string? modelKey = null,
        float? confidence = null,
        string? contextType = null,
        int? contextId = null,
        double? totalDurationSec = null,
        double? hostDurationSec = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tag);

        if (tag.Id > 0)
        {
            return EnsureApplicationAsync(hostType, hostId, tag.Id, null, sourceKey, sourceRunId, modelKey, confidence, contextType, contextId, totalDurationSec, hostDurationSec, cancellationToken);
        }

        return EnsureApplicationAsync(hostType, hostId, null, tag, sourceKey, sourceRunId, modelKey, confidence, contextType, contextId, totalDurationSec, hostDurationSec, cancellationToken);
    }

    public async Task SyncTagSetAsync(
        AffinityHostType hostType,
        int hostId,
        IReadOnlyCollection<int> previousTagIds,
        IReadOnlyCollection<int> currentTagIds,
        string sourceKey = "user",
        CancellationToken cancellationToken = default)
    {
        var previous = NormalizeTagIds(previousTagIds);
        var current = NormalizeTagIds(currentTagIds);
        var normalizedSourceKey = NormalizeSourceKey(sourceKey);

        var removedTagIds = previous.Except(current).ToArray();
        if (removedTagIds.Length > 0)
        {
            var removedApplications = await _db.TagApplications
                .Where(application => application.HostType == hostType
                    && application.HostId == hostId
                    && application.ContextType == null
                    && application.ContextId == null
                    && application.SourceKey == normalizedSourceKey
                    && removedTagIds.Contains(application.TagId))
                .ToListAsync(cancellationToken);

            if (removedApplications.Count > 0)
            {
                _db.TagApplications.RemoveRange(removedApplications);
            }
        }

        var addedCount = 0;
        foreach (var tagId in current.Except(previous))
        {
            await RecordAsync(hostType, hostId, tagId, normalizedSourceKey, cancellationToken: cancellationToken);
            addedCount++;
        }

        if (addedCount > 0 || removedTagIds.Length > 0)
            TraceTagChangesStaged(normalizedSourceKey, hostType, hostId, addedCount, removedTagIds.Length);
    }

    public async Task RemoveHostSourceApplicationsExceptAsync(
        AffinityHostType hostType,
        int hostId,
        string sourceKey,
        IReadOnlyCollection<int> keepTagIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedSourceKey = NormalizeSourceKey(sourceKey);
        var keep = NormalizeTagIds(keepTagIds);

        var stale = await _db.TagApplications
            .Where(application => application.HostType == hostType
                && application.HostId == hostId
                && application.ContextType == null
                && application.ContextId == null
                && application.SourceKey == normalizedSourceKey
                && !keep.Contains(application.TagId))
            .ToListAsync(cancellationToken);

        if (stale.Count > 0)
        {
            _db.TagApplications.RemoveRange(stale);
        }
    }

    public async Task RemoveForHostAsync(AffinityHostType hostType, int hostId, CancellationToken cancellationToken = default)
    {
        var applications = await _db.TagApplications
            .Where(application => application.HostType == hostType && application.HostId == hostId)
            .ToListAsync(cancellationToken);

        if (applications.Count > 0)
        {
            _db.TagApplications.RemoveRange(applications);
        }
    }

    public async Task<IReadOnlyDictionary<int, List<TagProvenanceDto>>> GetLookupAsync(
        AffinityHostType hostType,
        int hostId,
        IReadOnlyCollection<int> tagIds,
        CancellationToken cancellationToken = default)
    {
        var normalizedTagIds = NormalizeTagIds(tagIds);
        if (normalizedTagIds.Count == 0)
        {
            return new Dictionary<int, List<TagProvenanceDto>>();
        }

        using var scope = _scopeFactory?.CreateScope();
        var lookupDb = scope?.ServiceProvider.GetRequiredService<CoveContext>() ?? _db;

        var applications = await lookupDb.TagApplications
            .AsNoTracking()
            .Where(application => application.HostType == hostType && application.HostId == hostId && normalizedTagIds.Contains(application.TagId))
            .OrderBy(application => application.SourceKey)
            .ThenBy(application => application.CreatedAt)
            .ToListAsync(cancellationToken);

        return applications
            .GroupBy(application => application.TagId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(MapToDto).ToList());
    }

    private async Task EnsureApplicationAsync(
        AffinityHostType hostType,
        int hostId,
        int? tagId,
        Tag? tag,
        string sourceKey,
        string? sourceRunId,
        string? modelKey,
        float? confidence,
        string? contextType,
        int? contextId,
        double? totalDurationSec,
        double? hostDurationSec,
        CancellationToken cancellationToken)
    {
        var normalizedSourceKey = NormalizeSourceKey(sourceKey);
        var normalizedSourceRunId = NormalizeOptional(sourceRunId);
        var normalizedModelKey = NormalizeOptional(modelKey);
        var normalizedContextType = NormalizeContextType(contextType);
        var normalizedContextId = normalizedContextType == null ? null : contextId;

        TagApplication? application;
        if (tagId.HasValue)
        {
            application = _db.TagApplications.Local.FirstOrDefault(
                candidate => candidate.HostType == hostType
                    && candidate.HostId == hostId
                    && candidate.ContextType == normalizedContextType
                    && candidate.ContextId == normalizedContextId
                    && candidate.TagId == tagId.Value
                    && candidate.SourceKey == normalizedSourceKey
                    && candidate.SourceRunId == normalizedSourceRunId
                    && candidate.ModelKey == normalizedModelKey);

            if (application is null)
            {
                application = await _db.TagApplications.FirstOrDefaultAsync(
                    candidate => candidate.HostType == hostType
                        && candidate.HostId == hostId
                        && candidate.ContextType == normalizedContextType
                        && candidate.ContextId == normalizedContextId
                        && candidate.TagId == tagId.Value
                        && candidate.SourceKey == normalizedSourceKey
                        && candidate.SourceRunId == normalizedSourceRunId
                        && candidate.ModelKey == normalizedModelKey,
                    cancellationToken);
            }
        }
        else
        {
            application = _db.TagApplications.Local.FirstOrDefault(
                candidate => candidate.HostType == hostType
                    && candidate.HostId == hostId
                    && candidate.ContextType == normalizedContextType
                    && candidate.ContextId == normalizedContextId
                    && ReferenceEquals(candidate.Tag, tag)
                    && candidate.SourceKey == normalizedSourceKey
                    && candidate.SourceRunId == normalizedSourceRunId
                    && candidate.ModelKey == normalizedModelKey);
        }

        if (application is null)
        {
            application = new TagApplication
            {
                HostType = hostType,
                HostId = hostId,
                ContextType = normalizedContextType,
                ContextId = normalizedContextId,
                TagId = tagId ?? 0,
                Tag = tag,
                SourceKey = normalizedSourceKey,
                SourceRunId = normalizedSourceRunId,
                ModelKey = normalizedModelKey,
                Confidence = confidence,
                TotalDurationSec = totalDurationSec,
                HostDurationSec = hostDurationSec,
            };
            _db.TagApplications.Add(application);
            return;
        }

        if (confidence.HasValue && (!application.Confidence.HasValue || confidence.Value > application.Confidence.Value))
        {
            application.Confidence = confidence.Value;
        }

        if (totalDurationSec.HasValue)
        {
            application.TotalDurationSec = totalDurationSec.Value;
        }

        if (hostDurationSec.HasValue)
        {
            application.HostDurationSec = hostDurationSec.Value;
        }
    }

    private static HashSet<int> NormalizeTagIds(IReadOnlyCollection<int> tagIds)
        => tagIds.Where(static tagId => tagId > 0).ToHashSet();

    private static string NormalizeSourceKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "user";
        }

        var trimmed = value.Trim();
        return trimmed.ToLowerInvariant() switch
        {
            "scraper" => "scraper:local",
            "metadata" => "metadata:default",
            "import:stash" => "stash-import",
            _ => trimmed,
        };
    }

    private static string NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static string? NormalizeContextType(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static TagProvenanceDto MapToDto(TagApplication application)
        => new(
            application.SourceKey,
            string.IsNullOrWhiteSpace(application.SourceRunId) ? null : application.SourceRunId,
            string.IsNullOrWhiteSpace(application.ModelKey) ? null : application.ModelKey,
            application.Confidence,
            application.CreatedAt.ToString("o"),
            application.ContextType,
            application.ContextId,
            application.TotalDurationSec,
            application.HostDurationSec);
}
