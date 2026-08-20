using System.Text.Json;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<TextDocumentDto> CreateTextAsync(
        string title,
        CancellationToken cancellationToken = default)
        => CreateTextAsync(
            new TextDocumentCreateDto(
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

    public Task<TextDocumentDto> CreateTextAsync(
        TextDocumentCreateDto text,
        CancellationToken cancellationToken = default)
        => SendAsync<TextDocumentDto>(HttpMethod.Post, "/api/texts", text, cancellationToken);

    public Task<TextDocumentDto> CreateTextFromFileAsync(
        string filePath,
        CancellationToken cancellationToken = default)
        => SendAsync<TextDocumentDto>(
            HttpMethod.Post,
            "/api/texts/from-file",
            new FileBackedCreateDto(filePath),
            cancellationToken);

    public Task<TextDocumentDto> GetTextByIdAsync(
        int textId,
        CancellationToken cancellationToken = default)
        => SendAsync<TextDocumentDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/texts/{textId}"),
            payload: null,
            cancellationToken);

    public Task<TextContentDto> GetTextContentAsync(
        int textId,
        CancellationToken cancellationToken = default)
        => SendAsync<TextContentDto>(
            HttpMethod.Get,
            WithCacheNonce($"/api/texts/{textId}/content"),
            payload: null,
            cancellationToken);

    public Task<TextDocumentDto> UpdateTextAsync(
        int textId,
        object update,
        CancellationToken cancellationToken = default)
        => SendAsync<TextDocumentDto>(
            HttpMethod.Put,
            $"/api/texts/{textId}",
            update,
            cancellationToken);

    public Task<PaginatedResponse<TextDocumentDto>> FindTextsAsync(
        FilteredQueryRequest<TextDocumentFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<PaginatedResponse<TextDocumentDto>>(
            HttpMethod.Post,
            "/api/texts/find",
            request,
            cancellationToken);

    public Task<TextAggregate> AggregateTextsAsync(
        FilteredQueryRequest<TextDocumentFilter> request,
        CancellationToken cancellationToken = default)
        => SendAsync<TextAggregate>(
            HttpMethod.Post,
            "/api/texts/aggregate",
            request,
            cancellationToken);

    public async Task<int> BulkUpdateTextsAsync(
        BulkTextDocumentUpdateDto request,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JsonElement>(
            HttpMethod.Post,
            "/api/texts/bulk",
            request,
            cancellationToken);
        return response.GetProperty("updated").GetInt32();
    }

    public async Task DeleteTextAsync(
        int textId,
        bool deleteFile = false,
        bool deleteGenerated = false,
        CancellationToken cancellationToken = default)
    {
        var requestUri = $"/api/texts/{textId}?deleteFile={deleteFile.ToString().ToLowerInvariant()}&deleteGenerated={deleteGenerated.ToString().ToLowerInvariant()}";
        using var response = await _client.DeleteAsync(requestUri, cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.NoContent)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"DELETE {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}). Response: {body}");
    }

    public Task BulkDeleteTextsAsync(
        BatchDeleteDto request,
        CancellationToken cancellationToken = default)
        => SendForNoContentAsync(
            HttpMethod.Delete,
            "/api/texts/bulk",
            request,
            cancellationToken);
}
