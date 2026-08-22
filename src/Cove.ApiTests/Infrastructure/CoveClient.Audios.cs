using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<AudioDto> CreateAudioAsync(
        string title,
        CancellationToken cancellationToken = default)
        => CreateAudioAsync(
            new AudioCreateDto(
                Title: title,
                Code: null,
                Details: null,
                Organized: false,
                StudioId: null,
                Date: null,
                Urls: [],
                TagIds: [],
                PerformerIds: [],
                GroupIds: []),
            cancellationToken);

    public Task<AudioDto> CreateAudioAsync(
        AudioCreateDto audio,
        CancellationToken cancellationToken = default)
        => SendAsync<AudioDto>(HttpMethod.Post, "/api/audios", audio, cancellationToken);

    public Task<AudioDto> CreateAudioFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
        => SendAsync<AudioDto>(
            HttpMethod.Post,
            "/api/audios/from-file",
            new FileBackedCreateDto(filePath),
            cancellationToken);

    public async Task<ApiBinaryContent> GetAudioStreamAsync(
        int audioId,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/audios/{audioId}/stream");
        using var response = await _client.GetAsync(requestUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"GET {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
        }

        return new ApiBinaryContent(
            await response.Content.ReadAsByteArrayAsync(cancellationToken),
            response.Content.Headers.ContentType?.MediaType);
    }

    public async Task<string> RescanAudioAsync(
        int audioId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            $"/api/audios/{audioId}/rescan",
            payload: null,
            cancellationToken);
        return response.GetProperty("jobId").GetString()
            ?? throw new InvalidOperationException($"POST /api/audios/{audioId}/rescan did not return a job id.");
    }

    public Task<AudioDto> GetAudioByIdAsync(
        int audioId,
        CancellationToken cancellationToken = default)
        => SendAsync<AudioDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/audios/{audioId}"),
            payload: null,
            cancellationToken);

    public Task<AudioDto> UpdateAudioAsync(
        int audioId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<AudioDto>(
            HttpMethod.Put,
            $"/api/audios/{audioId}",
            update,
            cancellationToken);

    public Task<PaginatedResponse<AudioDto>> FindAudiosAsync(
        FilteredQueryRequest<AudioFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<AudioDto>>(
            HttpMethod.Post,
            "/api/audios/find",
            request,
            cancellationToken);

    public Task<AudioAggregate> AggregateAudiosAsync(
        FilteredQueryRequest<AudioFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<AudioAggregate>(
            HttpMethod.Post,
            "/api/audios/aggregate",
            request,
            cancellationToken);

    public Task<VideoHistoryDto> GetAudioHistoryAsync(
        AudioDto audio,
        CancellationToken cancellationToken = default)
        => SendAsync<VideoHistoryDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/audios/{audio.Id}/history"),
            payload: null,
            cancellationToken);

    public Task<int> IncrementAudioLikeAsync(
        AudioDto audio,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Post, $"/api/audios/{audio.Id}/like", payload: null, cancellationToken);

    public Task<int> AddHistoricalAudioLikeAsync(
        AudioDto audio,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(
            HttpMethod.Post,
            $"/api/audios/{audio.Id}/like/historical",
            new HistoricalLikeDto(at),
            cancellationToken);

    public Task DeleteHistoricalAudioLikeAsync(
        AudioDto audio,
        DateTime at,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            $"/api/audios/{audio.Id}/like/history?at={Uri.EscapeDataString(at.ToUniversalTime().ToString("O"))}",
            new { },
            cancellationToken);

    public Task<int> DecrementAudioLikeAsync(
        AudioDto audio,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Delete, $"/api/audios/{audio.Id}/like", payload: null, cancellationToken);

    public Task<int> ResetAudioLikeAsync(
        AudioDto audio,
        CancellationToken cancellationToken = default)
        => SendAsync<int>(HttpMethod.Post, $"/api/audios/{audio.Id}/like/reset", payload: null, cancellationToken);

    public Task ResetAudioActivityAsync(
        AudioDto audio,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Post,
            $"/api/audios/{audio.Id}/activity/reset",
            new { },
            cancellationToken);

    public async Task<int> BulkUpdateAudiosAsync(
        BulkAudioUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/audios/bulk",
            request,
            cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public async Task DeleteAudioAsync(
        int audioId,
        bool deleteFile = false,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/audios/{audioId}?deleteFile={deleteFile.ToString().ToLowerInvariant()}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task BulkDeleteAudiosAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            "/api/audios/bulk",
            request,
            cancellationToken);
}
