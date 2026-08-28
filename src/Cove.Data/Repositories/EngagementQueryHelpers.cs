using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Repositories;

public static class EngagementQueryHelpers
{
    public static int? CurrentUserId(CoveContext db) => db.CurrentPrincipalForReadOptimization?.UserId;

    public static IQueryable<T> ApplyRatingMinimum<T>(CoveContext db, IQueryable<T> query, int? userId, RatingHostType hostType, int minRating)
        where T : class
    {
        if (userId is not int selectedUserId)
            return query.Where(_ => false);

        return query.Where(entity => db.Ratings.Any(rating =>
            rating.UserId == selectedUserId &&
            rating.HostType == hostType &&
            rating.HostId == EF.Property<int>(entity, "Id") &&
            rating.Aspect == "overall" &&
            rating.Value >= minRating));
    }

    public static IQueryable<T> ApplyRatingCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, RatingHostType hostType, IntCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        // "Is rated" / "is not rated" for the current user. A rating is stored per-user in the ratings
        // table, and the value projection below returns 0 for an unrated entity — so null-ness cannot be
        // expressed as an int comparison (and ApplyInt has no IsNull/NotNull case, which is why these
        // modifiers previously matched everything). Resolve them here against the existence of a rating row.
        if (criterion.Modifier is CriterionModifier.IsNull or CriterionModifier.NotNull)
        {
            // No signed-in user → nobody has a rating: "not rated" (IsNull) matches all, "rated" matches none.
            if (userId is not int uid)
                return criterion.Modifier == CriterionModifier.IsNull ? query : query.Where(_ => false);

            return criterion.Modifier == CriterionModifier.NotNull
                ? query.Where(entity => db.Ratings.Any(rating =>
                    rating.UserId == uid &&
                    rating.HostType == hostType &&
                    rating.HostId == EF.Property<int>(entity, "Id") &&
                    rating.Aspect == "overall"))
                : query.Where(entity => !db.Ratings.Any(rating =>
                    rating.UserId == uid &&
                    rating.HostType == hostType &&
                    rating.HostId == EF.Property<int>(entity, "Id") &&
                    rating.Aspect == "overall"));
        }

        if (userId is not int selectedUserId)
            return FilterHelpers.ApplyInt(query, criterion, _ => 0);

        return FilterHelpers.ApplyInt(query, criterion, entity =>
            db.Ratings
                .Where(rating =>
                    rating.UserId == selectedUserId &&
                    rating.HostType == hostType &&
                    rating.HostId == EF.Property<int>(entity, "Id") &&
                    rating.Aspect == "overall")
                .Select(rating => rating.Value)
                .FirstOrDefault());
    }

    public static IQueryable<T> ApplyFavoriteCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, BoolCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        if (userId is not int selectedUserId)
            return criterion.Value ? query.Where(_ => false) : query;

        return FilterHelpers.ApplyBool(query, criterion, entity =>
            db.UserEntityAffinities.Any(affinity =>
                affinity.UserId == selectedUserId &&
                affinity.HostType == hostType &&
                affinity.HostId == EF.Property<int>(entity, "Id") &&
                affinity.IsFavorite));
    }

    public static IQueryable<T> ApplyAffinityIntCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, IntCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        if (userId is not int selectedUserId)
            return FilterHelpers.ApplyInt(query, criterion, _ => 0);

        return FilterHelpers.ApplyInt(query, criterion, entity =>
            db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => EF.Property<int>(affinity, propertyName))
                .FirstOrDefault());
    }

    public static IQueryable<T> ApplyFavoriteCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, BoolCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        if (userId is not int selectedUserId)
            return criterion.Value ? query.Where(_ => false) : query;

        return criterion.Value
            ? query.Where(entity => db.UserEntityAffinities.Any(affinity =>
                affinity.UserId == selectedUserId &&
                affinity.HostType == hostType &&
                affinity.HostId == EF.Property<int>(entity, "Id") &&
                affinity.IsFavorite))
            : query.Where(entity => !db.UserEntityAffinities.Any(affinity =>
                affinity.UserId == selectedUserId &&
                affinity.HostType == hostType &&
                affinity.HostId == EF.Property<int>(entity, "Id") &&
                affinity.IsFavorite));
    }

    public static IQueryable<T> ApplyAffinityDoubleAsIntCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, IntCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        if (userId is not int selectedUserId)
            return FilterHelpers.ApplyInt(query, criterion, _ => 0);

        return FilterHelpers.ApplyInt(query, criterion, entity =>
            db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => (int)EF.Property<double>(affinity, propertyName))
                .FirstOrDefault());
    }

    public static IQueryable<T> ApplyAffinityTimestampCriterion<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, TimestampCriterion? criterion)
        where T : class
    {
        if (criterion == null)
            return query;

        if (userId is not int selectedUserId)
            return FilterHelpers.ApplyNullableTimestamp(query, criterion, _ => null);

        return FilterHelpers.ApplyNullableTimestamp(query, criterion, entity =>
            db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => EF.Property<DateTime?>(affinity, propertyName))
                .FirstOrDefault());
    }

    public static IQueryable<T> ApplyRatingSort<T>(CoveContext db, IQueryable<T> query, int? userId, RatingHostType hostType, bool desc)
        where T : class
    {
        if (userId is not int selectedUserId)
            return desc
                ? query.OrderByDescending(entity => EF.Property<int>(entity, "Id"))
                : query.OrderBy(entity => EF.Property<int>(entity, "Id"));

        var sortQuery = query.Select(entity => new
        {
            Entity = entity,
            Rating = db.Ratings
                .Where(rating =>
                    rating.UserId == selectedUserId &&
                    rating.HostType == hostType &&
                    rating.HostId == EF.Property<int>(entity, "Id") &&
                    rating.Aspect == "overall")
                .Select(rating => (int?)rating.Value)
                .FirstOrDefault(),
        });

        return desc
            ? sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 1 : 0).ThenByDescending(item => item.Rating).ThenByDescending(item => EF.Property<int>(item.Entity, "Id")).Select(item => item.Entity)
            : sortQuery.OrderBy(item => item.Rating == null || item.Rating <= 0 ? 0 : 1).ThenBy(item => item.Rating).ThenBy(item => EF.Property<int>(item.Entity, "Id")).Select(item => item.Entity);
    }

    public static IQueryable<T> ApplyAffinityIntSort<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, bool desc)
        where T : class
    {
        if (userId is not int selectedUserId)
            return desc
                ? query.OrderByDescending(entity => EF.Property<int>(entity, "Id"))
                : query.OrderBy(entity => EF.Property<int>(entity, "Id"));

        return CompoundSortOrdering.Append(
            query,
            ordered: null,
            entity => db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => EF.Property<int>(affinity, propertyName))
                .FirstOrDefault(),
            desc)
            .ThenByDirection(entity => EF.Property<int>(entity, "Id"), desc);
    }

    public static IQueryable<T> ApplyAffinityDoubleSort<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, bool desc)
        where T : class
    {
        if (userId is not int selectedUserId)
            return desc
                ? query.OrderByDescending(entity => EF.Property<int>(entity, "Id"))
                : query.OrderBy(entity => EF.Property<int>(entity, "Id"));

        return CompoundSortOrdering.Append(
            query,
            ordered: null,
            entity => db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => EF.Property<double>(affinity, propertyName))
                .FirstOrDefault(),
            desc)
            .ThenByDirection(entity => EF.Property<int>(entity, "Id"), desc);
    }

    public static IQueryable<T> ApplyAffinityTimestampSort<T>(CoveContext db, IQueryable<T> query, int? userId, AffinityHostType hostType, string propertyName, bool desc)
        where T : class
    {
        if (userId is not int selectedUserId)
            return desc
                ? query.OrderByDescending(entity => EF.Property<int>(entity, "Id"))
                : query.OrderBy(entity => EF.Property<int>(entity, "Id"));

        return CompoundSortOrdering.Append(
            query,
            ordered: null,
            entity => db.UserEntityAffinities
                .Where(affinity =>
                    affinity.UserId == selectedUserId &&
                    affinity.HostType == hostType &&
                    affinity.HostId == EF.Property<int>(entity, "Id"))
                .Select(affinity => EF.Property<DateTime?>(affinity, propertyName))
                .FirstOrDefault() ?? (desc ? DateTime.MinValue : DateTime.MaxValue),
            desc)
            .ThenByDirection(entity => EF.Property<int>(entity, "Id"), desc);
    }

    public static IQueryable<T> ApplyInteractionTimestampSort<T>(CoveContext db, IQueryable<T> query, int? userId, InteractionHostType hostType, InteractionKind kind, bool desc)
        where T : class
    {
        if (userId is not int selectedUserId)
            return desc
                ? query.OrderByDescending(entity => EF.Property<int>(entity, "Id"))
                : query.OrderBy(entity => EF.Property<int>(entity, "Id"));

        return CompoundSortOrdering.Append(
            query,
            ordered: null,
            entity => db.Interactions
                .Where(interaction =>
                    interaction.UserId == selectedUserId &&
                    interaction.HostType == hostType &&
                    interaction.HostId == EF.Property<int>(entity, "Id") &&
                    interaction.Kind == kind)
                .Select(interaction => (DateTime?)interaction.At)
                .Max() ?? (desc ? DateTime.MinValue : DateTime.MaxValue),
            desc)
            .ThenByDirection(entity => EF.Property<int>(entity, "Id"), desc);
    }

    private static IOrderedQueryable<T> ThenByDirection<T, TKey>(
        this IOrderedQueryable<T> query,
        System.Linq.Expressions.Expression<Func<T, TKey>> keySelector,
        bool desc)
        => desc ? query.ThenByDescending(keySelector) : query.ThenBy(keySelector);
}
