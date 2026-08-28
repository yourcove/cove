using System.Reflection;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public sealed record EntityListSortDefinition(string Entity, string Key, string Label, string? KnownBrokenReason = null)
{
    public string RowId => $"sort:{Entity}:{Key}";
}

public sealed record EntityListFilterDefinition(string Entity, string Key, string CriterionType, IReadOnlyList<string> Operators, string? KnownBrokenReason = null)
{
    public string RowId => $"filter:{Entity}:{Key}";
}

public static class EntityListSortFilterCatalog
{
    private static readonly IReadOnlyDictionary<string, Type> FilterTypesByEntity = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
    {
        ["videos"] = typeof(VideoFilter),
        ["images"] = typeof(ImageFilter),
        ["audios"] = typeof(AudioFilter),
        ["texts"] = typeof(TextDocumentFilter),
        ["galleries"] = typeof(GalleryFilter),
        ["groups"] = typeof(GroupFilter),
        ["performers"] = typeof(PerformerFilter),
        ["studios"] = typeof(StudioFilter),
        ["tags"] = typeof(TagFilter),
    };

    public static IReadOnlyList<string> Entities { get; } =
    [
        "videos",
        "images",
        "audios",
        "texts",
        "galleries",
        "groups",
        "segments",
        "performers",
        "studios",
        "tags",
        "faces",
    ];

    public static IReadOnlyList<EntityListSortDefinition> Sorts { get; } =
    [
        // Videos
        new("videos", "updated_at", "Updated At"),
        new("videos", "created_at", "Created At"),
        new("videos", "title", "Title"),
        new("videos", "date", "Date"),
        new("videos", "rating", "Rating"),
        new("videos", "play_count", "Play Count"),
        new("videos", "like_counter", "Likes"),
        new("videos", "last_like_at", "Last Like Date"),
        new("videos", "duration", "Duration"),
        new("videos", "file_size", "File Size"),
        new("videos", "file_mod_time", "File Modification Time"),
        new("videos", "file_count", "File Count"),
        new("videos", "path", "Path"),
        new("videos", "resolution", "Resolution"),
        new("videos", "framerate", "Frame Rate"),
        new("videos", "bitrate", "Bitrate"),
        new("videos", "phash", "pHash"),
        new("videos", "tag_count", "Tag Count"),
        new("videos", "performer_count", "Performer Count"),
        new("videos", "performer_age", "Performer Age"),
        new("videos", "studio", "Studio"),
        new("videos", "code", "Studio Code"),
        new("videos", "last_played_at", "Last Played"),
        new("videos", "play_duration", "Play Duration"),
        new("videos", "resume_time", "Resume Time"),
        new("videos", "organized", "Organized"),
        new("videos", "random", "Random"),

        // Images
        new("images", "updated_at", "Updated At"),
        new("images", "created_at", "Created At"),
        new("images", "date", "Date"),
        new("images", "file_mod_time", "File Modification Time"),
        new("images", "file_size", "File Size"),
        new("images", "resolution", "Resolution"),
        new("images", "path", "Path"),
        new("images", "title", "Title"),
        new("images", "rating", "Rating"),
        new("images", "like_counter", "Likes"),
        new("images", "performer_count", "Performer Count"),
        new("images", "tag_count", "Tag Count"),
        new("images", "random", "Random"),
        new("images", "visual_match", "Visual Match"),

        // Audios
        new("audios", "updatedAt", "Updated At"),
        new("audios", "createdAt", "Created At"),
        new("audios", "date", "Date"),
        new("audios", "duration", "Duration"),
        new("audios", "rating", "Rating"),
        new("audios", "play_count", "Play Count"),
        new("audios", "like_counter", "Likes"),
        new("audios", "play_duration", "Play Duration"),
        new("audios", "last_played_at", "Last Played"),
        new("audios", "file_size", "File Size"),
        new("audios", "file_mod_time", "File Modification Time"),
        new("audios", "file_count", "File Count"),
        new("audios", "path", "Path"),
        new("audios", "bitrate", "Bitrate"),
        new("audios", "has_video_files", "Has Video Files"),
        new("audios", "track_count", "Track Count"),
        new("audios", "tag_count", "Tag Count"),
        new("audios", "performer_count", "Performer Count"),
        new("audios", "title", "Title"),
        new("audios", "random", "Random"),

        // Texts
        new("texts", "updatedAt", "Updated At"),
        new("texts", "createdAt", "Created At"),
        new("texts", "date", "Date"),
        new("texts", "words", "Words"),
        new("texts", "pages", "Pages"),
        new("texts", "rating", "Rating"),
        new("texts", "read_count", "Read Count"),
        new("texts", "like_counter", "Likes"),
        new("texts", "read_duration", "Read Duration"),
        new("texts", "last_read_at", "Last Read"),
        new("texts", "file_size", "File Size"),
        new("texts", "file_mod_time", "File Modification Time"),
        new("texts", "file_count", "File Count"),
        new("texts", "path", "Path"),
        new("texts", "tag_count", "Tag Count"),
        new("texts", "performer_count", "Performer Count"),
        new("texts", "title", "Title"),
        new("texts", "random", "Random"),

        // Galleries
        new("galleries", "updated_at", "Updated At"),
        new("galleries", "created_at", "Created At"),
        new("galleries", "date", "Date"),
        new("galleries", "studio", "Studio"),
        new("galleries", "file_mod_time", "File Modification Time"),
        new("galleries", "file_count", "File Count"),
        new("galleries", "path", "Path"),
        new("galleries", "title", "Title"),
        new("galleries", "code", "Studio Code"),
        new("galleries", "photographer", "Photographer"),
        new("galleries", "organized", "Organized"),
        new("galleries", "rating", "Rating"),
        new("galleries", "like_counter", "Likes"),
        new("galleries", "last_like_at", "Last Liked Date"),
        new("galleries", "image_count", "Image Count"),
        new("galleries", "video_count", "Video Count"),
        new("galleries", "performer_count", "Performer Count"),
        new("galleries", "tag_count", "Tag Count"),
        new("galleries", "typical_resolution", "Typical Resolution"),
        new("galleries", "random", "Random"),

        // Groups
        new("groups", "sort_order", "Manual Order"),
        new("groups", "name", "Name"),
        new("groups", "date", "Date"),
        new("groups", "rating", "Rating"),
        new("groups", "random", "Random"),
        new("groups", "created_at", "Created At"),
        new("groups", "updated_at", "Updated At"),
        new("groups", "item_count", "Item Count"),
        new("groups", "video_count", "Video Count"),
        new("groups", "image_count", "Image Count"),
        new("groups", "audio_count", "Audio Count"),
        new("groups", "text_count", "Text Count"),
        new("groups", "gallery_count", "Gallery Count"),
        new("groups", "performer_count", "Performer Item Count"),
        new("groups", "studio_count", "Studio Item Count"),
        new("groups", "tag_item_count", "Tag Item Count"),
        new("groups", "tag_count", "Tag Count"),
        new("groups", "face_count", "Face Count"),
        new("groups", "segment_count", "Segment Count"),
        new("groups", "subgroup_count", "Subgroup Count"),
        new("groups", "containing_group_count", "Containing Group Count"),
        new("groups", "cached_item_count", "Cached Item Count"),
        new("groups", "last_resolved_at", "Last Resolved"),
        new("groups", "query_source_key", "Query Source Key"),
        new("groups", "show_in_video_lists", "Show In Video Lists"),
        new("groups", "aliases", "Aliases"),

        // Segments
        new("segments", "random", "Random"),
        new("segments", "updated_at", "Updated At"),
        new("segments", "created_at", "Created At"),
        new("segments", "start_sec", "Start Time"),
        new("segments", "end_sec", "End Time"),
        new("segments", "duration", "Duration"),
        new("segments", "confidence", "Confidence"),
        new("segments", "title", "Title"),
        new("segments", "video_title", "Video Title"),
        new("segments", "kind", "Kind"),
        new("segments", "source_key", "Source Key"),
        new("segments", "tag_name", "Tag Name"),
        new("segments", "performer", "Performer"),
        new("segments", "ref", "Reference"),

        // Performers
        new("performers", "name", "Name"),
        new("performers", "rating", "Rating"),
        new("performers", "video_count", "Video Count"),
        new("performers", "audio_count", "Audio Count"),
        new("performers", "text_count", "Text Count"),
        new("performers", "image_count", "Image Count"),
        new("performers", "gallery_count", "Gallery Count"),
        new("performers", "latest_video_date", "Latest Video Date"),
        new("performers", "total_file_size", "Total File Size"),
        new("performers", "tag_count", "Tag Count"),
        new("performers", "career_length", "Career Length"),
        new("performers", "last_like_at", "Last Like At"),
        new("performers", "last_played_at", "Last Played At"),
        new("performers", "measurements", "Measurements"),
        new("performers", "like_counter", "Likes"),
        new("performers", "play_count", "Play Count"),
        new("performers", "birthdate", "Birthdate"),
        new("performers", "height", "Height"),
        new("performers", "weight", "Weight"),
        new("performers", "created_at", "Created At"),
        new("performers", "updated_at", "Updated At"),
        new("performers", "random", "Random"),

        // Studios
        new("studios", "name", "Name"),
        new("studios", "rating", "Rating"),
        new("studios", "video_count", "Video Count"),
        new("studios", "gallery_count", "Gallery Count"),
        new("studios", "image_count", "Image Count"),
        new("studios", "latest_video_date", "Latest Video Date"),
        new("studios", "total_file_size", "Total File Size"),
        new("studios", "parent_count", "Parent Studio Count"),
        new("studios", "child_count", "Substudios Count"),
        new("studios", "tag_count", "Tag Count"),
        new("studios", "updated_at", "Updated At"),
        new("studios", "random", "Random"),
        new("studios", "created_at", "Created At"),

        // Tags
        new("tags", "name", "Name"),
        new("tags", "rating", "Rating"),
        new("tags", "tag_group", "Tag Group"),
        new("tags", "video_count", "Video Count"),
        new("tags", "gallery_count", "Gallery Count"),
        new("tags", "group_count", "Group Count"),
        new("tags", "image_count", "Image Count"),
        new("tags", "performer_count", "Performer Count"),
        new("tags", "studio_count", "Studio Count"),
        new("tags", "latest_video_date", "Latest Video Date"),
        new("tags", "total_file_size", "Total File Size"),
        new("tags", "random", "Random"),
        new("tags", "created_at", "Created At"),
        new("tags", "updated_at", "Updated At"),

        // Faces — single key per field; direction comes from the shared asc/desc toggle,
        // matching the convention used by every other entity list.
        new("faces", "suggestion_confidence", "Suggested Match Confidence"),
        new("faces", "updated", "Updated At"),
        new("faces", "created", "Created At"),
        new("faces", "label", "Label"),
        new("faces", "performer_name", "Performer Name"),
        new("faces", "primary_source_key", "Source"),
        new("faces", "detection_count", "Detection Count"),
        new("faces", "appearance_count", "Appearance Count"),
        new("faces", "frame_sample_count", "Frame Sample Count"),
        new("faces", "video_count", "Video Count"),
        new("faces", "image_count", "Image Count"),
        new("faces", "cover_present", "Has Cover"),
        new("faces", "random", "Random"),
    ];

    public static IReadOnlyList<EntityListFilterDefinition> Filters { get; } = BuildFilters();

    public static Type? GetFilterType(string entity)
        => FilterTypesByEntity.TryGetValue(entity, out var filterType) ? filterType : null;

    private static IReadOnlyList<EntityListFilterDefinition> BuildFilters()
    {
        var rows = new List<EntityListFilterDefinition>();
        foreach (var (entity, filterType) in FilterTypesByEntity)
        {
            foreach (var property in filterType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.Name is nameof(VideoFilter.CustomFieldCriteria) or nameof(VideoFilter.CustomFieldCriterion))
                    continue;

                var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (!TryGetCriterionOperators(propertyType, out var criterionType, out var operators))
                    continue;

                rows.Add(new EntityListFilterDefinition(entity, property.Name, criterionType, operators));
            }
        }

        rows.AddRange([
            new EntityListFilterDefinition("faces", "performerId", "query-param:int", ["equals"]),
            new EntityListFilterDefinition("faces", "linked", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("faces", "ignored", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("faces", "merged", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("faces", "mergedIntoFaceId", "query-param:int", ["equals"]),
            new EntityListFilterDefinition("faces", "label", "query-param:string", ["equals", "not_equals", "includes", "excludes", "matches_regex", "not_matches_regex", "is_null", "not_null"]),
            new EntityListFilterDefinition("faces", "primarySourceKey", "query-param:string", ["equals", "not_equals", "includes", "excludes", "matches_regex", "not_matches_regex", "is_null", "not_null"]),
            new EntityListFilterDefinition("faces", "hasCover", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("faces", "detectionCount", "query-param:int", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("faces", "appearanceCount", "query-param:int", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("faces", "frameSampleCount", "query-param:int", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("faces", "videoCount", "query-param:int", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("faces", "imageCount", "query-param:int", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("segments", "ids", "query-param:int-list", ["includes"]),
            new EntityListFilterDefinition("segments", "videoId", "query-param:int", ["equals"]),
            new EntityListFilterDefinition("segments", "videoIds", "query-param:int-list", ["includes"]),
            new EntityListFilterDefinition("segments", "excludeVideoIds", "query-param:int-list", ["excludes"]),
            new EntityListFilterDefinition("segments", "videoTitle", "query-param:string", ["includes"]),
            new EntityListFilterDefinition("segments", "tagId", "query-param:int", ["equals"]),
            new EntityListFilterDefinition("segments", "tagIds", "query-param:int-list", ["includes"]),
            new EntityListFilterDefinition("segments", "kind", "query-param:string", ["includes"]),
            new EntityListFilterDefinition("segments", "sourceKey", "query-param:string", ["includes"]),
            new EntityListFilterDefinition("segments", "title", "query-param:string", ["equals", "not_equals", "includes", "excludes", "is_null", "not_null"]),
            new EntityListFilterDefinition("segments", "hostType", "query-param:enum", ["equals"]),
            new EntityListFilterDefinition("segments", "sourceRunId", "query-param:string", ["equals", "not_equals", "includes", "excludes", "is_null", "not_null"]),
            new EntityListFilterDefinition("segments", "colorHint", "query-param:string", ["equals", "not_equals", "includes", "excludes", "is_null", "not_null"]),
            new EntityListFilterDefinition("segments", "hasImage", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("segments", "hasPayload", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("segments", "startSec", "query-param:number", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("segments", "endSec", "query-param:number", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("segments", "createdAt", "query-param:timestamp", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("segments", "updatedAt", "query-param:timestamp", ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"]),
            new EntityListFilterDefinition("segments", "tagged", "query-param:bool", ["true", "false"]),
            new EntityListFilterDefinition("segments", "minConfidence", "query-param:number", ["greater_than_or_equal"]),
            new EntityListFilterDefinition("segments", "minDurationSec", "query-param:number", ["greater_than_or_equal"]),
        ]);

        return rows
            .OrderBy(row => EntityOrder(row.Entity))
            .ThenBy(row => row.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int EntityOrder(string entity)
    {
        for (var index = 0; index < Entities.Count; index++)
        {
            if (Entities[index].Equals(entity, StringComparison.OrdinalIgnoreCase))
                return index;
        }

        return int.MaxValue;
    }

    private static bool TryGetCriterionOperators(Type type, out string criterionType, out IReadOnlyList<string> operators)
    {
        if (type == typeof(IntCriterion))
        {
            criterionType = nameof(IntCriterion);
            operators = ["equals", "not_equals", "greater_than", "less_than", "between", "not_between"];
            return true;
        }

        if (type == typeof(StringCriterion))
        {
            criterionType = nameof(StringCriterion);
            operators = ["equals", "not_equals", "includes", "excludes", "matches_regex", "not_matches_regex", "is_null", "not_null"];
            return true;
        }

        if (type == typeof(BoolCriterion))
        {
            criterionType = nameof(BoolCriterion);
            operators = ["true", "false"];
            return true;
        }

        if (type == typeof(MultiIdCriterion))
        {
            criterionType = nameof(MultiIdCriterion);
            operators = ["includes", "excludes", "includes_all", "excludes_all", "is_null", "not_null"];
            return true;
        }

        if (type == typeof(DateCriterion))
        {
            criterionType = nameof(DateCriterion);
            operators = ["equals", "not_equals", "greater_than", "less_than", "between", "not_between", "is_null", "not_null"];
            return true;
        }

        if (type == typeof(TimestampCriterion))
        {
            criterionType = nameof(TimestampCriterion);
            operators = ["equals", "not_equals", "greater_than", "less_than", "between", "not_between", "is_null", "not_null"];
            return true;
        }

        if (type == typeof(FingerprintCriterion))
        {
            criterionType = nameof(FingerprintCriterion);
            operators = ["equals", "not_equals", "includes", "excludes", "is_null", "not_null"];
            return true;
        }

        if (type == typeof(TagDurationCriterion))
        {
            criterionType = nameof(TagDurationCriterion);
            operators = ["greater_than", "less_than", "between", "not_between"];
            return true;
        }

        criterionType = string.Empty;
        operators = [];
        return false;
    }
}
