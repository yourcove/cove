using System.Linq.Expressions;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public sealed class UnsupportedCompoundSortException(string key)
    : Exception($"Compound sort key '{key}' is not supported.")
{
    public string Key { get; } = key;
}

public static class CompoundSortOrdering
{
    public static List<SortClause> Normalize(IEnumerable<SortClause>? clauses, IReadOnlySet<string> supportedKeys)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = (clauses ?? [])
            .Where(clause => clause is not null && !string.IsNullOrWhiteSpace(clause.Key))
            .ToList();
        var unsupported = candidates.FirstOrDefault(clause => !supportedKeys.Contains(clause.Key));
        if (unsupported is not null)
            throw new UnsupportedCompoundSortException(unsupported.Key);

        return candidates
            .Where(clause => seen.Add(clause.Key))
            .Take(SortClause.MaxClauses)
            .ToList();
    }

    public static IOrderedQueryable<TEntity> Append<TEntity, TKey>(
        IQueryable<TEntity> query,
        IOrderedQueryable<TEntity>? ordered,
        Expression<Func<TEntity, TKey>> keySelector,
        bool descending)
        => ordered is null
            ? descending ? query.OrderByDescending(keySelector) : query.OrderBy(keySelector)
            : descending ? ordered.ThenByDescending(keySelector) : ordered.ThenBy(keySelector);

    public static IQueryable<TEntity> Finish<TEntity>(
        IQueryable<TEntity> query,
        IOrderedQueryable<TEntity>? ordered,
        Expression<Func<TEntity, int>> idSelector)
        => ordered is null ? query.OrderBy(idSelector) : ordered.ThenBy(idSelector);
}

public sealed class CompoundSortRegistry<TEntity> where TEntity : class
{
    private readonly IReadOnlyDictionary<string, Action<CompoundSortQuery<TEntity>, bool>> _handlers;

    public CompoundSortRegistry(IEnumerable<KeyValuePair<string, Action<CompoundSortQuery<TEntity>, bool>>> handlers)
    {
        _handlers = handlers.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlySet<string> SupportedKeys => _handlers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

    public List<SortClause> Normalize(IEnumerable<SortClause>? clauses)
        => CompoundSortOrdering.Normalize(clauses, SupportedKeys);

    public void Apply(CompoundSortQuery<TEntity> query, IEnumerable<SortClause> clauses)
    {
        foreach (var clause in clauses)
            _handlers[clause.Key](query, clause.Direction == Cove.Core.Enums.SortDirection.Desc);
    }
}

public sealed class CompoundSortQuery<TEntity> where TEntity : class
{
    private sealed class SortRow
    {
        public required TEntity Entity { get; init; }
        public UserEntityAffinity? Affinity { get; init; }
        public Rating? Rating { get; init; }
        public DateTime? LastInteractionAt { get; init; }
    }

    private readonly IQueryable<SortRow> _query;
    private readonly bool _hasAffinity;
    private readonly bool _hasRating;
    private IOrderedQueryable<SortRow>? _ordered;

    private CompoundSortQuery(IQueryable<SortRow> query, bool hasAffinity, bool hasRating)
    {
        _query = query;
        _hasAffinity = hasAffinity;
        _hasRating = hasRating;
    }

    public static CompoundSortQuery<TEntity> Create(
        CoveContext db,
        IQueryable<TEntity> query,
        int? userId,
        AffinityHostType? affinityHostType,
        RatingHostType? ratingHostType,
        bool includeAffinity,
        bool includeRating,
        InteractionHostType? interactionHostType = null,
        InteractionKind? interactionKind = null,
        bool includeInteraction = false)
    {
        var hasAffinity = includeAffinity && userId.HasValue && affinityHostType.HasValue;
        var hasRating = includeRating && userId.HasValue && ratingHostType.HasValue;
        var hasInteraction = includeInteraction && userId.HasValue && interactionHostType.HasValue && interactionKind.HasValue;
        IQueryable<SortRow> rows = query.Select(entity => new SortRow { Entity = entity });

        if (hasAffinity)
        {
            var selectedUserId = userId!.Value;
            var selectedHostType = affinityHostType!.Value;
            var affinities = db.UserEntityAffinities.Where(affinity =>
                affinity.UserId == selectedUserId && affinity.HostType == selectedHostType);

            rows = rows
                .GroupJoin(
                    affinities,
                    row => EF.Property<int>(row.Entity, "Id"),
                    affinity => affinity.HostId,
                    (row, matches) => new { row, matches })
                .SelectMany(
                    item => item.matches.DefaultIfEmpty(),
                    (item, affinity) => new SortRow
                    {
                        Entity = item.row.Entity,
                        Affinity = affinity,
                        Rating = item.row.Rating,
                        LastInteractionAt = item.row.LastInteractionAt,
                    });
        }

        if (hasRating)
        {
            var selectedUserId = userId!.Value;
            var selectedHostType = ratingHostType!.Value;
            var ratings = db.Ratings.Where(rating =>
                rating.UserId == selectedUserId &&
                rating.HostType == selectedHostType &&
                rating.Aspect == "overall");

            rows = rows
                .GroupJoin(
                    ratings,
                    row => EF.Property<int>(row.Entity, "Id"),
                    rating => rating.HostId,
                    (row, matches) => new { row, matches })
                .SelectMany(
                    item => item.matches.DefaultIfEmpty(),
                    (item, rating) => new SortRow
                    {
                        Entity = item.row.Entity,
                        Affinity = item.row.Affinity,
                        Rating = rating,
                        LastInteractionAt = item.row.LastInteractionAt,
                    });
        }

        if (hasInteraction)
        {
            var selectedUserId = userId!.Value;
            var selectedHostType = interactionHostType!.Value;
            var selectedKind = interactionKind!.Value;
            var interactions = db.Interactions
                .Where(interaction =>
                    interaction.UserId == selectedUserId &&
                    interaction.HostType == selectedHostType &&
                    interaction.Kind == selectedKind)
                .GroupBy(interaction => interaction.HostId)
                .Select(group => new { HostId = group.Key, At = group.Max(interaction => interaction.At) });

            rows = rows
                .GroupJoin(
                    interactions,
                    row => EF.Property<int>(row.Entity, "Id"),
                    interaction => interaction.HostId,
                    (row, matches) => new { row, matches })
                .SelectMany(
                    item => item.matches.DefaultIfEmpty(),
                    (item, interaction) => new SortRow
                    {
                        Entity = item.row.Entity,
                        Affinity = item.row.Affinity,
                        Rating = item.row.Rating,
                        LastInteractionAt = interaction == null ? null : interaction.At,
                    });
        }

        return new CompoundSortQuery<TEntity>(rows, hasAffinity, hasRating);
    }

    public void Append<TKey>(Expression<Func<TEntity, TKey>> keySelector, bool descending)
    {
        var rowParameter = Expression.Parameter(typeof(SortRow), "row");
        var entity = Expression.Property(rowParameter, nameof(SortRow.Entity));
        var body = new ReplaceExpressionVisitor(keySelector.Parameters[0], entity).Visit(keySelector.Body)!;
        var rowSelector = Expression.Lambda<Func<SortRow, TKey>>(body, rowParameter);
        _ordered = CompoundSortOrdering.Append(_query, _ordered, rowSelector, descending);
    }

    public void AppendRating(bool descending)
    {
        if (!_hasRating) return;
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.Rating == null || row.Rating.Value <= 0
                ? (descending ? 1 : 0)
                : (descending ? 0 : 1),
            descending: false);
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.Rating == null ? (int?)null : row.Rating.Value,
            descending);
    }

    public void AppendAffinityInt(string propertyName, bool descending)
    {
        if (!_hasAffinity) return;
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.Affinity == null ? 0 : EF.Property<int>(row.Affinity, propertyName),
            descending);
    }

    public void AppendAffinityDouble(string propertyName, bool descending)
    {
        if (!_hasAffinity) return;
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.Affinity == null ? 0d : EF.Property<double>(row.Affinity, propertyName),
            descending);
    }

    public void AppendAffinityTimestamp(string propertyName, bool descending)
    {
        if (!_hasAffinity) return;
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.Affinity == null
                ? (descending ? DateTime.MinValue : DateTime.MaxValue)
                : EF.Property<DateTime?>(row.Affinity, propertyName)
                    ?? (descending ? DateTime.MinValue : DateTime.MaxValue),
            descending);
    }

    public void AppendInteractionTimestamp(bool descending)
    {
        _ordered = CompoundSortOrdering.Append(
            _query,
            _ordered,
            row => row.LastInteractionAt ?? (descending ? DateTime.MinValue : DateTime.MaxValue),
            descending);
    }

    public IQueryable<TEntity> Finish(Expression<Func<TEntity, int>> idSelector)
    {
        Append(idSelector, descending: false);
        return _ordered!.Select(row => row.Entity);
    }

    private sealed class ReplaceExpressionVisitor(Expression source, Expression replacement) : ExpressionVisitor
    {
        public override Expression? Visit(Expression? node)
            => node == source ? replacement : base.Visit(node);
    }
}
