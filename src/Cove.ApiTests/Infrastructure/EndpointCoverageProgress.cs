namespace Cove.ApiTests.Infrastructure;

public sealed record EndpointCoverageException(ApiEndpointId Endpoint, string Reason);

public static class EndpointCoverageProgress
{
    public const int ExpectedMappedEndpoints = 509;

    public const int ExpectedTemporarilyUnmappedEndpoints = 9;

    public static IReadOnlySet<ApiEndpointId> TemporarilyUnmapped { get; } =
        TemporaryUnmappedEndpointText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ApiEndpointId.Parse)
            .ToHashSet();

    public static IReadOnlyList<EndpointCoverageException> Exceptions { get; } = [];

    private const string TemporaryUnmappedEndpointText = """
        DELETE /api/tags/{id:int}
        DELETE /api/videos/{id:int}/play
        GET /api/faces/capabilities
        GET /api/faces/{id:int}/host-tracks
        POST /api/faces/{id:int}/not-present
        POST /api/faces/{id:int}/split
        POST /api/tags/merge
        POST /api/videos/{id:int}/play
        POST /api/videos/{id:int}/play/reset
        """;
}
