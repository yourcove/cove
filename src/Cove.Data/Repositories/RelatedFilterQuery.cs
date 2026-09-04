using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;

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
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        var matchingLinks = await BuildMatchingVideoPerformerLinksAsync(db, criterion, visiblePerformerIds, ct);
        return ApplyVideoPerformerLinkMatch(query, visiblePerformerIds, matchingLinks, Mode(criterion), UsesLegacyNone(criterion));
    }

    public static async Task<IQueryable<Video>> ApplyDistinctVideoPerformersAsync(
        CoveContext db,
        IQueryable<Video> query,
        IReadOnlyList<RelatedFilterCriterion<PerformerFilter>> criteria,
        CancellationToken ct = default)
    {
        if (criteria.Count < 2) return query;
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        var matchingLinks = new List<IQueryable<VideoPerformer>>(criteria.Count);
        foreach (var criterion in criteria)
            matchingLinks.Add(await BuildMatchingVideoPerformerLinksAsync(db, criterion, visiblePerformerIds, ct));

        var video = Expression.Parameter(typeof(Video), "video");
        var predicate = BuildDistinctVideoPerformerPredicate(video, matchingLinks, 0, []);
        return query.Where(Expression.Lambda<Func<Video, bool>>(predicate, video));
    }

    private static Expression BuildDistinctVideoPerformerPredicate(
        ParameterExpression video,
        IReadOnlyList<IQueryable<VideoPerformer>> matchingLinks,
        int index,
        IReadOnlyList<ParameterExpression> previousLinks)
    {
        var link = Expression.Parameter(typeof(VideoPerformer), $"link{index}");
        var match = Expression.Parameter(typeof(VideoPerformer), $"match{index}");
        Expression condition = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Any),
            [typeof(VideoPerformer)],
            matchingLinks[index].Expression,
            Expression.Quote(Expression.Lambda<Func<VideoPerformer, bool>>(
                Expression.AndAlso(
                    Expression.Equal(Expression.Property(match, nameof(VideoPerformer.VideoId)), Expression.Property(video, nameof(Video.Id))),
                    Expression.Equal(Expression.Property(match, nameof(VideoPerformer.PerformerId)), Expression.Property(link, nameof(VideoPerformer.PerformerId)))),
                match)));

        foreach (var previous in previousLinks)
            condition = Expression.AndAlso(condition,
                Expression.NotEqual(Expression.Property(link, nameof(VideoPerformer.PerformerId)), Expression.Property(previous, nameof(VideoPerformer.PerformerId))));

        if (index + 1 < matchingLinks.Count)
            condition = Expression.AndAlso(condition, BuildDistinctVideoPerformerPredicate(video, matchingLinks, index + 1, [.. previousLinks, link]));

        return Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Any),
            [typeof(VideoPerformer)],
            Expression.Property(video, nameof(Video.VideoPerformers)),
            Expression.Lambda<Func<VideoPerformer, bool>>(condition, link));
    }

    private static async Task<IQueryable<VideoPerformer>> BuildMatchingVideoPerformerLinksAsync(
        CoveContext db,
        RelatedFilterCriterion<PerformerFilter> criterion,
        IQueryable<int> visiblePerformerIds,
        CancellationToken ct)
    {
        var visibleLinks = db.Set<VideoPerformer>().Where(link => visiblePerformerIds.Contains(link.PerformerId));
        var hasPerformerCondition = HasPerformerCondition(criterion);
        var performerIds = hasPerformerCondition ? await MatchingPerformerIdsAsync(db, criterion, ct) : null;
        var occurrenceCriterion = criterion.PerformerOccurrenceTagsCriterion;
        ExpandedHierarchyCriterion? expandedOccurrenceTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(occurrenceCriterion))
        {
            expandedOccurrenceTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, occurrenceCriterion!, ct);
            occurrenceCriterion = expandedOccurrenceTags.Criterion;
        }

        IQueryable<VideoPerformer> matchingLinks;
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<VideoPerformer>? union = null;
            if (hasPerformerCondition)
                union = visibleLinks.Where(link => performerIds!.Contains(link.PerformerId));
            if (HasMultiIdCondition(criterion.PerformerIdsCriterion))
                union = Union(union, ApplyVideoPerformerIdCriterion(visibleLinks, criterion.PerformerIdsCriterion!));
            if (criterion.AgeAtHostDateCriterion != null)
                union = Union(union, ApplyVideoPerformerAgeCriterion(visibleLinks, criterion.AgeAtHostDateCriterion));
            if (HasMultiIdCondition(occurrenceCriterion))
                union = Union(union, ApplyVideoPerformerOccurrenceTagCriterion(
                    db,
                    visibleLinks,
                    occurrenceCriterion!,
                    expandedOccurrenceTags?.ValueGroups,
                    expandedOccurrenceTags?.RequiredIdGroups));
            matchingLinks = union ?? visibleLinks;
        }
        else
        {
            matchingLinks = visibleLinks;
            if (hasPerformerCondition)
                matchingLinks = matchingLinks.Where(link => performerIds!.Contains(link.PerformerId));
            if (HasMultiIdCondition(criterion.PerformerIdsCriterion))
                matchingLinks = ApplyVideoPerformerIdCriterion(matchingLinks, criterion.PerformerIdsCriterion!);
            if (criterion.AgeAtHostDateCriterion != null)
                matchingLinks = ApplyVideoPerformerAgeCriterion(matchingLinks, criterion.AgeAtHostDateCriterion);
            if (HasMultiIdCondition(occurrenceCriterion))
                matchingLinks = ApplyVideoPerformerOccurrenceTagCriterion(
                    db,
                    matchingLinks,
                    occurrenceCriterion!,
                    expandedOccurrenceTags?.ValueGroups,
                    expandedOccurrenceTags?.RequiredIdGroups);
        }

        return matchingLinks;
    }

    private static IQueryable<T> Union<T>(IQueryable<T>? current, IQueryable<T> branch)
        => current == null ? branch : current.Union(branch);

    private static bool HasMultiIdCondition(MultiIdCriterion? criterion)
        => criterion != null && (criterion.Modifier is CriterionModifier.IsNull or CriterionModifier.NotNull
            || criterion.Value.Any(id => id > 0)
            || criterion.Excludes?.Any(id => id > 0) == true
            || criterion.RequiredIds?.Any(id => id > 0) == true);

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

    private static IQueryable<VideoPerformer> ApplyVideoPerformerIdCriterion(
        IQueryable<VideoPerformer> links,
        MultiIdCriterion criterion)
    {
        var ids = criterion.Value.Where(id => id > 0).Distinct().ToArray();
        if (criterion.Modifier == CriterionModifier.IsNull)
            links = links.Where(_ => false);
        else if (criterion.Modifier != CriterionModifier.NotNull && ids.Length > 0)
        {
            if (criterion.Modifier == CriterionModifier.Includes)
                links = links.Where(link => ids.Contains(link.PerformerId));
            else if (criterion.Modifier == CriterionModifier.IncludesAll)
            {
                foreach (var id in ids)
                    links = links.Where(link => link.PerformerId == id);
            }
            else if (criterion.Modifier == CriterionModifier.Excludes)
                links = links.Where(link => !ids.Contains(link.PerformerId));
            else if (criterion.Modifier == CriterionModifier.ExcludesAll)
            {
                var matchingAll = links;
                foreach (var id in ids)
                    matchingAll = matchingAll.Where(link => link.PerformerId == id);
                links = links.Where(link => !matchingAll.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId));
            }
        }

        var excludedIds = criterion.Excludes?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (excludedIds.Length > 0)
            links = links.Where(link => !excludedIds.Contains(link.PerformerId));

        foreach (var requiredId in criterion.RequiredIds?.Where(id => id > 0).Distinct() ?? [])
            links = links.Where(link => link.PerformerId == requiredId);

        return links;
    }

    private static IQueryable<VideoPerformer> ApplyVideoPerformerOccurrenceTagCriterion(
        CoveContext db,
        IQueryable<VideoPerformer> links,
        MultiIdCriterion criterion,
        IReadOnlyList<int[]>? valueGroups,
        IReadOnlyList<int[]>? requiredIdGroups)
    {
        var applications = db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Video
                && application.ContextType == "performer"
                && application.ContextId != null);
        var groups = valueGroups?.Where(group => group.Length > 0).ToArray()
            ?? criterion.Value.Where(tagId => tagId > 0).Select(tagId => new[] { tagId }).ToArray();
        var tagIds = groups.SelectMany(group => group).Distinct().ToArray();

        if (criterion.Modifier == CriterionModifier.IsNull)
            links = links.Where(link => !applications.Any(application =>
                application.HostId == link.VideoId && application.ContextId == link.PerformerId));
        else if (criterion.Modifier == CriterionModifier.NotNull)
            links = links.Where(link => applications.Any(application =>
                application.HostId == link.VideoId && application.ContextId == link.PerformerId));
        else if (tagIds.Length > 0)
        {
            if (criterion.Modifier == CriterionModifier.Includes)
                links = links.Where(link => applications.Any(application =>
                    application.HostId == link.VideoId
                    && application.ContextId == link.PerformerId
                    && tagIds.Contains(application.TagId)));
            else if (criterion.Modifier == CriterionModifier.IncludesAll)
            {
                foreach (var group in groups)
                    links = links.Where(link => applications.Any(application =>
                        application.HostId == link.VideoId
                        && application.ContextId == link.PerformerId
                        && group.Contains(application.TagId)));
            }
            else if (criterion.Modifier == CriterionModifier.Excludes)
                links = links.Where(link => !applications.Any(application =>
                    application.HostId == link.VideoId
                    && application.ContextId == link.PerformerId
                    && tagIds.Contains(application.TagId)));
            else if (criterion.Modifier == CriterionModifier.ExcludesAll)
            {
                var matchingAll = links;
                foreach (var group in groups)
                    matchingAll = matchingAll.Where(link => applications.Any(application =>
                        application.HostId == link.VideoId
                        && application.ContextId == link.PerformerId
                        && group.Contains(application.TagId)));
                links = links.Where(link => !matchingAll.Any(match =>
                    match.VideoId == link.VideoId && match.PerformerId == link.PerformerId));
            }
        }

        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (excludedTagIds.Length > 0)
            links = links.Where(link => !applications.Any(application =>
                application.HostId == link.VideoId
                && application.ContextId == link.PerformerId
                && excludedTagIds.Contains(application.TagId)));

        var requiredGroups = requiredIdGroups is { Count: > 0 }
            ? requiredIdGroups
            : criterion.RequiredIds?.Where(tagId => tagId > 0).Distinct().Select(tagId => new[] { tagId }).ToArray() ?? [];
        foreach (var group in requiredGroups)
            links = links.Where(link => applications.Any(application =>
                application.HostId == link.VideoId
                && application.ContextId == link.PerformerId
                && group.Contains(application.TagId)));

        return links;
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

    public static async Task<IQueryable<Performer>> ApplyAudioFilterToPerformersAsync(
        CoveContext db,
        IQueryable<Performer> query,
        RelatedFilterCriterion<AudioFilter>? criterion,
        CancellationToken ct = default)
    {
        if (criterion == null) return query;
        var matchingAudioIds = await MatchingAudioIdsAsync(db, criterion, ct);
        var links = db.Set<AudioPerformer>().AsNoTracking();
        var matchingPerformerIds = links
            .Where(link => matchingAudioIds.Contains(link.AudioId))
            .Select(link => link.PerformerId)
            .Distinct();
        var mode = Mode(criterion);
        if (mode == RelatedFilterMode.AtLeastOne)
            return query.Where(performer => matchingPerformerIds.Contains(performer.Id));
        if (mode == RelatedFilterMode.None && UsesLegacyNone(criterion))
            return query.Where(performer => !matchingPerformerIds.Contains(performer.Id));

        var visibleAudios = await AudioFilterQuery.BuildAsync(db, null, null, includeRelatedFilters: false, ct);
        var visibleAudioIds = visibleAudios.Select(audio => audio.Id);
        var visibleLinks = links.Where(link => visibleAudioIds.Contains(link.AudioId));
        var performersWithVisibleAudios = visibleLinks.Select(link => link.PerformerId).Distinct();
        var performersWithNonMatchingVisibleAudios = visibleLinks
            .Where(link => !matchingAudioIds.Contains(link.AudioId))
            .Select(link => link.PerformerId)
            .Distinct();

        return mode switch
        {
            RelatedFilterMode.Every => query.Where(performer => performersWithVisibleAudios.Contains(performer.Id)
                && !performersWithNonMatchingVisibleAudios.Contains(performer.Id)),
            RelatedFilterMode.None => query.Where(performer => performersWithVisibleAudios.Contains(performer.Id)
                && !matchingPerformerIds.Contains(performer.Id)),
            _ => query,
        };
    }

    private static async Task<IQueryable<int>> MatchingAudioIdsAsync(
        CoveContext db,
        RelatedFilterCriterion<AudioFilter> criterion,
        CancellationToken ct)
    {
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<Audio>? union = null;
            if (!string.IsNullOrWhiteSpace(criterion.FindFilter?.Q))
                union = await AudioFilterQuery.BuildAsync(db, null, criterion.FindFilter, includeRelatedFilters: false, ct);
            if (criterion.ObjectFilter != null)
            {
                foreach (var property in typeof(AudioFilter).GetProperties())
                {
                    var value = property.GetValue(criterion.ObjectFilter);
                    if (!HasFilterValue(value)) continue;
                    if (property.Name == nameof(AudioFilter.CustomFieldCriteria))
                    {
                        foreach (var customFieldCriterion in criterion.ObjectFilter.CustomFieldCriteria)
                        {
                            var branch = await AudioFilterQuery.BuildAsync(db,
                                new AudioFilter { CustomFieldCriteria = [customFieldCriterion] }, null, false, ct);
                            union = union == null ? branch : union.Union(branch);
                        }
                        continue;
                    }
                    var branchFilter = new AudioFilter();
                    property.SetValue(branchFilter, value);
                    var branchQuery = await AudioFilterQuery.BuildAsync(db, branchFilter, null, false, ct);
                    union = union == null ? branchQuery : union.Union(branchQuery);
                }
            }
            if (union != null) return union.Select(audio => audio.Id);
        }

        var audios = await AudioFilterQuery.BuildAsync(db, criterion.ObjectFilter, criterion.FindFilter, includeRelatedFilters: false, ct);
        return audios.Select(audio => audio.Id);
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
