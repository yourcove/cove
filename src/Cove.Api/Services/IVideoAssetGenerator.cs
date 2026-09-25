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

    /// <summary>
    /// The stereoscopic preview clip of a VR video (both eyes' flat views side by side), for galleries
    /// that show previews in 3D. False for non-VR videos.
    /// </summary>
    Task<bool> GenerateVrPreviewFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Whether the stereoscopic preview clip exists; true for videos that do not need one.</summary>
    bool HasVrPreview(int videoId) => true;

    /// <summary>
    /// The stereoscopic card image of a VR video (both eyes' flat views side by side), for galleries
    /// that show covers in 3D. False for non-VR videos.
    /// </summary>
    Task<bool> GenerateVrCardFromFileAsync(
        int videoId,
        int sourceFileId,
        bool overwrite,
        CancellationToken ct = default) => Task.FromResult(false);

    /// <summary>Whether the stereoscopic card exists; true for videos that do not need one.</summary>
    bool HasVrCard(int videoId) => true;

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
