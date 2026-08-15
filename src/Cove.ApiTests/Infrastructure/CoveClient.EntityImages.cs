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
    public Task UploadAudioImageAsync(AudioDto audio, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/audios/{audio.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetAudioImageAsync(AudioDto audio, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/audios/{audio.Id}/image", cancellationToken);
    public Task DeleteAudioImageAsync(AudioDto audio, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/audios/{audio.Id}/image", cancellationToken);
    public Task UploadTextImageAsync(TextDocumentDto text, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/texts/{text.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetTextImageAsync(TextDocumentDto text, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/texts/{text.Id}/image", cancellationToken);
    public Task DeleteTextImageAsync(TextDocumentDto text, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/texts/{text.Id}/image", cancellationToken);
    public Task UploadVideoImageAsync(VideoDto video, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/videos/{video.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetVideoImageAsync(VideoDto video, string? query = null, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/videos/{video.Id}/image{query}", cancellationToken);
    public Task DeleteVideoImageAsync(VideoDto video, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/videos/{video.Id}/image", cancellationToken);
    public Task UploadSegmentImageAsync(SegmentDto segment, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/segments/{segment.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetSegmentImageAsync(SegmentDto segment, string? query = null, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/segments/{segment.Id}/image{query}", cancellationToken);
    public Task DeleteSegmentImageAsync(SegmentDto segment, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/segments/{segment.Id}/image", cancellationToken);
    public Task UploadStudioImageAsync(StudioDto studio, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/studios/{studio.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetStudioImageAsync(StudioDto studio, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/studios/{studio.Id}/image", cancellationToken);
    public Task DeleteStudioImageAsync(StudioDto studio, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/studios/{studio.Id}/image", cancellationToken);
    public Task UploadTagImageAsync(TagDetailDto tag, byte[] image, string mediaType = "image/png", CancellationToken cancellationToken = default) => UploadEntityImageAsync($"/api/tags/{tag.Id}/image", image, mediaType, cancellationToken);
    public Task<ApiBinaryContent> GetTagImageAsync(TagDetailDto tag, CancellationToken cancellationToken = default) => GetEntityImageAsync($"/api/tags/{tag.Id}/image", cancellationToken);
    public Task DeleteTagImageAsync(TagDetailDto tag, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/tags/{tag.Id}/image", cancellationToken);
    public Task DeletePerformerImageAsync(PerformerDto performer, CancellationToken cancellationToken = default) => DeleteEntityImageAsync($"/api/performers/{performer.Id}/image", cancellationToken);

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
        return new ApiBinaryContent(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.CacheControl?.ToString());
    }

    private async Task DeleteEntityImageAsync(string requestUri, CancellationToken cancellationToken)
    {
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent) return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }
}
