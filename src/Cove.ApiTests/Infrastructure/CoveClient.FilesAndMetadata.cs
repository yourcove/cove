using System.Net.Http.Headers;
using System.Text.Json;
using Cove.Api.Services;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<CustomFieldDefinitionDto> CreateCustomFieldDefinitionAsync(
        CustomFieldDefinitionCreateDto definition,
        CancellationToken cancellationToken = default)
        => SendAsync<CustomFieldDefinitionDto>(HttpMethod.Post, "/api/custom-fields", definition, cancellationToken);

    public async Task<ApiBinaryContent> GetTextFileAsync(
        TextDocumentDto text,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/texts/{text.Id}/file");
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

    public async Task UploadPerformerImageAsync(
        PerformerDto performer,
        byte[] image,
        CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        using var imageContent = new ByteArrayContent(image);
        imageContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        content.Add(imageContent, "file", "performer.png");

        var requestUri = $"/api/performers/{performer.Id}/image";
        using var response = await _client.PostAsync(requestUri, content, cancellationToken);
        _ = await ApiResponse.ReadAsync<JsonElement>(
            response,
            $"POST {requestUri}",
            cancellationToken);
    }

    public async Task<ApiBinaryContent> GetPerformerImageAsync(
        PerformerDto performer,
        CancellationToken cancellationToken = default)
    {
        var requestUri = WithCacheNonce($"/api/performers/{performer.Id}/image");
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

    public Task<IReadOnlyList<DirectoryEntryDto>> BrowseDirectoryAsync(
        string path,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<DirectoryEntryDto>>(
            HttpMethod.Get,
            $"/api/files/browse?path={Uri.EscapeDataString(path)}&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<IReadOnlyList<ScrapeAttemptDto>> GetVideoScrapeAttemptsAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<ScrapeAttemptDto>>(
            HttpMethod.Get,
            $"/api/scrape-attempts?entityType=video&entityId={video.Id}&apiTestNonce={Guid.NewGuid():N}",
            payload: null,
            cancellationToken);

    public Task<StashPreviewResult> PreviewStashMigrationAsync(
        string databasePath,
        CancellationToken cancellationToken = default)
        => SendAsync<StashPreviewResult>(
            HttpMethod.Post,
            "/api/stash-migration/preview",
            new { stashDbPath = databasePath },
            cancellationToken);

    public async Task<bool> GetVideoPreviewAvailabilityAsync(
        VideoDto video,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Get,
            WithCacheNonce($"/api/stream/video/{video.Id}/preview/status"),
            payload: null,
            cancellationToken);
        return response.GetProperty("available").GetBoolean();
    }

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

    public Task<IReadOnlyList<MetadataServerPerformerMatchDto>> SearchPerformerMetadataServiceAsync(
        PerformerDto performer,
        string name,
        MetadataServicePerformerHandle metadataPerformer,
        CancellationToken cancellationToken = default)
        => SendAsync<IReadOnlyList<MetadataServerPerformerMatchDto>>(
            HttpMethod.Get,
            WithCacheNonce($"/api/performers/{performer.Id}/metadata-server/search?term={Uri.EscapeDataString(name)}&endpoint={Uri.EscapeDataString(metadataPerformer.Endpoint.AbsoluteUri)}"),
            payload: null,
            cancellationToken);

    public Task<PerformerDto> ImportPerformerFromMetadataServiceAsync(
        PerformerDto performer,
        MetadataServerPerformerMatchDto match,
        CancellationToken cancellationToken = default)
        => SendAsync<PerformerDto>(
            HttpMethod.Post,
            $"/api/performers/{performer.Id}/metadata-server/import",
            new MetadataServerPerformerImportRequestDto
            {
                Endpoint = match.Endpoint,
                PerformerId = match.Id,
            },
            cancellationToken);
}

public sealed record ApiBinaryContent(byte[] Content, string? MediaType);
