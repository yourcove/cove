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

    public Task<ApiVideoStreamContent> TranscodeVideoAsync(
        int videoId,
        string? resolution,
        double? start,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(resolution))
            query.Add($"resolution={Uri.EscapeDataString(resolution)}");
        if (start.HasValue)
            query.Add($"start={start.Value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}");

        var requestUri = $"/api/stream/video/{videoId}/transcode{(query.Count > 0 ? $"?{string.Join('&', query)}" : string.Empty)}";
        return GetVideoStreamContentAsync(requestUri, cancellationToken);
    }

    public async Task<ApiHlsPlaylistContent> GetHlsProfileAsync(
        int videoId,
        string profile,
        bool propagateAccessToken,
        CancellationToken cancellationToken = default)
    {
        var route = $"/api/stream/video/{videoId}/hls/{Uri.EscapeDataString(profile)}.m3u8";
        var requestUri = propagateAccessToken
            ? $"{route}?access_token={Uri.EscapeDataString(AccessToken)}&ignored=secret"
            : route;
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"GET {route} returned {(int)response.StatusCode} ({response.StatusCode}); expected 200 (OK). Response length: {content.Length}");
        }

        return new ApiHlsPlaylistContent(
            content,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl?.ToString());
    }

    public Task<ApiVideoStreamContent> GetHlsSegmentAsync(
        int videoId,
        string segment,
        CancellationToken cancellationToken = default)
        => GetVideoStreamContentAsync(
            $"/api/stream/video/{videoId}/hls/segment/{Uri.EscapeDataString(segment)}",
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
            response.Headers.CacheControl?.ToString(),
            response.Headers.AcceptRanges.ToArray());
    }
}

public sealed record ApiVideoStreamContent(
    byte[] Bytes,
    string? MediaType,
    string? CacheControl,
    IReadOnlyList<string> AcceptRanges);

public sealed record ApiHlsPlaylistContent(
    string Text,
    string? MediaType,
    string? CacheControl);
