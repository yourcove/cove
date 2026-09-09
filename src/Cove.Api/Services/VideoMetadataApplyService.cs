using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public interface IVideoMetadataApplyService
{
    Task<bool> ApplyAsync(int videoId, ScrapedVideoDto metadata, DownloaderMetadataApplyOptions? options = null, CancellationToken ct = default);
}

public class VideoMetadataApplyService(CoveContext db, IEventBus eventBus, IVideoCoverService videoCoverService, ITagProvenanceService tagProvenanceService, IFieldProvenanceService? fieldProvenanceService = null) : IVideoMetadataApplyService
{
    public async Task<bool> ApplyAsync(int videoId, ScrapedVideoDto metadata, DownloaderMetadataApplyOptions? options = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new DownloaderMetadataApplyOptions();

        var video = await db.Videos
            .Include(item => item.Urls)
            .Include(item => item.VideoTags).ThenInclude(item => item.Tag)
            .Include(item => item.VideoPerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .FirstOrDefaultAsync(item => item.Id == videoId, ct);

        if (video == null)
            return false;

        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(metadata.SourceScraperId);

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            video.Title = metadata.Title.Trim();
            fieldProvenance["title"] = video.Title;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Code))
        {
            video.Code = metadata.Code.Trim();
            fieldProvenance["code"] = video.Code;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Details))
        {
            video.Details = metadata.Details.Trim();
            fieldProvenance["details"] = video.Details;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Director))
        {
            video.Director = metadata.Director.Trim();
            fieldProvenance["director"] = video.Director;
        }

        if (ScrapedVideoDateParser.TryParse(metadata.Date, out var parsedDate))
        {
            video.Date = parsedDate;
            video.DatePrecision = DatePrecision.Day;
            fieldProvenance["date"] = parsedDate.ToString("yyyy-MM-dd");
        }

        if (options.MarkOrganized)
            video.Organized = true;

        await videoCoverService.TryApplyRemoteCoverAsync(video, metadata.ImageUrl, ct);
        if (!string.IsNullOrWhiteSpace(metadata.ImageUrl))
            fieldProvenance["image_url"] = metadata.ImageUrl.Trim();

        var urls = NormalizeNames(metadata.Urls);
        if (urls.Count > 0)
            fieldProvenance["urls"] = urls;
        ApplyUrls(video, urls);

        var tagNames = NormalizeNames(metadata.TagNames);
        if (tagNames.Count > 0)
            fieldProvenance["tags"] = tagNames;
        await ApplyTagsAsync(video, tagNames, options.CreateMissingTags, sourceKey, ct);

        var performerNames = NormalizeNames(metadata.PerformerNames);
        if (performerNames.Count > 0)
            fieldProvenance["performers"] = performerNames;
        await ApplyPerformersAsync(video, performerNames, options.CreateMissingPerformers, ct);

        var studioName = string.IsNullOrWhiteSpace(metadata.StudioName) ? null : metadata.StudioName.Trim();
        if (!string.IsNullOrWhiteSpace(studioName))
            fieldProvenance["studio"] = studioName;
        await ApplyStudioAsync(video, studioName, options.CreateMissingStudio, ct);

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Video, video.Id, fieldProvenance, sourceKey, cancellationToken: ct);

        await db.SaveChangesAsync(ct);
        eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", video.Id));
        return true;
    }

    private static void ApplyUrls(Video video, IReadOnlyList<string> urls)
    {
        var existing = video.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in NormalizeNames(urls))
        {
            if (existing.Add(url))
                video.Urls.Add(new VideoUrl { VideoId = video.Id, Url = url });
        }
    }

    private static string BuildScraperSourceKey(string? scraperId)
        => string.IsNullOrWhiteSpace(scraperId) ? "scraper" : $"scraper:{scraperId.Trim()}";

    private async Task ApplyTagsAsync(Video video, IReadOnlyList<string> tagNames, bool createMissing, string sourceKey, CancellationToken ct)
    {
        var names = NormalizeNames(tagNames);
        if (names.Count == 0)
            return;

        var tagLookup = await RelationNameResolver.ResolveTagsAsync(db, names, ct);

        var existing = video.VideoTags
            .Where(item => item.Tag != null)
            .Select(item => item.Tag!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!tagLookup.TryGetValue(name, out var tag))
            {
                if (!createMissing)
                    continue;

                tag = new Tag { Name = name };
                db.Tags.Add(tag);
                tagLookup[name] = tag;
            }

            if (existing.Add(tag.Name))
                video.VideoTags.Add(new VideoTag { Video = video, Tag = tag });

            await tagProvenanceService.RecordAsync(AffinityHostType.Video, video.Id, tag, sourceKey, cancellationToken: ct);
        }
    }

    private async Task ApplyPerformersAsync(Video video, IReadOnlyList<string> performerNames, bool createMissing, CancellationToken ct)
    {
        var names = NormalizeNames(performerNames);
        if (names.Count == 0)
            return;

        var performerLookup = await RelationNameResolver.ResolvePerformersAsync(db, names, ct);

        var existing = video.VideoPerformers
            .Where(item => item.Performer != null)
            .Select(item => item.Performer!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!performerLookup.TryGetValue(name, out var performer))
            {
                if (!createMissing)
                    continue;

                performer = new Performer { Name = name };
                db.Performers.Add(performer);
                performerLookup[name] = performer;
            }

            if (existing.Add(performer.Name))
                video.VideoPerformers.Add(new VideoPerformer { Video = video, Performer = performer });
        }
    }

    private async Task ApplyStudioAsync(Video video, string? studioName, bool createMissing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(studioName))
            return;

        var normalizedStudioName = studioName.Trim();
        var studio = await RelationNameResolver.ResolveStudioAsync(db, normalizedStudioName, ct);
        if (studio == null && !createMissing)
            return;

        studio ??= new Studio { Name = normalizedStudioName };

        if (studio.Id == 0)
            db.Studios.Add(studio);

        video.Studio = studio;
        video.StudioId = studio.Id == 0 ? null : studio.Id;
    }

    private static List<string> NormalizeNames(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
