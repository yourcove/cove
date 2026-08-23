using System.Net;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<ApiVideoStreamContent> GetGeneratedVideoScreenshotAsync(
        int videoId,
        double? seconds,
        CancellationToken cancellationToken = default)
        => GetVideoStreamContentAsync(
            $"/api/stream/video/{videoId}/screenshot{(seconds.HasValue ? $"?seconds={seconds.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}" : string.Empty)}",
            cancellationToken);

    private async Task<ApiVideoStreamContent> GetVideoStreamContentAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(WithCacheNonce(requestUri), cancellationToken);
        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}); expected 200 (OK). Response length: {content.Length}");
        }

        return new ApiVideoStreamContent(
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl?.ToString());
    }
}

public sealed record ApiVideoStreamContent(
    byte[] Bytes,
    string? MediaType,
    string? CacheControl);
