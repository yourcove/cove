using System.Linq.Expressions;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Cove.Data.Repositories;

/// <summary>
/// Resolves the small related-entity dictionaries once, then searches the indexed ID arrays
/// on videos; gallery and group members and path matches resolve to video IDs when there are
/// few enough. Match predicates and scores otherwise contain no per-video subqueries.
/// </summary>
internal sealed class VideoTextSearch(CoveContext db, string search, IReadOnlyList<VideoTextSearch.Term> terms,
    ILookup<int, int>? galleryMembers = null, ILookup<int, int>? groupMembers = null, int[]? pathVideos = null)
{
    internal sealed record Term(string Text, int[] ExactTags, int[] PartialTags,
        int[] ExactPerformers, int[] PartialPerformers, int[] ExactStudios, int[] PartialStudios,
        int[] ExactGalleries, int[] PartialGalleries, int[] ExactGroups, int[] PartialGroups);

    // Member ids are sent as query parameters, several times per term. A short token can match
    // most gallery or group names, so above this many members the search tests the link tables.
    // Not a result limit: every member still matches, only through a subquery.
    internal const int MemberSubqueryThreshold = 20_000;

    // Path matches are resolved the same way. A common fragment such as a file extension matches
    // nearly every video, so above this many the search tests the files table directly,
    // still matching all of them.
    internal const int PathSubqueryThreshold = 100_000;

    private sealed class NameRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
    }
    private sealed class MemberRow
    {
        public int OwnerId { get; init; }
        public int VideoId { get; init; }
    }
    private bool IsPostgres => db.Database.IsNpgsql();

    internal static async Task<VideoTextSearch> CreateAsync(CoveContext db, string search, CancellationToken ct,
        int memberSubqueryThreshold = MemberSubqueryThreshold, int pathSubqueryThreshold = PathSubqueryThreshold)
    {
        var tokens = FullTextSearchHelpers.TokenizeSearchTerms(search);
        if (tokens.Count == 0)
            return new(db, search, []);

        // Apply entity authorization before resolving names and aliases. Never cache these
        // results across requests or principals.
        var tags = await db.Tags.AsNoTracking()
            .Select(tag => new NameRow { Id = tag.Id, Name = tag.Name })
            .Concat(db.Tags.SelectMany(tag => tag.Aliases.Select(alias => new NameRow { Id = alias.TagId, Name = alias.Alias })))
            .Where(row => tokens.Any(token => row.Name.ToLower().Contains(token))).ToListAsync(ct);
        var performers = await db.Performers.AsNoTracking()
            .Select(performer => new NameRow { Id = performer.Id, Name = performer.Name })
            .Concat(db.Performers.SelectMany(performer => performer.Aliases.Select(alias => new NameRow { Id = alias.PerformerId, Name = alias.Alias })))
            .Where(row => tokens.Any(token => row.Name.ToLower().Contains(token))).ToListAsync(ct);
        var studios = await db.Studios.AsNoTracking().Select(studio => new NameRow { Id = studio.Id, Name = studio.Name })
            .Where(row => tokens.Any(token => row.Name.ToLower().Contains(token))).ToListAsync(ct);
        var galleries = await db.Galleries.AsNoTracking().Where(gallery => gallery.Title != null)
            .Select(gallery => new NameRow { Id = gallery.Id, Name = gallery.Title! })
            .Where(row => tokens.Any(token => row.Name.ToLower().Contains(token))).ToListAsync(ct);
        var groups = await db.Groups.AsNoTracking().Select(group => new NameRow { Id = group.Id, Name = group.Name })
            .Where(row => tokens.Any(token => row.Name.ToLower().Contains(token))).ToListAsync(ct);

        // Gallery and group membership lives in link tables. Resolve member videos here so the
        // search tests the video row (Id = ANY) instead of an EXISTS that defeats the index BitmapOr.
        var galleryIds = galleries.Select(row => row.Id).Distinct().ToArray();
        var galleryMembers = await ResolveMembersAsync(galleryIds, db.Set<VideoGallery>().AsNoTracking()
            .Where(link => galleryIds.Contains(link.GalleryId))
            .Select(link => new MemberRow { OwnerId = link.GalleryId, VideoId = link.VideoId }), memberSubqueryThreshold, ct);
        var groupIds = groups.Select(row => row.Id).Distinct().ToArray();
        var groupMembers = await ResolveMembersAsync(groupIds, db.GroupItems.AsNoTracking()
            .Where(item => item.VideoId != null && groupIds.Contains(item.GroupId))
            .Select(item => new MemberRow { OwnerId = item.GroupId, VideoId = item.VideoId!.Value }), memberSubqueryThreshold, ct);

        // Path substrings have no index to test them on the video row, and an OR with a files subquery
        // stops PostgreSQL combining the indexed arms. Find the videos with a file containing every
        // term once, so the search can test Id = ANY like the related-name matches. The cap lookups
        // only count rows, so their order is irrelevant; it keeps EF from warning about an unordered Take.
        var matchingFiles = db.VideoFiles.AsNoTracking().Where(file => file.VideoId != null);
        foreach (var token in tokens)
            matchingFiles = matchingFiles.Where(file => file.Path.ToLower().Contains(token));
        var pathVideos = await matchingFiles.Select(file => file.VideoId!.Value).Distinct()
            .OrderBy(videoId => videoId).Take(pathSubqueryThreshold + 1).ToArrayAsync(ct);

        // Use the original token order (including repeated words) for phrase recognition.
        var normalizedQuery = " " + NormalizeWords(search) + " ";
        int[] Matches(List<NameRow> rows, string token, bool exact, bool wholeWord = false)
            => rows.Where(row =>
            {
                var name = NormalizeWords(row.Name);
                var matchesToken = wholeWord
                    ? (" " + name + " ").Contains(" " + token + " ", StringComparison.Ordinal)
                    : row.Name.Contains(token, StringComparison.OrdinalIgnoreCase);
                return matchesToken && (!exact || (name.Length > 0 && normalizedQuery.Contains(" " + name + " ", StringComparison.Ordinal)));
            }).Select(row => row.Id).Distinct().ToArray();

        return new(db, search, tokens.Select(token => new Term(token,
            Matches(tags, token, true, true), Matches(tags, token, false, true),
            Matches(performers, token, true), Matches(performers, token, false),
            Matches(studios, token, true), Matches(studios, token, false),
            Matches(galleries, token, true), Matches(galleries, token, false),
            Matches(groups, token, true), Matches(groups, token, false))).ToArray(),
            galleryMembers, groupMembers, pathVideos.Length > pathSubqueryThreshold ? null : pathVideos);
    }

    /// <summary>Member videos by owner, or null when there are more than <paramref name="threshold"/>.</summary>
    private static async Task<ILookup<int, int>?> ResolveMembersAsync(int[] ownerIds, IQueryable<MemberRow> members, int threshold, CancellationToken ct)
    {
        if (ownerIds.Length == 0)
            return Enumerable.Empty<MemberRow>().ToLookup(row => row.OwnerId, row => row.VideoId);
        var rows = await members.OrderBy(row => row.OwnerId).Take(threshold + 1).ToListAsync(ct);
        return rows.Count > threshold ? null : rows.ToLookup(row => row.OwnerId, row => row.VideoId);
    }

    private static int[] MemberVideos(ILookup<int, int> members, int[] ownerIds)
        => ownerIds.SelectMany(id => members[id]).Distinct().ToArray();

    private static string NormalizeWords(string value)
        => string.Join(' ', new string(value.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    internal IQueryable<Video> Apply(IQueryable<Video> query)
    {
        if (terms.Count == 0)
            return query;

        // Keep matching predicates on the same video row. Nesting candidate-ID
        // unions here made broad searches deduplicate and refetch a million rows
        // repeatedly, even though text and ID arrays already have GIN indexes.
        Expression<Func<Video, bool>> matches = video => true;
        foreach (var term in terms)
            matches = And(matches, Or(TextMatch(term.Text), Related(term, false)));

        // The substring fallback handles paths, which PostgreSQL indexes as whole lexemes rather than
        // as words. It matches every term within one file. Resolved candidates keep the predicate on
        // the video row. Past the cap the search checks each candidate video's own files: an uncorrelated
        // `Id IN (files ...)` subquery here was planned as a per-row scan of a materialized match list and
        // did not finish on large libraries, while a correlated EXISTS probes the (VideoId, Path) index.
        if (pathVideos != null)
            return query.Where(Or(matches, MemberOf(pathVideos)));
        var files = db.VideoFiles.Where(file => file.VideoId != null);
        foreach (var term in terms)
        {
            var token = term.Text;
            files = files.Where(file => file.Path.ToLower().Contains(token));
        }
        return query.Where(Or(matches, video => files.Any(file => file.VideoId == video.Id)));
    }

    private Expression<Func<Video, bool>> TextMatch(string token)
    {
        if (IsPostgres)
        {
            var prefix = token + ":*";
            return video => EF.Property<NpgsqlTsVector>(video, "SearchVector").Matches(EF.Functions.ToTsQuery("simple", prefix));
        }
        return video => (video.Title != null && video.Title.ToLower().Contains(token))
            || (video.Code != null && video.Code.ToLower().Contains(token))
            || (video.Details != null && video.Details.ToLower().Contains(token))
            || (video.Director != null && video.Director.ToLower().Contains(token))
            || (video.Captions != null && video.Captions.ToLower().Contains(token))
            || (video.FileSearchText != null && video.FileSearchText.ToLower().Contains(token))
            || (video.SearchText != null && video.SearchText.ToLower().Contains(token));
    }

    internal IQueryable<Video> Order(IQueryable<Video> query)
    {
        if (terms.Count == 0)
            return query.OrderByDescending(video => video.UpdatedAt).ThenBy(video => video.Id);

        var lower = search.Trim().ToLowerInvariant();
        var padded = " " + lower + " ";
        Expression<Func<Video, int>> score = video =>
            (video.Title != null && video.Title.ToLower() == lower ? 100 : 0)
            + ((video.Title != null && (video.Title.ToLower().Contains(lower)
                    || (video.Title.Trim() != "" && padded.Contains(" " + video.Title.ToLower() + " "))))
                || (video.Code != null && video.Code.ToLower().Contains(lower)) ? 40 : 0)
            + ((video.Details != null && video.Details.ToLower().Contains(lower))
                || (video.Director != null && video.Director.ToLower().Contains(lower)) ? 4 : 0);

        foreach (var term in terms)
        {
            // Only the strongest indexed field and strongest relationship count for a
            // term. Repeated words, aliases and overlapping tags cannot multiply a score.
            score = Add(score, Points(Related(term, true), 20));
            score = Add(score, Points(And(Related(term, false), Not(Related(term, true))), 8));
            var token = term.Text;
            if (IsPostgres)
            {
                var high = token + ":*A";
                var medium = token + ":*B";
                var any = token + ":*";
                score = Add(score, video => EF.Property<NpgsqlTsVector>(video, "SearchVector").Matches(EF.Functions.ToTsQuery("simple", high)) ? 8
                    : EF.Property<NpgsqlTsVector>(video, "SearchVector").Matches(EF.Functions.ToTsQuery("simple", medium)) ? 2
                    : EF.Property<NpgsqlTsVector>(video, "SearchVector").Matches(EF.Functions.ToTsQuery("simple", any)) ? 1 : 0);
            }
            else
            {
                score = Add(score, video => (video.Title != null && video.Title.ToLower().Contains(token)) || (video.Code != null && video.Code.ToLower().Contains(token)) ? 8
                    : (video.Details != null && video.Details.ToLower().Contains(token)) || (video.Director != null && video.Director.ToLower().Contains(token)) ? 2
                    : (video.Captions != null && video.Captions.ToLower().Contains(token)) || (video.FileSearchText != null && video.FileSearchText.ToLower().Contains(token)) || (video.SearchText != null && video.SearchText.ToLower().Contains(token)) ? 1 : 0);
            }
        }
        return query.OrderByDescending(score).ThenByDescending(video => video.UpdatedAt).ThenBy(video => video.Id);
    }

    private Expression<Func<Video, bool>> Related(Term term, bool exact)
    {
        var tags = exact ? term.ExactTags : term.PartialTags;
        var performers = exact ? term.ExactPerformers : term.PartialPerformers;
        var studios = exact ? term.ExactStudios : term.PartialStudios;
        var galleries = exact ? term.ExactGalleries : term.PartialGalleries;
        var groups = exact ? term.ExactGroups : term.PartialGroups;
        Expression<Func<Video, bool>> result = video => false;
        if (tags.Length > 0)
            result = Or(result, IsPostgres ? video => video.TagIds.Any(id => tags.Contains(id))
                : video => video.VideoTags.Any(link => tags.Contains(link.TagId)));
        if (performers.Length > 0)
            result = Or(result, IsPostgres ? video => video.PerformerIds.Any(id => performers.Contains(id))
                : video => video.VideoPerformers.Any(link => performers.Contains(link.PerformerId)));
        if (studios.Length > 0)
            result = Or(result, video => video.StudioId != null && studios.Contains(video.StudioId.Value));
        if (galleries.Length > 0)
            result = Or(result, galleryMembers is { } resolvedGalleries
                ? MemberOf(MemberVideos(resolvedGalleries, galleries))
                : video => video.VideoGalleries.Any(link => galleries.Contains(link.GalleryId)));
        if (groups.Length > 0)
            result = Or(result, groupMembers is { } resolvedGroups
                ? MemberOf(MemberVideos(resolvedGroups, groups))
                : video => video.GroupItems.Any(item => groups.Contains(item.GroupId)));
        return result;
    }

    private static Expression<Func<Video, bool>> MemberOf(int[] videoIds)
        => videoIds.Length == 0 ? video => false : video => videoIds.Contains(video.Id);
    private static Expression<Func<Video, int>> Points(Expression<Func<Video, bool>> test, int points)
        => Expression.Lambda<Func<Video, int>>(Expression.Condition(test.Body, Expression.Constant(points), Expression.Constant(0)), test.Parameters);
    private static Expression<Func<Video, bool>> Not(Expression<Func<Video, bool>> test)
        => Expression.Lambda<Func<Video, bool>>(Expression.Not(test.Body), test.Parameters);
    private static Expression<Func<Video, bool>> And(Expression<Func<Video, bool>> a, Expression<Func<Video, bool>> b)
        => Combine(a, b, Expression.AndAlso);
    private static Expression<Func<Video, bool>> Or(Expression<Func<Video, bool>> a, Expression<Func<Video, bool>> b)
        => Combine(a, b, Expression.OrElse);
    private static Expression<Func<Video, int>> Add(Expression<Func<Video, int>> a, Expression<Func<Video, int>> b)
        => Combine(a, b, Expression.Add);
    private static Expression<Func<Video, T>> Combine<T>(Expression<Func<Video, T>> a, Expression<Func<Video, T>> b, Func<Expression, Expression, BinaryExpression> combine)
        => Expression.Lambda<Func<Video, T>>(combine(a.Body, new ReplaceParameter(b.Parameters[0], a.Parameters[0]).Visit(b.Body)!), a.Parameters);
    private sealed class ReplaceParameter(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }
}
