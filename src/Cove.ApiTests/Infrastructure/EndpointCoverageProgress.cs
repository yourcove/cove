namespace Cove.ApiTests.Infrastructure;

public sealed record EndpointCoverageException(ApiEndpointId Endpoint, string Reason);

public static class EndpointCoverageProgress
{
    public const int ExpectedMappedEndpoints = 487;

    public const int ExpectedTemporarilyUnmappedEndpoints = 17;

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
        POST /api/extensions/registry/install
        POST /api/faces/{id:int}/not-present
        POST /api/faces/{id:int}/split
        POST /api/files/folders/{id:int}/reveal
        POST /api/files/{id:int}/reveal
        POST /api/tags/merge
        POST /api/videos/{id:int}/play
        POST /api/videos/{id:int}/play/reset
        """;
}
