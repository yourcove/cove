using System.Net.Http.Headers;
using System.Text.Json;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task UploadGroupFrontImageAsync(GroupDto group, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/groups/{group.Id}/image/front", image, mediaType, cancellationToken);
    public Task UploadGroupBackImageAsync(GroupDto group, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/groups/{group.Id}/image/back", image, mediaType, cancellationToken);
    public Task UploadGalleryImageAsync(GalleryDto gallery, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/galleries/{gallery.Id}/image", image, mediaType, cancellationToken);
    public Task UploadGalleryBackImageAsync(GalleryDto gallery, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/galleries/{gallery.Id}/image/back", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetGroupFrontImageAsync(GroupDto group, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/groups/{group.Id}/image/front", cancellationToken);
    public Task<ApiBinaryContent> GetGroupBackImageAsync(GroupDto group, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/groups/{group.Id}/image/back", cancellationToken);
    public Task<ApiBinaryContent> GetGalleryImageAsync(GalleryDto gallery, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/galleries/{gallery.Id}/image", cancellationToken);
    public Task<ApiBinaryContent> GetGalleryBackImageAsync(GalleryDto gallery, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/galleries/{gallery.Id}/image/back", cancellationToken);
    public Task DeleteGroupFrontImageAsync(GroupDto group, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/groups/{group.Id}/image/front", cancellationToken);
    public Task DeleteGroupBackImageAsync(GroupDto group, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/groups/{group.Id}/image/back", cancellationToken);
    public Task DeleteGalleryImageAsync(GalleryDto gallery, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/galleries/{gallery.Id}/image", cancellationToken);
    public Task DeleteGalleryBackImageAsync(GalleryDto gallery, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/galleries/{gallery.Id}/image/back", cancellationToken);

    private async Task UploadEntityImageAsync(string requestUri, byte[] image, string mediaType, CancellationToken cancellationToken)
    {
        using var content = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        content.Add(imageContent, "file", "entity-image.png");
        using var response = await _client.PostAsync(requestUri, content, cancellationToken);
        _ = await ApiResponse.ReadAsync<JsonElement>(response, $"POST {requestUri}", cancellationToken);
    }

    private async Task<ApiBinaryContent> GetEntityImageAsync(string requestUri, CancellationToken cancellationToken)
    {
        requestUri = WithCacheNonce(requestUri);
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }
        return new ApiBinaryContent(await response.Content.ReadAsByteArrayAsync(cancellationToken), response.Content.Headers.ContentType?.MediaType);
    }

    private async Task DeleteEntityImageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }
}
