using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using Cove.Core.Entities;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Cove.Data.Repositories;

public static class FullTextSearchHelpers
{
    private const string SearchVectorProperty = "SearchVector";
    private const string SearchConfig = "simple";

    private static readonly MethodInfo EnumerableAnyMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .First(m => m.Name == nameof(Enumerable.Any)
            && m.GetParameters().Length == 2);

    /// <summary>
    /// Augments a free-text search result with relational matches against applied tags and
    /// performers, returning the union (distinct) of the two. Tag matching is whole-word
    /// (including aliases) so that searching "1F" does not match a tag named "1F1M". Performer
    /// matching is substring (including aliases) to mirror video search behavior. Descriptions
    /// are intentionally not handled here because every entity already indexes its description
    /// field in its full-text <c>SearchVector</c>.
    /// </summary>
    /// <param name="textQuery">The full-text search result to extend.</param>
    /// <param name="baseQuery">The pre-text-search (already scoped/filtered) query to match against.</param>
    /// <param name="search">The raw search term.</param>
    /// <param name="tagSelectors">Selectors projecting an entity to its applied tags.</param>
    /// <param name="performerSelectors">Selectors projecting an entity to its applied performers.</param>
    public static IQueryable<T> ApplyRelationalMatches<T>(
        IQueryable<T> textQuery,
        IQueryable<T> baseQuery,
        string? search,
        Expression<Func<T, IEnumerable<Tag>>>[]? tagSelectors = null,
        Expression<Func<T, IEnumerable<Performer>>>[]? performerSelectors = null)
        where T : BaseEntity
    {
        var normalized = Normalize(search);
        if (normalized is null)
            return textQuery;

        var lower = normalized.ToLowerInvariant();
        var wordTerm = $" {lower} ";

        Expression<Func<Tag, bool>> tagMatches = tag =>
            (" " + tag.Name.ToLower() + " ").Contains(wordTerm)
            || tag.Aliases.Any(alias => (" " + alias.Alias.ToLower() + " ").Contains(wordTerm));

        Expression<Func<Performer, bool>> performerMatches = performer =>
            performer.Name.ToLower().Contains(lower)
            || performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(lower));

        var entityParam = Expression.Parameter(typeof(T), "entity");
        Expression? body = null;

        foreach (var selector in tagSelectors ?? [])
            body = OrElse(body, BuildAnyMatch(selector, entityParam, tagMatches));

        foreach (var selector in performerSelectors ?? [])
            body = OrElse(body, BuildAnyMatch(selector, entityParam, performerMatches));

        if (body is null)
            return textQuery;

        var predicate = Expression.Lambda<Func<T, bool>>(body, entityParam);
        return UnionMatchesById(baseQuery, textQuery, baseQuery.Where(predicate));
    }

    /// <summary>
    /// Unions a file-path substring match into an existing free-text result so the main search box
    /// also finds file-backed entities by their file path (not just title). Paths are stored in
    /// forward-slash form, so the term's backslashes are normalized before matching. Substring
    /// matching is used rather than the full-text vector because PostgreSQL tokenizes paths into
    /// lexemes (e.g. "clip.mp4" -> "clip", "mp4") that don't reliably match a partial path the user
    /// types. Combines candidate IDs like <see cref="ApplyRelationalMatches"/> and works on both
    /// PostgreSQL and the SQLite test provider.
    /// </summary>
    public static IQueryable<T> ApplyFilePathMatch<T, TFile>(
        IQueryable<T> textQuery,
        IQueryable<T> baseQuery,
        string? search,
        Expression<Func<T, IEnumerable<TFile>>> filesSelector)
        where T : BaseEntity
        where TFile : BaseFileEntity
    {
        var normalized = Normalize(search);
        if (normalized is null)
            return textQuery;

        // Tokenize the query on separators (whitespace, _, -, ., +, /, …) and require every token to
        // appear in the file path. This makes separators interchangeable — the way Stash treats them —
        // so "foo slut", "foo_slut", "foo-bar", etc. all match a file named "foo_bar_slut". Each token
        // must match the SAME file's path (all tokens ANDed inside files.Any) rather than being spread
        // across different files of a multi-file entity.
        var tokens = TokenizeSearchTerms(normalized);
        if (tokens.Count == 0)
            return textQuery;

        var fileParam = Expression.Parameter(typeof(TFile), "file");
        var pathLower = Expression.Call(
            Expression.Property(fileParam, nameof(BaseFileEntity.Path)),
            StringToLowerMethod);
        Expression? fileBody = null;
        foreach (var token in tokens)
        {
            var contains = Expression.Call(pathLower, StringContainsMethod, Expression.Constant(token));
            fileBody = fileBody is null ? contains : Expression.AndAlso(fileBody, contains);
        }

        var fileMatches = Expression.Lambda<Func<TFile, bool>>(fileBody!, fileParam);

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var body = BuildAnyMatch(filesSelector, entityParam, fileMatches);
        var predicate = Expression.Lambda<Func<T, bool>>(body, entityParam);
        return UnionMatchesById(baseQuery, textQuery, baseQuery.Where(predicate));
    }

    /// <summary>
    /// Combines match queries by projecting only their entity IDs, then intersects those candidates
    /// with the original scoped query. The outer query preserves authorization and caller filters,
    /// while the narrow UNION ALL avoids DISTINCT sorting complete entity rows.
    /// </summary>
    public static IQueryable<T> UnionMatchesById<T>(IQueryable<T> baseQuery, params IQueryable<T>[] matchQueries)
        where T : BaseEntity
    {
        if (matchQueries.Length == 0)
            return baseQuery.Where(_ => false);

        var matchingIds = matchQueries[0].Select(entity => entity.Id);
        foreach (var matchQuery in matchQueries.Skip(1))
            matchingIds = matchingIds.Concat(matchQuery.Select(entity => entity.Id));

        return baseQuery.Where(entity => matchingIds.Contains(entity.Id));
    }

    private static readonly MethodInfo StringToLowerMethod =
        typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!;
    private static readonly MethodInfo StringContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    /// <summary>
    /// Splits free-text search input into lowercase alphanumeric tokens, treating every non-alphanumeric
    /// character (space, _, -, ., +, /, …) as a separator. Matches how <see cref="BuildPrefixQuery"/>
    /// tokenizes for full-text search, so file-path matching and title matching stay consistent.
    /// </summary>
    internal static List<string> TokenizeSearchTerms(string search)
    {
        var tokens = new List<string>();
        var token = new StringBuilder();

        void Flush()
        {
            if (token.Length == 0)
                return;
            var value = token.ToString();
            if (!tokens.Contains(value))
                tokens.Add(value);
            token.Clear();
        }

        foreach (var ch in search)
        {
            if (char.IsLetterOrDigit(ch))
                token.Append(char.ToLowerInvariant(ch));
            else
                Flush();
        }

        Flush();
        return tokens;
    }

    private static Expression OrElse(Expression? left, Expression right)
        => left is null ? right : Expression.OrElse(left, right);

    private static Expression BuildAnyMatch<T, TElement>(
        Expression<Func<T, IEnumerable<TElement>>> collectionSelector,
        ParameterExpression entityParam,
        Expression<Func<TElement, bool>> elementPredicate)
    {
        var collectionBody = new ParameterReplacer(collectionSelector.Parameters[0], entityParam)
            .Visit(collectionSelector.Body)!;
        var anyMethod = EnumerableAnyMethod.MakeGenericMethod(typeof(TElement));
        return Expression.Call(anyMethod, collectionBody, elementPredicate);
    }

    private sealed class ParameterReplacer(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node)
            => node == from ? to : base.VisitParameter(node);
    }

    public static bool IsActive(CoveContext db, string? search)
        => SupportsPostgresFullText(db) && !string.IsNullOrWhiteSpace(search);

    public static IQueryable<T> Apply<T>(
        CoveContext db,
        IQueryable<T> query,
        string? search,
        params Expression<Func<T, string?>>[] fallbackSelectors)
        where T : BaseEntity
    {
        var normalized = Normalize(search);
        if (normalized is null)
            return query;

        if (!SupportsPostgresFullText(db))
            return FilterHelpers.ApplyBooleanKeywordSearch(query, normalized, fallbackSelectors);

        var prefixQuery = BuildPrefixQuery(normalized);
        if (prefixQuery is null)
            return query;

        return query.Where(entity => EF.Property<NpgsqlTsVector>(entity, SearchVectorProperty)
            .Matches(EF.Functions.ToTsQuery(SearchConfig, prefixQuery)));
    }

    public static bool ShouldOrderByRelevance(CoveContext db, string? search, string? explicitSort)
        => IsActive(db, search) && (string.IsNullOrWhiteSpace(explicitSort) || IsRelevanceSort(explicitSort));

    public static bool IsRelevanceSort(string? sort)
        => string.Equals(sort, "relevance", StringComparison.OrdinalIgnoreCase);

    public static IQueryable<T> OrderByRelevance<T>(CoveContext db, IQueryable<T> query, string? search)
        where T : BaseEntity
    {
        var normalized = Normalize(search);
        if (normalized is null || !SupportsPostgresFullText(db))
            return query;

        var prefixQuery = BuildPrefixQuery(normalized);
        if (prefixQuery is null)
            return query;

        return query
            .OrderByDescending(entity => EF.Property<NpgsqlTsVector>(entity, SearchVectorProperty)
                .Rank(EF.Functions.ToTsQuery(SearchConfig, prefixQuery)))
            .ThenByDescending(entity => entity.UpdatedAt)
            .ThenBy(entity => entity.Id);
    }

    public static IQueryable<T> OrderByExactThenRelevance<T>(
        CoveContext db,
        IQueryable<T> query,
        string? search,
        Expression<Func<T, string?>> titleSelector,
        IReadOnlyList<IQueryable<int>>? candidatePriorityIds = null)
        where T : BaseEntity
    {
        var normalized = Normalize(search);
        if (normalized is null)
            return query;

        var entityParam = Expression.Parameter(typeof(T), "entity");
        var title = new ParameterReplacer(titleSelector.Parameters[0], entityParam)
            .Visit(titleSelector.Body)!;
        var exactTitle = Expression.Lambda<Func<T, bool>>(
            Expression.AndAlso(
                Expression.NotEqual(title, Expression.Constant(null, typeof(string))),
                Expression.Equal(
                    Expression.Call(title, StringToLowerMethod),
                    Expression.Constant(normalized.ToLowerInvariant()))),
            entityParam);

        IOrderedQueryable<T> ordered = query.OrderByDescending(exactTitle);
        string? prefixQuery = null;

        if (SupportsPostgresFullText(db))
        {
            prefixQuery = BuildPrefixQuery(normalized);
            if (prefixQuery is not null)
            {
                ordered = ordered.ThenByDescending(entity => EF.Property<NpgsqlTsVector>(entity, SearchVectorProperty)
                    .Rank(EF.Functions.ToTsQuery(SearchConfig, prefixQuery)));
            }
        }

        foreach (var priorityIds in candidatePriorityIds ?? [])
        {
            ordered = prefixQuery is null
                ? ordered.ThenByDescending(entity => priorityIds.Contains(entity.Id))
                : ordered.ThenByDescending(entity => EF.Property<NpgsqlTsVector>(entity, SearchVectorProperty)
                    .Rank(EF.Functions.ToTsQuery(SearchConfig, prefixQuery)) == 0
                        ? priorityIds.Contains(entity.Id)
                        : false);
        }

        return ordered
            .ThenByDescending(entity => entity.UpdatedAt)
            .ThenBy(entity => entity.Id);
    }

    private static bool SupportsPostgresFullText(CoveContext db)
        => db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true;

    private static string? Normalize(string? search)
    {
        var normalized = search?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    /// <summary>
    /// Builds a prefix-matching <c>to_tsquery</c> expression from free-text input so that
    /// partial words match (e.g. "sa" matches "sapphix", "small ho" matches "small hole").
    /// Each whitespace-delimited token is sanitized to its alphanumeric lexeme, suffixed with
    /// the prefix operator (<c>:*</c>), and combined with AND so every token must match.
    /// Returns <c>null</c> when no usable tokens remain.
    /// </summary>
    private static string? BuildPrefixQuery(string search)
    {
        var builder = new StringBuilder();
        var token = new StringBuilder();

        void FlushToken()
        {
            if (token.Length == 0)
                return;

            if (builder.Length > 0)
                builder.Append(" & ");

            builder.Append(token).Append(":*");
            token.Clear();
        }

        foreach (var ch in search)
        {
            if (char.IsLetterOrDigit(ch))
                token.Append(char.ToLowerInvariant(ch));
            else
                FlushToken();
        }

        FlushToken();

        return builder.Length == 0 ? null : builder.ToString();
    }
}
