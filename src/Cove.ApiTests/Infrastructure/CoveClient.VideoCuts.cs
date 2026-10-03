using Cove.Api.Controllers;
using Cove.Api.Services;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<VideoCutPreview> PreviewVideoCutAsync(
        int videoId,
        VideoCutPreviewRequestDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoCutPreview>(
            HttpMethod.Post,
            $"/api/videos/{videoId}/cut/preview",
            request,
            cancellationToken);

    public Task<VideoConversionJobStart> StartVideoCutAsync(
        VideoCutRequestDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoConversionJobStart>(
            HttpMethod.Post,
            "/api/videos/cut",
            request,
            cancellationToken);
}
