using System.Net;
using Cove.Core.DTOs;

namespace Cove.ApiTests.Infrastructure;

public sealed partial class CoveClient
{
    public Task<FileMoveResult> MoveFilesAsync(
        MoveFilesDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<FileMoveResult>(HttpMethod.Post, "/api/files/move", request, cancellationToken);

    public Task<FileDeleteResult> DeleteFilesAsync(
        DeleteFilesDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<FileDeleteResult>(HttpMethod.Post, "/api/files/delete", request, cancellationToken);

    public Task<FileFingerprintUpdateResult> SetFileFingerprintsAsync(
        FileSetFingerprintsDto request,
        CancellationToken cancellationToken = default)
        => SendAsync<FileFingerprintUpdateResult>(HttpMethod.Post, "/api/files/fingerprints", request, cancellationToken);

    public Task RevealFileInManagerAsync(
        int fileId,
        CancellationToken cancellationToken = default)
        => PostForEmptyOkAsync($"/api/files/{fileId}/reveal", cancellationToken);

    public Task RevealFolderInManagerAsync(
        int folderId,
        CancellationToken cancellationToken = default)
        => PostForEmptyOkAsync($"/api/files/folders/{folderId}/reveal", cancellationToken);

    private async Task PostForEmptyOkAsync(
        string requestUri,
        CancellationToken cancellationToken)
    {
        using var response = await _client.PostAsync(requestUri, content: null, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is not HttpStatusCode.OK)
        {
            throw new InvalidOperationException(
                $"POST {requestUri} returned {(int)response.StatusCode} ({response.StatusCode}); expected 200 (OK). Response: {body}");
        }

        if (body.Length != 0)
            throw new InvalidOperationException($"POST {requestUri} returned a non-empty 200 response.");
    }
}

public sealed record FileMoveResult(int Moved, int Total);

public sealed record FileDeleteResult(int Deleted);

public sealed record FileFingerprintUpdateResult(int Updated);
