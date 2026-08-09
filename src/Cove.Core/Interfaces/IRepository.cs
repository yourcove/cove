using System.Text.Json;
using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

public interface IRepository<T> where T : class
{
    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);
    Task<T> AddAsync(T entity, CancellationToken ct = default);
    Task UpdateAsync(T entity, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
}

public interface IVideoRepository : IRepository<Video>
{
    Task<(IReadOnlyList<Video> Items, int TotalCount)> FindAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<VideoAggregate> AggregateAsync(VideoFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Video?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
    /// <summary>Returns VideoPerformer join rows (with Performer.RemoteIds included) for the given video IDs.</summary>
    Task<IReadOnlyList<VideoPerformer>> GetVideoPerformersAsync(IReadOnlyList<int> videoIds, CancellationToken ct = default);
}

public sealed record VideoAggregate(int Count, double Duration, long FileSize);

public interface IPerformerRepository : IRepository<Performer>
{
    Task<(IReadOnlyList<Performer> Items, int TotalCount)> FindAsync(PerformerFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Performer?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);

    /// <summary>
    /// Finds performers whose name/aliases match any of <paramref name="names"/>, or whose remote ID
    /// at <paramref name="remoteEndpoint"/> matches any of <paramref name="remoteIds"/>.
    /// Results include Aliases and RemoteIds navigations. Useful for deduplication and external-source linking.
    /// </summary>
    Task<IReadOnlyList<Performer>> FindByNamesOrRemoteIdsAsync(
        IReadOnlyList<string> names,
        string? remoteEndpoint,
        IReadOnlyList<string> remoteIds,
        CancellationToken ct = default);

    /// <summary>
    /// Finds a single performer by remote endpoint + remote ID, including Aliases and RemoteIds.
    /// Returns null if not found.
    /// </summary>
    Task<Performer?> FindByRemoteIdAsync(string remoteEndpoint, string remoteId, CancellationToken ct = default);
}

public interface ITagRepository : IRepository<Tag>
{
    Task<(IReadOnlyList<Tag> Items, int TotalCount)> FindAsync(TagFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Tag?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
    Task<Tag?> GetByNameAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Tag>> FindByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default);
    /// <summary>
    /// Finds all tags whose names match <paramref name="names"/> (case-insensitive), creating any that
    /// don't exist yet. Handles unique-constraint races with automatic retry.
    /// Returns a case-insensitive dictionary of tag name → Tag.
    /// </summary>
    Task<Dictionary<string, Tag>> FindOrCreateByNamesAsync(IReadOnlyList<string> names, CancellationToken ct = default);
}

public interface IStudioRepository : IRepository<Studio>
{
    Task<(IReadOnlyList<Studio> Items, int TotalCount)> FindAsync(StudioFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Studio?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
}

public interface IGalleryRepository : IRepository<Gallery>
{
    Task<(IReadOnlyList<Gallery> Items, int TotalCount)> FindAsync(GalleryFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<GalleryAggregate> AggregateAsync(GalleryFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Gallery?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
}

public sealed record GalleryAggregate(int Count, long FileSize);

public interface IImageRepository : IRepository<Image>
{
    Task<(IReadOnlyList<Image> Items, int TotalCount)> FindAsync(ImageFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<ImageAggregate> AggregateAsync(ImageFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Image?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
    /// <summary>Returns ImagePerformer join rows (with Performer.RemoteIds included) for the given image IDs.</summary>
    Task<IReadOnlyList<ImagePerformer>> GetImagePerformersAsync(IReadOnlyList<int> imageIds, CancellationToken ct = default);
    /// <summary>Returns the tag IDs currently linked to <paramref name="imageId"/> via the ImageTag join table.</summary>
    Task<IReadOnlyList<int>> GetTagIdsAsync(int imageId, CancellationToken ct = default);
    /// <summary>Adds an ImageTag join row (change-tracked). Call SaveChangesAsync on any repo to commit.</summary>
    void AddTagLink(int imageId, int tagId);
}

public sealed record ImageAggregate(int Count, long FileSize);
public sealed record AudioAggregate(int Count, double Duration, long FileSize);
public sealed record TextAggregate(int Count, long FileSize);

public interface IGroupRepository : IRepository<Group>
{
    Task<(IReadOnlyList<Group> Items, int TotalCount)> FindAsync(GroupFilter? filter, FindFilter? findFilter, CancellationToken ct = default);
    Task<Group?> GetByIdWithRelationsAsync(int id, CancellationToken ct = default);
}

public interface ISavedFilterRepository : IRepository<SavedFilter>
{
    Task<IReadOnlyList<SavedFilter>> GetByModeAsync(Cove.Core.Enums.FilterMode mode, CancellationToken ct = default);
    // User-scoped variants: only return the given user's saved filters (userId null => unowned rows).
    Task<IReadOnlyList<SavedFilter>> GetAllForUserAsync(int? userId, CancellationToken ct = default);
    Task<IReadOnlyList<SavedFilter>> GetByModeForUserAsync(Cove.Core.Enums.FilterMode mode, int? userId, CancellationToken ct = default);
}

// Filter models
public sealed record SortClause(string Key, Cove.Core.Enums.SortDirection Direction)
{
    public const int MaxClauses = 5;

    public static List<SortClause> Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clauses = new List<SortClause>();
        foreach (var rawClause in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = rawClause.LastIndexOf(':');
            if (separator <= 0) continue;

            var key = rawClause[..separator].Trim();
            var rawDirection = rawClause[(separator + 1)..].Trim();
            if (key.Length == 0 || !seen.Add(key)) continue;

            var direction = string.Equals(rawDirection, "desc", StringComparison.OrdinalIgnoreCase)
                ? Cove.Core.Enums.SortDirection.Desc
                : string.Equals(rawDirection, "asc", StringComparison.OrdinalIgnoreCase)
                    ? Cove.Core.Enums.SortDirection.Asc
                    : (Cove.Core.Enums.SortDirection?)null;
            if (direction == null) continue;

            clauses.Add(new SortClause(key, direction.Value));
            if (clauses.Count >= MaxClauses) break;
        }

        return clauses;
    }
}

public class FindFilter
{
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
    public int PerPage { get; set; } = 25;
    public string? Sort { get; set; }
    public Cove.Core.Enums.SortDirection Direction { get; set; } = Cove.Core.Enums.SortDirection.Asc;
    public List<SortClause>? Sorts { get; set; }
    public int? Seed { get; set; }
}

// Criterion modifier for advanced filters
public enum CriterionModifier
{
    Equals, NotEquals, GreaterThan, LessThan,
    Includes, Excludes, IncludesAll, ExcludesAll,
    IsNull, NotNull, Between, NotBetween,
    MatchesRegex, NotMatchesRegex,
    UnderPath, NotUnderPath
}

public class IntCriterion { public int Value { get; set; } public int? Value2 { get; set; } public CriterionModifier Modifier { get; set; } = CriterionModifier.Equals; }
public class StringCriterion { public string Value { get; set; } = ""; public CriterionModifier Modifier { get; set; } = CriterionModifier.Equals; }
public class CustomFieldCriterion : StringCriterion { public string Key { get; set; } = ""; public string Type { get; set; } = "text"; public string? Value2 { get; set; } }
public class FingerprintCriterion { public string Type { get; set; } = "md5"; public string Value { get; set; } = ""; public CriterionModifier Modifier { get; set; } = CriterionModifier.Equals; }
public class BoolCriterion { public bool Value { get; set; } }
public class MultiIdCriterion { public List<int> Value { get; set; } = []; public CriterionModifier Modifier { get; set; } = CriterionModifier.Includes; public List<int>? Excludes { get; set; } public List<int>? RequiredIds { get; set; } public int? RequiredIdsDepth { get; set; } public int? Depth { get; set; } }
public class DateCriterion { public string Value { get; set; } = ""; public string? Value2 { get; set; } public CriterionModifier Modifier { get; set; } = CriterionModifier.Equals; }
public class TimestampCriterion { public string Value { get; set; } = ""; public string? Value2 { get; set; } public CriterionModifier Modifier { get; set; } = CriterionModifier.Equals; }
public class TagDurationClause
{
    public int TagId { get; set; }
    public double? Value { get; set; }
    public double? Value2 { get; set; }
    public CriterionModifier Modifier { get; set; } = CriterionModifier.GreaterThan;
    public string Unit { get; set; } = "seconds";
    public string ContextMode { get; set; } = "any";
    public string? ContextType { get; set; }
}

public class TagDurationCriterion : TagDurationClause
{
    public List<TagDurationClause> Clauses { get; set; } = [];
}

public class VideoFilter
{
    public List<int>? Ids { get; set; }
    public string? Title { get; set; }
    public string? Code { get; set; }
    public string? Path { get; set; }
    public int? Rating { get; set; }
    public bool? Organized { get; set; }
    public bool? IsVr { get; set; }
    public int? StudioId { get; set; }
    public int? GroupId { get; set; }
    public int? GalleryId { get; set; }
    public List<int>? TagIds { get; set; }
    public List<int>? PerformerIds { get; set; }
    // Advanced criteria
    public IntCriterion? RatingCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public IntCriterion? DurationCriterion { get; set; }
    public IntCriterion? ResolutionCriterion { get; set; }
    public IntCriterion? PlayCountCriterion { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public TagDurationCriterion? TagDurationCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public MultiIdCriterion? GroupsCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public BoolCriterion? IsVrCriterion { get; set; }
    public BoolCriterion? HasSegmentsCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public FingerprintCriterion? FingerprintCriterion { get; set; }
    public StringCriterion? HashCriterion { get; set; }
    public StringCriterion? ChecksumCriterion { get; set; }
    public BoolCriterion? DuplicatedPhashCriterion { get; set; }
    public BoolCriterion? DuplicatedTitleCriterion { get; set; }
    public BoolCriterion? DuplicatedRemoteIdCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public BoolCriterion? PerformerFavoriteCriterion { get; set; }
    public StringCriterion? VideoCodecCriterion { get; set; }
    public StringCriterion? AudioCodecCriterion { get; set; }
    public IntCriterion? FrameRateCriterion { get; set; }
    public IntCriterion? BitrateInterval { get; set; }
    public IntCriterion? FileCountCriterion { get; set; }
    public StringCriterion? RemoteIdCriterion { get; set; }
    public StringCriterion? RemoteIdValueCriterion { get; set; }
    public IntCriterion? RemoteIdCountCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public StringCriterion? DuplicatedCriterion { get; set; }
    public StringCriterion? OrientationCriterion { get; set; }
    public StringCriterion? TitleCriterion { get; set; }
    public StringCriterion? CodeCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? DirectorCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? ResumeTimeCriterion { get; set; }
    public IntCriterion? PlayDurationCriterion { get; set; }
    public TimestampCriterion? LastPlayedAtCriterion { get; set; }
    public MultiIdCriterion? GalleriesCriterion { get; set; }
    public MultiIdCriterion? PerformerTagsCriterion { get; set; }
    public IntCriterion? PerformerAgeCriterion { get; set; }
    public StringCriterion? CaptionsCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class PerformerFilter
{
    public string? Name { get; set; }
    public bool? Favorite { get; set; }
    public int? Rating { get; set; }
    public List<int>? TagIds { get; set; }
    public int? StudioId { get; set; }
    // Advanced criteria
    public StringCriterion? NameCriterion { get; set; }
    public IntCriterion? RatingCriterion { get; set; }
    public IntCriterion? AgeCriterion { get; set; }
    public StringCriterion? GenderCriterion { get; set; }
    public StringCriterion? EthnicityCriterion { get; set; }
    public StringCriterion? CountryCriterion { get; set; }
    public BoolCriterion? FavoriteCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public IntCriterion? VideoCountCriterion { get; set; }
    public IntCriterion? StudioCountCriterion { get; set; }
    public IntCriterion? ImageCountCriterion { get; set; }
    public IntCriterion? GalleryCountCriterion { get; set; }
    public DateCriterion? BirthdateCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public IntCriterion? WeightCriterion { get; set; }
    public IntCriterion? HeightCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public StringCriterion? RemoteIdCriterion { get; set; }
    public StringCriterion? RemoteIdValueCriterion { get; set; }
    public IntCriterion? RemoteIdCountCriterion { get; set; }
    public StringCriterion? DisambiguationCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? EyeColorCriterion { get; set; }
    public StringCriterion? HairColorCriterion { get; set; }
    public StringCriterion? MeasurementsCriterion { get; set; }
    public StringCriterion? FakeTitsCriterion { get; set; }
    public IntCriterion? PenisLengthCriterion { get; set; }
    public StringCriterion? CircumcisedCriterion { get; set; }
    public DateCriterion? CareerStartCriterion { get; set; }
    public DateCriterion? CareerEndCriterion { get; set; }
    public IntCriterion? CareerLengthCriterion { get; set; }
    public StringCriterion? TattooCriterion { get; set; }
    public StringCriterion? PiercingsCriterion { get; set; }
    public StringCriterion? AliasesCriterion { get; set; }
    public DateCriterion? DeathDateCriterion { get; set; }
    public IntCriterion? PlayCountCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public MultiIdCriterion? GroupsCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class TagFilter
{
    public string? Name { get; set; }
    public bool? Favorite { get; set; }
    public int? Rating { get; set; }
    // Advanced criteria
    public BoolCriterion? FavoriteCriterion { get; set; }
    public IntCriterion? RatingCriterion { get; set; }
    public IntCriterion? VideoCountCriterion { get; set; }
    public bool VideoCountIncludesChildren { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public bool PerformerCountIncludesChildren { get; set; }
    public MultiIdCriterion? ParentsCriterion { get; set; }
    public MultiIdCriterion? ChildrenCriterion { get; set; }
    public MultiIdCriterion? TagGroupsCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public StringCriterion? NameCriterion { get; set; }
    public StringCriterion? SortNameCriterion { get; set; }
    public StringCriterion? RemoteIdCriterion { get; set; }
    public StringCriterion? RemoteIdValueCriterion { get; set; }
    public IntCriterion? RemoteIdCountCriterion { get; set; }
    public StringCriterion? AliasesCriterion { get; set; }
    public StringCriterion? DescriptionCriterion { get; set; }
    public IntCriterion? ImageCountCriterion { get; set; }
    public bool ImageCountIncludesChildren { get; set; }
    public IntCriterion? GalleryCountCriterion { get; set; }
    public bool GalleryCountIncludesChildren { get; set; }
    public IntCriterion? StudioCountCriterion { get; set; }
    public bool StudioCountIncludesChildren { get; set; }
    public IntCriterion? GroupCountCriterion { get; set; }
    public bool GroupCountIncludesChildren { get; set; }
    public IntCriterion? ParentCountCriterion { get; set; }
    public IntCriterion? ChildCountCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
    /// <summary>Namespaced predicates contributed and executed by enabled extensions.</summary>
    public List<ExtensionFilterCriterion> ExtensionCriteria { get; set; } = [];
}

public sealed class ExtensionFilterCriterion
{
    public string ExtensionId { get; set; } = string.Empty;
    public string FilterId { get; set; } = string.Empty;
    public string Modifier { get; set; } = "equals";
    public JsonElement Value { get; set; }
}

public class StudioFilter
{
    public string? Name { get; set; }
    public bool? Favorite { get; set; }
    public int? ParentId { get; set; }
    public List<int>? TagIds { get; set; }
    // Advanced criteria
    public IntCriterion? RatingCriterion { get; set; }
    public BoolCriterion? FavoriteCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public IntCriterion? VideoCountCriterion { get; set; }
    public IntCriterion? GalleryCountCriterion { get; set; }
    public IntCriterion? ImageCountCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public StringCriterion? RemoteIdCriterion { get; set; }
    public StringCriterion? RemoteIdValueCriterion { get; set; }
    public IntCriterion? RemoteIdCountCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public StringCriterion? NameCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? AliasesCriterion { get; set; }
    public MultiIdCriterion? ParentsCriterion { get; set; }
    public IntCriterion? ParentCountCriterion { get; set; }
    public IntCriterion? ChildCountCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? GroupCountCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class GalleryFilter
{
    public List<int>? Ids { get; set; }
    public string? Title { get; set; }
    public int? Rating { get; set; }
    public bool? Organized { get; set; }
    public int? StudioId { get; set; }
    public int? ImageId { get; set; }
    public List<int>? TagIds { get; set; }
    public List<int>? PerformerIds { get; set; }
    // Advanced criteria
    public IntCriterion? RatingCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public IntCriterion? ImageCountCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public TimestampCriterion? LastLikedAtCriterion { get; set; }
    public StringCriterion? TitleCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public FingerprintCriterion? FingerprintCriterion { get; set; }
    public StringCriterion? ChecksumCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public BoolCriterion? PerformerFavoriteCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public StringCriterion? CodeCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? PhotographerCriterion { get; set; }
    public IntCriterion? FileCountCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public IntCriterion? PerformerAgeCriterion { get; set; }
    public IntCriterion? TypicalResolutionCriterion { get; set; }
    public MultiIdCriterion? VideosCriterion { get; set; }
    public MultiIdCriterion? PerformerTagsCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class ImageFilter
{
    public List<int>? Ids { get; set; }
    public string? Title { get; set; }
    public int? Rating { get; set; }
    public bool? Organized { get; set; }
    public int? StudioId { get; set; }
    public int? GalleryId { get; set; }
    public List<int>? TagIds { get; set; }
    public List<int>? PerformerIds { get; set; }
    // Advanced criteria
    public IntCriterion? RatingCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public MultiIdCriterion? GalleriesCriterion { get; set; }
    public StringCriterion? TitleCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public IntCriterion? ResolutionCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public FingerprintCriterion? FingerprintCriterion { get; set; }
    public StringCriterion? ChecksumCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public BoolCriterion? PerformerFavoriteCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public StringCriterion? CodeCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? PhotographerCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public IntCriterion? FileCountCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public IntCriterion? PerformerAgeCriterion { get; set; }
    public StringCriterion? OrientationCriterion { get; set; }
    public MultiIdCriterion? PerformerTagsCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class AudioFilter
{
    public IntCriterion? RatingCriterion { get; set; }
    public StringCriterion? TitleCriterion { get; set; }
    public StringCriterion? CodeCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public StringCriterion? FormatCriterion { get; set; }
    public StringCriterion? AudioCodecCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public BoolCriterion? HasVideoFilesCriterion { get; set; }
    public BoolCriterion? HasCoverCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public IntCriterion? DurationCriterion { get; set; }
    public IntCriterion? BitRateCriterion { get; set; }
    public IntCriterion? FileSizeCriterion { get; set; }
    public TimestampCriterion? FileModTimeCriterion { get; set; }
    public IntCriterion? FileCountCriterion { get; set; }
    public IntCriterion? TrackCountCriterion { get; set; }
    public StringCriterion? TrackTitleCriterion { get; set; }
    public IntCriterion? SampleRateCriterion { get; set; }
    public IntCriterion? ChannelsCriterion { get; set; }
    public IntCriterion? PlayCountCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public IntCriterion? PlayDurationCriterion { get; set; }
    public TimestampCriterion? LastPlayedAtCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public MultiIdCriterion? PerformerTagsCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public MultiIdCriterion? GroupsCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class TextDocumentFilter
{
    public IntCriterion? RatingCriterion { get; set; }
    public StringCriterion? TitleCriterion { get; set; }
    public StringCriterion? CodeCriterion { get; set; }
    public StringCriterion? DetailsCriterion { get; set; }
    public StringCriterion? ContentCriterion { get; set; }
    public StringCriterion? PathCriterion { get; set; }
    public StringCriterion? FormatCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public BoolCriterion? OrganizedCriterion { get; set; }
    public BoolCriterion? HasCoverCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public IntCriterion? WordCountCriterion { get; set; }
    public IntCriterion? PageCountCriterion { get; set; }
    public IntCriterion? FileSizeCriterion { get; set; }
    public TimestampCriterion? FileModTimeCriterion { get; set; }
    public IntCriterion? FileCountCriterion { get; set; }
    public IntCriterion? PlayCountCriterion { get; set; }
    public IntCriterion? LikeCounterCriterion { get; set; }
    public IntCriterion? PlayDurationCriterion { get; set; }
    public TimestampCriterion? LastReadAtCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public IntCriterion? PerformerCountCriterion { get; set; }
    public MultiIdCriterion? PerformerTagsCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public MultiIdCriterion? GroupsCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}

public class GroupFilter
{
    public string? Name { get; set; }
    public int? Rating { get; set; }
    public int? StudioId { get; set; }
    public List<int>? TagIds { get; set; }
    // Advanced criteria
    public IntCriterion? RatingCriterion { get; set; }
    public IntCriterion? DurationCriterion { get; set; }
    public StringCriterion? NameCriterion { get; set; }
    public MultiIdCriterion? StudiosCriterion { get; set; }
    public MultiIdCriterion? TagsCriterion { get; set; }
    public DateCriterion? DateCriterion { get; set; }
    public StringCriterion? UrlCriterion { get; set; }
    public TimestampCriterion? CreatedAtCriterion { get; set; }
    public TimestampCriterion? UpdatedAtCriterion { get; set; }
    public BoolCriterion? IsMissingCriterion { get; set; }
    public StringCriterion? DirectorCriterion { get; set; }
    public StringCriterion? SynopsisCriterion { get; set; }
    public StringCriterion? KindCriterion { get; set; }
    public StringCriterion? AliasesCriterion { get; set; }
    public StringCriterion? QuerySourceKeyCriterion { get; set; }
    public StringCriterion? AllowedHostTypesCriterion { get; set; }
    public BoolCriterion? HasQueryCriterion { get; set; }
    public BoolCriterion? IsBuiltInCriterion { get; set; }
    public BoolCriterion? ShowInVideoListsCriterion { get; set; }
    public TimestampCriterion? LastResolvedAtCriterion { get; set; }
    public IntCriterion? SortOrderCriterion { get; set; }
    public IntCriterion? CachedItemCountCriterion { get; set; }
    public MultiIdCriterion? PerformersCriterion { get; set; }
    public IntCriterion? ItemCountCriterion { get; set; }
    public IntCriterion? VideoCountCriterion { get; set; }
    public IntCriterion? ImageCountCriterion { get; set; }
    public IntCriterion? AudioCountCriterion { get; set; }
    public IntCriterion? TextCountCriterion { get; set; }
    public IntCriterion? GalleryCountCriterion { get; set; }
    public IntCriterion? PerformerItemCountCriterion { get; set; }
    public IntCriterion? StudioItemCountCriterion { get; set; }
    public IntCriterion? TagItemCountCriterion { get; set; }
    public IntCriterion? FaceCountCriterion { get; set; }
    public IntCriterion? SegmentCountCriterion { get; set; }
    public IntCriterion? SubGroupCountCriterion { get; set; }
    public IntCriterion? ContainingGroupCountCriterion { get; set; }
    public IntCriterion? TagCountCriterion { get; set; }
    public CustomFieldCriterion? CustomFieldCriterion { get; set; }
    public List<CustomFieldCriterion> CustomFieldCriteria { get; set; } = [];
}
