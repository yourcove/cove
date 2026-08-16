namespace Cove.ApiTests.Infrastructure;

public sealed record EndpointCoverageException(ApiEndpointId Endpoint, string Reason);

public static class EndpointCoverageProgress
{
    public const int ExpectedMappedEndpoints = 478;

    public const int ExpectedTemporarilyUnmappedEndpoints = 26;

    public static IReadOnlySet<ApiEndpointId> TemporarilyUnmapped { get; } =
        TemporaryUnmappedEndpointText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ApiEndpointId.Parse)
            .ToHashSet();

    public static IReadOnlyList<EndpointCoverageException> Exceptions { get; } = [];

    private const string TemporaryUnmappedEndpointText = """
        DELETE /api/tags/{id:int}
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
        POST /api/database/wipe
        POST /api/extensions/registry/install
        POST /api/faces/{id:int}/not-present
        POST /api/faces/{id:int}/split
        POST /api/files/folders/{id:int}/reveal
        POST /api/files/{id:int}/reveal
        POST /api/stash-migration/import
        POST /api/system/shutdown
        POST /api/tags/merge
        POST /api/videos/{id:int}/cover/from-frame
        POST /api/videos/{id:int}/generate-screenshot
        POST /api/videos/{id:int}/play
        POST /api/videos/{id:int}/play/reset
        POST /api/videos/{id:int}/rescan
        """;
}
