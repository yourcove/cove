using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

/// <summary>Composes permission-aware related-entity subqueries into list queries.</summary>
public static class RelatedFilterQuery
{
    public static async Task<IQueryable<Video>> ApplyToVideosAsync(
        CoveContext db,
        IQueryable<Video> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        return criterion.Exclude
            ? query.Where(video => !video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(video => video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    public static async Task<IQueryable<Image>> ApplyToImagesAsync(
        CoveContext db,
        IQueryable<Image> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        return criterion.Exclude
            ? query.Where(image => !image.ImagePerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(image => image.ImagePerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    public static async Task<IQueryable<Gallery>> ApplyToGalleriesAsync(
        CoveContext db,
        IQueryable<Gallery> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        return criterion.Exclude
            ? query.Where(gallery => !gallery.GalleryPerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(gallery => gallery.GalleryPerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    public static async Task<IQueryable<Audio>> ApplyToAudiosAsync(
        CoveContext db,
        IQueryable<Audio> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        return criterion.Exclude
            ? query.Where(audio => !audio.AudioPerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(audio => audio.AudioPerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    public static async Task<IQueryable<TextDocument>> ApplyToTextsAsync(
        CoveContext db,
        IQueryable<TextDocument> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        return criterion.Exclude
            ? query.Where(text => !text.TextPerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(text => text.TextPerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    public static async Task<IQueryable<Performer>> ApplyToPerformersAsync(
        CoveContext db,
        IQueryable<Performer> query,
        RelatedFilterCriterion<VideoFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var videos = await new VideoRepository(db).BuildFilteredQueryAsync(
            criterion.ObjectFilter,
            criterion.FindFilter,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        var videoIds = videos.Select(video => video.Id);
        return criterion.Exclude
            ? query.Where(performer => !performer.VideoPerformers.Any(link => videoIds.Contains(link.VideoId)))
            : query.Where(performer => performer.VideoPerformers.Any(link => videoIds.Contains(link.VideoId)));
    }

    private static async Task<IQueryable<int>> MatchingPerformerIdsAsync(
        CoveContext db,
        RelatedFilterCriterion<PerformerFilter> criterion,
        CancellationToken ct)
    {
        var performers = await new PerformerRepository(db).BuildFilteredQueryAsync(
            criterion.ObjectFilter,
            criterion.FindFilter,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        return performers.Select(performer => performer.Id);
    }
}
