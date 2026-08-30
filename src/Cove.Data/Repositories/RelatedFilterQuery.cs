using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

/// <summary>Composes permission-aware related-entity subqueries into list queries.</summary>
public static class RelatedFilterQuery
{
    private static RelatedFilterMode Mode<TFilter>(RelatedFilterCriterion<TFilter> criterion) where TFilter : class
        => criterion.Exclude && criterion.Mode == RelatedFilterMode.AtLeastOne ? RelatedFilterMode.None : criterion.Mode;

    private static bool UsesLegacyNone<TFilter>(RelatedFilterCriterion<TFilter> criterion) where TFilter : class
        => criterion.Exclude && criterion.Mode == RelatedFilterMode.AtLeastOne;

    public static async Task<IQueryable<Video>> ApplyToVideosAsync(
        CoveContext db,
        IQueryable<Video> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        if (criterion.AgeAtHostDateCriterion != null)
            return ApplyVideoPerformerAgeMatch(query, performerIds, visiblePerformerIds, criterion.AgeAtHostDateCriterion, Mode(criterion), UsesLegacyNone(criterion));
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None => query.Where(video => !video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            _ => query.Where(video => video.VideoPerformers.Any(link => performerIds.Contains(link.PerformerId))),
        };
    }

    private static IQueryable<Video> ApplyVideoPerformerAgeMatch(
        IQueryable<Video> query,
        IQueryable<int> performerIds,
        IQueryable<int> visiblePerformerIds,
        IntCriterion criterion,
        RelatedFilterMode mode,
        bool legacyNone)
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
        var matchedIds = matched.Select(video => video.Id);
        if (mode == RelatedFilterMode.AtLeastOne) return matched;
        if (mode == RelatedFilterMode.None)
            return legacyNone
                ? query.Where(video => !matchedIds.Contains(video.Id))
                : query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !matchedIds.Contains(video.Id));

        return criterion.Modifier switch
        {
            CriterionModifier.Equals => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) != value))),
            CriterionModifier.NotEquals => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) == value))),
            CriterionModifier.GreaterThan => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) <= value))),
            CriterionModifier.LessThan => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) >= value))),
            CriterionModifier.Between => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) < value ||
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) > value2))),
            CriterionModifier.NotBetween => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)) && !video.VideoPerformers.Any(link =>
                visiblePerformerIds.Contains(link.PerformerId) && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null ||
                (video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) >= value &&
                video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (video.Date.Value.Month == link.Performer.Birthdate.Value.Month && video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) <= value2)))),
            _ => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
        };
    }

    public static async Task<IQueryable<Image>> ApplyToImagesAsync(
        CoveContext db,
        IQueryable<Image> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(image => image.ImagePerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !image.ImagePerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(image => image.ImagePerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !image.ImagePerformers.Any(link => performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None => query.Where(image => !image.ImagePerformers.Any(link => performerIds.Contains(link.PerformerId))),
            _ => query.Where(image => image.ImagePerformers.Any(link => performerIds.Contains(link.PerformerId))),
        };
    }

    public static async Task<IQueryable<Gallery>> ApplyToGalleriesAsync(
        CoveContext db,
        IQueryable<Gallery> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(gallery => gallery.GalleryPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !gallery.GalleryPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(gallery => gallery.GalleryPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !gallery.GalleryPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None => query.Where(gallery => !gallery.GalleryPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            _ => query.Where(gallery => gallery.GalleryPerformers.Any(link => performerIds.Contains(link.PerformerId))),
        };
    }

    public static async Task<IQueryable<Audio>> ApplyToAudiosAsync(
        CoveContext db,
        IQueryable<Audio> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(audio => audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(audio => audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !audio.AudioPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None => query.Where(audio => !audio.AudioPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            _ => query.Where(audio => audio.AudioPerformers.Any(link => performerIds.Contains(link.PerformerId))),
        };
    }

    public static async Task<IQueryable<TextDocument>> ApplyToTextsAsync(
        CoveContext db,
        IQueryable<TextDocument> query,
        RelatedFilterCriterion<PerformerFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var performerIds = await MatchingPerformerIdsAsync(db, criterion, ct);
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(text => text.TextPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !text.TextPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId) && !performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(text => text.TextPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !text.TextPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            RelatedFilterMode.None => query.Where(text => !text.TextPerformers.Any(link => performerIds.Contains(link.PerformerId))),
            _ => query.Where(text => text.TextPerformers.Any(link => performerIds.Contains(link.PerformerId))),
        };
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
        var visibleVideos = await new VideoRepository(db).BuildFilteredQueryAsync(
            null,
            null,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        var visibleVideoIds = visibleVideos.Select(video => video.Id);
        return Mode(criterion) switch
        {
            RelatedFilterMode.Every => query.Where(performer => performer.VideoPerformers.Any(link => visibleVideoIds.Contains(link.VideoId))
                && !performer.VideoPerformers.Any(link => visibleVideoIds.Contains(link.VideoId) && !videoIds.Contains(link.VideoId))),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(performer => performer.VideoPerformers.Any(link => visibleVideoIds.Contains(link.VideoId))
                && !performer.VideoPerformers.Any(link => videoIds.Contains(link.VideoId))),
            RelatedFilterMode.None => query.Where(performer => !performer.VideoPerformers.Any(link => videoIds.Contains(link.VideoId))),
            _ => query.Where(performer => performer.VideoPerformers.Any(link => videoIds.Contains(link.VideoId))),
        };
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

    private static async Task<IQueryable<int>> VisiblePerformerIdsAsync(CoveContext db, CancellationToken ct)
    {
        var performers = await new PerformerRepository(db).BuildFilteredQueryAsync(
            null,
            null,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        return performers.Select(performer => performer.Id);
    }
}
