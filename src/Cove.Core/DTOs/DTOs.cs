using Cove.Core.Enums;
using Cove.Core.Interfaces;

namespace Cove.Core.DTOs;

/// <summary>Generic request for POST-based filtered queries.</summary>
public class FilteredQueryRequest<TFilter> where TFilter : class, new()
{
    public FindFilter? FindFilter { get; set; }
    public TFilter? ObjectFilter { get; set; }
}

// ===== SCENE DTOs =====
public record SceneDto(
    int Id, string? Title, string? Code, string? Details, string? Director,
    string? Date, int? Rating, bool Organized, int? StudioId, string? StudioName,
    double ResumeTime, double PlayDuration, int PlayCount, string? LastPlayedAt,
    int OCounter, List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    List<VideoFileDto> Files, List<SceneMarkerSummaryDto> Markers,
    List<GroupSummaryDto> Groups, List<GallerySummaryDto> Galleries,
    List<SceneRemoteIdDto> RemoteIds, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt);

public record SceneRemoteIdDto(string Endpoint, string RemoteId);

public record SceneGroupInputDto(int GroupId, int SceneIndex = 0);

public record SceneCreateDto(
    string? Title, string? Code, string? Details, string? Director,
    string? Date, int? Rating, bool Organized, int? StudioId,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds,
    List<SceneGroupInputDto>? Groups);

public record SceneUpdateDto(
    string? Title, string? Code, string? Details, string? Director,
    string? Date, int? Rating, bool? Organized, int? StudioId,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds,
    List<SceneGroupInputDto>? Groups, Dictionary<string, object>? CustomFields);

// ===== PERFORMER DTOs =====
public record PerformerDto(
    int Id, string Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool Favorite, int? Rating, string? Details, bool IgnoreAutoTag,
    List<string> Urls, List<string> Aliases, List<TagDto> Tags,
    List<PerformerRemoteIdDto> RemoteIds,
    int SceneCount, int ImageCount, int GalleryCount, int GroupCount,
    string? ImagePath, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt);

public record PerformerRemoteIdDto(string Endpoint, string RemoteId);

public record PerformerSummaryDto(int Id, string Name, string? Disambiguation, string? Gender, bool Favorite, string? ImagePath);

public record GallerySummaryDto(int Id, string? Title, string? Date);

public record PerformerCreateDto(
    string Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool Favorite, int? Rating, string? Details, bool IgnoreAutoTag,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds);

public record PerformerUpdateDto(
    string? Name, string? Disambiguation, string? Gender,
    string? Birthdate, string? DeathDate, string? Ethnicity, string? Country,
    string? EyeColor, string? HairColor, int? HeightCm, int? Weight,
    string? Measurements, string? FakeTits, double? PenisLength, string? Circumcised,
    string? CareerStart, string? CareerEnd, string? Tattoos, string? Piercings,
    bool? Favorite, int? Rating, string? Details, bool? IgnoreAutoTag,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, Dictionary<string, object>? CustomFields);

// ===== TAG DTOs =====
public record TagDto(int Id, string Name, string? Description, bool Favorite, bool IgnoreAutoTag, List<string> Aliases);

public record TagListDto(int Id, string Name, string? Description, bool Favorite, bool IgnoreAutoTag, List<string> Aliases,
    int SceneCount, int SceneMarkerCount, int ImageCount, int GalleryCount, int GroupCount, int PerformerCount, int StudioCount, string? ImagePath);

public record TagDetailDto(
    int Id, string Name, string? SortName, string? Description, bool Favorite, bool IgnoreAutoTag,
    List<string> Aliases, List<TagDto> Parents, List<TagDto> Children,
    int SceneCount, int PerformerCount, int ImageCount, int GalleryCount,
    int StudioCount, int GroupCount, int MarkerCount,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt);

public record TagCreateDto(string Name, string? SortName, string? Description, bool Favorite, bool IgnoreAutoTag, List<string>? Aliases, List<int>? ParentIds, List<int>? ChildIds);
public record TagUpdateDto(string? Name, string? SortName, string? Description, bool? Favorite, bool? IgnoreAutoTag, List<string>? Aliases, List<int>? ParentIds, List<int>? ChildIds, Dictionary<string, object>? CustomFields);

// ===== STUDIO DTOs =====
public record StudioDto(int Id, string Name, int? ParentId, string? ParentName, int? Rating, bool Favorite, string? Details, bool IgnoreAutoTag, bool Organized,
    List<string> Urls, List<string> Aliases, List<TagDto> Tags, List<StudioRemoteIdDto> RemoteIds,
    int SceneCount, int ImageCount, int GalleryCount, int GroupCount, int PerformerCount, int ChildStudioCount,
    string? ImagePath, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt);

public record StudioRemoteIdDto(string Endpoint, string RemoteId);

public record StudioCreateDto(string Name, int? ParentId, int? Rating, bool Favorite, string? Details, bool IgnoreAutoTag, bool Organized,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds);

public record StudioUpdateDto(string? Name, int? ParentId, int? Rating, bool? Favorite, string? Details, bool? IgnoreAutoTag, bool? Organized,
    List<string>? Urls, List<string>? Aliases, List<int>? TagIds, Dictionary<string, object>? CustomFields);

// ===== GALLERY DTOs =====
public record GalleryDto(int Id, string? Title, string? Code, string? Date, string? Details, string? Photographer,
    int? Rating, bool Organized, int? StudioId, string? StudioName,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    int ImageCount, int SceneCount, List<int> SceneIds, string? FolderPath, List<GalleryFileInfoDto> Files,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    string? CoverPath = null, int? CoverImageId = null);

public record GalleryFileInfoDto(int Id, string Path, long Size, string ModTime, List<FingerprintDto> Fingerprints);

public record GalleryCreateDto(string? Title, string? Code, string? Date, string? Details, string? Photographer,
    int? Rating, bool Organized, int? StudioId, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds);

public record GalleryUpdateDto(string? Title, string? Code, string? Date, string? Details, string? Photographer,
    int? Rating, bool? Organized, int? StudioId, List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? SceneIds, Dictionary<string, object>? CustomFields);

// ===== IMAGE DTOs =====
public record ImageDto(int Id, string? Title, string? Code, string? Details, string? Photographer,
    int? Rating, bool Organized, int OCounter, int? StudioId, string? StudioName, string? Date,
    List<string> Urls, List<TagDto> Tags, List<PerformerSummaryDto> Performers,
    int GalleryCount, List<int> GalleryIds, List<ImageFileDto> Files, Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt);

public record ImageFileDto(int Id, string Path, string Basename, string Format, int Width, int Height, long Size);

public record ImageCreateDto(string? Title, string? Code, string? Details, string? Photographer,
    int? Rating, bool Organized, int? StudioId, string? Date,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds);

public record ImageUpdateDto(string? Title, string? Code, string? Details, string? Photographer,
    int? Rating, bool? Organized, int? StudioId, string? Date,
    List<string>? Urls, List<int>? TagIds, List<int>? PerformerIds, List<int>? GalleryIds, Dictionary<string, object>? CustomFields);

// ===== GROUP DTOs =====
public record GroupDto(int Id, string Name, string? Aliases, int? Duration, string? Date,
    int? Rating, int? StudioId, string? StudioName, string? Director, string? Synopsis,
    List<string> Urls, List<TagDto> Tags, int SceneCount, int SubGroupCount, int ContainingGroupCount,
    Dictionary<string, object>? CustomFields, string CreatedAt, string UpdatedAt,
    string? FrontImagePath, string? BackImagePath);

public record GroupSummaryDto(int Id, string Name, int SceneIndex);

public record GroupCreateDto(string Name, string? Aliases, int? Duration, string? Date,
    int? Rating, int? StudioId, string? Director, string? Synopsis,
    List<string>? Urls, List<int>? TagIds);

public record GroupUpdateDto(string? Name, string? Aliases, int? Duration, string? Date,
    int? Rating, int? StudioId, string? Director, string? Synopsis,
    List<string>? Urls, List<int>? TagIds, Dictionary<string, object>? CustomFields);

// ===== SHARED DTOs =====
public record VideoFileDto(int Id, string Path, string Basename, string Format,
    int Width, int Height, double Duration, string VideoCodec, string AudioCodec,
    double FrameRate, long BitRate, long Size, List<FingerprintDto> Fingerprints,
    List<CaptionDto>? Captions = null);

public record CaptionDto(int Id, string LanguageCode, string CaptionType, string Filename);

public record FingerprintDto(string Type, string Value);

public record SceneMarkerSummaryDto(int Id, string Title, double Seconds, double? EndSeconds, int PrimaryTagId, string PrimaryTagName);

public record SceneMarkerWallDto(
    int Id, string Title, double Seconds, double? EndSeconds,
    int PrimaryTagId, string PrimaryTagName,
    int SceneId, string SceneTitle, string SceneFilePath,
    List<TagSummaryDto> Tags);

public record TagSummaryDto(int Id, string Name);

public record SceneMarkerCreateDto(string Title, double Seconds, double? EndSeconds, int PrimaryTagId, List<int>? TagIds);

public record SceneMarkerUpdateDto(string? Title, double? Seconds, double? EndSeconds, int? PrimaryTagId);

public record PaginatedResponse<T>(IReadOnlyList<T> Items, int TotalCount, int Page, int PerPage);

public record StatsDto(int SceneCount, int ImageCount, int GalleryCount, int PerformerCount,
    int StudioCount, int TagCount, int GroupCount, long TotalFileSize, double TotalPlayDuration);

// ===== AUTH DTOs =====
public record LoginRequest(string Username, string Password);
public record LoginResponse(string Token, string Username);
public record ApiKeyResponse(string ApiKey);

// ===== CONFIG DTOs =====
public record SystemStatusDto(
    string Version,
    string AppDir,
    string ConfigFile,
    string DatabasePath,
    bool MigrationRequired = false,
    string[]? PendingMigrations = null);

public record CoveConfigDto
{
    public List<CovePathDto> CovePaths { get; init; } = [];
    public string? GeneratedPath { get; init; }
    public string? CachePath { get; init; }
    public string Host { get; init; } = "0.0.0.0";
    public int Port { get; init; } = 9999;
    public int MaxParallelTasks { get; init; } = 1;
    public bool CalculateMd5 { get; init; }
    public List<string> VideoExtensions { get; init; } = [];
    public List<string> ImageExtensions { get; init; } = [];
    public List<string> GalleryExtensions { get; init; } = [];
    public List<string> ExcludePatterns { get; init; } = [];
    public List<string> ExcludeImagePatterns { get; init; } = [];
    public List<string> ExcludeGalleryPatterns { get; init; } = [];
    public bool CreateGalleriesFromFolders { get; init; }
    public bool WriteImageThumbnails { get; init; }
    public bool CreateImageClipsFromVideos { get; init; }
    public string GalleryCoverRegex { get; init; } = "(poster|cover|folder|board)\\.[^\\.]+$";
    public bool DeleteGeneratedDefault { get; init; } = true;
    public InterfaceConfigDto Interface { get; init; } = new();
    public UiConfigDto Ui { get; init; } = new();
    public SecurityConfigDto Security { get; init; } = new();
    public ScrapingConfigDto Scraping { get; init; } = new();
    public Dictionary<string, Dictionary<string, object?>> PluginConfigurations { get; init; } = [];
    public List<string> DisabledPlugins { get; init; } = [];
}

public record CovePathDto
{
    public string Path { get; init; } = "";
    public bool ExcludeVideo { get; init; }
    public bool ExcludeImage { get; init; }
}

public record InterfaceConfigDto
{
    public string? Language { get; init; }
    public List<string> MenuItems { get; init; } = [];
}

public record UiConfigDto
{
    public string? Title { get; init; }
    public bool AbbreviateCounters { get; init; }
    public RatingSystemOptionsDto RatingSystemOptions { get; init; } = new();
    public bool DeleteFileDefault { get; init; }
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
    public int MaxSessionAgeMinutes { get; init; } = 60;
    public string? NewPassword { get; init; }
}

public record ScrapingConfigDto
{
    public List<string> ScraperDirectories { get; init; } = [];
    public List<PackageSourceDto> ScraperPackageSources { get; init; } = [];
    public List<MetadataServerDto> MetadataServers { get; init; } = [];
}

public record PackageSourceDto
{
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
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

public record MetadataServerPerformerImportRequestDto(string Endpoint, string PerformerId);

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

public record MetadataServerStudioImportRequestDto(string Endpoint, string StudioId);

public record MetadataServerEntityCandidateDto(
    string RemoteId,
    string Name,
    bool ExistsLocally,
    int? LocalId
);

public record MetadataServerSceneEntityOverrideDto
{
    public string RemoteId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public int? LocalId { get; init; }
}

public record MetadataServerSceneMatchDto(
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

public record MetadataServerSceneImportRequestDto
{
    public string Endpoint { get; init; } = string.Empty;
    public string SceneId { get; init; } = string.Empty;
    public bool SetCoverImage { get; init; } = true;
    public bool SetTags { get; init; } = true;
    public bool SetPerformers { get; init; } = true;
    public bool SetStudio { get; init; } = true;
    public bool OnlyExistingTags { get; init; }
    public bool OnlyExistingPerformers { get; init; }
    public bool OnlyExistingStudio { get; init; }
    public bool MarkOrganized { get; init; }
    public List<string>? ExcludedTagNames { get; init; }
    public List<string>? ExcludedPerformerNames { get; init; }
    public MetadataServerSceneEntityOverrideDto? StudioOverride { get; init; }
    public List<MetadataServerSceneEntityOverrideDto>? PerformerOverrides { get; init; }
    public List<MetadataServerSceneEntityOverrideDto>? TagOverrides { get; init; }
}

public record MetadataServerEndpointDto(string Endpoint);

public record ScraperSummaryDto(
    string Id,
    string Name,
    string EntityType,
    List<string> SupportedScrapes,
    List<string> Urls,
    string SourcePath
);

// ===== ACTIVITY DTOs =====
public record SceneActivityDto(double? ResumeTime, double? PlayDuration);

public record SceneHistoryDto(List<string> PlayHistory, List<string> OHistory);

// ===== BULK UPDATE DTOs =====
public enum BulkUpdateMode { Set, Add, Remove }

public record BulkSceneUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkPerformerUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Favorite { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkImageUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkGalleryUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Organized { get; init; }
    public int? StudioId { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
    public List<int>? PerformerIds { get; init; }
    public BulkUpdateMode PerformerMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkStudioUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public bool? Favorite { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

public record BulkTagUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public bool? Favorite { get; init; }
    public bool? IgnoreAutoTag { get; init; }
}

public record BulkGroupUpdateDto
{
    public List<int> Ids { get; init; } = [];
    public int? Rating { get; init; }
    public int? StudioId { get; init; }
    public List<int>? TagIds { get; init; }
    public BulkUpdateMode TagMode { get; init; } = BulkUpdateMode.Add;
}

// ===== MERGE DTOs =====
public record SceneMergeDto(int TargetId, List<int> SourceIds);
public record PerformerMergeDto(int TargetId, List<int> SourceIds);
public record TagMergeDto(int TargetId, List<int> SourceIds);
public record StudioMergeDto(int TargetId, List<int> SourceIds);

// ===== GROUP HIERARCHY DTOs =====
public record AddSubGroupDto(int SubGroupId, int? OrderIndex = null, string? Description = null);
public record ReorderSubGroupsDto(List<int> SubGroupIds);

// ===== BATCH/BULK DTOs =====
public record BatchDeleteDto(List<int> Ids);
public record BulkSceneMarkerUpdateDto(List<int> Ids, int? PrimaryTagId, string? TagMode, List<int>? TagIds);

// ===== FILE OPERATION DTOs =====
public record MoveFilesDto(List<int> FileIds, string DestinationPath);
public record DeleteFilesDto(List<int> FileIds, bool DeleteFromDisk);
public record FileSetFingerprintsDto(int FileId, List<FingerprintEntryDto> Fingerprints);
public record FingerprintEntryDto(string Type, string Value);
public record SceneAssignFileDto(int FileId);
public record GallerySetCoverDto(int ImageId);

// ===== GALLERY ADVANCED DTOs =====
public record GalleryAddImagesDto(List<int> ImageIds);
public record GalleryRemoveImagesDto(List<int> ImageIds);
public record GalleryChapterDto(int Id, string Title, int ImageIndex, int GalleryId, string CreatedAt, string UpdatedAt);
public record GalleryChapterCreateDto(string Title, int ImageIndex);
public record GalleryChapterUpdateDto(string? Title, int? ImageIndex);

// ===== GROUP ADVANCED DTOs =====
public record GroupSubGroupsDto(List<GroupSubGroupEntryDto> SubGroups);
public record GroupSubGroupEntryDto(int GroupId, int SceneIndex);

// ===== METADATA OPERATION DTOs =====
public record ScanOptionsDto
{
    public List<string>? Paths { get; init; }
    public bool ScanGenerators { get; init; }
    public bool ScanGenerateCovers { get; init; }
    public bool ScanGeneratePreviews { get; init; }
    public bool ScanGenerateSprites { get; init; }
    public bool ScanGeneratePhashes { get; init; }
    public bool ScanGenerateThumbnails { get; init; }
    public bool ScanGenerateImagePhashes { get; init; }
    public bool Rescan { get; init; }
}

public record GenerateOptionsDto
{
    public bool Thumbnails { get; init; } = true;
    public bool Previews { get; init; }
    public bool Sprites { get; init; }
    public bool Markers { get; init; }
    public bool Phashes { get; init; }
    public bool ImageThumbnails { get; init; }
    public bool ImagePhashes { get; init; }
    public bool Overwrite { get; init; }
    public List<int>? SceneIds { get; init; }
    public List<string>? Paths { get; init; }
}

public record AutoTagOptionsDto
{
    public List<string>? Performers { get; init; }
    public List<string>? Studios { get; init; }
    public List<string>? Tags { get; init; }
}

public record CleanOptionsDto
{
    public List<string>? Paths { get; init; }
    public bool DryRun { get; init; }
}

public record ExportOptionsDto
{
    public bool IncludeScenes { get; init; } = true;
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
    public List<int>? SceneIds { get; init; }
    public bool SetCoverImage { get; init; } = true;
    public bool SetTags { get; init; } = true;
    public bool SetPerformers { get; init; } = true;
    public bool SetStudio { get; init; } = true;
    public bool CreateTags { get; init; } = true;
    public bool CreatePerformers { get; init; } = true;
    public bool CreateStudios { get; init; } = true;
    public bool MarkOrganized { get; init; }
    public bool SkipMultipleMatches { get; init; } = true;
    public bool SkipSingleNamePerformers { get; init; } = true;
}

// ===== DATABASE OPERATION DTOs =====
public record BackupResultDto(string BackupPath, long SizeBytes, string Timestamp);

// ===== SCRAPER DTOs =====
public record ScrapeUrlDto(string Url, string ContentType);

public record ScrapedSceneDto
{
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

// ===== SCRAPER EXECUTION REQUEST DTOs =====
public record ScrapeUrlRequest(string ScraperId, string EntityType, string Url);
public record ScrapeNameRequest(string ScraperId, string EntityType, string Name);
public record ScrapeFragmentRequest(string ScraperId, string EntityType, Dictionary<string, object> Fragment);

// ===== PLUGIN DTOs =====
public record PluginDto(string Id, string Name, string Description, string Version, bool Enabled, List<PluginTaskDto> Tasks, List<PluginSettingSchemaDto>? Settings = null, string? Url = null);
public record PluginSettingSchemaDto(string Name, string Type, string? DisplayName, string? Description);
public record PluginTaskDto(string Name, string Description);
public record RunPluginTaskDto(string PluginId, string TaskName, Dictionary<string, string>? Args);
public record PluginSettingsDto(Dictionary<string, bool> EnabledMap);

public record PackageDto(string Name, string Description, string Version, string SourceUrl, string Type, bool Installed, string? InstalledVersion);
public record InstallPackagesDto(List<InstallPackageEntryDto> Packages);
public record InstallPackageEntryDto(string Id, string SourceUrl);

// ===== DLNA DTOs =====
public record DlnaStatusDto(bool Running, string? UntilDisabled, List<string> RecentIps, List<string> AllowedIps);
public record DlnaToggleDto(int? DurationMinutes);
public record DlnaIpDto(string IpAddress, int? DurationMinutes);

// ===== DIRECTORY LISTING =====
public record DirectoryEntryDto(string Path, bool IsDirectory);

// ===== SAVED FILTER ADVANCED =====
public record SetDefaultFilterDto(string Mode, int? FilterId);
