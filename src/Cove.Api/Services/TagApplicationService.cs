using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;

using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class TagApplicationValidationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class TagApplicationService(CoveContext db)
{
    public async Task<TagApplication> AddAsync(TagApplicationCreateDto dto, CancellationToken ct)
    {
        if (!TryParseHostType(dto.HostType, out var hostType))
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Host type is invalid.");

        if (dto.HostId <= 0)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Host id is required.");

        if (dto.TagId <= 0)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Tag id is required.");

        if (!await HostExistsAsync(hostType, dto.HostId, ct))
            throw new TagApplicationValidationException(StatusCodes.Status404NotFound, "Host does not exist.");

        var tagExists = await db.Tags.AsNoTracking().AnyAsync(tag => tag.Id == dto.TagId, ct);
        if (!tagExists)
            throw new TagApplicationValidationException(StatusCodes.Status404NotFound, "Tag does not exist.");

        var contextType = NormalizeContextType(dto.ContextType);
        var contextId = contextType == null ? null : dto.ContextId;
        await ValidateContextAsync(hostType, dto.HostId, contextType, contextId, ct);

        var sourceKey = NormalizeSourceKey(dto.SourceKey);
        var sourceRunId = NormalizeOptional(dto.SourceRunId);
        var modelKey = NormalizeOptional(dto.ModelKey);

        var application = await db.TagApplications.FirstOrDefaultAsync(candidate =>
            candidate.HostType == hostType
            && candidate.HostId == dto.HostId
            && candidate.ContextType == contextType
            && candidate.ContextId == contextId
            && candidate.TagId == dto.TagId
            && candidate.SourceKey == sourceKey
            && candidate.SourceRunId == sourceRunId
            && candidate.ModelKey == modelKey,
            ct);

        if (application == null)
        {
            application = new TagApplication
            {
                HostType = hostType,
                HostId = dto.HostId,
                ContextType = contextType,
                ContextId = contextId,
                TagId = dto.TagId,
                SourceKey = sourceKey,
                SourceRunId = sourceRunId,
                ModelKey = modelKey,
            };
            db.TagApplications.Add(application);
        }

        if (dto.Confidence.HasValue) application.Confidence = dto.Confidence.Value;
        if (dto.TotalDurationSec.HasValue) application.TotalDurationSec = dto.TotalDurationSec.Value;
        if (dto.HostDurationSec.HasValue) application.HostDurationSec = dto.HostDurationSec.Value;

        await db.SaveChangesAsync(ct);

        return await db.TagApplications
            .Include(item => item.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(item => item.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .FirstAsync(item => item.Id == application.Id, ct);
    }

    public async Task<TagApplication?> DeleteAsync(int id, CancellationToken ct)
    {
        var application = await db.TagApplications.FirstOrDefaultAsync(item => item.Id == id, ct);
        if (application == null)
            return null;

        db.TagApplications.Remove(application);
        await db.SaveChangesAsync(ct);
        return application;
    }

    /// <summary>
    /// Deletes the host-level (non-contextual) tag applications for a single (host, tag) pair —
    /// the rows that drive whether a derived tag appears on the host. This is the "the AI is wrong
    /// about this video" correction: it removes the model's finding for one host without touching
    /// the global tag threshold or the underlying timeline segments. A re-run of the extension under
    /// a new model version may re-derive it, which is acceptable. Deleting through the tracked context
    /// lets CoveContext refresh the affected tag's denormalized counts on save.
    /// </summary>
    public async Task<int> DeleteHostTagApplicationsAsync(string hostType, int hostId, int tagId, CancellationToken ct)
    {
        if (!TryParseHostType(hostType, out var parsedHostType))
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Host type is invalid.");

        if (hostId <= 0 || tagId <= 0)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Host id and tag id are required.");

        var applications = await db.TagApplications
            .Where(application => application.HostType == parsedHostType
                && application.HostId == hostId
                && application.TagId == tagId
                && application.ContextType == null
                && application.ContextId == null)
            .ToListAsync(ct);

        if (applications.Count == 0)
            return 0;

        db.TagApplications.RemoveRange(applications);
        await db.SaveChangesAsync(ct);
        return applications.Count;
    }

    public IQueryable<TagApplication> BuildQuery(string? hostType, int? hostId, string? contextType, int? contextId)
    {
        var query = db.TagApplications
            .Include(item => item.Tag).ThenInclude(tag => tag!.Aliases)
            .Include(item => item.Tag).ThenInclude(tag => tag!.TagGroup)
            .AsSplitQuery()
            .AsNoTracking()
            .AsQueryable();

        if (TryParseHostType(hostType, out var parsedHostType))
            query = query.Where(item => item.HostType == parsedHostType);
        if (hostId.HasValue)
            query = query.Where(item => item.HostId == hostId.Value);

        var normalizedContextType = NormalizeContextType(contextType);
        if (normalizedContextType != null)
            query = query.Where(item => item.ContextType == normalizedContextType);
        if (contextId.HasValue)
            query = query.Where(item => item.ContextId == contextId.Value);

        return query;
    }

    private async Task ValidateContextAsync(AffinityHostType hostType, int hostId, string? contextType, int? contextId, CancellationToken ct)
    {
        if (contextType == null)
            return;

        if (contextId is not > 0)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Context id is required.");

        if (contextType == "performer")
        {
            var performerContextExists = hostType switch
            {
                AffinityHostType.Video => await db.Set<VideoPerformer>().AsNoTracking().AnyAsync(item => item.VideoId == hostId && item.PerformerId == contextId.Value, ct),
                AffinityHostType.Image => await db.Set<ImagePerformer>().AsNoTracking().AnyAsync(item => item.ImageId == hostId && item.PerformerId == contextId.Value, ct),
                AffinityHostType.Audio => await db.Set<AudioPerformer>().AsNoTracking().AnyAsync(item => item.AudioId == hostId && item.PerformerId == contextId.Value, ct),
                AffinityHostType.Text => await db.Set<TextPerformer>().AsNoTracking().AnyAsync(item => item.TextDocumentId == hostId && item.PerformerId == contextId.Value, ct),
                _ => throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Contextual performer tag applications are not supported for this host."),
            };

            if (!performerContextExists)
                throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Context does not belong to the host.");

            return;
        }

        if (hostType != AffinityHostType.Video)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "This context type is only supported for video hosts.");

        var exists = contextType switch
        {
            "detection" => await db.Detections.AsNoTracking().AnyAsync(item => item.Id == contextId.Value && item.HostType == DetectionHostType.Video && item.HostId == hostId, ct),
            "face" => await db.FaceAppearances.AsNoTracking().AnyAsync(item => item.FaceId == contextId.Value && item.HostType == FaceAppearanceHostType.Video && item.HostId == hostId, ct),
            "segment" => await db.Segments.AsNoTracking().AnyAsync(item => item.Id == contextId.Value && item.HostType == SegmentHostType.Video && item.HostId == hostId, ct),
            _ => throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Context type is invalid."),
        };

        if (!exists)
            throw new TagApplicationValidationException(StatusCodes.Status400BadRequest, "Context does not belong to the host.");
    }

    private Task<bool> HostExistsAsync(AffinityHostType hostType, int hostId, CancellationToken ct)
        => hostType switch
        {
            AffinityHostType.Video => db.Videos.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Image => db.Images.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Audio => db.Audios.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Text => db.TextDocuments.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Performer => db.Performers.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Face => db.Faces.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Tag => db.Tags.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Studio => db.Studios.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Gallery => db.Galleries.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            AffinityHostType.Group => db.Groups.AsNoTracking().AnyAsync(item => item.Id == hostId, ct),
            _ => Task.FromResult(false),
        };

    private static bool TryParseHostType(string? value, out AffinityHostType hostType)
    {
        if (Enum.TryParse(value, ignoreCase: true, out hostType))
            return true;

        hostType = default;
        return false;
    }

    private static string? NormalizeContextType(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();

    private static string NormalizeSourceKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "user";

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
}
