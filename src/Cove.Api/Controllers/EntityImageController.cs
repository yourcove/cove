using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Cove.Api.Services;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api")]
public class EntityImageController(CoveContext db, IBlobService blobService, IThumbnailService thumbnailService) : ControllerBase
{
    // ── Scenes ──────────────────────────────────────────────────

    [HttpPost("scenes/{id:int}/image")]
    public async Task<IActionResult> UploadSceneImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Scenes.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.ImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.ImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.ImageBlobId });
    }

    [HttpGet("scenes/{id:int}/image")]
    public async Task<IActionResult> GetSceneImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Scenes.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.ImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("scenes/{id:int}/image")]
    public async Task<IActionResult> DeleteSceneImage(int id, CancellationToken ct)
    {
        var entity = await db.Scenes.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);
        entity.ImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Performers ──────────────────────────────────────────────

    [HttpPost("performers/{id:int}/image")]
    public async Task<IActionResult> UploadPerformerImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Performers.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.ImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.ImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.ImageBlobId });
    }

    [HttpGet("performers/{id:int}/image")]
    public async Task<IActionResult> GetPerformerImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Performers.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.ImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("performers/{id:int}/image")]
    public async Task<IActionResult> DeletePerformerImage(int id, CancellationToken ct)
    {
        var entity = await db.Performers.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);
        entity.ImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Studios ─────────────────────────────────────────────────

    [HttpPost("studios/{id:int}/image")]
    public async Task<IActionResult> UploadStudioImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Studios.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.ImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.ImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.ImageBlobId });
    }

    [HttpGet("studios/{id:int}/image")]
    public async Task<IActionResult> GetStudioImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Studios.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.ImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("studios/{id:int}/image")]
    public async Task<IActionResult> DeleteStudioImage(int id, CancellationToken ct)
    {
        var entity = await db.Studios.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);
        entity.ImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Tags ────────────────────────────────────────────────────

    [HttpPost("tags/{id:int}/image")]
    public async Task<IActionResult> UploadTagImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Tags.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.ImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.ImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.ImageBlobId });
    }

    [HttpGet("tags/{id:int}/image")]
    public async Task<IActionResult> GetTagImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Tags.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.ImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("tags/{id:int}/image")]
    public async Task<IActionResult> DeleteTagImage(int id, CancellationToken ct)
    {
        var entity = await db.Tags.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);
        entity.ImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Groups (front) ──────────────────────────────────────────

    [HttpPost("groups/{id:int}/image/front")]
    public async Task<IActionResult> UploadGroupFrontImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Groups.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.FrontImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.FrontImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.FrontImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.FrontImageBlobId });
    }

    [HttpGet("groups/{id:int}/image/front")]
    public async Task<IActionResult> GetGroupFrontImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Groups.FindAsync([id], ct);
        if (entity?.FrontImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.FrontImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("groups/{id:int}/image/front")]
    public async Task<IActionResult> DeleteGroupFrontImage(int id, CancellationToken ct)
    {
        var entity = await db.Groups.FindAsync([id], ct);
        if (entity?.FrontImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.FrontImageBlobId, ct);
        entity.FrontImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Groups (back) ───────────────────────────────────────────

    [HttpPost("groups/{id:int}/image/back")]
    public async Task<IActionResult> UploadGroupBackImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Groups.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.BackImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.BackImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.BackImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.BackImageBlobId });
    }

    [HttpGet("groups/{id:int}/image/back")]
    public async Task<IActionResult> GetGroupBackImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Groups.FindAsync([id], ct);
        if (entity?.BackImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.BackImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("groups/{id:int}/image/back")]
    public async Task<IActionResult> DeleteGroupBackImage(int id, CancellationToken ct)
    {
        var entity = await db.Groups.FindAsync([id], ct);
        if (entity?.BackImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.BackImageBlobId, ct);
        entity.BackImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Galleries ───────────────────────────────────────────────

    [HttpPost("galleries/{id:int}/image")]
    public async Task<IActionResult> UploadGalleryImage(int id, IFormFile file, CancellationToken ct)
    {
        if (!IsImage(file)) return BadRequest("File must be an image.");

        var entity = await db.Galleries.FindAsync([id], ct);
        if (entity == null) return NotFound();

        if (entity.ImageBlobId != null)
            await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);

        await using var stream = file.OpenReadStream();
        entity.ImageBlobId = await blobService.StoreBlobAsync(stream, file.ContentType, ct);
        await db.SaveChangesAsync(ct);

        return Ok(new { blobId = entity.ImageBlobId });
    }

    [HttpGet("galleries/{id:int}/image")]
    public async Task<IActionResult> GetGalleryImage(int id, [FromQuery] int? max, [FromQuery] string? v, CancellationToken ct)
    {
        var entity = await db.Galleries.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        return await ServeBlobAsync(entity.ImageBlobId, max, !string.IsNullOrWhiteSpace(v), ct);
    }

    [HttpDelete("galleries/{id:int}/image")]
    public async Task<IActionResult> DeleteGalleryImage(int id, CancellationToken ct)
    {
        var entity = await db.Galleries.FindAsync([id], ct);
        if (entity?.ImageBlobId == null) return NotFound();

        await blobService.DeleteBlobAsync(entity.ImageBlobId, ct);
        entity.ImageBlobId = null;
        await db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Gallery Cover (Set from gallery images) ─────────────────

    [HttpPut("galleries/{id:int}/cover")]
    public async Task<IActionResult> SetGalleryCover(int id, [FromBody] GallerySetCoverDto dto, CancellationToken ct)
    {
        var gallery = await db.Galleries.FindAsync([id], ct);
        if (gallery == null) return NotFound();

        // Verify image belongs to the gallery
        var belongs = await db.Set<ImageGallery>()
            .AnyAsync(ig => ig.GalleryId == id && ig.ImageId == dto.ImageId, ct);
        if (!belongs) return BadRequest("Image does not belong to this gallery");

        gallery.CoverImageId = dto.ImageId;
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpDelete("galleries/{id:int}/cover")]
    public async Task<IActionResult> ResetGalleryCover(int id, CancellationToken ct)
    {
        var gallery = await db.Galleries.FindAsync([id], ct);
        if (gallery == null) return NotFound();

        gallery.CoverImageId = null;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static bool IsImage(IFormFile file) =>
        file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    private async Task<IActionResult> ServeBlobAsync(string blobId, int? maxDimension, bool immutable, CancellationToken ct)
    {
        (Stream stream, string contentType, bool supportsRangeRequests)? result;

        if (maxDimension.HasValue && maxDimension.Value > 0)
        {
            result = await thumbnailService.GetBlobImageThumbnailStreamAsync(blobId, maxDimension.Value, ct);
        }
        else
        {
            var blob = await blobService.GetBlobAsync(blobId, ct);
            result = blob == null ? null : (blob.Value.Stream, blob.Value.ContentType, blob.Value.Stream.CanSeek);
        }

        if (result == null) return NotFound();

        var cacheControl = immutable
            ? "public, max-age=31536000, immutable"
            : "public, max-age=3600";

        Response.Headers.CacheControl = cacheControl;
        return File(result.Value.stream, result.Value.contentType, enableRangeProcessing: result.Value.supportsRangeRequests);
    }
}
