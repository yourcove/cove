using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<ImageDto> CreateImageAsync(
        string title,
        CancellationToken cancellationToken = default)
        => CreateImageAsync(
            new ImageCreateDto(
                Title: title,
                Code: null,
                Details: null,
                Photographer: null,
                Rating: null,
                Organized: false,
                StudioId: null,
                Date: null,
                Urls: [],
                TagIds: [],
                PerformerIds: [],
                GalleryIds: [],
                GroupIds: []),
            cancellationToken);

    public Task<ImageDto> CreateImageAsync(
        ImageCreateDto image,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageDto>(HttpMethod.Post, "/api/images", image, cancellationToken);

    public Task<ImageDto> GetImageByIdAsync(
        int imageId,
        CancellationToken cancellationToken = default)
        => SendAsync<ImageDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/images/{imageId}"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<ImageDto>> GetImagesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<ImageDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/images?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public Task<GalleryDto> CreateGalleryAsync(
        GalleryCreateDto gallery,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryDto>(HttpMethod.Post, "/api/galleries", gallery, cancellationToken);

    public Task<GalleryDto> GetGalleryByIdAsync(
        int galleryId,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/galleries/{galleryId}"),
            payload: null,
            cancellationToken);

    public Task<int> GetGalleryLikeCountAsync(
        GalleryDto gallery,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Get,
            WithCacheNonce($"/api/galleries/{gallery.Id}/like-count"),
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<GalleryDto>> GetGalleriesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<GalleryDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/galleries?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public async Task<IReadOnlyList<TextDocumentDto>> GetTextsAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<TextDocumentDto>>(
            HttpMethod.Get,
            WithCacheNonce("/api/texts?perPage=250"),
            payload: null,
            cancellationToken);
        return result.Items;
    }
}
