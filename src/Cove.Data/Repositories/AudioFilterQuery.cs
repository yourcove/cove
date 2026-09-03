using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public static class AudioFilterQuery
{
    public static async Task<IQueryable<Audio>> BuildAsync(
        CoveContext db,
        AudioFilter? filter,
        FindFilter? findFilter,
        bool includeRelatedFilters = true,
        CancellationToken ct = default)
    {
        ExpandedHierarchyCriterion? expandedTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.TagsCriterion))
        {
            expandedTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, filter!.TagsCriterion!, ct);
            filter.TagsCriterion = expandedTags.Criterion;
        }

        ExpandedHierarchyCriterion? expandedStudios = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.StudiosCriterion))
        {
            expandedStudios = await HierarchicalCriterionExpander.ExpandStudiosAsync(db, filter!.StudiosCriterion!, ct);
            filter.StudiosCriterion = expandedStudios.Criterion;
        }

        var audioBase = db.Audios.AsNoTracking().AsQueryable();
        var audioText = FullTextSearchHelpers.Apply(db, audioBase, findFilter?.Q,
            audio => audio.Title, audio => audio.Code, audio => audio.Details,
            audio => audio.FileSearchText, audio => audio.SearchText);
        var query = FullTextSearchHelpers.ApplyRelationalMatches(audioText, audioBase, findFilter?.Q,
            tagSelectors: [audio => audio.AudioTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [audio => audio.AudioPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, audioBase, findFilter?.Q, audio => audio.Files);
        query = ApplyFilter(db, query, filter, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups);

        return includeRelatedFilters
            ? await RelatedFilterQuery.ApplyToAudiosAsync(db, query, filter?.PerformerFilterCriterion, ct)
            : query;
    }

    private static IQueryable<Audio> ApplyFilter(CoveContext db, IQueryable<Audio> query, AudioFilter? filter, IReadOnlyList<int[]>? tagGroups, IReadOnlyList<int[]>? requiredTagGroups, IReadOnlyList<int[]>? studioGroups, IReadOnlyList<int[]>? requiredStudioGroups)
    {
        if (filter == null) return query;
        var userId = EngagementQueryHelpers.CurrentUserId(db);
        query = EngagementQueryHelpers.ApplyRatingCriterion(db, query, userId, RatingHostType.Audio, filter.RatingCriterion);
        query = EngagementQueryHelpers.ApplyFavoriteCriterion(db, query, userId, AffinityHostType.Audio, filter.FavoriteCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, userId, AffinityHostType.Audio, nameof(UserEntityAffinity.ViewCount), filter.PlayCountCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, userId, AffinityHostType.Audio, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
        query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(db, query, userId, AffinityHostType.Audio, nameof(UserEntityAffinity.TotalConsumedSec), filter.PlayDurationCriterion);
        query = EngagementQueryHelpers.ApplyAffinityTimestampCriterion(db, query, userId, AffinityHostType.Audio, nameof(UserEntityAffinity.LastConsumedAt), filter.LastPlayedAtCriterion);
        query = FilterHelpers.ApplyString(query, filter.TitleCriterion, audio => audio.Title);
        query = FilterHelpers.ApplyString(query, filter.CodeCriterion, audio => audio.Code);
        query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, audio => audio.Details);
        query = FilterHelpers.ApplyFilePath(query, filter.PathCriterion, audio => audio.Files);
        query = FilterHelpers.ApplyStringCollection(query, filter.FormatCriterion, audio => audio.Files.Select(file => file.Format));
        query = FilterHelpers.ApplyStringCollection(query, filter.AudioCodecCriterion, audio => audio.Files.Select(file => file.AudioCodec));
        query = FilterHelpers.ApplyStringCollection(query, filter.UrlCriterion, audio => audio.Urls.Select(url => url.Url));
        query = FilterHelpers.ApplyBool(query, filter.OrganizedCriterion, audio => audio.Organized);
        query = FilterHelpers.ApplyBool(query, filter.HasVideoFilesCriterion, audio => audio.HasVideoFiles);
        query = FilterHelpers.ApplyBool(query, filter.HasCoverCriterion, audio => audio.ImageBlobId != null && audio.ImageBlobId != string.Empty);
        query = FilterHelpers.ApplyDate(query, filter.DateCriterion, audio => audio.Date);
        query = FilterHelpers.ApplyInt(query, filter.DurationCriterion, audio => (int)audio.MaxDuration);
        query = FilterHelpers.ApplyLong(query, filter.BitRateCriterion, audio => audio.MaxBitRate);
        query = FilterHelpers.ApplyLong(query, filter.FileSizeCriterion, audio => audio.MaxFileSize);
        query = FilterHelpers.ApplyNullableTimestamp(query, filter.FileModTimeCriterion, audio => audio.MaxFileModTime);
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, audio => audio.FileCount);
        query = FilterHelpers.ApplyInt(query, filter.TrackCountCriterion, audio => audio.Tracks.Count);
        query = FilterHelpers.ApplyStringCollection(query, filter.TrackTitleCriterion, audio => audio.Tracks.Select(track => track.Title));
        query = FilterHelpers.ApplyInt(query, filter.SampleRateCriterion, audio => audio.Files.Max(file => file.SampleRate) ?? 0);
        query = FilterHelpers.ApplyInt(query, filter.ChannelsCriterion, audio => audio.Files.Max(file => file.Channels) ?? 0);
        query = ApplyEffectiveTagCountCriterion(db, query, filter.TagCountCriterion);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, audio => audio.AudioPerformers.Count);
        query = ApplyAudioTagCriterion(db, query, filter.TagsCriterion, tagGroups, requiredTagGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, audio => audio.AudioPerformers.Select(link => link.PerformerId));
        query = ApplyPerformerOccurrenceTagCriterion(db, query, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter));
        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, audio => audio.StudioId, studioGroups, requiredStudioGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.GroupsCriterion, audio => db.GroupItems.Where(item => item.HostType == "audio" && item.HostId == audio.Id && item.Kind == GroupItemKind.Audio).Select(item => item.GroupId));
        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, audio => audio.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, audio => audio.UpdatedAt);
        return query.ApplyCustomFieldCriteria(db, CustomFieldEntityTypes.Audio, filter.CustomFieldCriterion, filter.CustomFieldCriteria);
    }

    private static IQueryable<Audio> ApplyEffectiveTagCountCriterion(CoveContext db, IQueryable<Audio> query, IntCriterion? criterion)
    {
        if (criterion == null) return query;
        var tags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio);
        return FilterHelpers.ApplyInt(query, criterion, audio => tags.Where(tag => tag.HostId == audio.Id).Select(tag => tag.TagId).Distinct().Count());
    }

    private static IQueryable<Audio> ApplyAudioTagCriterion(CoveContext db, IQueryable<Audio> query, MultiIdCriterion? criterion, IReadOnlyList<int[]>? valueGroups, IReadOnlyList<int[]>? requiredGroups)
    {
        if (criterion == null) return query;
        var tags = EffectiveHostTagQuery.ForHostType(db, AffinityHostType.Audio);
        if (criterion.Modifier == CriterionModifier.IsNull) query = query.Where(audio => !tags.Any(tag => tag.HostId == audio.Id));
        else if (criterion.Modifier == CriterionModifier.NotNull) query = query.Where(audio => tags.Any(tag => tag.HostId == audio.Id));
        else
        {
            var ids = criterion.Value.Where(id => id > 0).Distinct().ToArray();
            if (ids.Length > 0)
                query = criterion.Modifier switch
                {
                    CriterionModifier.Excludes => query.Where(audio => !tags.Any(tag => tag.HostId == audio.Id && ids.Contains(tag.TagId))),
                    CriterionModifier.ExcludesAll when valueGroups is { Count: > 0 } => ApplyTagGroups(query, tags, valueGroups, true),
                    CriterionModifier.IncludesAll when valueGroups is { Count: > 0 } => ApplyTagGroups(query, tags, valueGroups, false),
                    CriterionModifier.ExcludesAll => ApplyTagGroups(query, tags, ids.Select(id => new[] { id }).ToArray(), true),
                    CriterionModifier.IncludesAll => ApplyTagGroups(query, tags, ids.Select(id => new[] { id }).ToArray(), false),
                    _ => query.Where(audio => tags.Any(tag => tag.HostId == audio.Id && ids.Contains(tag.TagId))),
                };
        }
        var excluded = criterion.Excludes?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (excluded.Length > 0) query = query.Where(audio => !tags.Any(tag => tag.HostId == audio.Id && excluded.Contains(tag.TagId)));
        var required = criterion.RequiredIds?.Where(id => id > 0).Distinct().Select(id => new[] { id }).ToArray() ?? [];
        if (required.Length > 0) query = ApplyTagGroups(query, tags, required, false);
        if (requiredGroups is { Count: > 0 }) query = ApplyTagGroups(query, tags, requiredGroups, false);
        return query;
    }

    private static IQueryable<Audio> ApplyTagGroups(IQueryable<Audio> query, IQueryable<EffectiveHostTagRow> tags, IReadOnlyList<int[]> groups, bool excludeAll)
    {
        var matching = query;
        foreach (var group in groups)
        {
            var ids = group.Distinct().ToArray();
            matching = matching.Where(audio => tags.Any(tag => tag.HostId == audio.Id && ids.Contains(tag.TagId)));
        }
        return excludeAll ? query.Where(audio => !matching.Any(match => match.Id == audio.Id)) : matching;
    }

    private static int[] GetIncludedPerformerIds(AudioFilter filter)
    {
        var ids = new HashSet<int>();
        if (filter.PerformersCriterion?.Value is { Count: > 0 } && filter.PerformersCriterion.Modifier is CriterionModifier.Includes or CriterionModifier.IncludesAll) ids.UnionWith(filter.PerformersCriterion.Value.Where(id => id > 0));
        if (filter.PerformersCriterion?.RequiredIds is { Count: > 0 }) ids.UnionWith(filter.PerformersCriterion.RequiredIds.Where(id => id > 0));
        return ids.ToArray();
    }

    private static IQueryable<Audio> ApplyPerformerOccurrenceTagCriterion(CoveContext db, IQueryable<Audio> query, MultiIdCriterion? criterion, IReadOnlyCollection<int> performerIds)
    {
        if (criterion == null) return query;
        var included = criterion.Value.Where(id => id > 0).Distinct().ToArray();
        var excluded = criterion.Excludes?.Where(id => id > 0).Distinct().ToArray() ?? [];
        if (included.Length == 0 && excluded.Length == 0) return query;
        var applications = db.TagApplications.AsNoTracking().Where(item => item.HostType == AffinityHostType.Audio && item.ContextType == "performer" && item.ContextId != null);
        if (performerIds.Count > 0) { var ids = performerIds.ToArray(); applications = applications.Where(item => item.ContextId != null && ids.Contains(item.ContextId.Value)); }
        if (included.Length > 0)
        {
            if (criterion.Modifier == CriterionModifier.Excludes) query = query.Where(audio => !applications.Any(item => item.HostId == audio.Id && included.Contains(item.TagId)));
            else if (criterion.Modifier is CriterionModifier.IncludesAll or CriterionModifier.ExcludesAll)
            {
                var matching = query;
                foreach (var tagId in included) matching = matching.Where(audio => applications.Any(item => item.HostId == audio.Id && item.TagId == tagId));
                query = criterion.Modifier == CriterionModifier.ExcludesAll ? query.Where(audio => !matching.Select(item => item.Id).Contains(audio.Id)) : matching;
            }
            else query = query.Where(audio => applications.Any(item => item.HostId == audio.Id && included.Contains(item.TagId)));
        }
        if (excluded.Length > 0) query = query.Where(audio => !applications.Any(item => item.HostId == audio.Id && excluded.Contains(item.TagId)));
        return query;
    }
}
