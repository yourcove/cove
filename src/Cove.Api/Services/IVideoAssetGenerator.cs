namespace Cove.Api.Services;

/// <summary>
/// Generates video assets from the exact file selected by a path-scoped operation.
/// </summary>
public interface IVideoAssetGenerator
{
    Task<bool> GenerateThumbnailFromFileAsync(
        int videoId,
        int sourceFileId,
        double? atSeconds,
        CancellationToken ct = default);

    Task<bool> GeneratePreviewFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default);

    Task<bool> GenerateSpriteFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default);

    Task<bool> GenerateSegmentPreviewFromFileAsync(
        int videoId,
        int sourceFileId,
        double startSec,
        double? endSec,
        bool overwrite,
        CancellationToken ct = default);
}
