using Cove.Api.Controllers;

namespace Cove.ApiTests.Infrastructure;

public enum ReadEndpoint
{
    AiDataSummary,
    AiRuns,
    ApiTokens,
    Audios,
    Audit,
    CurrentUser,
    Bookmarks,
    ContentRules,
    CustomFields,
    LatestConfigBackup,
    Embeddings,
    Extensions,
    Faces,
    Galleries,
    GlobalSearch,
    Groups,
    Images,
    Jobs,
    Logs,
    FilesystemPolicy,
    Performers,
    Plugins,
    Roles,
    SavedFilters,
    SegmentDisplayProfiles,
    Segments,
    ShareLinks,
    Studios,
    SystemStatus,
    TagApplications,
    TagGroups,
    Tags,
    Texts,
    Users,
    Videos,
}

public enum JsonResponseShape
{
    Object,
    Array,
    Paginated,
}

public sealed record ReadEndpointDefinition(
    ReadEndpoint Endpoint,
    Type ControllerType,
    string RequestUri,
    JsonResponseShape ExpectedShape)
{
    public ApiEndpointId CoveredEndpoint => ApiEndpointId.Create(
        "GET",
        RequestUri.Split('?', 2)[0]);
}

public static class ReadEndpointCatalog
{
    public static IReadOnlyList<ReadEndpointDefinition> All { get; } =
    [
        new(ReadEndpoint.AiDataSummary, typeof(AiDataController), "/api/ai-data/summary", JsonResponseShape.Object),
        new(ReadEndpoint.AiRuns, typeof(AiRunsController), "/api/ai-runs", JsonResponseShape.Paginated),
        new(ReadEndpoint.ApiTokens, typeof(ApiTokensController), "/api/apitokens", JsonResponseShape.Array),
        new(ReadEndpoint.Audios, typeof(AudiosController), "/api/audios", JsonResponseShape.Paginated),
        new(ReadEndpoint.Audit, typeof(AuditController), "/api/audit", JsonResponseShape.Paginated),
        new(ReadEndpoint.CurrentUser, typeof(AuthController), "/api/auth/me", JsonResponseShape.Object),
        new(ReadEndpoint.Bookmarks, typeof(BookmarksController), "/api/me/bookmarks", JsonResponseShape.Array),
        new(ReadEndpoint.ContentRules, typeof(ContentRulesController), "/api/content-rules", JsonResponseShape.Array),
        new(ReadEndpoint.CustomFields, typeof(CustomFieldsController), "/api/custom-fields", JsonResponseShape.Array),
        new(ReadEndpoint.LatestConfigBackup, typeof(DatabaseController), "/api/database/config/latest-backup", JsonResponseShape.Object),
        new(ReadEndpoint.Embeddings, typeof(EmbeddingsController), "/api/embeddings", JsonResponseShape.Paginated),
        new(ReadEndpoint.Extensions, typeof(ExtensionsController), "/api/extensions", JsonResponseShape.Array),
        new(ReadEndpoint.Faces, typeof(FacesController), "/api/faces", JsonResponseShape.Paginated),
        new(ReadEndpoint.Galleries, typeof(GalleriesController), "/api/galleries", JsonResponseShape.Paginated),
        new(ReadEndpoint.GlobalSearch, typeof(GlobalSearchController), "/api/search/global?q=unused", JsonResponseShape.Object),
        new(ReadEndpoint.Groups, typeof(GroupsController), "/api/groups", JsonResponseShape.Paginated),
        new(ReadEndpoint.Images, typeof(ImagesController), "/api/images", JsonResponseShape.Paginated),
        new(ReadEndpoint.Jobs, typeof(JobsController), "/api/jobs", JsonResponseShape.Array),
        new(ReadEndpoint.Logs, typeof(LogsController), "/api/logs", JsonResponseShape.Array),
        new(ReadEndpoint.FilesystemPolicy, typeof(MetadataController), "/api/metadata/filesystem-policy", JsonResponseShape.Object),
        new(ReadEndpoint.Performers, typeof(PerformersController), "/api/performers", JsonResponseShape.Paginated),
        new(ReadEndpoint.Plugins, typeof(PluginsController), "/api/plugins", JsonResponseShape.Array),
        new(ReadEndpoint.Roles, typeof(RolesController), "/api/roles", JsonResponseShape.Array),
        new(ReadEndpoint.SavedFilters, typeof(SavedFiltersController), "/api/savedfilters", JsonResponseShape.Array),
        new(ReadEndpoint.SegmentDisplayProfiles, typeof(SegmentDisplayProfilesController), "/api/segment-display-profiles", JsonResponseShape.Array),
        new(ReadEndpoint.Segments, typeof(SegmentsController), "/api/segments", JsonResponseShape.Paginated),
        new(ReadEndpoint.ShareLinks, typeof(ShareLinksController), "/api/share-links", JsonResponseShape.Array),
        new(ReadEndpoint.Studios, typeof(StudiosController), "/api/studios", JsonResponseShape.Paginated),
        new(ReadEndpoint.SystemStatus, typeof(SystemController), "/api/system/status", JsonResponseShape.Object),
        new(ReadEndpoint.TagApplications, typeof(TagApplicationsController), "/api/tagapplications", JsonResponseShape.Array),
        new(ReadEndpoint.TagGroups, typeof(TagGroupsController), "/api/taggroups", JsonResponseShape.Array),
        new(ReadEndpoint.Tags, typeof(TagsController), "/api/tags", JsonResponseShape.Paginated),
        new(ReadEndpoint.Texts, typeof(TextsController), "/api/texts", JsonResponseShape.Paginated),
        new(ReadEndpoint.Users, typeof(UsersController), "/api/users", JsonResponseShape.Array),
        new(ReadEndpoint.Videos, typeof(VideosController), "/api/videos", JsonResponseShape.Paginated),
    ];

    public static ReadEndpointDefinition Get(ReadEndpoint endpoint)
        => All.Single(definition => definition.Endpoint == endpoint);
}
