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
}

public sealed record FileMoveResult(int Moved, int Total);

public sealed record FileDeleteResult(int Deleted);

public sealed record FileFingerprintUpdateResult(int Updated);
