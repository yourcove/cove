using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<IReadOnlyList<GalleryChapterDto>> GetGalleryChaptersAsync(
        GalleryDto gallery,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<GalleryChapterDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/galleries/{gallery.Id}/chapters"),
            payload: null,
            cancellationToken);

    public Task<GalleryChapterDto> CreateGalleryChapterAsync(
        GalleryDto gallery,
        GalleryChapterCreateDto chapter,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryChapterDto>(
            HttpMethod.Post,
            $"/api/galleries/{gallery.Id}/chapters",
            chapter,
            cancellationToken);

    public Task<GalleryChapterDto> UpdateGalleryChapterAsync(
        GalleryDto gallery,
        GalleryChapterDto chapter,
        GalleryChapterUpdateDto update,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryChapterDto>(
            HttpMethod.Put,
            $"/api/galleries/{gallery.Id}/chapters/{chapter.Id}",
            update,
            cancellationToken);

    public Task DeleteGalleryChapterAsync(
        GalleryDto gallery,
        GalleryChapterDto chapter,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/galleries/{gallery.Id}/chapters/{chapter.Id}",
            new { },
            cancellationToken);

    public Task<EntityEngagementDto> SetGalleryRatingAsync(
        GalleryDto gallery,
        int rating,
        CancellationToken cancellationToken = default)
        => SendAsync<EntityEngagementDto>(
            HttpMethod.Put,
            $"/api/engagement/{AffinityHostType.Gallery}/{gallery.Id}/rating",
            new VideoRatingDto(rating, "overall"),
            cancellationToken);

    public Task<GalleryDto> UpdateGalleryAsync(
        int galleryId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryDto>(
            HttpMethod.Put,
            $"/api/galleries/{galleryId}",
            update,
            cancellationToken);

    public async Task<string> RescanGalleryAsync(
        int galleryId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendForExpectedStatusAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/galleries/{galleryId}/rescan",
            payload: null,
            System.Net.HttpStatusCode.OK,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST /api/galleries/{galleryId}/rescan did not return a job id.");
    }

    public Task<PaginatedResponse<GalleryDto>> FindGalleriesAsync(
        FilteredQueryRequest<GalleryFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<GalleryDto>>(
            HttpMethod.Post,
            "/api/galleries/find",
            request,
            cancellationToken);

    public Task<GalleryAggregate> AggregateGalleriesAsync(
        FilteredQueryRequest<GalleryFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<GalleryAggregate>(
            HttpMethod.Post,
            "/api/galleries/aggregate",
            request,
            cancellationToken);

    public async Task<int> BulkUpdateGalleriesAsync(
        BulkGalleryUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/galleries/bulk",
            request,
            cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public Task DeleteGalleryAsync(
        int galleryId,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/galleries/{galleryId}",
            new { },
            cancellationToken);

    public async Task<int> BulkDeleteGalleriesAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Delete,
            "/api/galleries/bulk",
            request,
            cancellationToken);
        return response.GetProperty("deleted").GetInt32();
    }

    public async Task<int> RemoveGalleryImagesAsync(
        GalleryDto gallery,
        IReadOnlyList<ImageDto> images,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Delete,
            $"/api/galleries/{gallery.Id}/images",
            new GalleryRemoveImagesDto(images.Select(image => image.Id).ToList()),
            cancellationToken);
        return response.GetProperty("removed").GetInt32();
    }
}
