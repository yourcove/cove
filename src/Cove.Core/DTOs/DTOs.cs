using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cove.Core.Enums;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.Core.DTOs;

/// <summary>Generic request for POST-based filtered queries.</summary>
public class FilteredQueryRequest<TFilter> where TFilter : class, new()
{
    public FindFilter? FindFilter { get; set; }
    public TFilter? ObjectFilter { get; set; }
}

// ===== VIDEO DTOs =====
public record VideoDto(
    int Id, string? Title, string? Code, string? Details, string? Director,
    string? Date, bool Organized, bool IsVr, int? StudioId, string? StudioName,
    string? Captions,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    List<VideoFileDto> Files,
    List<GroupSummaryDto> Groups, List<GallerySummaryDto> Galleries,
    List<VideoRemoteIdDto> RemoteIds, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    List<TagApplicationDto>? ContextTagApplications = null,
    List<FieldProvenanceDto>? FieldProvenance = null,
    int? ParentVideoId = null,
    string? ParentVideoTitle = null,
    double? ClipStartSec = null,
    double? ClipEndSec = null,
    int ChildVideoCount = 0,
    string? ImagePath = null);

public record VideoListEntryDto(string Kind, int Id, VideoDto? Video = null, GroupDto? Group = null);

public record VideoRemoteIdDto(string Endpoint, string RemoteId);

public record VideoGroupInputDto(int GroupId, int VideoIndex = 0);
public record VideoCreateDto(
    string? Title, string? Code, string? Details, string? Director,
    string? Date, int? Rating, bool Organized, int? StudioId,
    string? Captions,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds,
    List<VideoGroupInputDto>? Groups, List<VideoRemoteIdDto>? RemoteIds = null, Dictionary<string, object>? CustomFields = null,
    int? ParentVideoId = null, double? ClipStartSec = null, double? ClipEndSec = null, bool IsVr = false);

public record VideoUpdateDto(
    string? Title, string? Code, string? Details, string? Director,
    string? Date, int? Rating, bool? Organized, int? StudioId,
    string? Captions,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds,
    List<VideoGroupInputDto>? Groups, List<VideoRemoteIdDto>? RemoteIds, Dictionary<string, object>? CustomFields,
    double? ClipStartSec = null, double? ClipEndSec = null, bool? IsVr = null,
    List<string>? ClearFields = null);

// ===== PERFORMER DTOs =====
public record PerformerDto(
    int Id, string Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool Favorite, string? Details,
    List<string> Urls, List<string> Aliases, List<TagDto> Tags,
    List<PerformerRemoteIdDto> RemoteIds,
    int VideoCount, int ImageCount, int GalleryCount, int GroupCount, int AudioCount, int TextCount,
    string? ImagePath, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    List<FieldProvenanceDto>? FieldProvenance = null, int FaceCount = 0);

public record PerformerRemoteIdDto(string Endpoint, string RemoteId);

public record PerformerSummaryDto(int Id, string Name, string? Disambiguation, string? Gender, string? Birthdate, bool Favorite, string? ImagePath, int VideoCount = 0, int ImageCount = 0, int GalleryCount = 0, int AudioCount = 0, int TextCount = 0);

public record GallerySummaryDto(int Id, string? Title, string? Date);

public record PerformerCreateDto(
    string Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool Favorite, int? Rating, string? Details,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, List<PerformerRemoteIdDto>? RemoteIds = null, Dictionary<string, object>? CustomFields = null);

public record PerformerUpdateDto(
    string? Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool? Favorite, int? Rating, string? Details,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, List<PerformerRemoteIdDto>? RemoteIds,
    Dictionary<string, object>? CustomFields, List<string>? ClearFields = null);

// ===== TAG DTOs =====
public record TagProvenanceDto(
    string SourceKey,
    string? SourceRunId,
    string? ModelKey,
    float? Confidence,
    string AppliedAt,
    string? ContextType = null,
    int? ContextId = null,
    double? TotalDurationSec = null,
    double? HostDurationSec = null);

public record FieldProvenanceDto(
    string FieldKey,
    string SourceKey,
    string? SourceRunId,
    string? ModelKey,
    JsonElement? Value,
    float? Confidence,
    string CreatedAt);

public record TagDto(
    int Id,
    string Name,
    string? Description,
    bool Favorite,
    List<string> Aliases,
    bool? ShowAsSegment = null,
    string? SegmentColorOverride = null,
    int? SegmentLaneOverride = null,
    List<TagProvenanceDto>? Provenance = null,
    string? Color = null,
    int? TagGroupId = null,
    string? TagGroupName = null,
    string? TagGroupColor = null,
    double? MinOccurrenceSec = null,
    double? MinOccurrencePercent = null,
    Dictionary<string, object>? CustomFields = null,
    bool IsDerived = false,
    bool CanRemove = true,
    double? EffectiveDurationSec = null,
    double? EffectiveDurationPercent = null,
    bool Organized = false,
    bool CanReportIncorrect = false,
    bool HasImage = false);

public record TagListDto(
    int Id,
    string Name,
    string? Description,
    bool Favorite,
    List<string> Aliases,
    int VideoCount,
    int SegmentCount,
    int ImageCount,
    int GalleryCount,
    int GroupCount,
    int PerformerCount,
    int StudioCount,
    string? ImagePath,
    bool? ShowAsSegment = null,
    string? SegmentColorOverride = null,
    int? SegmentLaneOverride = null,
    string? Color = null,
    int? TagGroupId = null,
    string? TagGroupName = null,
    string? TagGroupColor = null,
    double? MinOccurrenceSec = null,
    double? MinOccurrencePercent = null,
    bool Organized = false);

public record TagDetailDto(
    int Id, string Name, string? SortName, string? Description, bool Favorite,
    List<string> Aliases, List<TagDto> Parents, List<TagDto> Children,
    int VideoCount, int PerformerCount, int ImageCount, int GalleryCount,
    int StudioCount, int GroupCount, int AudioCount, int TextCount, int SegmentCount,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    bool? ShowAsSegment = null, string? SegmentColorOverride = null, int? SegmentLaneOverride = null,
    string? Color = null, int? TagGroupId = null, string? TagGroupName = null, string? TagGroupColor = null,
    double? MinOccurrenceSec = null, double? MinOccurrencePercent = null, List<TagRemoteIdDto>? RemoteIds = null,
    bool Organized = false,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record TagRemoteIdDto(string Endpoint, string RemoteId);

public record TagSegmentWallDto(
    int Id,
    string? Title,
    double StartSec,
    double? EndSec,
    string Kind,
    string SourceKey,
    float? Confidence,
    int VideoId,
    string VideoTitle);

public record TagGraphNodeDto(
    int Id,
    string Name,
    bool Favorite,
    string? Description,
    string? ImagePath,
    int? TagGroupId,
    string? TagGroupName,
    string? TagGroupColor,
    List<int> ParentIds,
    List<int> ChildIds,
    int TotalUsageCount,
    int VideoCount,
    int SegmentCount,
    int ImageCount,
    int GalleryCount,
    int GroupCount,
    int PerformerCount,
    int StudioCount);
public record TagGraphLinkDto(int SourceId, int TargetId);
public record TagGraphResponseDto(List<TagGraphNodeDto> Items, List<TagGraphLinkDto> Links, int TotalCount);

public record TagGroupDto(int Id, string Name, string? Description, string? Color, int SortOrder, int TagCount, string CreatedAt, string UpdatedAt);
public record TagGroupCreateDto(string Name, string? Description = null, string? Color = null, int? SortOrder = null);
public record TagGroupUpdateDto(string? Name = null, string? Description = null, string? Color = null, int? SortOrder = null);

public record TagApplicationDto(
    int Id,
    string HostType,
    int HostId,
    string? ContextType,
    int? ContextId,
    TagDto Tag,
    string SourceKey,
    string? SourceRunId,
    string? ModelKey,
    float? Confidence,
    double? TotalDurationSec,
    double? HostDurationSec,
    string AppliedAt);

public record TagApplicationCreateDto(
    string HostType,
    int HostId,
    int TagId,
    string SourceKey = "user",
    string? ContextType = null,
    int? ContextId = null,
    string? SourceRunId = null,
    string? ModelKey = null,
    float? Confidence = null,
    double? TotalDurationSec = null,
    double? HostDurationSec = null);

public record TagCreateDto(
    string Name,
    string? SortName,
    string? Description,
    bool Favorite,
    List<string>? Aliases,
    List<int>? ParentIds,
    List<int>? ChildIds,
    bool? ShowAsSegment = null,
    string? SegmentColorOverride = null,
    int? SegmentLaneOverride = null,
    string? Color = null,
    int? TagGroupId = null,
    double? MinOccurrenceSec = null,
    double? MinOccurrencePercent = null,
    Dictionary<string, object>? CustomFields = null,
    List<TagRemoteIdDto>? RemoteIds = null,
    bool Organized = false);
public record TagUpdateDto(
    string? Name,
    string? SortName,
    string? Description,
    bool? Favorite,
    List<string>? Aliases,
    List<int>? ParentIds,
    List<int>? ChildIds,
    Dictionary<string, object>? CustomFields,
    bool? ShowAsSegment = null,
    string? SegmentColorOverride = null,
    int? SegmentLaneOverride = null,
    string? Color = null,
    int? TagGroupId = null,
    double? MinOccurrenceSec = null,
    double? MinOccurrencePercent = null,
    List<TagRemoteIdDto>? RemoteIds = null,
    bool? Organized = null,
    List<string>? ClearFields = null);

// ===== STUDIO DTOs =====
public record StudioDto(int Id, string Name, int? ParentId, string? ParentName, bool Favorite, string? Details, bool Organized,
    List<string> Urls, List<string> Aliases, List<TagDto> Tags, List<StudioRemoteIdDto> RemoteIds,
    int VideoCount, int ImageCount, int GalleryCount, int GroupCount, int PerformerCount, int ChildStudioCount, int AudioCount, int TextCount,
    string? ImagePath, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record StudioRemoteIdDto(string Endpoint, string RemoteId);

public record StudioCreateDto(string Name, int? ParentId, int? Rating, bool Favorite, string? Details, bool Organized,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, List<StudioRemoteIdDto>? RemoteIds = null, Dictionary<string, object>? CustomFields = null);

public record StudioUpdateDto(string? Name, int? ParentId, int? Rating, bool? Favorite, string? Details, bool? Organized,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, List<StudioRemoteIdDto>? RemoteIds,
    Dictionary<string, object>? CustomFields, List<string>? ClearFields = null);

// ===== GALLERY DTOs =====
public record GalleryDto(int Id, string? Title, string? Code, string? Date, string? Details, string? Photographer,
    bool Organized, int? StudioId, string? StudioName,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    int ImageCount, int VideoCount, List<int> VideoIds, string? FolderPath, List<GalleryFileInfoDto> Files,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    string? CoverPath = null, int? CoverImageId = null,
    string? BackCoverPath = null,
    List<FieldProvenanceDto>? FieldProvenance = null,
    // Filename/folder-name fallback for display when Title is null (scan no longer stores the
    // filename as Title). Prefers a zip-gallery file basename, else the folder name. Null when neither
    // is available; the UI falls back to "Gallery {id}".
    string? DisplayName = null);

public record GalleryFileInfoDto(int Id, string Path, long Size, string ModTime, List<FingerprintDto> Fingerprints);

public record GalleryCreateDto(string? Title, string? Code, string? Date, string? Details, string? Photographer,
    int? Rating, bool Organized, int? StudioId, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? VideoIds, Dictionary<string, object>? CustomFields = null);

public record GalleryUpdateDto(string? Title, string? Code, string? Date, string? Details, string? Photographer,
    int? Rating, bool? Organized, int? StudioId, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds,
    List<int>? VideoIds, Dictionary<string, object>? CustomFields, List<string>? ClearFields = null);

// ===== IMAGE DTOs =====
public record ImageDto(int Id, string? Title, string? Code, string? Details, string? Photographer,
    bool Organized, int? StudioId, string? StudioName, string? Date,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    int GalleryCount, List<int> GalleryIds, List<GallerySummaryDto> Galleries, List<GroupSummaryDto> Groups, List<ImageFileDto> Files, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    List<TagApplicationDto>? ContextTagApplications = null,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record ImageFileDto(int Id, string Path, string Basename, string Format, int Width, int Height, long Size);

public record ImageCreateDto(string? Title, string? Code, string? Details, string? Photographer,
    int? Rating, bool Organized, int? StudioId, string? Date,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds, List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields = null);

public record ImageUpdateDto(string? Title, string? Code, string? Details, string? Photographer,
    int? Rating, bool? Organized, int? StudioId, string? Date,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds,
    List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields, List<string>? ClearFields = null);

// ===== AUDIO DTOs =====
public record AudioDto(
    int Id, string? Title, string? Code, string? Details, bool Organized,
    int? StudioId, string? StudioName, string? Date,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    List<AudioTrackDto> Tracks, List<AudioFileDto> Files, List<GroupSummaryDto> Groups,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    int FileCount, double MaxDuration, bool HasVideoFiles, string? ImagePath = null,
    List<TagApplicationDto>? ContextTagApplications = null,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record AudioFileDto(
    int Id, string Path, string Basename, string Format, double Duration,
    string AudioCodec, long BitRate, int? SampleRate, int? Channels, long Size,
    bool HasVideoTrack);

public record AudioTrackDto(int Id, int OrderIndex, string? Title, double StartSec, double? EndSec);

public record AudioCreateDto(
    string? Title, string? Code, string? Details, bool Organized, int? StudioId,
    string? Date, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds,
    List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields = null);

public record AudioUpdateDto(
    string? Title, string? Code, string? Details, bool? Organized, int? StudioId,
    string? Date, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds,
    List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields,
    List<string>? ClearFields = null);

// ===== TEXT DTOs =====
public record TextDocumentDto(
    int Id, string? Title, string? Code, string? Details, bool Organized,
    int? StudioId, string? StudioName, string? Date,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    List<TextFileDto> Files, List<GroupSummaryDto> Groups,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    int FileCount, int? MaxWordCount, int? MaxPageCount, string? ImagePath = null,
    List<TagApplicationDto>? ContextTagApplications = null,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record TextFileDto(
    int Id, string Path, string Basename, string Format, int? PageCount,
    int? WordCount, string? ExcerptText, long Size);

public record TextContentDto(string Format, string RenderMode, string Content);

public record TextDocumentCreateDto(
    string? Title, string? Code, string? Details, bool Organized, int? StudioId,
    string? Date, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds,
    List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields = null);

public record TextDocumentUpdateDto(
    string? Title, string? Code, string? Details, bool? Organized, int? StudioId,
    string? Date, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds,
    List<VideoGroupInputDto>? GroupIds, Dictionary<string, object>? CustomFields,
    List<string>? ClearFields = null);

// ===== GROUP DTOs =====
public record GroupDto(int Id, string Name, string? Aliases, string? Date,
    int? StudioId, string? StudioName, string? Director, string? Description,
    List<string> Urls, List<TagDto> Tags, int VideoCount, int ItemCount, bool IsCompilation, int SubGroupCount, int ContainingGroupCount,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    string? FrontImagePath, string? BackImagePath,
    GroupKind Kind = GroupKind.Static,
    string? QuerySourceKey = null,
    string? QueryJson = null,
    string? LastResolvedAt = null,
    int? CachedItemCount = null,
    bool ShowInVideoLists = false,
    List<string>? AllowedHostTypes = null,
    int SortOrder = 0,
    int ImageCount = 0,
    int AudioCount = 0,
    int TextCount = 0,
    int GalleryCount = 0,
    int PerformerCount = 0,
    int StudioCount = 0,
    int TagItemCount = 0,
    int FaceCount = 0,
    int SegmentCount = 0,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record GroupSummaryDto(int Id, string Name, int VideoIndex);

public record GroupItemDto(
    int Id,
    int GroupId,
    int OrderIndex,
    GroupItemKind Kind,
    int? VideoId,
    string? VideoTitle,
    string HostType,
    int HostId,
    int? ImageId,
    string? ImageTitle,
    int? ChildGroupId,
    string? ChildGroupName,
    double? StartSec,
    double? EndSec,
    string? Title,
    string? Notes,
    string? SourceSpanKey,
    int? SourceProfileId,
    string? SourceQueryJson,
    string? SnapshotAt,
    string CreatedAt,
    string UpdatedAt);

public record GroupItemCreateDto(
    int OrderIndex,
    GroupItemKind Kind,
    int? VideoId,
    string? HostType,
    int? HostId,
    double? StartSec,
    double? EndSec,
    string? Title,
    string? Notes,
    string? SourceSpanKey,
    int? SourceProfileId,
    string? SourceQueryJson = null);

public record GroupItemUpdateDto(
    int OrderIndex,
    GroupItemKind Kind,
    double? StartSec,
    double? EndSec,
    string? Title,
    string? Notes);

public record GroupItemsReorderDto(List<int> Ids, int StartIndex = 0);

public record GroupItemsRemoveHostsDto(GroupItemKind Kind, List<int> HostIds);

public record GroupItemSpanInputDto(
    string? SpanKey,
    int? VideoId,
    double? StartSec,
    double? EndSec,
    string? Title,
    int? ProfileId,
    SegmentSpanDerivedQueryDto? DerivedQuery = null);

public record GroupItemsFromSpansDto(List<GroupItemSpanInputDto> Spans);

public record GroupPlaybackManifestItemDto(
    int GroupItemId,
    string HostType,
    int HostId,
    int? VideoId,
    int? AudioId,
    int? ImageId,
    int? TextId,
    int? SegmentId,
    string? VideoTitle,
    string Src,
    double StartSec,
    double? EndSec,
    double? DurationSec,
    double? DisplayDurationSec,
    string? PosterPath,
    string? Title,
    string? Format = null,
    bool HasVideoTrack = false);

public record GroupPlaybackManifestDto(List<GroupPlaybackManifestItemDto> Items);

public record GroupCreateDto(string Name, string? Aliases, string? Date,
    int? Rating, int? StudioId, string? Director, string? Description,
    List<string>? Urls, List<int>? TagIds, Dictionary<string, object>? CustomFields = null,
    GroupKind? Kind = null,
    string? QuerySourceKey = null,
    string? QueryJson = null,
    bool? ShowInVideoLists = null,
    List<string>? AllowedHostTypes = null,
    int? SortOrder = null);

public record GroupUpdateDto(string? Name, string? Aliases, string? Date,
    int? Rating, int? StudioId, string? Director, string? Description,
    List<string>? Urls, List<int>? TagIds, Dictionary<string, object>? CustomFields,
    GroupKind? Kind = null,
    string? QuerySourceKey = null,
    string? QueryJson = null,
    bool? ShowInVideoLists = null,
    List<string>? AllowedHostTypes = null,
    int? SortOrder = null,
    List<string>? ClearFields = null);

public record GroupQueryUpdateDto(string QuerySourceKey, string? QueryJson = null, int? CacheTtlSec = null);

// ===== SHARED DTOs =====
public record VideoFileDto(int Id, string Path, string Basename, string Format,
    int Width, int Height, double Duration, string VideoCodec, string AudioCodec,
    double FrameRate, long BitRate, long Size, List<FingerprintDto> Fingerprints,
    List<CaptionDto>? Captions = null);

public record CaptionDto(int Id, string LanguageCode, string CaptionType, string Filename);

public record FingerprintDto(string Type, string Value);

public record FileBackedCreateDto(string FilePath);

public record TagSummaryDto(int Id, string Name);

public sealed record ResolvedSpan(
    string SpanKey,
    SegmentHostType HostType,
    int HostId,
    double StartSec,
    double EndSec,
    string? SourceKey,
    string? Kind,
    int? TagId,
    string? TagName,
    string? ColorHint,
    int? Lane,
    bool CollapsedToInstant,
    IReadOnlyList<int> SegmentIds);

public record ResolvedSpanIntervalDto(double StartSec, double EndSec);

public record ResolvedSpanDetailDto(
    ResolvedSpan Span,
    int VideoId,
    string? VideoTitle,
    IReadOnlyList<ResolvedSpanIntervalDto> Intervals,
    int ProfileId,
    int ProfileVersion);

public record VideoResolvedSpansDto(
    IReadOnlyList<ResolvedSpan> Spans,
    int ProfileId,
    int ProfileVersion);

public record ResolvedSpanListDto(IReadOnlyList<ResolvedSpan> Spans);

public record SegmentSpanOperandDto(
    string? SourceKey,
    string? Kind,
    List<int>? TagIds,
    float? MinConfidence,
    List<long>? RefIds = null);

public record SegmentSpanQueryRequestDto(
    int? Profile,
    string Operator,
    List<SegmentSpanOperandDto> Operands,
    double? MergeGapSec,
    double? MinDurationSec);

public record SegmentDisplayProfileDto(
    int Id,
    string Name,
    string? Description,
    int? UserId,
    bool IsSystem,
    bool IsDefault,
    int Version,
    string CreatedAt,
    string UpdatedAt);

public record SegmentDisplayProfileCreateDto(
    string Name,
    string? Description,
    bool IsDefault);

public record SegmentDisplayProfileUpdateDto(
    string Name,
    string? Description);

public record SegmentDistinctValueDto(
    string Value,
    int Count);

public record SegmentDisplayProfilePreviewRequestDto(
    int VideoId,
    List<SegmentDisplayRuleCreateDto> Rules);

// ===== Segment Span Search =====

public record SegmentSpanDerivedQueryDto(
    string Operator,
    List<SegmentSpanOperandDto> Operands,
    double? MergeGapSec,
    double? MinDurationSec);

public record SegmentSpanSearchRequestDto(
    int? Profile,
    SegmentSpanDerivedQueryDto? DerivedQuery,
    int? Page,
    int? PerPage,
    string? Sort,
    string? Direction,
    string? Q,
    string? VideoTitle,
    int[]? VideoIds,
    int[]? ExcludeVideoIds,
    int[]? TagIds = null,
    string? Kind = null,
    string? SourceKey = null,
    long[]? RefIds = null,
    int[]? PerformerIds = null,
    float? Confidence = null,
    float? Confidence2 = null,
    string? ConfidenceModifier = null,
    double? DurationSec = null,
    double? DurationSec2 = null,
    string? DurationModifier = null,
    string? Title = null,
    string? TitleModifier = null,
    string? HostType = null,
    string? SourceCategory = null,
    string? SourceRunId = null,
    string? SourceRunIdModifier = null,
    string? ColorHint = null,
    string? ColorHintModifier = null,
    bool? HasImage = null,
    bool? HasPayload = null,
    double? StartSec = null,
    double? StartSec2 = null,
    string? StartSecModifier = null,
    double? EndSec = null,
    double? EndSec2 = null,
    string? EndSecModifier = null,
    string? CreatedAt = null,
    string? CreatedAt2 = null,
    string? CreatedAtModifier = null,
    string? UpdatedAt = null,
    string? UpdatedAt2 = null,
    string? UpdatedAtModifier = null,
    int? Seed = null,
    int? TagDepth = null);

public record SegmentSpanSearchResultItemDto(
    ResolvedSpan Span,
    int VideoId,
    string? VideoTitle,
    string? VideoUpdatedAt,
    int ProfileId);

public record SegmentSpanSearchResponseDto(
    IReadOnlyList<SegmentSpanSearchResultItemDto> Items,
    // TotalCount is exact when known cheaply (the full result was materialized this request); it is -1
    // when the page was served via early termination without resolving every video — in that case the
    // caller should use HasMore for navigation and fetch the exact total from the spans/count endpoint.
    int TotalCount,
    int Page,
    int PerPage,
    bool HasMore = false);

public record SegmentSpanCountResponseDto(int TotalCount);

public static class ResolvedSpanKeys
{
    public static string Create(int videoId, int profileId, string? sourceKey, string? kind, int? tagId, double startSec, double endSec)
    {
        var payload = $"v1|{videoId}|{profileId}|{sourceKey ?? string.Empty}|{kind ?? string.Empty}|{tagId?.ToString() ?? string.Empty}|{ToMilliseconds(startSec)}|{ToMilliseconds(endSec)}";
        var hashBytes = SHA1.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hashBytes[..8]).ToLowerInvariant();
    }

    public static string CreateDerivedQuery(string? kind, double startSec, double endSec)
        => $"dq-{NormalizeDerivedKind(kind)}-{ToMilliseconds(startSec)}-{ToMilliseconds(endSec)}";

    public static bool TryParseDerivedQuery(string spanKey, out string kind, out double startSec, out double endSec)
    {
        kind = string.Empty;
        startSec = 0;
        endSec = 0;

        if (string.IsNullOrWhiteSpace(spanKey))
            return false;

        var parts = spanKey.Split('-', 4, StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[0], "dq", StringComparison.Ordinal))
            return false;

        if (string.IsNullOrWhiteSpace(parts[1]))
            return false;

        if (!long.TryParse(parts[2], out var startMs) || !long.TryParse(parts[3], out var endMs))
            return false;

        kind = parts[1];
        startSec = startMs / 1000d;
        endSec = endMs / 1000d;
        return true;
    }

    private static long ToMilliseconds(double seconds) => (long)Math.Round(seconds * 1000, MidpointRounding.AwayFromZero);

    private static string NormalizeDerivedKind(string? kind) =>
        string.IsNullOrWhiteSpace(kind) ? "derived" : kind.Trim().ToLowerInvariant();
}

public record SegmentDto(
    int Id,
    SegmentHostType HostType,
    int HostId,
    double StartSec,
    double? EndSec,
    int? TagId,
    string? TagName,
    string? Kind,
    long? RefId,
    JsonElement? Payload,
    string SourceKey,
    string? SourceRunId,
    float? Confidence,
    string? Title,
    string? ColorHint,
    string CreatedAt,
    string UpdatedAt,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record SegmentRecordDto(
    int Id,
    SegmentHostType HostType,
    int HostId,
    string? HostTitle,
    double StartSec,
    double? EndSec,
    int? TagId,
    string? TagName,
    string? Kind,
    long? RefId,
    string? RefLabel,
    int? PerformerId,
    string? PerformerName,
    JsonElement? Payload,
    string SourceKey,
    string? SourceRunId,
    float? Confidence,
    string? Title,
    string? ColorHint,
    string CreatedAt,
    string UpdatedAt,
    List<FieldProvenanceDto>? FieldProvenance = null);

public record SegmentCreateDto(
    double StartSec,
    double? EndSec,
    int? TagId,
    string? Kind,
    long? RefId,
    JsonElement? Payload,
    string? SourceKey,
    string? SourceRunId,
    float? Confidence,
    string? Title,
    string? ColorHint);

public record SegmentUpdateDto(
    double StartSec,
    double? EndSec,
    int? TagId,
    string? Kind,
    long? RefId,
    JsonElement? Payload,
    string SourceKey,
    string? SourceRunId,
    float? Confidence,
    string? Title,
    string? ColorHint);

public record SegmentDisplayRuleDto(
    int Id,
    string? SourceKey,
    string? Kind,
    int? TagId,
    string? TagName,
    string? TagCategory,
    SegmentHostType? HostType,
    bool Visible,
    float? MinConfidence,
    double? MinDurationSec,
    double? MergeGapSec,
    bool CollapseToInstant,
    string? ColorOverride,
    int? Lane,
    int? Priority,
    int? UserId,
    string CreatedAt,
    string UpdatedAt);

public record SegmentDisplayRuleCreateDto(
    string? SourceKey,
    string? Kind,
    int? TagId,
    string? TagCategory,
    SegmentHostType? HostType,
    bool Visible,
    float? MinConfidence,
    double? MinDurationSec,
    double? MergeGapSec,
    bool CollapseToInstant,
    string? ColorOverride,
    int? Lane,
    int? Priority);

public record SegmentDisplayRuleUpdateDto(
    string? SourceKey,
    string? Kind,
    int? TagId,
    string? TagCategory,
    SegmentHostType? HostType,
    bool Visible,
    float? MinConfidence,
    double? MinDurationSec,
    double? MergeGapSec,
    bool CollapseToInstant,
    string? ColorOverride,
    int? Lane,
    int? Priority);

public record DetectionDto(
    int Id,
    DetectionHostType HostType,
    int HostId,
    double? ObservedAtSec,
    int FrameWidth,
    int FrameHeight,
    string Class,
    float Score,
    float X,
    float Y,
    float W,
    float H,
    JsonElement? Extra,
    string? RefKind,
    long? RefId,
    string? GroupKey,
    string SourceKey,
    string? SourceRunId,
    string CreatedAt,
    string UpdatedAt);

public record DetectionCreateDto(
    double? ObservedAtSec,
    int FrameWidth,
    int FrameHeight,
    string Class,
    float Score,
    float X,
    float Y,
    float W,
    float H,
    JsonElement? Extra,
    string? RefKind,
    long? RefId,
    string? GroupKey,
    string? SourceKey,
    string? SourceRunId);

public record DetectionUpdateDto(
    double? ObservedAtSec,
    int FrameWidth,
    int FrameHeight,
    string Class,
    float Score,
    float X,
    float Y,
    float W,
    float H,
    JsonElement? Extra,
    string? RefKind,
    long? RefId,
    string? GroupKey,
    string SourceKey,
    string? SourceRunId);

public record FaceDto(
    int Id,
    string? Label,
    int? PerformerId,
    string? PerformerName,
    string? CoverImageUrl,
    bool Ignored,
    int? MergedIntoFaceId,
    int DetectionCount,
    int VideoCount,
    int ImageCount,
    string? PrimarySourceKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int AppearanceCount = 0,
    int FrameSampleCount = 0,
    FaceTopSuggestionDto? TopSuggestion = null,
    List<FieldProvenanceDto>? FieldProvenance = null,
    // 1-based position of this face among all (non-merged) faces linked to the same performer, with the
    // total. Lets the UI disambiguate "<performer> 1/2/3…" when a performer has multiple linked faces.
    // 0/0 when unlinked or when the ordinal wasn't computed for this response.
    int PerformerFaceIndex = 0,
    int PerformerFaceCount = 0);

public record FaceCreateDto(
    string? Label,
    int? PerformerId,
    bool Ignored,
    string? PrimarySourceKey);

public record FaceUpdateDto(
    string? Label,
    int? PerformerId,
    bool Ignored,
    string? PrimarySourceKey);

public record FaceLinkDto(int? PerformerId, bool SetPerformerImage = false);

public record FaceBatchLinkTopSuggestionDto(
    IReadOnlyList<int> FaceIds,
    // When true, top suggestions that are reference (SAIE) matches without a local performer are
    // created via their provider (which may scrape a configured metadata server) and then linked.
    // When false (default) such faces are skipped.
    bool CreateFromReference = false,
    // A face whose top matches conflict (the same face matched two or more different performers) is
    // skipped unless LinkConflicting is true. When linking conflicts, MergeConflicting merges every
    // competing match into the top one; otherwise the single top match is linked directly.
    bool LinkConflicting = false,
    bool MergeConflicting = false);

public record FaceBatchDeleteDto(IReadOnlyList<int> FaceIds);

public record FaceBatchOperationResultDto(
    IReadOnlyList<int> Succeeded,
    IReadOnlyList<FaceBatchSkippedDto> Skipped,
    IReadOnlyList<FaceBatchFailedDto> Failed);

public record FaceBatchSkippedDto(int FaceId, string Reason);

public record FaceBatchFailedDto(int FaceId, string Error);

public record FaceCreatePerformerDto(
    string Name,
    bool SetPerformerImage = true);

public record FaceHostFaceDto(
    int Id,
    string? Label,
    int? PerformerId,
    string? PerformerName,
    string? CoverImageUrl,
    int AppearanceCount,
    int FrameSampleCount,
    int VideoCount,
    int ImageCount,
    double? FirstSeenAtSec,
    double? LastSeenAtSec,
    float? TopConfidence);

public record FaceMergeDto(int TargetFaceId);

public record FaceIgnoreDto(bool Ignored);

public record FaceDeleteImpactDto(
    int DetectionCount,
    int EmbeddingCount,
    int SegmentCount,
    bool HasCoverImage,
    int ReleasedMergedFaceCount);

public record FaceSuggestionDecisionDto(
    int PerformerId,
    string Decision,
    bool SetPerformerImage = false,
    // The other competing matches when Decision is "merge". Each id mirrors PerformerId (a real performer
    // id or a provider-encoded reference id). Null/empty for accept and reject.
    IReadOnlyList<int>? SecondaryPerformerIds = null,
    // Set when accepting a reference (metadata-server) match that resolved to an existing local performer
    // (PerformerId is the positive local id). The host records this server's remote id on that performer
    // and, when ReferenceUpdateMetadata is true, scrapes the server to refresh it.
    string? ReferenceEndpoint = null,
    string? ReferenceExternalId = null,
    bool ReferenceUpdateMetadata = false);

public record FaceSuggestionEvidenceDto(
    int FaceId,
    string? ThumbnailUrl,
    float Similarity);

public record FaceSuggestionDto(
    int PerformerId,
    string PerformerName,
    string? CoverImageUrl,
    float Confidence,
    string Why,
    IReadOnlyList<FaceSuggestionEvidenceDto> Evidence,
    int? LocalPerformerId = null,
    string? ExternalUrl = null,
    bool LocalPerformerHasImage = false,
    bool LocalPerformerIsLocalOnly = false,
    // Set when 2+ reference matches from different sources compete for the same face. All competing
    // suggestions for a face share the same id, so the UI can group them into a single "possible
    // duplicate" choice (use one, use the other, or merge them).
    string? ConflictGroupId = null,
    // True when this is a reference (metadata-server) match and linking it will scrape/refresh the
    // performer from that server (the "Update existing performers from metadata servers" setting is on).
    // The compare UI hides the "use face image for this local performer" option in that case, since the
    // performer's image will come from the metadata server instead.
    bool ReferenceWillRefreshFromMetadata = false,
    // For a reference (metadata-server) match, the originating server's GraphQL endpoint and that
    // server's id for this performer. Carried back on accept so the host can record the remote id on the
    // (possibly already-existing) local performer and, when enabled, scrape it. Null for non-reference
    // suggestions.
    string? ReferenceEndpoint = null,
    string? ReferenceExternalId = null);

public record FaceSimilarDto(
    int Id,
    string? Label,
    int? PerformerId,
    string? PerformerName,
    string? CoverImageUrl,
    bool Ignored,
    int? MergedIntoFaceId,
    int DetectionCount,
    int VideoCount,
    int ImageCount,
    string? PrimarySourceKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int AppearanceCount,
    int FrameSampleCount,
    float Distance);

public record EmbeddingDto(
    int Id,
    EmbeddingHostType HostType,
    int HostId,
    string Kind,
    string? KindFamily,
    EmbeddingModality Modality,
    bool IsSemantic,
    int Dim,
    float[] Vector,
    int SectionIndex,
    double? StartSec,
    double? EndSec,
    string SourceKey,
    string? SourceRunId,
    JsonElement? Meta,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record EmbeddingSearchRequestDto(
    string? QueryText,
    float[]? QueryVector,
    string? Kind,
    string? KindFamily,
    EmbeddingHostType? HostType,
    int? HostId,
    EmbeddingModality? Modality,
    bool? IsSemantic,
    string? SourceKey,
    int K = 20);

public record EmbeddingSearchResultDto(
    int EmbeddingId,
    EmbeddingHostType HostType,
    int HostId,
    string Kind,
    string? KindFamily,
    EmbeddingModality Modality,
    bool IsSemantic,
    int SectionIndex,
    double? StartSec,
    double? EndSec,
    string SourceKey,
    string? SourceRunId,
    float Distance);

public record AiRunDto(
    int Id,
    string RunKey,
    string SourceKey,
    AiRunTargetType TargetType,
    int TargetId,
    string? Trigger,
    string? JobId,
    AiRunStatus Status,
    DateTime StartedAt,
    DateTime? CompletedAt,
    string? LoadPolicy,
    double? FrameIntervalSec,
    bool? Vr,
    JsonElement? Request,
    JsonElement? Models,
    JsonElement? Summary,
    string? Error,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public record AiDataSelectorDto(
    string? SourceKey,
    string? SourceRunId,
    string? Model,
    string? Modality,
    string? HostType,
    int? HostId,
    List<string>? Kinds);

public record AiDataPurgeRequestDto(
    string? SourceKey,
    string? SourceRunId,
    string? Model,
    string? Modality,
    string? HostType,
    int? HostId,
    List<string>? Kinds,
    bool DryRun = false)
{
    public AiDataSelectorDto ToSelectorDto()
        => new(SourceKey, SourceRunId, Model, Modality, HostType, HostId, Kinds);
}

public record AiDataSummaryItemDto(
    string Kind,
    string? Detail,
    string SourceKey,
    string? SourceRunId,
    string? Model,
    string HostType,
    int Count);

public record AiDataSummaryDto(
    IReadOnlyList<AiDataSummaryItemDto> Items,
    IReadOnlyDictionary<string, int> Totals,
    int TotalCount);

public record AiDataPurgeResultDto(IReadOnlyDictionary<string, int> RemovedCounts);

public record PaginatedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PerPage);

public record StatsDto(
    int VideoCount,
    int ImageCount,
    int GalleryCount,
    int PerformerCount,
    int StudioCount,
    int TagCount,
    int GroupCount,
    int AudioCount,
    int TextCount,
    int SegmentCount,
    int FaceCount,
    int FaceAppearanceCount,
    int EmbeddingCount,
    int DetectionCount,
    int TagApplicationCount,
    int AiRunCount,
    long VideoFileSize,
    long ImageFileSize,
    long AudioFileSize,
    long TextFileSize,
    long TotalFileSize,
    double VideoDuration,
    double AudioDuration,
    double TotalPlayDuration,
    long VideoPlayCount,
    long AudioPlayCount,
    long TextReadCount,
    long ImageViewCount,
    long SegmentViewCount,
    long VideoCompleteCount,
    long AudioCompleteCount,
    long TextCompleteCount,
    long ImageCompleteCount,
    long SegmentCompleteCount,
    double VideoConsumedSeconds,
    double AudioConsumedSeconds,
    double TextConsumedSeconds,
    double ImageConsumedSeconds,
    double SegmentConsumedSeconds,
    long TotalLikes,
    long TotalDerivedLikes,
    long TotalFavorites);

// ===== AUTH DTOs =====
public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username);
public record ApiKeyResponse(string ApiKey);

// ===== CONFIG DTOs =====
public record SystemStatusDto(
    string Version,
    string? AppDir,
    string? ConfigFile,
    string DatabasePath,
    bool MigrationRequired = false,
    string[]? PendingMigrations = null,
    bool AuthEnabled = false,
    bool MigrationStatusUnknown = false,
    string? MigrationStatusError = null);

public record CoveConfigDto
{
    public List<CovePathDto> CovePaths { get; init; } = [];
    public string? GeneratedPath { get; init; }
    public string? CachePath { get; init; }
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 5073;
    // Mirrors CoveConfiguration: fresh-install default leaves ~3 logical processors free.
    public int MaxParallelTasks { get; init; } = System.Math.Max(1, System.Environment.ProcessorCount - 3);
    public int MaxConcurrentDownloads { get; init; } = 3;
    public List<DownloaderPathOverrideDto> DownloaderPathOverrides { get; init; } = [];
    public bool CalculateMd5 { get; init; }
    public string FrameExtractionMode { get; init; } = "external";
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public int MaxStreamingTranscodeSize { get; init; }
    // Unified hardware-acceleration policy: "off" | "auto" | "nvenc" | "qsv" | "vaapi" | "amf" | "videotoolbox".
    // Nullable so the server can tell a real value from "absent" (old config) and migrate the legacy fields below.
    public string? HardwareAcceleration { get; init; }
    public int HardwareEncodeSessionLimit { get; init; }
    public string? FfmpegInputArgs { get; init; }
    public string? FfmpegOutputArgs { get; init; }
    public string PreviewPreset { get; init; } = "slow";

    // ---- Legacy ffmpeg fields (deserialized from older cove-config.json for one-time migration in
    // ConfigService.ApplyToLive; never re-emitted by GetConfig, so they disappear after the next save). ----
    [System.Obsolete("Migrated into HardwareAcceleration")] public bool? EnableFfmpegHwAccel { get; init; }
    [System.Obsolete("Migrated into HardwareAcceleration")] public string? TranscodeHardwareAcceleration { get; init; }
    [System.Obsolete("Migrated into FfmpegInputArgs")] public string? TranscodeInputArgs { get; init; }
    [System.Obsolete("Migrated into FfmpegOutputArgs")] public string? TranscodeOutputArgs { get; init; }
    [System.Obsolete("Migrated into FfmpegInputArgs")] public string? LiveTranscodeInputArgs { get; init; }
    [System.Obsolete("Migrated into FfmpegOutputArgs")] public string? LiveTranscodeOutputArgs { get; init; }
    [System.Obsolete("Removed; was never read")] public int? MaxTranscodeSize { get; init; }
    public string PreviewAudio { get; init; } = "false";
    public List<string> VideoExtensions { get; init; } = [];
    public List<string> ImageExtensions { get; init; } = [];
    public List<string> GalleryExtensions { get; init; } = [];
    public List<string> AudioExtensions { get; init; } = [];
    public List<string> TextExtensions { get; init; } = [];
    public List<string> ExcludePatterns { get; init; } = [];
    public List<string> ExcludeImagePatterns { get; init; } = [];
    public List<string> ExcludeGalleryPatterns { get; init; } = [];
    public bool CreateGalleriesFromFolders { get; init; }
    public bool WriteImageThumbnails { get; init; }
    public bool CreateImageClipsFromVideos { get; init; }
    public string GalleryCoverRegex { get; init; } = "(poster|cover|folder|board)\\.[^\\.]+$";
    public bool DeleteGeneratedDefault { get; init; } = true;
    public string LogLevel { get; init; } = "Info";
    public InterfaceConfigDto Interface { get; init; } = new();
    public UiConfigDto Ui { get; init; } = new();
    public SecurityConfigDto Security { get; init; } = new();
    public ScrapingConfigDto Scraping { get; init; } = new();
    public List<CustomFieldDefinitionDto> CustomFieldDefinitions { get; init; } = [];
    public Dictionary<string, Dictionary<string, object?>> PluginConfigurations { get; init; } = [];
    public List<string> DisabledPlugins { get; init; } = [];
}

public record CustomFieldDefinitionDto
{
    public int Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "text";
    public List<string> EntityTypes { get; init; } = [];
    public List<string> Options { get; init; } = [];
    public bool Filterable { get; init; } = true;
    public bool Sortable { get; init; }
    public bool IsMultiValue { get; init; }
    public int DisplayOrder { get; init; }
    public string? CreatedAt { get; init; }
    public string? UpdatedAt { get; init; }
}

public record CustomFieldDefinitionCreateDto
{
    public string? Key { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "text";
    public List<string> EntityTypes { get; init; } = [];
    public List<string> Options { get; init; } = [];
    public bool Filterable { get; init; } = true;
    public bool Sortable { get; init; }
    public bool IsMultiValue { get; init; }
    public int? DisplayOrder { get; init; }
}

public record CustomFieldDefinitionUpdateDto
{
    public string? Key { get; init; }
    public string? Label { get; init; }
    public string? Type { get; init; }
    public List<string>? EntityTypes { get; init; }
    public List<string>? Options { get; init; }
    public bool? Filterable { get; init; }
    public bool? Sortable { get; init; }
    public bool? IsMultiValue { get; init; }
    public int? DisplayOrder { get; init; }
}

public record CustomFieldDefinitionSyncDto
{
    public int? Id { get; init; }
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = "text";
    public List<string> EntityTypes { get; init; } = [];
    public List<string> Options { get; init; } = [];
    public bool Filterable { get; init; } = true;
    public bool Sortable { get; init; }
    public bool IsMultiValue { get; init; }
    public int? DisplayOrder { get; init; }
}

public record BookmarkDto(AffinityHostType HostType, int HostId, string CreatedAt);
public record BookmarkBatchRequestDto(AffinityHostType HostType, List<int> HostIds);
public record BookmarkToggleDto(AffinityHostType HostType, int HostId, bool Saved);
public record BookmarkStateDto(AffinityHostType HostType, int HostId, bool Saved, string? CreatedAt);

public record DynamicGroupSourceDto(string Key, string DisplayName);

public record CovePathDto
{
    public string Path { get; init; } = "";
    public bool ExcludeVideo { get; init; }
    public bool ExcludeImage { get; init; }
    public bool ExcludeAudio { get; init; }
    public bool ExcludeText { get; init; }
}

public record DownloaderPathOverrideDto
{
    public string DownloaderId { get; init; } = string.Empty;
    public string? Site { get; init; }
    public string Path { get; init; } = string.Empty;
}

public record InterfaceConfigDto
{
    public string? Language { get; init; }
    public List<string> MenuItems { get; init; } = [];
    public bool HandyConnectionEnabled { get; init; }
    public string? HandyKey { get; init; }
    public int? DefaultDurationForImages { get; init; }
    public bool DisableDropdownCreatePerformer { get; init; }
    public bool DisableDropdownCreateStudio { get; init; }
    public bool DisableDropdownCreateTag { get; init; }
}

public record UiConfigDto
{
    public string? Title { get; init; }
    public string? FaviconPath { get; init; }
    public string? LogoPath { get; init; }
    public bool TroubleshootingModeEnabled { get; init; }
    public bool AbbreviateCounters { get; init; }
    public RatingSystemOptionsDto RatingSystemOptions { get; init; } = new();
    public bool ShowStudioAsText { get; init; }
    public string? CustomCss { get; init; }
    public string? CustomJs { get; init; }
    public bool EnableCSSCustomization { get; init; }
    public bool EnableJSCustomization { get; init; }
    public string? CustomLocalesPath { get; init; }
    public bool AutostartVideo { get; init; } = true;
    public bool AutostartVideoOnPlaySelected { get; init; } = true;
    public bool AutoplayOnListClick { get; init; }
    public int MaxLoopDuration { get; init; }
    public bool AlwaysResumeOnPlayback { get; init; } = true;
    public double PlayerVideoStartPercent { get; init; }
    public double PlayerVideoStartMinDuration { get; init; }
    public bool ContinuePlaylistDefault { get; init; }
    public bool ShowAbLoopControls { get; init; } = true;
    public bool SoundOnPreview { get; init; }
    public double PreviewSegmentDuration { get; init; } = 0.75;
    public int PreviewSegments { get; init; } = 12;
    public string PreviewExcludeStart { get; init; } = "0";
    public string PreviewExcludeEnd { get; init; } = "0";
    public bool WallShowTitle { get; init; } = true;
    public int WallPlayback { get; init; } = 1;
    public string WallPreviewType { get; init; } = "video";
    public string ImageObjectFit { get; init; } = "contain";
    public string VideoObjectFit { get; init; } = "cover";
    public string FeedVideoSource { get; init; } = "preview";
    public bool FeedVideoSound { get; init; }
    public double FeedVideoStartPercent { get; init; }
    public double FeedVideoStartMinDuration { get; init; }
    public bool DeleteFileDefault { get; init; }
    public int SlideshowDelay { get; init; } = 5000;
    public bool NoBrowser { get; init; }
    public bool NotificationsEnabled { get; init; } = true;
    public Dictionary<string, string> KeybindingOverrides { get; init; } = [];
}

public record RatingSystemOptionsDto
{
    public RatingSystemType Type { get; init; } = RatingSystemType.Stars;
    public RatingStarPrecision StarPrecision { get; init; } = RatingStarPrecision.Full;
}

public record SecurityConfigDto
{
    public bool Enabled { get; init; }
    public string? Username { get; init; }
    public bool AllowAnonymousShareLinks { get; init; } = true;
    public bool EnforceDefaultDeny { get; init; } = true;
    public List<string>? KnownProxies { get; init; } = [];
    public List<string>? TrustedHosts { get; init; } = [];
    public string? NewPassword { get; init; }
}

public record ScrapingConfigDto
{
    public List<string> ScraperDirectories { get; init; } = [];
    public List<MetadataServerDto> MetadataServers { get; init; } = [];
    public List<ScraperPreferenceDto> ScraperPreferences { get; init; } = [];
    public IdentifyDefaultsConfigDto IdentifyDefaults { get; init; } = new();
    public ScrapeApplyDefaultsConfigDto ScrapeApplyDefaults { get; init; } = new();
    public MetadataBatchDefaultsConfigDto MetadataBatchDefaults { get; init; } = new();
}

public record ScrapeApplyDefaultsConfigDto
{
    public bool CreateMissingTags { get; init; } = true;
    public bool CreateMissingPerformers { get; init; } = true;
    public bool CreateMissingStudio { get; init; } = true;
    public bool MarkOrganized { get; init; }
    public bool HydratePerformers { get; init; }
}

public record ScraperPreferenceDto
{
    public string EntityType { get; init; } = "";
    public string Site { get; init; } = "";
    public string ScraperId { get; init; } = "";
}

public record IdentifyDefaultsConfigDto
{
    public bool CreateTags { get; init; } = true;
    public bool CreatePerformers { get; init; } = true;
    public bool CreateStudios { get; init; } = true;
    public int? AutoApplyMinFingerprintMatches { get; init; } = 4;
    public int? AutoApplyMaxDurationDifferenceSeconds { get; init; } = 5;
    public int? AutoApplyMaxPhashDistance { get; init; }
}

public record MetadataBatchDefaultsConfigDto
{
    public bool RefreshAlreadyTagged { get; init; }
    public bool CreateParentStudios { get; init; } = true;
    public List<string> ExcludeFields { get; init; } = [];
}

public record MetadataServerDto
{
    public string Endpoint { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Name { get; init; } = "";
    public int MaxRequestsPerMinute { get; init; } = 240;
}

public record MetadataServerValidationResultDto(bool Valid, string Status, string? Username);

public record MetadataServerPerformerMatchDto(
    string Endpoint,
    string MetadataServerName,
    string Id,
    string Name,
    string? Disambiguation,
    string? Gender,
    string? BirthDate,
    string? Country,
    string? ImageUrl,
    bool Deleted,
    string? MergedIntoId,
    List<string> Aliases,
    List<string> Urls
);

public record MetadataServerPerformerImportRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public string PerformerId { get; init; } = string.Empty;
    public Dictionary<string, string>? FieldStrategies { get; init; }
}

public record MetadataServerStudioMatchDto(
    string Endpoint,
    string MetadataServerName,
    string Id,
    string Name,
    string? ImageUrl,
    List<string> Aliases,
    List<string> Urls,
    string? ParentName
);

public record MetadataServerStudioImportRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public string StudioId { get; init; } = string.Empty;
    public Dictionary<string, string>? FieldStrategies { get; init; }
}

public record MetadataServerTagMatchDto(
    string Endpoint,
    string MetadataServerName,
    string Id,
    string Name,
    string? Description,
    List<string> Aliases
);

public record MetadataServerTagImportRequestDto(string Endpoint, string TagId);

public record MetadataServerFindByIdsRequestDto(string Endpoint, List<string> Ids);

public record MetadataServerBatchTagItemResultDto(
    int LocalId,
    string LocalName,
    string Outcome,
    string? RemoteId = null,
    string? Message = null
);

public record MetadataServerBatchTagResultDto(
    int Processed,
    int Updated,
    int Skipped,
    int Failed,
    List<MetadataServerBatchTagItemResultDto> Items
);

public record MetadataServerPerformerBatchTagRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public List<int>? Ids { get; init; }
    public PerformerFilter? Filter { get; init; }
    public bool SelectAll { get; init; }
    public bool RefreshAlreadyTagged { get; init; }
    public List<string>? ExcludeFields { get; init; }
}

public record MetadataServerStudioBatchTagRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public List<int>? Ids { get; init; }
    public StudioFilter? Filter { get; init; }
    public bool SelectAll { get; init; }
    public bool RefreshAlreadyTagged { get; init; }
    public List<string>? ExcludeFields { get; init; }
    public bool CreateParentStudios { get; init; } = true;
}

public record MetadataServerTagBatchTagRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public List<int>? Ids { get; init; }
    public TagFilter? Filter { get; init; }
    public bool SelectAll { get; init; }
    public bool RefreshAlreadyTagged { get; init; }
    public List<string>? ExcludeFields { get; init; }
}

public record MetadataServerEntityCandidateDto(
    string RemoteId,
    string Name,
    bool ExistsLocally,
    int? LocalId
);

public record MetadataServerVideoEntityOverrideDto
{
    public string RemoteId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public int? LocalId { get; init; }
}

public record MetadataServerVideoMatchDto(
    string Endpoint,
    string MetadataServerName,
    string Id,
    string? Title,
    string? Code,
    string? Date,
    string? Director,
    string? Details,
    string? StudioName,
    string? ImageUrl,
    int? Duration,
    List<string> PerformerNames,
    List<string> TagNames,
    List<string> Urls,
    List<string> FingerprintAlgorithms,
    int MatchCount,
    List<MetadataServerFingerprintDto> Fingerprints,
    MetadataServerEntityCandidateDto? StudioCandidate,
    List<MetadataServerEntityCandidateDto> PerformerCandidates,
    List<MetadataServerEntityCandidateDto> TagCandidates
);

public record MetadataServerFingerprintDto(string Algorithm, string Hash, int? Duration);

public record MetadataServerVideoImportRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public string VideoId { get; init; } = string.Empty;
    public bool SetCoverImage { get; init; } = true;
    // When false (default), the remote cover only replaces an auto-generated frame cover; an explicitly
    // set cover (upload or chosen frame, i.e. ImageBlobId != null) is preserved. The tagger sets this true
    // when the user explicitly chose to replace. Identify leaves it false so it never clobbers a user cover.
    public bool OverwriteExplicitCover { get; init; }
    public bool SetTags { get; init; } = true;
    public bool SetPerformers { get; init; } = true;
    public bool SetStudio { get; init; } = true;
    public bool OnlyExistingTags { get; init; }
    public bool OnlyExistingPerformers { get; init; }
    public bool OnlyExistingStudio { get; init; }
    public bool MarkOrganized { get; init; }
    public List<string>? ExcludedTagNames { get; init; }
    public List<string>? ExcludedPerformerNames { get; init; }
    public MetadataServerVideoEntityOverrideDto? StudioOverride { get; init; }
    public List<MetadataServerVideoEntityOverrideDto>? PerformerOverrides { get; init; }
    public List<MetadataServerVideoEntityOverrideDto>? TagOverrides { get; init; }
    public Dictionary<string, string>? FieldStrategies { get; init; }
    public List<string>? PerformerGenders { get; init; }
    public bool SkipSingleNamePerformers { get; init; }
}

public record MetadataServerEndpointDto(string Endpoint);

public record ScraperSummaryDto(
    string Id,
    string Name,
    string EntityType,
    List<string> SupportedScrapes,
    List<string> Urls,
    string SourcePath,
    List<string>? PreferenceSites = null
);

public record CreateScrapeAttemptDto(
    string ScraperId,
    string EntityType,
    int? EntityId,
    string InputKind,
    string? Url,
    string? Name,
    Dictionary<string, object>? Fragment);

public record ScrapeAttemptDto(
    Guid Id,
    string ScraperId,
    string EntityType,
    int? EntityId,
    string InputKind,
    string? InputJson,
    string? ResultJson,
    string? CandidateResultsJson,
    string? EntitySnapshotJson,
    string Status,
    string? Error,
    string CreatedAt,
    string? AppliedAt);

public record ApplyVideoScrapeAttemptDto(
    List<string>? ReplaceFields,
    Dictionary<string, string>? CollectionModes,
    bool CreateMissingTags = true,
    bool CreateMissingPerformers = true,
    bool CreateMissingStudio = true,
    bool MarkOrganized = false,
    bool HydratePerformers = false,
    int? SelectedCandidateIndex = null,
    List<ScrapeCollectionItemSelectionDto>? TagSelections = null,
    List<ScrapeCollectionItemSelectionDto>? PerformerSelections = null);

public record ScrapeCollectionItemSelectionDto(string? Name, string? Action);

// Lets the scrape dialog ask the backend which scraped names already resolve to an existing
// entity (by name or alias) using the exact same matcher the apply path uses, so its
// "matches existing" vs "will create" prediction stays in lockstep with save behavior.
public record ResolveScrapeRelationsRequestDto
{
    public List<string> Performers { get; init; } = [];
    public List<string> Tags { get; init; } = [];
}

// One entry per requested name that matched an existing entity. MatchedName is the existing
// entity's primary name, which differs from Input when the match was via an alias.
public record ScrapeRelationMatchDto(string Input, string MatchedName);

public record ResolveScrapeRelationsResultDto
{
    public List<ScrapeRelationMatchDto> Performers { get; init; } = [];
    public List<ScrapeRelationMatchDto> Tags { get; init; } = [];
}

public record PerformerScrapeUrlRequestDto(string? Url, bool CreateMissingTags = true);

public record PerformerScrapeRequestDto(string? InputKind, string? ScraperId, string? Url, string? Name, bool CreateMissingTags = true);

public record PerformerScrapePreviewDto(ScrapedPerformerDto Scraped, string InputKind, string? SourceValue);

public record PerformerApplyScrapedRequestDto
{
    public ScrapedPerformerDto Scraped { get; init; } = new();
    public bool CreateMissingTags { get; init; } = true;
    public List<string>? ReplaceFields { get; init; }
    public Dictionary<string, string>? CollectionModes { get; init; }
}

public record DownloaderDescriptorDto(
    string Id,
    string Name,
    string SupportedEntity,
    List<string> SupportedUrlPatterns,
    List<string> Capabilities
);

public record DownloaderQualityOptionDto(string Id, string Label, string? Description = null);

public record DownloaderMatchDto(
    string DownloaderId,
    string DownloaderName,
    string SupportedEntity,
    string NormalizedUrl,
    string? Label,
    List<DownloaderQualityOptionDto> QualityOptions,
    string? SourceUrl = null
);

public record DownloaderMatchRequestDto(string Url);

public record DownloaderPreflightRequestDto
{
    public string Url { get; init; } = string.Empty;
    public string Entity { get; init; } = string.Empty;
    public int? EntityId { get; init; }
}

public record DownloaderPreflightResponseDto(bool IsDuplicate, string? DuplicateReason);

public record DownloaderStartRequestDto
{
    public string DownloaderId { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public string? SourceUrl { get; init; }
    public string Entity { get; init; } = string.Empty;
    public int? EntityId { get; init; }
    public string? QualityId { get; init; }
    public bool AutoApplyMetadata { get; init; }
    public bool AllowDuplicateDownload { get; init; }
    public bool CreateMissingTags { get; init; } = true;
    public bool CreateMissingPerformers { get; init; } = true;
    public bool CreateMissingStudio { get; init; } = true;
    public bool MarkOrganized { get; init; }
}

public record DownloaderBatchItemDto
{
    public string? DownloaderId { get; init; }
    public string Url { get; init; } = string.Empty;
    public string? SourceUrl { get; init; }
    public string Entity { get; init; } = string.Empty;
    public int? EntityId { get; init; }
    public string? QualityId { get; init; }
    public string? Label { get; init; }
    public string? Title { get; init; }
    public bool CreateEntityIfMissing { get; init; }
    public bool AutoApplyMetadata { get; init; }
    public bool CreateMissingTags { get; init; }
    public bool CreateMissingPerformers { get; init; }
    public bool CreateMissingStudio { get; init; }
    public bool MarkOrganized { get; init; }
    public List<int>? GalleryIds { get; init; }
    public List<VideoGroupInputDto>? GroupIds { get; init; }
}

public record DownloaderBatchStartIssueDto(string Kind, string Label, string Reason);

public record DownloaderBatchFollowUpDto
{
    public bool ScrapeVideos { get; init; }
    public bool AutoApplyMetadata { get; init; }
    public bool AllowDuplicateDownloads { get; init; }
    public bool CreateMissingTags { get; init; }
    public bool CreateMissingPerformers { get; init; }
    public bool CreateMissingStudio { get; init; }
    public bool MarkOrganized { get; init; }
    public GenerateOptionsDto? Generate { get; init; }
}

public record DownloaderBatchStartRequestDto
{
    public List<DownloaderBatchItemDto> Items { get; init; } = [];
    public DownloaderBatchFollowUpDto FollowUp { get; init; } = new();
    public bool PreflightBeforeQueue { get; init; } = true;
}

// ===== PLAYBACK TRACKING DTOs =====
public record PlaybackIntervalInputDto(double StartSec, double EndSec);

public record PlaybackIntervalsRequestDto(
    string HostType,
    int HostId,
    Guid SessionId,
    double MediaDurationSec,
    double CurrentPositionSec,
    string State,
    List<PlaybackIntervalInputDto> Intervals,
    string? Surface = null,
    string? ScopeKey = null,
    string? ParentHostType = null,
    int? ParentHostId = null,
    string? ItemHostType = null,
    int? ItemHostId = null,
    int? GroupItemId = null,
    int? SegmentId = null,
    double? ClipStartSec = null,
    double? ClipEndSec = null,
    bool? Autoplay = null,
    bool? Muted = null,
    bool? Fullscreen = null,
    double? PlaybackRate = null,
    string? Route = null,
    string? Referrer = null,
    string? RecommendationSource = null,
    JsonElement? Context = null);

public record PlaybackIntervalDto(double StartSec, double EndSec, string RecordedAt);

public record VideoPlaybackSessionDto(
    Guid SessionId,
    string StartedAt,
    string LastSeenAt,
    string? EndedAt,
    string State,
    double MediaDurationSec,
    double TotalWatchedSec,
    double? LastPositionSec,
    bool IsCompleted,
    List<PlaybackIntervalDto> Intervals);

// Non-playback events timeline (pause, seek, search, filter, etc.)
public record InteractionEventDto(string Kind, string At, JsonElement? Meta = null);

public record VideoRatingDto(int? Value, string Aspect = "overall");

public record EntityEngagementDto(
    int HostId,
    bool IsFavorite,
    int? Rating,
    double ResumeTime,
    double PlayDuration,
    int PlayCount,
    string? LastPlayedAt,
    int LikeCount,
    int DerivedLikeCount,
    int PageVisitCount,
    int CompleteCount);

public record EntityRatingsDto(int HostId, Dictionary<string, int> Ratings);

public record EntityFavoriteDto(bool IsFavorite);

public record EntityEngagementBatchRequestDto(AffinityHostType HostType, List<int> HostIds);

public record EngagementInteractionWriteDto(
    string HostType,
    int? HostId,
    string Kind,
    JsonElement? Meta = null);

public record EngagementInteractionDto(
    int Id,
    string HostType,
    int? HostId,
    string Kind,
    string At,
    JsonElement? Meta = null);

public record VideoHistoryDto(
    List<string> PlayHistory,
    List<string> LikeHistory,
    /// <summary>Non-playback engagement events (search, filter, likes, etc.).</summary>
    List<InteractionEventDto>? Events = null,
    /// <summary>Merged watched intervals across all sessions (the unique sections this user has ever watched).</summary>
    List<PlaybackIntervalDto>? AllTimeWatchedIntervals = null,
    /// <summary>Total unique seconds watched across all sessions.</summary>
    double? TotalDistinctWatchedSec = null,
    /// <summary>Per-session playback history.</summary>
    List<VideoPlaybackSessionDto>? Sessions = null);

// ===== BULK UPDATE DTOs =====
public enum BulkUpdateMode { Set, Add, Remove }

public record BulkVideoUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public bool? IsVr { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Code { get; init; }
    public string? Director { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? GalleryIds { get; init; }
    public BulkUpdateMode GalleryMode { get; init; } = BulkUpdateMode.Add;
    public List<VideoGroupInputDto>? GroupIds { get; init; }
    public BulkUpdateMode GroupMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkPerformerUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Favorite { get; init; }
    public string? Gender { get; init; }
    public string? Details { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkImageUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Code { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? GalleryIds { get; init; }
    public BulkUpdateMode GalleryMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkGalleryUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Code { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkAudioUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Code { get; init; }
    public string? Details { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkTextDocumentUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Code { get; init; }
    public string? Details { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkStudioUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public int? Rating { get; init; }
    public bool? Favorite { get; init; }
    public string? Details { get; init; }
    public bool? Organized { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkTagUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public string? Description { get; init; }
    public string? Color { get; init; }
    public int? TagGroupId { get; init; }
    public double? MinOccurrenceSec { get; init; }
    public double? MinOccurrencePercent { get; init; }
    public bool? Organized { get; init; }
    public bool? Favorite { get; init; }
    public int? Rating { get; init; }
    public List<int>? ParentIds { get; init; }
    public BulkUpdateMode ParentMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? ChildIds { get; init; }
    public BulkUpdateMode ChildMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkGroupUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public List<string>? ClearFields { get; init; }
    public int? Rating { get; init; }
    public int? StudioId { get; init; }
    public string? Date { get; init; }
    public string? Director { get; init; }
    public string? Description { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

// ===== MERGE DTOs =====
public record VideoMergeDto(int TargetId, List<int> SourceIds);
public record PerformerMergeDto(int TargetId, List<int> SourceIds);
public record TagMergeDto(int TargetId, List<int> SourceIds);
public record StudioMergeDto(int TargetId, List<int> SourceIds);

// ===== GROUP HIERARCHY DTOs =====
public record AddSubGroupDto(int SubGroupId, int? OrderIndex = null, string? Description = null);
public record ReorderSubGroupsDto(List<int> SubGroupIds);

// ===== BATCH/BULK DTOs =====
public record BatchDeleteDto(List<int> Ids, bool DeleteFiles = false, bool DeleteGenerated = false);

// ===== FILE OPERATION DTOs =====
public record MoveFilesDto(List<int> FileIds, string DestinationPath);
public record DeleteFilesDto(List<int> FileIds, bool DeleteFromDisk);
public record FileSetFingerprintsDto(int FileId, List<FingerprintEntryDto> Fingerprints);
public record FingerprintEntryDto(string Type, string Value);
public record VideoAssignFileDto(int FileId);
public record GallerySetCoverDto(int ImageId);
public record EntityImageCoverSourceDto(int? ImageId = null, int? VideoId = null);

// ===== GALLERY ADVANCED DTOs =====
public record GalleryAddImagesDto(List<int> ImageIds);
public record GalleryRemoveImagesDto(List<int> ImageIds);
public record GalleryChapterDto(int Id, string Title, int ImageIndex, int GalleryId, string CreatedAt, string UpdatedAt);
public record GalleryChapterCreateDto(string Title, int ImageIndex);
public record GalleryChapterUpdateDto(string? Title, int? ImageIndex);

// ===== GROUP ADVANCED DTOs =====
public record GroupSubGroupsDto(List<GroupSubGroupEntryDto> SubGroups);
public record GroupSubGroupEntryDto(int GroupId, int VideoIndex);

// ===== METADATA OPERATION DTOs =====
/// <summary>A folder offered in the selective scan/generate picker. <see cref="HasChildren"/> lets the
/// UI show an expand affordance only for folders that actually contain subfolders.</summary>
public record LibraryFolderDto(string Name, string Path, bool HasChildren);

public record ScanOptionsDto
{
    public List<string>? Paths { get; init; }
    public bool ScanGenerators { get; init; }
    public bool ScanGenerateCovers { get; init; }
    public bool ScanGeneratePreviews { get; init; }
    public bool ScanGenerateSprites { get; init; }
    public bool ScanGeneratePhashes { get; init; }
    public bool ScanGenerateMd5 { get; init; }
    public bool ScanGenerateThumbnails { get; init; }
    public bool ScanGenerateImagePhashes { get; init; }
    public bool ScanGenerateAudioPhashes { get; init; }
    public bool ScanGenerateTextPhashes { get; init; }
    public bool Rescan { get; init; }
}

public record GenerateOptionsDto
{
    public bool Thumbnails { get; init; } = true;
    public bool Previews { get; init; }
    public bool Sprites { get; init; }
    public bool Segments { get; init; }
    public bool SegmentThumbnails { get; init; }
    public bool SegmentPreviews { get; init; }
    public bool Phashes { get; init; }
    public bool Md5 { get; init; }
    public bool ImageThumbnails { get; init; }
    public bool ImagePhashes { get; init; }
    public bool GalleryThumbnails { get; init; }
    public bool AudioPhashes { get; init; }
    public bool TextPhashes { get; init; }
    public bool Overwrite { get; init; }
    public List<int>? VideoIds { get; init; }
    public List<int>? ImageIds { get; init; }
    public List<int>? AudioIds { get; init; }
    public List<int>? TextIds { get; init; }
    public List<string>? Paths { get; init; }
}

public record CleanOptionsDto
{
    public List<string>? Paths { get; init; }
    public bool DryRun { get; init; }
}

public record ExportOptionsDto
{
    public bool IncludeVideos { get; init; } = true;
    public bool IncludePerformers { get; init; } = true;
    public bool IncludeStudios { get; init; } = true;
    public bool IncludeTags { get; init; } = true;
    public bool IncludeGalleries { get; init; } = true;
    public bool IncludeGroups { get; init; } = true;
}

public record ImportOptionsDto
{
    public string FilePath { get; init; } = "";
    public bool DuplicateHandling { get; init; } // true = overwrite
}

public record SyncFingerprintsOptionsDto
{
    public string? SourceUrl { get; init; }
    public string? ApiKey { get; init; }
}

// ===== IDENTIFY/TAGGER DTOs =====
public record IdentifyOptionsDto
{
    public List<string>? Sources { get; init; }
    public List<int>? VideoIds { get; init; }
    public bool SetCoverImage { get; init; } = true;
    public bool SetTags { get; init; } = true;
    public bool SetPerformers { get; init; } = true;
    public bool SetStudio { get; init; } = true;
    public bool? CreateTags { get; init; }
    public bool? CreatePerformers { get; init; }
    public bool? CreateStudios { get; init; }
    public bool MarkOrganized { get; init; }
    public bool SkipMultipleMatches { get; init; }
    public bool SkipSingleNamePerformers { get; init; } = true;
    public Dictionary<string, string>? FieldStrategies { get; init; }
    public List<string>? PerformerGenders { get; init; }
}

// ===== DATABASE OPERATION DTOs =====
public record BackupResultDto(string BackupPath, long SizeBytes, string Timestamp);

public record ConfigBackupResultDto(string BackupPath, long SizeBytes, string Timestamp);

public record WipeResultDto(string Message, string BackupPath, string Timestamp, string? ConfigBackupPath);
public record RestoreBackupRequestDto(string BackupPath);
public record RestoreBackupResultDto(string Message, string BackupPath, string? PreRestoreBackupPath);
public record DatabaseMigrationResultDto(
    string Message,
    string[] AppliedMigrations,
    string[] PendingMigrations,
    string? PreMigrationBackupPath,
    bool MigrationRequired);

// ===== SCRAPER DTOs =====
public record ScrapeUrlDto(string Url, string ContentType);

public record ScrapedVideoDto
{
    public string? SourceScraperId { get; init; }
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Details { get; init; }
    public string? Director { get; init; }
    public string? Date { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> PerformerNames { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
}

public record ScrapedPerformerDto
{
    public string? SourceScraperId { get; init; }
    public string? Name { get; init; }
    public string? Disambiguation { get; init; }
    public string? Gender { get; init; }
    public string? Birthdate { get; init; }
    public string? Country { get; init; }
    public string? Ethnicity { get; init; }
    public string? EyeColor { get; init; }
    public string? HairColor { get; init; }
    public int? HeightCm { get; init; }
    public int? Weight { get; init; }
    public string? Measurements { get; init; }
    public string? Tattoos { get; init; }
    public string? Piercings { get; init; }
    public string? Details { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public List<string> Aliases { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
}

public record ScrapedGalleryDto
{
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> PerformerNames { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
}

public record ScrapedImageDto
{
    public string? SourceScraperId { get; init; }
    public string? Title { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> PerformerNames { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
    public string? GalleryTitle { get; init; }
}

public record ScrapedAudioDto
{
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> PerformerNames { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
}

public record ScrapedTextDto
{
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> PerformerNames { get; init; } = [];
    public List<string> TagNames { get; init; } = [];
}

public record ScrapedGroupDto
{
    public string? SourceScraperId { get; init; }
    public string? Name { get; init; }
    public List<string> Aliases { get; init; } = [];
    public int? Duration { get; init; }
    public string? Date { get; init; }
    public string? Director { get; init; }
    public string? Details { get; init; }
    public string? Synopsis { get; init; }
    public int? Rating { get; init; }
    public string? ImageUrl { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? StudioName { get; init; }
    public List<string> TagNames { get; init; } = [];
}

public record VideoScrapeFingerprint(string Type, string Value, double? Duration = null);

public record VideoScrapeFile
{
    public string Path { get; init; } = string.Empty;
    public string? Format { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public double? DurationSeconds { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public double? FrameRate { get; init; }
    public long? BitRate { get; init; }
    public long? SizeBytes { get; init; }
    public List<VideoScrapeFingerprint> Fingerprints { get; init; } = [];
}

public record VideoScrapeInput
{
    public int? LocalVideoId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? Director { get; init; }
    public List<VideoScrapeFile> Files { get; init; } = [];
}

public record PerformerScrapeInput
{
    public int? LocalPerformerId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Name { get; init; }
    public string? Disambiguation { get; init; }
    public string? Gender { get; init; }
    public string? Birthdate { get; init; }
    public string? Country { get; init; }
    public string? Ethnicity { get; init; }
    public string? EyeColor { get; init; }
    public string? HairColor { get; init; }
    public int? HeightCm { get; init; }
    public int? Weight { get; init; }
    public string? Measurements { get; init; }
    public string? Details { get; init; }
    public List<string> Aliases { get; init; } = [];
}

public record GalleryScrapeInput
{
    public int? LocalGalleryId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
}

public record ImageScrapeInput
{
    public int? LocalImageId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Title { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
    public string? Photographer { get; init; }
}

public record AudioScrapeInput
{
    public int? LocalAudioId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
}

public record TextScrapeInput
{
    public int? LocalTextId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Title { get; init; }
    public string? Code { get; init; }
    public string? Date { get; init; }
    public string? Details { get; init; }
}

public record GroupScrapeInput
{
    public int? LocalGroupId { get; init; }
    public string? Url { get; init; }
    public List<string> Urls { get; init; } = [];
    public string? Name { get; init; }
    public string? Aliases { get; init; }
    public int? Duration { get; init; }
    public string? Date { get; init; }
    public string? Director { get; init; }
    public string? Details { get; init; }
    public string? Synopsis { get; init; }
}

// ===== SCRAPER EXECUTION REQUEST DTOs =====
public record RecomputeDerivedCountsResult(int EntitiesRecomputed);
public record ScrapeUrlRequest(string ScraperId, string EntityType, string Url);
public record ScrapeNameRequest(string ScraperId, string EntityType, string Name);
public record ScrapeFragmentRequest(string ScraperId, string EntityType, Dictionary<string, object> Fragment);
public record ScraperMatchUrlRequest(string Url, string? EntityType = null);

// ===== PLUGIN DTOs =====
public record PluginDto(string Id, string Name, string Description, string Version, bool Enabled, List<PluginTaskDto> Tasks, List<PluginSettingSchemaDto>? Settings = null, string? Url = null);
public record PluginSettingSchemaDto(string Name, string Type, string? DisplayName, string? Description);
public record PluginTaskDto(string Name, string Description);
public record RunPluginTaskDto(string PluginId, string TaskName, Dictionary<string, string>? Args);
public record PluginSettingsDto(Dictionary<string, bool> EnabledMap);

// ===== DIRECTORY LISTING =====
public record DirectoryEntryDto(string Path, bool IsDirectory);
