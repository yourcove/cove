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
        if (criterion.AgeAtHostDateCriterion != null)
            return ApplyVideoPerformerAgeMatch(query, performerIds, criterion.AgeAtHostDateCriterion, criterion.Exclude);
        return criterion.Exclude
            ? query.Where(video => !video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId)))
            : query.Where(video => video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId)));
    }

    private static IQueryable<Video> ApplyVideoPerformerAgeMatch(
        IQueryable<Video> query,
        IQueryable<int> performerIds,
        IntCriterion criterion,
        bool exclude)
    {
        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;
        var matched = criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) == value)),
            CriterionModifier.NotEquals => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) != value)),
            CriterionModifier.GreaterThan => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) > value)),
            CriterionModifier.LessThan => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) < value)),
            CriterionModifier.Between => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) >= value &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) <= value2)),
            CriterionModifier.NotBetween => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null &&
                (video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) < value ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) > value2))),
            _ => query,
        };
        if (!exclude) return matched;
        var matchedIds = matched.Select(video => video.Id);
        return query.Where(video => !matchedIds.Contains(video.Id));
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
