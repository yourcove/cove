namespace Cove.ApiTests.Infrastructure;

public sealed record EndpointCoverageException(ApiEndpointId Endpoint, string Reason);

public static class EndpointCoverageProgress
{
    public const int ExpectedMappedEndpoints = 462;

    public const int ExpectedTemporarilyUnmappedEndpoints = 42;

    public static IReadOnlySet<ApiEndpointId> TemporarilyUnmapped { get; } =
        TemporaryUnmappedEndpointText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ApiEndpointId.Parse)
            .ToHashSet();

    public static IReadOnlyList<EndpointCoverageException> Exceptions { get; } = [];

    private const string TemporaryUnmappedEndpointText = """
        DELETE /api/auth/external/links/{linkid:int}
        DELETE /api/jobs/{jobid}
        DELETE /api/tags/{id:int}
        DELETE /api/users/{id:int}/external-links/{linkid:int}
        DELETE /api/videos/{id:int}/play
        GET /api/extensions/registry/categories
        GET /api/extensions/registry/search
        GET /api/extensions/registry/updates
        GET /api/extensions/registry/{extensionid}
        GET /api/extensions/registry/{extensionid}/dependencies
        GET /api/faces/capabilities
        GET /api/faces/{id:int}/host-tracks
        GET /api/stash-migration/import/{jobid}
        GET /api/stream/video/{videoid:int}/hls/{profile}.m3u8
        GET /api/stream/video/{videoid:int}/transcode
        POST /api/auth/bootstrap-owner
        POST /api/auth/external/links/cancel
        POST /api/auth/external/links/confirm
        POST /api/auth/external/links/preview
        POST /api/auth/external/redeem
        POST /api/auth/setup-token-redeem
        POST /api/database/config/backup
        POST /api/database/config/restore
        POST /api/database/restore
        POST /api/database/wipe
        POST /api/extensions/registry/install
        POST /api/faces/{id:int}/not-present
        POST /api/faces/{id:int}/split
        POST /api/files/folders/{id:int}/reveal
        POST /api/files/{id:int}/reveal
        POST /api/stash-migration/import
        POST /api/system/config/ui
        POST /api/system/shutdown
        POST /api/tags/merge
        POST /api/videos/{id:int}/cover/from-frame
        POST /api/videos/{id:int}/generate-screenshot
        POST /api/videos/{id:int}/play
        POST /api/videos/{id:int}/play/reset
        POST /api/videos/{id:int}/rescan
        PUT /api/jobs/{jobid}/reorder
        PUT /api/system/config
        PUT /api/system/config/ui/{key}
        """;
}
