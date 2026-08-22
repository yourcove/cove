namespace Cove.ApiTests.Infrastructure;

public sealed record EndpointCoverageException(ApiEndpointId Endpoint, string Reason);

public static class EndpointCoverageProgress
{
    public const int ExpectedMappedEndpoints = 379;

    public const int ExpectedTemporarilyUnmappedEndpoints = 125;

    public static IReadOnlySet<ApiEndpointId> TemporarilyUnmapped { get; } =
        TemporaryUnmappedEndpointText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ApiEndpointId.Parse)
            .ToHashSet();

    public static IReadOnlyList<EndpointCoverageException> Exceptions { get; } = [];

    private const string TemporaryUnmappedEndpointText = """
        DELETE /api/apitokens/{id:guid}
        DELETE /api/auth/external/links/{linkid:int}
        DELETE /api/custom-fields/{id:int}
        DELETE /api/embeddings
        DELETE /api/galleries/{galleryid:int}/chapters/{chapterid:int}
        DELETE /api/jobs/{jobid}
        DELETE /api/share-links/{id:guid}
        DELETE /api/tagapplications/host/{hosttype}/{hostid:int}/tag/{tagid:int}
        DELETE /api/tagapplications/{id:int}
        DELETE /api/tags/{id:int}
        DELETE /api/users/{id:int}/external-links/{linkid:int}
        DELETE /api/videos/{id:int}/play
        DELETE /api/videos/{videoid:int}/detections/{id:int}
        GET /api/ai-runs/{id:int}
        GET /api/embeddings/{id:int}
        GET /api/extensions/registry/categories
        GET /api/extensions/registry/search
        GET /api/extensions/registry/updates
        GET /api/extensions/registry/{extensionid}
        GET /api/extensions/registry/{extensionid}/dependencies
        GET /api/faces/capabilities
        GET /api/faces/{id:int}/host-tracks
        GET /api/galleries/{id:int}/chapters
        GET /api/galleries/{id:int}/cover
        GET /api/jobs/backup/latest
        GET /api/jobs/history
        GET /api/performers/{id:int}/metadata-server/search
        GET /api/plugins/tasks
        GET /api/plugins/{pluginid}/config
        GET /api/scrape-attempts/{id:guid}
        GET /api/stash-migration/import/{jobid}
        GET /api/stream/image/{imageid:int}/thumbnail
        GET /api/stream/video/{videoid:int}/caption/{captionid:int}
        GET /api/stream/video/{videoid:int}/captions
        GET /api/stream/video/{videoid:int}/hls/segment/{segment}
        GET /api/stream/video/{videoid:int}/hls/{profile}.m3u8
        GET /api/stream/video/{videoid:int}/resolutions
        GET /api/stream/video/{videoid:int}/transcode
        GET /api/studios/{id:int}/metadata-server/search
        GET /api/system/ffmpeg-capabilities
        GET /api/tags/{id:int}/metadata-server/search
        GET /api/videos/{id:int}/metadata-server/search
        GET /api/videos/{videoid:int}/detections/{id:int}
        POST /api/ai-data/purge
        POST /api/apitokens
        POST /api/auth/bootstrap-owner
        POST /api/auth/external/links/cancel
        POST /api/auth/external/links/confirm
        POST /api/auth/external/links/preview
        POST /api/auth/external/redeem
        POST /api/auth/setup-token-redeem
        POST /api/custom-fields
        POST /api/database/backup
        POST /api/database/config/backup
        POST /api/database/config/restore
        POST /api/database/migrate
        POST /api/database/optimize
        POST /api/database/restore
        POST /api/database/wipe
        POST /api/embeddings/search
        POST /api/extensions/registry/install
        POST /api/faces/{id:int}/not-present
        POST /api/faces/{id:int}/split
        POST /api/files/delete
        POST /api/files/fingerprints
        POST /api/files/folders/{id:int}/reveal
        POST /api/files/move
        POST /api/files/{id:int}/reveal
        POST /api/galleries/{id:int}/chapters
        POST /api/galleries/{id:int}/rescan
        POST /api/jobs/backup
        POST /api/jobs/clean
        POST /api/jobs/generate-image-phashes
        POST /api/jobs/generate-thumbnails
        POST /api/jobs/generate-video-phashes
        POST /api/jobs/scan
        POST /api/performers/merge
        POST /api/performers/metadata-server/batch-tag
        POST /api/performers/metadata-server/find-by-ids
        POST /api/performers/{id:int}/apply-scraped
        POST /api/performers/{id:int}/metadata-server/import
        POST /api/performers/{id:int}/metadata-server/submit-draft
        POST /api/performers/{id:int}/scrape
        POST /api/performers/{id:int}/scrape-preview
        POST /api/performers/{id:int}/scrape-url
        POST /api/plugins/reload
        POST /api/plugins/run-task
        POST /api/plugins/settings
        POST /api/plugins/{pluginid}/config
        POST /api/scrape-attempts
        POST /api/scrape-attempts/resolve-relations
        POST /api/scrape-attempts/{id:guid}/apply
        POST /api/share-links
        POST /api/stash-migration/import
        POST /api/studios/metadata-server/batch-tag
        POST /api/studios/metadata-server/find-by-ids
        POST /api/studios/{id:int}/metadata-server/import
        POST /api/studios/{id:int}/metadata-server/submit-draft
        POST /api/system/config/ui
        POST /api/system/scrapers/scrape-fragment
        POST /api/system/scrapers/scrape-name
        POST /api/system/shutdown
        POST /api/tagapplications
        POST /api/tags/metadata-server/batch-tag
        POST /api/tags/metadata-server/find-by-ids
        POST /api/tags/merge
        POST /api/tags/{id:int}/metadata-server/import
        POST /api/tags/{id:int}/metadata-server/submit-draft
        POST /api/videos/metadata-server/find-by-ids
        POST /api/videos/{id:int}/activity/reset
        POST /api/videos/{id:int}/cover/from-frame
        POST /api/videos/{id:int}/generate-screenshot
        POST /api/videos/{id:int}/metadata-server/import
        POST /api/videos/{id:int}/metadata-server/submit-draft
        POST /api/videos/{id:int}/metadata-server/submit-fingerprints
        POST /api/videos/{id:int}/play
        POST /api/videos/{id:int}/play/reset
        POST /api/videos/{id:int}/rescan
        PUT /api/custom-fields
        PUT /api/custom-fields/{id:int}
        PUT /api/galleries/{galleryid:int}/chapters/{chapterid:int}
        PUT /api/jobs/{jobid}/reorder
        PUT /api/system/config
        PUT /api/system/config/ui/{key}
        PUT /api/videos/{videoid:int}/detections/{id:int}
        """;
}
