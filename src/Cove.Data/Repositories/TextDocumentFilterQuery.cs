using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

/// <summary>Builds the filtered, unsorted text document query the way <see cref="AudioFilterQuery"/> does for audios.</summary>
public static class TextDocumentFilterQuery
{
    public static async Task<IQueryable<TextDocument>> BuildAsync(
        CoveContext db,
        TextDocumentFilter? filter,
        FindFilter? findFilter,
        bool includeRelatedFilters = true,
        CancellationToken ct = default,
        FilterExpression<TextDocumentFilter>? expression = null)
    {
        using var relativeDates = RelativeDateEvaluation.Begin();
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

        ExpandedHierarchyCriterion? expandedPerformerTags = null;
        if (HierarchicalCriterionExpander.RequiresExpansion(filter?.PerformerTagsCriterion))
        {
            expandedPerformerTags = await HierarchicalCriterionExpander.ExpandTagsAsync(db, filter!.PerformerTagsCriterion!, ct);
            filter.PerformerTagsCriterion = expandedPerformerTags.Criterion;
        }

        var textBase = db.TextDocuments.AsNoTracking().AsQueryable();
        var textQuery = FullTextSearchHelpers.Apply(db, textBase, findFilter?.Q,
            text => text.Title, text => text.Code, text => text.Details,
            text => text.FileSearchText, text => text.SearchText);
        var query = FullTextSearchHelpers.ApplyRelationalMatches(textQuery, textBase, findFilter?.Q,
            tagSelectors: [text => text.TextTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [text => text.TextPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        query = FullTextSearchHelpers.ApplyFilePathMatch(query, textBase, findFilter?.Q, text => text.Files);
        query = ApplyFilter(db, query, filter, expandedTags?.ValueGroups, expandedTags?.RequiredIdGroups, expandedStudios?.ValueGroups, expandedStudios?.RequiredIdGroups, expandedPerformerTags?.ValueGroups);
        query = includeRelatedFilters
            ? await RelatedFilterQuery.ApplyToTextsAsync(db, query, filter?.PerformerFilterCriterion, ct)
            : query;
        // No distinct-performer assignment exists for texts, so a Distinct related scope is refused by the
        // shared walker rather than silently treated as Reuse. Leaves build their own queries, so OR, NOT and
        // just-one branches start from every readable text rather than repeating the conditions above.
        return await FilterExpressionQuery.ApplyAsync(textBase, query, expression, async (source, leaf) =>
        {
            var matches = await BuildAsync(db, leaf, null, includeRelatedFilters, ct);
            return source.Where(text => matches.Any(match => match.Id == text.Id));
        });
    }

    private static IQueryable<TextDocument> ApplyFilter(CoveContext db, IQueryable<TextDocument> query, TextDocumentFilter? filter, IReadOnlyList<int[]>? hierarchicalTagGroups, IReadOnlyList<int[]>? requiredTagGroups, IReadOnlyList<int[]>? hierarchicalStudioGroups, IReadOnlyList<int[]>? requiredStudioGroups, IReadOnlyList<int[]>? performerTagGroups)
    {
        if (filter == null)
            return query;

        query = EngagementQueryHelpers.ApplyRatingCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), RatingHostType.Text, filter.RatingCriterion);
        query = EngagementQueryHelpers.ApplyFavoriteCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, filter.FavoriteCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.ViewCount), filter.PlayCountCriterion);
        query = EngagementQueryHelpers.ApplyAffinityIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LikeCount), filter.LikeCounterCriterion);
        query = EngagementQueryHelpers.ApplyAffinityDoubleAsIntCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.TotalConsumedSec), filter.PlayDurationCriterion);
        query = EngagementQueryHelpers.ApplyAffinityTimestampCriterion(db, query, EngagementQueryHelpers.CurrentUserId(db), AffinityHostType.Text, nameof(UserEntityAffinity.LastConsumedAt), filter.LastReadAtCriterion);
        query = FilterHelpers.ApplyString(query, filter.TitleCriterion, text => text.Title);
        query = FilterHelpers.ApplyString(query, filter.CodeCriterion, text => text.Code);
        query = FilterHelpers.ApplyString(query, filter.DetailsCriterion, text => text.Details);
        query = FilterHelpers.ApplyString(query, filter.ContentCriterion, text => text.SearchText);
        query = FilterHelpers.ApplyFilePath(query, filter.PathCriterion, text => text.Files);
        query = ApplyTextFileStringCriterion(query, filter.FormatCriterion);
        query = FilterHelpers.ApplyStringCollection(query, filter.UrlCriterion, text => text.Urls.Select(url => url.Url));
        query = FilterHelpers.ApplyBool(query, filter.OrganizedCriterion, text => text.Organized);
        query = FilterHelpers.ApplyBool(query, filter.HasCoverCriterion, text => text.ImageBlobId != null && text.ImageBlobId != string.Empty);
        query = FilterHelpers.ApplyDate(query, filter.DateCriterion, text => text.Date);
        query = FilterHelpers.ApplyInt(query, filter.WordCountCriterion, text => text.MaxWordCount ?? 0);
        query = FilterHelpers.ApplyInt(query, filter.PageCountCriterion, text => text.MaxPageCount ?? 0);
        query = FilterHelpers.ApplyLong(query, filter.FileSizeCriterion, text => text.MaxFileSize);
        query = FilterHelpers.ApplyNullableTimestamp(query, filter.FileModTimeCriterion, text => text.MaxFileModTime);
        query = FilterHelpers.ApplyInt(query, filter.FileCountCriterion, text => text.FileCount);
        query = FilterHelpers.ApplyInt(query, filter.TagCountCriterion, text => text.TextTags.Count);
        query = FilterHelpers.ApplyInt(query, filter.PerformerCountCriterion, text => text.TextPerformers.Count);
        query = FilterHelpers.ApplyMultiId(query, filter.TagsCriterion, text => text.TextTags.Select(link => link.TagId), hierarchicalTagGroups, requiredTagGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.PerformersCriterion, text => text.TextPerformers.Select(link => link.PerformerId));
        query = PerformerOccurrenceTagQuery.Apply(db, query, AffinityHostType.Text, filter.PerformerTagsCriterion, GetIncludedPerformerIds(filter), performerTagGroups);
        query = FilterHelpers.ApplyStudioCriterion(query, filter.StudiosCriterion, text => text.StudioId, hierarchicalStudioGroups, requiredStudioGroups);
        query = FilterHelpers.ApplyMultiId(query, filter.GroupsCriterion, text => db.GroupItems
            .Where(item => item.HostType == "text" && item.HostId == text.Id && item.Kind == GroupItemKind.Text)
            .Select(item => item.GroupId));
        query = FilterHelpers.ApplyTimestamp(query, filter.CreatedAtCriterion, text => text.CreatedAt);
        query = FilterHelpers.ApplyTimestamp(query, filter.UpdatedAtCriterion, text => text.UpdatedAt);
        query = query.ApplyCustomFieldCriteria(db, CustomFieldEntityTypes.Text, filter.CustomFieldCriterion, filter.CustomFieldCriteria);

        return query;
    }

    private static IQueryable<TextDocument> ApplyTextFileStringCriterion(IQueryable<TextDocument> query, StringCriterion? criterion)
        => FilterHelpers.ApplyStringCollection(query, criterion, text => text.Files.Select(file => file.Format));

    private static int[] GetIncludedPerformerIds(TextDocumentFilter filter)
    {
        var ids = new HashSet<int>();
        if (filter.PerformersCriterion?.Value is { Count: > 0 }
            && filter.PerformersCriterion.Modifier is CriterionModifier.Includes or CriterionModifier.IncludesAll)
            ids.UnionWith(filter.PerformersCriterion.Value.Where(id => id > 0));

        if (filter.PerformersCriterion?.RequiredIds is { Count: > 0 })
            ids.UnionWith(filter.PerformersCriterion.RequiredIds.Where(id => id > 0));

        return ids.ToArray();
    }
}
