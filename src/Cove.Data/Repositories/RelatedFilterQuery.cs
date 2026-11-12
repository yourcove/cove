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

    public static async Task<IQueryable<Audio>> ApplyDistinctAudioPerformersAsync(
        CoveContext db,
        IQueryable<Audio> query,
        IReadOnlyList<RelatedFilterCriterion<PerformerFilter>> criteria,
        CancellationToken ct = default)
    {
        if (criteria.Count < 2) return query;
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        var matchingLinks = new List<IQueryable<AudioPerformer>>(criteria.Count);
        foreach (var criterion in criteria)
            matchingLinks.Add(await BuildMatchingAudioPerformerLinksAsync(db, criterion, visiblePerformerIds, ct));

        var audio = Expression.Parameter(typeof(Audio), "audio");
        var predicate = BuildDistinctAudioPerformerPredicate(audio, matchingLinks, 0, []);
        return query.Where(Expression.Lambda<Func<Audio, bool>>(predicate, audio));
    }

    private static Expression BuildDistinctAudioPerformerPredicate(
        ParameterExpression audio,
        IReadOnlyList<IQueryable<AudioPerformer>> matchingLinks,
        int index,
        IReadOnlyList<ParameterExpression> previousLinks)
    {
        var link = Expression.Parameter(typeof(AudioPerformer), $"link{index}");
        var match = Expression.Parameter(typeof(AudioPerformer), $"match{index}");
        Expression condition = Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Any),
            [typeof(AudioPerformer)],
            matchingLinks[index].Expression,
            Expression.Quote(Expression.Lambda<Func<AudioPerformer, bool>>(
                Expression.AndAlso(
                    Expression.Equal(Expression.Property(match, nameof(AudioPerformer.AudioId)), Expression.Property(audio, nameof(Audio.Id))),
                    Expression.Equal(Expression.Property(match, nameof(AudioPerformer.PerformerId)), Expression.Property(link, nameof(AudioPerformer.PerformerId)))),
                match)));

        foreach (var previous in previousLinks)
            condition = Expression.AndAlso(condition,
                Expression.NotEqual(Expression.Property(link, nameof(AudioPerformer.PerformerId)), Expression.Property(previous, nameof(AudioPerformer.PerformerId))));

        if (index + 1 < matchingLinks.Count)
            condition = Expression.AndAlso(condition, BuildDistinctAudioPerformerPredicate(audio, matchingLinks, index + 1, [.. previousLinks, link]));

        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(AudioPerformer)],
            Expression.Property(audio, nameof(Audio.AudioPerformers)),
            Expression.Lambda<Func<AudioPerformer, bool>>(condition, link));
    }

    public static async Task<IQueryable<Performer>> ApplyDistinctAudiosToPerformersAsync(
        CoveContext db,
        IQueryable<Performer> query,
        IReadOnlyList<RelatedFilterCriterion<AudioFilter>> criteria,
        CancellationToken ct = default)
    {
        if (criteria.Count < 2) return query;
        var matches = new List<Expression<Func<AudioPerformer, bool>>>(criteria.Count);
        foreach (var criterion in criteria)
        {
            if (HasAudioAppearanceCondition(criterion))
            {
                var appearances = await BuildMatchingPerformerAudioAppearancesAsync(db, criterion, ct);
                matches.Add(link => appearances.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId));
            }
            else
            {
                var matchingIds = await MatchingAudioIdsAsync(db, criterion, ct);
                matches.Add(link => matchingIds.Contains(link.AudioId));
            }
        }
        var performer = Expression.Parameter(typeof(Performer), "performer");
        var predicate = BuildDistinctPerformerAudioPredicate(performer, matches, 0, []);
        return query.Where(Expression.Lambda<Func<Performer, bool>>(predicate, performer));
    }

    private static Expression BuildDistinctPerformerAudioPredicate(
        ParameterExpression performer,
        IReadOnlyList<Expression<Func<AudioPerformer, bool>>> matches,
        int index,
        IReadOnlyList<ParameterExpression> previousLinks)
    {
        var link = Expression.Parameter(typeof(AudioPerformer), $"link{index}");
        var condition = new ParameterReplacer(matches[index].Parameters[0], link).Visit(matches[index].Body)!;
        foreach (var previous in previousLinks)
            condition = Expression.AndAlso(condition,
                Expression.NotEqual(Expression.Property(link, nameof(AudioPerformer.AudioId)), Expression.Property(previous, nameof(AudioPerformer.AudioId))));
        if (index + 1 < matches.Count)
            condition = Expression.AndAlso(condition, BuildDistinctPerformerAudioPredicate(performer, matches, index + 1, [.. previousLinks, link]));
        return Expression.Call(typeof(Enumerable), nameof(Enumerable.Any), [typeof(AudioPerformer)],
            Expression.Property(performer, nameof(Performer.AudioPerformers)),
            Expression.Lambda<Func<AudioPerformer, bool>>(condition, link));
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
        var hasPerformerCondition = HasEntityCondition(criterion);
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

    // Excludes a link that meets every condition, testing the link itself. Matching the conditions as a second
    // copy of the links and excluding its members let PostgreSQL build that copy from each matching tag
    // application's performer links before correlating it by host, which took seconds on a large library.
    private static IQueryable<TLink> WhereNotAll<TLink>(IQueryable<TLink> links, IEnumerable<Expression<Func<TLink, bool>>> conditions)
    {
        var link = Expression.Parameter(typeof(TLink), "link");
        Expression? all = null;
        foreach (var condition in conditions)
        {
            var body = new ParameterReplacer(condition.Parameters[0], link).Visit(condition.Body)!;
            all = all == null ? body : Expression.AndAlso(all, body);
        }

        // Every link meets an empty set of conditions, so none survives.
        return all == null
            ? links.Where(_ => false)
            : links.Where(Expression.Lambda<Func<TLink, bool>>(Expression.Not(all), link));
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

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
        var comparison = VideoPerformerAge.Comparison(criterion);
        return comparison == null ? links.Where(_ => false) : VideoPerformerAge.WhereLinks(links, comparison);
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
                links = WhereNotAll(links, ids.Select(id => (Expression<Func<VideoPerformer, bool>>)(link => link.PerformerId == id)));
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
                links = WhereNotAll(links, groups.Select(group => (Expression<Func<VideoPerformer, bool>>)(link => applications.Any(application =>
                    application.HostId == link.VideoId
                    && application.ContextId == link.PerformerId
                    && group.Contains(application.TagId)))));
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
        var visiblePerformerIds = await VisiblePerformerIdsAsync(db, ct);
        var matchingLinks = await BuildMatchingAudioPerformerLinksAsync(db, criterion, visiblePerformerIds, ct);
        return ApplyAudioPerformerLinkMatch(query, visiblePerformerIds, matchingLinks, Mode(criterion), UsesLegacyNone(criterion));
    }

    private static async Task<IQueryable<AudioPerformer>> BuildMatchingAudioPerformerLinksAsync(
        CoveContext db,
        RelatedFilterCriterion<PerformerFilter> criterion,
        IQueryable<int> visiblePerformerIds,
        CancellationToken ct)
    {
        var visibleLinks = db.Set<AudioPerformer>().Where(link => visiblePerformerIds.Contains(link.PerformerId));
        var hasPerformerCondition = HasEntityCondition(criterion);
        var performerIds = hasPerformerCondition ? await MatchingPerformerIdsAsync(db, criterion, ct) : null;
        var occurrenceCriterion = criterion.PerformerOccurrenceTagsCriterion;
        ExpandedHierarchyCriterion? expandedOccurrenceTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(occurrenceCriterion))
        {
            expandedOccurrenceTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, occurrenceCriterion!, ct);
            occurrenceCriterion = expandedOccurrenceTags.Criterion;
        }

        IQueryable<AudioPerformer> matchingLinks;
        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<AudioPerformer>? union = null;
            if (hasPerformerCondition)
                union = visibleLinks.Where(link => performerIds!.Contains(link.PerformerId));
            if (HasMultiIdCondition(criterion.PerformerIdsCriterion))
                union = Union(union, ApplyAudioPerformerIdCriterion(visibleLinks, criterion.PerformerIdsCriterion!));
            if (HasMultiIdCondition(occurrenceCriterion))
                union = Union(union, ApplyAudioPerformerOccurrenceTagCriterion(
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
                matchingLinks = ApplyAudioPerformerIdCriterion(matchingLinks, criterion.PerformerIdsCriterion!);
            if (HasMultiIdCondition(occurrenceCriterion))
                matchingLinks = ApplyAudioPerformerOccurrenceTagCriterion(
                    db,
                    matchingLinks,
                    occurrenceCriterion!,
                    expandedOccurrenceTags?.ValueGroups,
                    expandedOccurrenceTags?.RequiredIdGroups);
        }

        return matchingLinks;
    }

    private static IQueryable<Audio> ApplyAudioPerformerLinkMatch(
        IQueryable<Audio> query,
        IQueryable<int> visiblePerformerIds,
        IQueryable<AudioPerformer> matchingLinks,
        RelatedFilterMode mode,
        bool legacyNone)
        => mode switch
        {
            RelatedFilterMode.Every => query.Where(audio => audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId)
                    && !matchingLinks.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId))),
            RelatedFilterMode.None when !legacyNone => query.Where(audio => audio.AudioPerformers.Any(link => visiblePerformerIds.Contains(link.PerformerId))
                && !audio.AudioPerformers.Any(link => matchingLinks.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId))),
            RelatedFilterMode.None => query.Where(audio => !audio.AudioPerformers.Any(link => matchingLinks.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId))),
            _ => query.Where(audio => audio.AudioPerformers.Any(link => matchingLinks.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId))),
        };

    private static IQueryable<AudioPerformer> ApplyAudioPerformerIdCriterion(
        IQueryable<AudioPerformer> links,
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
                links = WhereNotAll(links, ids.Select(id => (Expression<Func<AudioPerformer, bool>>)(link => link.PerformerId == id)));
        }

        var excludedIds = criterion.Excludes?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (excludedIds.Length > 0)
            links = links.Where(link => !excludedIds.Contains(link.PerformerId));

        foreach (var requiredId in criterion.RequiredIds?.Where(id => id > 0).Distinct() ?? [])
            links = links.Where(link => link.PerformerId == requiredId);

        return links;
    }

    private static IQueryable<AudioPerformer> ApplyAudioPerformerOccurrenceTagCriterion(
        CoveContext db,
        IQueryable<AudioPerformer> links,
        MultiIdCriterion criterion,
        IReadOnlyList<int[]>? valueGroups,
        IReadOnlyList<int[]>? requiredIdGroups)
    {
        var applications = db.TagApplications.AsNoTracking()
            .Where(application => application.HostType == AffinityHostType.Audio
                && application.ContextType == "performer"
                && application.ContextId != null);
        var groups = valueGroups?.Where(group => group.Length > 0).ToArray()
            ?? criterion.Value.Where(tagId => tagId > 0).Select(tagId => new[] { tagId }).ToArray();
        var tagIds = groups.SelectMany(group => group).Distinct().ToArray();

        if (criterion.Modifier == CriterionModifier.IsNull)
            links = links.Where(link => !applications.Any(application =>
                application.HostId == link.AudioId && application.ContextId == link.PerformerId));
        else if (criterion.Modifier == CriterionModifier.NotNull)
            links = links.Where(link => applications.Any(application =>
                application.HostId == link.AudioId && application.ContextId == link.PerformerId));
        else if (tagIds.Length > 0)
        {
            if (criterion.Modifier == CriterionModifier.Includes)
                links = links.Where(link => applications.Any(application =>
                    application.HostId == link.AudioId
                    && application.ContextId == link.PerformerId
                    && tagIds.Contains(application.TagId)));
            else if (criterion.Modifier == CriterionModifier.IncludesAll)
            {
                foreach (var group in groups)
                    links = links.Where(link => applications.Any(application =>
                        application.HostId == link.AudioId
                        && application.ContextId == link.PerformerId
                        && group.Contains(application.TagId)));
            }
            else if (criterion.Modifier == CriterionModifier.Excludes)
                links = links.Where(link => !applications.Any(application =>
                    application.HostId == link.AudioId
                    && application.ContextId == link.PerformerId
                    && tagIds.Contains(application.TagId)));
            else if (criterion.Modifier == CriterionModifier.ExcludesAll)
                links = WhereNotAll(links, groups.Select(group => (Expression<Func<AudioPerformer, bool>>)(link => applications.Any(application =>
                    application.HostId == link.AudioId
                    && application.ContextId == link.PerformerId
                    && group.Contains(application.TagId)))));
        }

        var excludedTagIds = criterion.Excludes?.Where(tagId => tagId > 0).Distinct().ToArray() ?? [];
        if (excludedTagIds.Length > 0)
            links = links.Where(link => !applications.Any(application =>
                application.HostId == link.AudioId
                && application.ContextId == link.PerformerId
                && excludedTagIds.Contains(application.TagId)));

        var requiredGroups = requiredIdGroups is { Count: > 0 }
            ? requiredIdGroups
            : criterion.RequiredIds?.Where(tagId => tagId > 0).Distinct().Select(tagId => new[] { tagId }).ToArray() ?? [];
        foreach (var group in requiredGroups)
            links = links.Where(link => applications.Any(application =>
                application.HostId == link.AudioId
                && application.ContextId == link.PerformerId
                && group.Contains(application.TagId)));

        return links;
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
        if (HasVideoAppearanceCondition(criterion))
        {
            var appearances = VisibleVideoAppearances(db, await VisibleVideoIdsAsync(db, ct));
            var matchingAppearances = await BuildMatchingPerformerVideoAppearancesAsync(db, criterion, appearances, ct);
            return ApplyAppearanceMatch(
                query,
                appearances.Select(link => link.PerformerId).Distinct(),
                matchingAppearances.Select(link => link.PerformerId).Distinct(),
                appearances
                    .Where(link => !matchingAppearances.Any(match => match.VideoId == link.VideoId && match.PerformerId == link.PerformerId))
                    .Select(link => link.PerformerId)
                    .Distinct(),
                Mode(criterion),
                UsesLegacyNone(criterion));
        }

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

        var visibleVideoIds = await VisibleVideoIdsAsync(db, ct);
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
        if (HasAudioAppearanceCondition(criterion))
        {
            var appearances = await VisibleAudioAppearancesAsync(db, ct);
            var matchingAppearances = await BuildMatchingPerformerAudioAppearancesAsync(db, criterion, appearances, ct);
            return ApplyAppearanceMatch(
                query,
                appearances.Select(link => link.PerformerId).Distinct(),
                matchingAppearances.Select(link => link.PerformerId).Distinct(),
                appearances
                    .Where(link => !matchingAppearances.Any(match => match.AudioId == link.AudioId && match.PerformerId == link.PerformerId))
                    .Select(link => link.PerformerId)
                    .Distinct(),
                Mode(criterion),
                UsesLegacyNone(criterion));
        }

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

    // An appearance is one performer's link to one related video or audio. Conditions on it, such as the tags on
    // that performer's occurrence, must hold for the performer being listed rather than for anyone on the host, so
    // the performer side matches appearances instead of host ids whenever a criterion carries such a condition.
    private static bool HasVideoAppearanceCondition(RelatedFilterCriterion<VideoFilter> criterion)
        => criterion.AgeAtHostDateCriterion != null || HasMultiIdCondition(criterion.PerformerOccurrenceTagsCriterion);

    private static bool HasAudioAppearanceCondition(RelatedFilterCriterion<AudioFilter> criterion)
        => HasMultiIdCondition(criterion.PerformerOccurrenceTagsCriterion);

    private static async Task<IQueryable<int>> VisibleVideoIdsAsync(CoveContext db, CancellationToken ct)
    {
        var visibleVideos = await new VideoRepository(db).BuildFilteredQueryAsync(
            null,
            null,
            includeRelatedFilters: false,
            allowReadScopeOptimization: false,
            ct: ct);
        return visibleVideos.Select(video => video.Id);
    }

    private static IQueryable<VideoPerformer> VisibleVideoAppearances(CoveContext db, IQueryable<int> visibleVideoIds)
        => db.Set<VideoPerformer>().AsNoTracking().Where(link => visibleVideoIds.Contains(link.VideoId));

    private static async Task<IQueryable<AudioPerformer>> VisibleAudioAppearancesAsync(CoveContext db, CancellationToken ct)
    {
        var visibleAudios = await AudioFilterQuery.BuildAsync(db, null, null, includeRelatedFilters: false, ct);
        var visibleAudioIds = visibleAudios.Select(audio => audio.Id);
        return db.Set<AudioPerformer>().AsNoTracking().Where(link => visibleAudioIds.Contains(link.AudioId));
    }

    private static async Task<IQueryable<VideoPerformer>> BuildMatchingPerformerVideoAppearancesAsync(
        CoveContext db,
        RelatedFilterCriterion<VideoFilter> criterion,
        IQueryable<VideoPerformer> appearances,
        CancellationToken ct)
    {
        var matchingVideoIds = HasEntityCondition(criterion) ? await MatchingVideoIdsAsync(db, criterion, false, ct) : null;
        var occurrenceCriterion = criterion.PerformerOccurrenceTagsCriterion;
        var hasOccurrenceCondition = HasMultiIdCondition(occurrenceCriterion);
        ExpandedHierarchyCriterion? expandedOccurrenceTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(occurrenceCriterion))
        {
            expandedOccurrenceTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, occurrenceCriterion!, ct);
            occurrenceCriterion = expandedOccurrenceTags.Criterion;
        }

        if (criterion.ConditionOperator == RelatedFilterConditionOperator.Or)
        {
            IQueryable<VideoPerformer>? union = null;
            if (matchingVideoIds != null)
                union = appearances.Where(link => matchingVideoIds.Contains(link.VideoId));
            if (criterion.AgeAtHostDateCriterion != null)
                union = Union(union, ApplyVideoPerformerAgeCriterion(appearances, criterion.AgeAtHostDateCriterion));
            if (hasOccurrenceCondition)
                union = Union(union, ApplyVideoPerformerOccurrenceTagCriterion(
                    db,
                    appearances,
                    occurrenceCriterion!,
                    expandedOccurrenceTags?.ValueGroups,
                    expandedOccurrenceTags?.RequiredIdGroups));
            return union ?? appearances;
        }

        var matching = appearances;
        if (criterion.AgeAtHostDateCriterion != null)
            matching = ApplyVideoPerformerAgeCriterion(matching, criterion.AgeAtHostDateCriterion);
        if (hasOccurrenceCondition)
            matching = ApplyVideoPerformerOccurrenceTagCriterion(
                db,
                matching,
                occurrenceCriterion!,
                expandedOccurrenceTags?.ValueGroups,
                expandedOccurrenceTags?.RequiredIdGroups);
        return matchingVideoIds == null
            ? matching
            : IntersectAppearances(matching, appearances.Where(link => matchingVideoIds.Contains(link.VideoId)));
    }

    // Intersects the appearances meeting the appearance conditions with the appearances on matching hosts rather
    // than testing both on one link. PostgreSQL estimates the occurrence-tag conditions at a row or two; planned
    // together it then rescanned every matching video per appearance, taking 16 seconds for a video tag filter on a
    // 32,000-video library, and resolving the video ids into an array first was no better. Each side of a set
    // operation is planned on its own, where both are fast.
    private static IQueryable<TLink> IntersectAppearances<TLink>(IQueryable<TLink> meetingAppearanceConditions, IQueryable<TLink> onMatchingHosts)
        => meetingAppearanceConditions.Intersect(onMatchingHosts);

    private static async Task<IQueryable<AudioPerformer>> BuildMatchingPerformerAudioAppearancesAsync(
        CoveContext db,
        RelatedFilterCriterion<AudioFilter> criterion,
        CancellationToken ct)
        => await BuildMatchingPerformerAudioAppearancesAsync(db, criterion, await VisibleAudioAppearancesAsync(db, ct), ct);

    private static async Task<IQueryable<AudioPerformer>> BuildMatchingPerformerAudioAppearancesAsync(
        CoveContext db,
        RelatedFilterCriterion<AudioFilter> criterion,
        IQueryable<AudioPerformer> appearances,
        CancellationToken ct)
    {
        var matchingAudioIds = HasEntityCondition(criterion) ? await MatchingAudioIdsAsync(db, criterion, ct) : null;
        var occurrenceCriterion = criterion.PerformerOccurrenceTagsCriterion;
        var hasOccurrenceCondition = HasMultiIdCondition(occurrenceCriterion);
        ExpandedHierarchyCriterion? expandedOccurrenceTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(occurrenceCriterion))
        {
            expandedOccurrenceTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, occurrenceCriterion!, ct);
            occurrenceCriterion = expandedOccurrenceTags.Criterion;
        }

        var occurrenceMatches = hasOccurrenceCondition
            ? ApplyAudioPerformerOccurrenceTagCriterion(
                db,
                appearances,
                occurrenceCriterion!,
                expandedOccurrenceTags?.ValueGroups,
                expandedOccurrenceTags?.RequiredIdGroups)
            : appearances;
        if (matchingAudioIds == null)
            return occurrenceMatches;

        var audioMatches = appearances.Where(link => matchingAudioIds.Contains(link.AudioId));
        return criterion.ConditionOperator == RelatedFilterConditionOperator.Or
            ? audioMatches.Union(occurrenceMatches)
            : IntersectAppearances(occurrenceMatches, audioMatches);
    }

    /// <summary>
    /// Keeps performers by their appearances. Every and None require at least one visible appearance, as the
    /// host-id modes do; the legacy None does not.
    /// </summary>
    private static IQueryable<Performer> ApplyAppearanceMatch(
        IQueryable<Performer> query,
        IQueryable<int> performersWithAppearances,
        IQueryable<int> performersWithMatchingAppearances,
        IQueryable<int> performersWithNonMatchingAppearances,
        RelatedFilterMode mode,
        bool legacyNone)
        => mode switch
        {
            RelatedFilterMode.Every => query.Where(performer => performersWithAppearances.Contains(performer.Id)
                && !performersWithNonMatchingAppearances.Contains(performer.Id)),
            RelatedFilterMode.None when !legacyNone => query.Where(performer => performersWithAppearances.Contains(performer.Id)
                && !performersWithMatchingAppearances.Contains(performer.Id)),
            RelatedFilterMode.None => query.Where(performer => !performersWithMatchingAppearances.Contains(performer.Id)),
            _ => query.Where(performer => performersWithMatchingAppearances.Contains(performer.Id)),
        };

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

    private static bool HasEntityCondition<TFilter>(RelatedFilterCriterion<TFilter> criterion) where TFilter : class
        => !string.IsNullOrWhiteSpace(criterion.FindFilter?.Q)
            || criterion.ObjectFilter != null && typeof(TFilter).GetProperties().Any(property => HasFilterValue(property.GetValue(criterion.ObjectFilter)));

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
