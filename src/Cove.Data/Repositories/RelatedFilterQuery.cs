using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

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
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or && criterion.AgeAtHostDateCriterion != null)
        {
            var visibleLinks = db.Set<VideoPerformer>().Where(link => visiblePerformerIds.Contains(link.PerformerId));
            var ageLinks = ApplyVideoPerformerAgeCriterion(visibleLinks, criterion.AgeAtHostDateCriterion);
            var matchingLinks = HasPerformerCondition(criterion)
                ? visibleLinks.Where(link => performerIds.Contains(link.PerformerId)).Union(ageLinks)
                : ageLinks;
            return ApplyVideoPerformerLinkMatch(query, visiblePerformerIds, matchingLinks, Mode(criterion), UsesLegacyNone(criterion));
        }
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

    private static IQueryable<Video> ApplyVideoPerformerLinkMatch(
        IQueryable<Video> query,
        IQueryable<int> visiblePerformerIds,
        IQueryable<VideoPerformer> matchingLinks,
        RelatedFilterMode mode,
        bool legacyNone)
        => mode switch
        {
            RelatedFilterMode.Every => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)
                    && !matchingLinks.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId))),
            RelatedFilterMode.None when !legacyNone => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => matchingLinks.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId))),
            RelatedFilterMode.None => query.Where(video => !video.VideoPerformers.Any(link => matchingLinks.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId))),
            _ => query.Where(video => video.VideoPerformers.Any(link => matchingLinks.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId))),
        };

    private static IQueryable<VideoPerformer> ApplyVideoPerformerAgeCriterion(IQueryable<VideoPerformer> links, IntCriterion criterion)
    {
        var value = criterion.Value;
        var value2 = criterion.Value2 ?? value;
        return criterion.Modifier switch
        {
            CriterionModifier.Equals => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) == value),
            CriterionModifier.NotEquals => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) != value),
            CriterionModifier.GreaterThan => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) > value),
            CriterionModifier.LessThan => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) < value),
            CriterionModifier.Between => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) >= value
                && link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) <= value2),
            CriterionModifier.NotBetween => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null
                && (link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) < value
                || link.Video.Date.Value.Year - link.Performer.Birthdate.Value.Year
                - ((link.Video.Date.Value.Month < link.Performer.Birthdate.Value.Month || (link.Video.Date.Value.Month == link.Performer.Birthdate.Value.Month && link.Video.Date.Value.Day < link.Performer.Birthdate.Value.Day)) ? 1 : 0) > value2)),
            CriterionModifier.IsNull => links.Where(link => link.Video!.Date == null || link.Performer!.Birthdate == null),
            CriterionModifier.NotNull => links.Where(link => link.Video!.Date != null && link.Performer!.Birthdate != null),
            _ => links.Where(_ => false),
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
            CriterionModifier.IsNull => query.Where(video => video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && (video.Date == null || link.Performer!.Birthdate == null))),
            CriterionModifier.NotNull => query.Where(video => video.Date != null && video.VideoPerformers.Any(link =>
                performerIds.Contains(link.PerformerId) && link.Performer!.Birthdate != null)),
            _ => query.Where(_ => false),
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
            CriterionModifier.IsNull => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)
                    && (!performerIds.Contains(link.PerformerId) || video.Date != null && link.Performer!.Birthdate != null))),
            CriterionModifier.NotNull => query.Where(video => video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !video.VideoPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)
                    && (!performerIds.Contains(link.PerformerId) || video.Date == null || link.Performer!.Birthdate == null))),
            _ => query.Where(_ => false),
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
        // Project the authorized relationship sets before applying them to the performer query. The raw link
        // projection has no independent visibility semantics; video authorization is enforced by the video-ID
        // subquery and performer authorization remains on the caller's performer query.
        var canUseUnfilteredVideoTree = db.CanReadVideoTagTreeWithoutAuthorizationFilters
            && UsesOnlyVideoTagFilter(criterion);
        var matchingVideoIds = await MatchingVideoIdsAsync(db, criterion, canUseUnfilteredVideoTree, ct);
        var links = db.Database.IsRelational()
            ? db.Set<UnfilteredVideoPerformerLink>()
            : db.Set<VideoPerformer>().Select(link => new UnfilteredVideoPerformerLink
            {
                VideoId = link.VideoId,
                PerformerId = link.PerformerId,
            });
        var matchingPerformerIds = links
            .Where(link => matchingVideoIds.Contains(link.VideoId))
            .Select(link => link.PerformerId)
            .Distinct();
        var mode = Mode(criterion);
        if (mode == RelatedFilterMode.AtLeastOne)
        {
            if (!canUseUnfilteredVideoTree)
            {
                var authorizedVideoIds = await matchingVideoIds.Distinct().ToArrayAsync(ct);
                matchingPerformerIds = links
                    .Where(link => authorizedVideoIds.Contains(link.VideoId))
                    .Select(link => link.PerformerId)
                    .Distinct();
            }
            return query.Where(performer => matchingPerformerIds.Contains(performer.Id));
        }
        if (mode == RelatedFilterMode.None && UsesLegacyNone(criterion))
            return query.Where(performer => !matchingPerformerIds.Contains(performer.Id));

        var visibleVideos = await new VideoRepository(db).BuildFilteredQueryAsync(
            null,
            null,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        var visibleVideoIds = visibleVideos.Select(video => video.Id);
        var visibleLinks = links
            .Where(link => visibleVideoIds.Contains(link.VideoId));
        var performersWithVisibleVideos = visibleLinks
            .Select(link => link.PerformerId)
            .Distinct();
        var performersWithNonMatchingVisibleVideos = visibleLinks
            .Where(link => !matchingVideoIds.Contains(link.VideoId))
            .Select(link => link.PerformerId)
            .Distinct();

        return mode switch
        {
            RelatedFilterMode.Every => query.Where(performer => performersWithVisibleVideos.Contains(performer.Id)
                && !performersWithNonMatchingVisibleVideos.Contains(performer.Id)),
            RelatedFilterMode.None when !UsesLegacyNone(criterion) => query.Where(performer => performersWithVisibleVideos.Contains(performer.Id)
                && !matchingPerformerIds.Contains(performer.Id)),
            _ => query,
        };
    }

    private static async Task<IQueryable<int>> MatchingPerformerIdsAsync(
        CoveContext db,
        RelatedFilterCriterion<PerformerFilter> criterion,
        CancellationToken ct)
    {
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<Performer>? union = null;
            var repository = new PerformerRepository(db);
            if (!string.IsNullOrWhiteSpace(criterion.FindFilter?.Q))
                union = await repository.BuildFilteredQueryAsync(null, criterion.FindFilter, false, false, ct);
            if (criterion.ObjectFilter != null)
            {
                foreach (var property in typeof(PerformerFilter).GetProperties())
                {
                    var value = property.GetValue(criterion.ObjectFilter);
                    if (!HasFilterValue(value)) continue;
                    if (property.Name == nameof(PerformerFilter.CustomFieldCriteria))
                    {
                        foreach (var customFieldCriterion in criterion.ObjectFilter.CustomFieldCriteria)
                        {
                            var customFieldBranch = await repository.BuildFilteredQueryAsync(
                                new PerformerFilter { CustomFieldCriteria = [customFieldCriterion] }, null, false, false, ct);
                            union = union == null ? customFieldBranch : union.Union(customFieldBranch);
                        }
                        continue;
                    }
                    if (property.Name == nameof(PerformerFilter.RemoteIdCriterion)
                        && HasFilterValue(criterion.ObjectFilter.RemoteIdValueCriterion)) continue;
                    var branchFilter = new PerformerFilter();
                    property.SetValue(branchFilter, value);
                    if (property.Name == nameof(PerformerFilter.RemoteIdValueCriterion))
                        branchFilter.RemoteIdCriterion = criterion.ObjectFilter.RemoteIdCriterion;
                    var branch = await repository.BuildFilteredQueryAsync(branchFilter, null, false, false, ct);
                    union = union == null ? branch : union.Union(branch);
                }
            }
            if (union != null) return union.Select(performer => performer.Id);
        }
        var performers = await new PerformerRepository(db).BuildFilteredQueryAsync(
            criterion.ObjectFilter,
            criterion.FindFilter,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        return performers.Select(performer => performer.Id);
    }

    private static bool HasPerformerCondition(RelatedFilterCriterion<PerformerFilter> criterion)
        => !string.IsNullOrWhiteSpace(criterion.FindFilter?.Q)
            || criterion.ObjectFilter != null && typeof(PerformerFilter).GetProperties().Any(property => HasFilterValue(property.GetValue(criterion.ObjectFilter)));

    private static bool HasFilterValue(object? value)
        => value switch
        {
            null => false,
            string text => !string.IsNullOrWhiteSpace(text),
            System.Collections.IEnumerable sequence => sequence.Cast<object>().Any(),
            _ => true,
        };

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

    private static async Task<IQueryable<int>> MatchingVideoIdsAsync(
        CoveContext db,
        RelatedFilterCriterion<VideoFilter> criterion,
        bool ignoreAuthorizationFilters,
        CancellationToken ct)
    {
        var repository = new VideoRepository(db);
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<Video>? union = null;
            if (!string.IsNullOrWhiteSpace(criterion.FindFilter?.Q))
                union = await repository.BuildFilteredQueryAsync(null, criterion.FindFilter, false, false, ct);
            if (criterion.ObjectFilter != null)
            {
                foreach (var property in typeof(VideoFilter).GetProperties())
                {
                    var value = property.GetValue(criterion.ObjectFilter);
                    if (!HasFilterValue(value)) continue;
                    if (property.Name == nameof(VideoFilter.CustomFieldCriteria))
                    {
                        foreach (var customFieldCriterion in criterion.ObjectFilter.CustomFieldCriteria)
                        {
                            var customFieldBranch = await repository.BuildFilteredQueryAsync(
                                new VideoFilter { CustomFieldCriteria = [customFieldCriterion] }, null, false, false, ct);
                            union = union == null ? customFieldBranch : union.Union(customFieldBranch);
                        }
                        continue;
                    }
                    if (property.Name == nameof(VideoFilter.RemoteIdCriterion)
                        && HasFilterValue(criterion.ObjectFilter.RemoteIdValueCriterion)) continue;
                    var branchFilter = new VideoFilter();
                    property.SetValue(branchFilter, value);
                    if (property.Name == nameof(VideoFilter.RemoteIdValueCriterion))
                        branchFilter.RemoteIdCriterion = criterion.ObjectFilter.RemoteIdCriterion;
                    var branch = await repository.BuildFilteredQueryAsync(branchFilter, null, false, false, ct);
                    union = union == null ? branch : union.Union(branch);
                }
            }
            if (union != null)
                return (ignoreAuthorizationFilters ? union.IgnoreQueryFilters() : union).Select(video => video.Id);
        }
        var videos = await repository.BuildFilteredQueryAsync(criterion.ObjectFilter, criterion.FindFilter, false, false, ct);
        return (ignoreAuthorizationFilters ? videos.IgnoreQueryFilters() : videos).Select(video => video.Id);
    }

    private static bool UsesOnlyVideoTagFilter(RelatedFilterCriterion<VideoFilter> criterion)
    {
        if (criterion.FindFilter != null || criterion.ObjectFilter?.TagsCriterion == null)
            return false;

        return typeof(VideoFilter).GetProperties()
            .Where(property => property.Name != nameof(VideoFilter.TagsCriterion))
            .All(property => !HasFilterValue(property.GetValue(criterion.ObjectFilter)));
    }
}
