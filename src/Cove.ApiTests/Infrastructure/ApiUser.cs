using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed class ApiUser : IDisposable
{
    private readonly HttpClient _client;

    internal ApiUser(Uri baseAddress, string accessToken)
    {
        BaseAddress = baseAddress;
        AccessToken = accessToken;
        _client = new HttpClient { BaseAddress = baseAddress };
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    }

    public Uri BaseAddress { get; }

    public string AccessToken { get; }

    public Task<PerformerDto> CreatePerformerAsync(
        PerformerCreateDto performer,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(HttpMethod.Post, "/api/performers", performer, cancellationToken);

    public Task<VideoDto> CreateVideoAsync(
        string title,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            "/api/videos",
            new VideoCreateDto(
                Title: title,
                Code: null,
                Details: null,
                Director: null,
                Date: null,
                Rating: null,
                Organized: false,
                StudioId: null,
                Captions: null,
                Urls: [],
                TagIds: [],
                PerformerIds: [],
                GalleryIds: [],
                Groups: []),
            cancellationToken);

    public Task<VideoDto> GetVideoByIdAsync(
        int videoId,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Get,
            $"/api/videos/{videoId}?apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<VideoDto> ImportVideoFromMetadataServiceAsync(
        VideoDto video,
        MetadataServiceSceneHandle metadataScene,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Post,
            $"/api/videos/{video.Id}/metadata-server/import",
            new MetadataServerVideoImportRequestDto
            {
                Endpoint = metadataScene.Endpoint.AbsoluteUri,
                VideoId = metadataScene.Id,
            },
            cancellationToken);

    public Task<VideoDto> RemoveTagFromVideoAsync(
        VideoDto video,
        TagDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoDto>(
            HttpMethod.Put,
            $"/api/videos/{video.Id}",
            new
            {
                tagIds = video.Tags
                    .Where(candidate => candidate.CanRemove && candidate.Id != tag.Id)
                    .Select(candidate => candidate.Id)
                    .ToArray(),
            },
            cancellationToken);

    public Task<TagDetailDto> CreateTagAsync(
        TagCreateDto tag,
        CancellationToken cancellationToken = default)
        => SendAsync<TagDetailDto>(HttpMethod.Post, "/api/tags", tag, cancellationToken);

    public async Task<bool> TagExistsAsync(
        int tagId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/tags/{tagId}?apiTestNonce={Guid.NewGuid():N}";
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.OK)
            return true;
        if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            return false;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task<PerformerDto> GetPerformerByIdAsync(
        int performerId,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Get,
            $"/api/performers/{performerId}?apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public async Task<IReadOnlyList<PerformerDto>> GetPerformersAsync(
        CancellationToken cancellationToken = default)
    {
        var result = await SendAsync<PaginatedResponse<PerformerDto>>(
            HttpMethod.Get,
            $"/api/performers?perPage=250&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);
        return result.Items;
    }

    public async Task<PerformerDto> LinkTagToPerformerAsync(
        TagDetailDto tag,
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var current = await GetPerformerByIdAsync(performer.Id, cancellationToken);
        var tagIds = current.Tags
            .Select(existingTag => existingTag.Id)
            .Append(tag.Id)
            .Distinct()
            .ToList();

        return await SendAsync<PerformerDto>(
            HttpMethod.Put,
            $"/api/performers/{performer.Id}",
            new { tagIds },
            cancellationToken);
    }

    public HttpClient CreateHttpClient()
    {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);
        return client;
    }

    public void Dispose() => _client.Dispose();

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string requestUri,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, requestUri);
        if (payload is not null)
            request.Content = JsonContent.Create(payload, options: ApiJson.Options);

        using var response = await _client.SendAsync(request, cancellationToken);
        return await ApiResponse.ReadAsync<T>(
            response,
            $"{method} {requestUri}",
            cancellationToken);
    }
}
