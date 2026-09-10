using System.Linq.Expressions;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Cove.Data.Repositories;

/// <summary>
/// Resolves the small related-entity dictionaries once, then searches the indexed ID arrays
/// on videos. Scores contain no per-video relationship-name or alias subqueries.
/// </summary>
internal sealed class VideoTextSearch(CoveContext db, string search, IReadOnlyList<VideoTextSearch.Term> terms)
{
    internal sealed record Term(string Text, int[] ExactTags, int[] PartialTags,
        int[] ExactPerformers, int[] PartialPerformers, int[] ExactStudios, int[] PartialStudios,
        int[] ExactGalleries, int[] PartialGalleries, int[] ExactGroups, int[] PartialGroups);

    private sealed class NameRow
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";
    }
    private bool IsPostgres => db.Database.IsNpgsql();

    internal static async Task<VideoTextSearch> CreateAsync(CoveContext db, string search, CancellationToken ct)
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
            Matches(groups, token, true), Matches(groups, token, false))).ToArray());
    }

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

        // The substring fallback is deliberately same-file. It handles paths whose
        // PostgreSQL lexemes differ from ordinary words, without mixing separate files.
        var files = db.VideoFiles.Where(file => file.VideoId != null);
        foreach (var term in terms)
        {
            var token = term.Text;
            files = files.Where(file => file.Path.ToLower().Contains(token));
        }
        var fileIds = files.Select(file => file.VideoId!.Value);
        return query.Where(Or(matches, video => fileIds.Contains(video.Id)));
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
            result = Or(result, video => video.VideoGalleries.Any(link => galleries.Contains(link.GalleryId)));
        if (groups.Length > 0)
            result = Or(result, video => video.GroupItems.Any(item => groups.Contains(item.GroupId)));
        return result;
    }

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
