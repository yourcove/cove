using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.EmbeddingsRead)]
public class EmbeddingsController(CoveContext db, IEmbeddingService embeddingService, ITextEncoderRegistry textEncoderRegistry, AiDataPurgeService? aiDataPurgeService = null) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PaginatedResponse<EmbeddingDto>>> List(
        [FromQuery] EmbeddingHostType? hostType,
        [FromQuery] int? hostId,
        [FromQuery] string? kind,
        [FromQuery] string? kindFamily,
        [FromQuery] string? sourceKey,
        [FromQuery] string? sourceRunId,
        [FromQuery] int page = 1,
        [FromQuery] int perPage = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        perPage = Math.Clamp(perPage, 1, 250);

        var query = db.Embeddings.AsNoTracking().AsQueryable();

        if (hostType.HasValue)
            query = query.Where(embedding => embedding.HostType == hostType.Value);

        if (hostId.HasValue)
            query = query.Where(embedding => embedding.HostId == hostId.Value);

        if (!string.IsNullOrWhiteSpace(kind))
            query = query.Where(embedding => embedding.Kind == kind);

        if (!string.IsNullOrWhiteSpace(kindFamily))
            query = query.Where(embedding => embedding.KindFamily == kindFamily);

        if (!string.IsNullOrWhiteSpace(sourceKey))
            query = query.Where(embedding => embedding.SourceKey == sourceKey);

        if (!string.IsNullOrWhiteSpace(sourceRunId))
            query = query.Where(embedding => embedding.SourceRunId == sourceRunId);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(embedding => embedding.CreatedAt)
            .Skip((page - 1) * perPage)
            .Take(perPage)
            .ToListAsync(cancellationToken);

        return Ok(new PaginatedResponse<EmbeddingDto>(items.Select(MapToDto).ToList(), totalCount, page, perPage));
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<EmbeddingDto>> GetById(int id, CancellationToken cancellationToken)
    {
        var embedding = await db.Embeddings.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, cancellationToken);
        return embedding is null ? NotFound() : Ok(MapToDto(embedding));
    }

    [HttpDelete]
    [RequiresPermission(Permissions.SystemSettingsWrite)]
    public async Task<ActionResult<AiDataPurgeResultDto>> Delete([FromBody] AiDataSelectorDto selector, CancellationToken cancellationToken)
    {
        if (aiDataPurgeService is null)
        {
            return Problem("AI data purge service is unavailable.", statusCode: StatusCodes.Status500InternalServerError);
        }

        if (!AiDataPurgeService.TryValidateDestructiveSelector(selector, out var error, scopeKinds: ["embedding"]))
        {
            return BadRequest(new { error });
        }

        var removedCount = await aiDataPurgeService.DeleteEmbeddingsAsync(selector, dryRun: false, cancellationToken);
        return Ok(new AiDataPurgeResultDto(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["embedding"] = removedCount,
        }));
    }

    [HttpPost("search")]
    public async Task<ActionResult<IReadOnlyList<EmbeddingSearchResultDto>>> Search(
        [FromBody] EmbeddingSearchRequestDto dto,
        CancellationToken cancellationToken)
    {
        if ((dto.QueryVector is null || dto.QueryVector.Length == 0) && string.IsNullOrWhiteSpace(dto.QueryText))
            return ValidationProblem("Provide either queryVector or queryText.");

        if (!string.IsNullOrWhiteSpace(dto.QueryText) && string.IsNullOrWhiteSpace(dto.KindFamily))
            return ValidationProblem("kindFamily is required when queryText is provided.");

        Vector queryVector;
        if (dto.QueryVector is { Length: > 0 })
        {
            queryVector = new Vector(dto.QueryVector);
        }
        else
        {
            var encoder = textEncoderRegistry.Resolve(dto.KindFamily!);
            if (encoder is null)
                return Conflict($"No text encoder is registered for kind family '{dto.KindFamily}'.");

            queryVector = await encoder.EncodeAsync(dto.QueryText!, cancellationToken);
        }

        var results = await embeddingService.KnnAsync(
            queryVector,
            Math.Clamp(dto.K, 1, 100),
            new EmbeddingSearchOptions
            {
                HostType = dto.HostType,
                HostId = dto.HostId,
                Kind = Clean(dto.Kind),
                KindFamily = Clean(dto.KindFamily),
                Modality = dto.Modality,
                IsSemantic = dto.IsSemantic,
                SourceKey = Clean(dto.SourceKey),
            },
            cancellationToken);

        return Ok(results.Select(result => new EmbeddingSearchResultDto(
            result.Embedding.Id,
            result.Embedding.HostType,
            result.Embedding.HostId,
            result.Embedding.Kind,
            result.Embedding.KindFamily,
            result.Embedding.Modality,
            result.Embedding.IsSemantic,
            result.Embedding.SectionIndex,
            result.Embedding.StartSec,
            result.Embedding.EndSec,
            result.Embedding.SourceKey,
            result.Embedding.SourceRunId,
            result.Distance)).ToList());
    }

    private static EmbeddingDto MapToDto(Embedding embedding) => new(
        embedding.Id,
        embedding.HostType,
        embedding.HostId,
        embedding.Kind,
        embedding.KindFamily,
        embedding.Modality,
        embedding.IsSemantic,
        embedding.Dim,
        embedding.Vector.ToArray(),
        embedding.SectionIndex,
        embedding.StartSec,
        embedding.EndSec,
        embedding.SourceKey,
        embedding.SourceRunId,
        embedding.Meta?.RootElement.Clone(),
        embedding.CreatedAt,
        embedding.UpdatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
