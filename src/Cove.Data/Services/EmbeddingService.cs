using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;

namespace Cove.Data.Services;

public sealed class EmbeddingService(
    CoveContext db,
    IEnumerable<ITextEncoder> encoders,
    IExtensionServiceExchange? serviceExchange = null) : IEmbeddingService, ITextEncoderRegistry
{
    // Text encoders arrive from two places: host registrations resolved through DI (the `encoders`
    // enumerable) and extension-published encoders surfaced through the cross-extension service
    // exchange. Since the extensions-runtime redesign each extension lives in its own isolated
    // container, so an encoder registered by one extension (e.g. AI.Core's semantic text encoder) is
    // NOT visible through the injected enumerable when this registry is resolved inside a *different*
    // extension's container (e.g. AI.Visual running a semantic search). We therefore resolve live on
    // each call and merge the exchange-published encoders in — mirroring
    // FacesController.ActiveSuggesters(). Without the exchange leg, visual semantic search resolves no
    // encoder for "semantic.v1" and the query is never sent to nsfw_ai_server.
    private readonly IEnumerable<ITextEncoder> _encoders = encoders;
    private readonly IExtensionServiceExchange? _serviceExchange = serviceExchange;

    public ITextEncoder? Resolve(string kindFamily)
    {
        if (string.IsNullOrWhiteSpace(kindFamily))
            return null;

        foreach (var encoder in _encoders)
        {
            if (string.Equals(encoder.KindFamily, kindFamily, StringComparison.OrdinalIgnoreCase))
                return encoder;
        }

        foreach (var encoder in _serviceExchange?.GetAll<ITextEncoder>() ?? [])
        {
            if (string.Equals(encoder.KindFamily, kindFamily, StringComparison.OrdinalIgnoreCase))
                return encoder;
        }

        return null;
    }

    public async Task<IReadOnlyList<EmbeddingSearchResult>> KnnAsync(
        Vector query,
        int k,
        EmbeddingSearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (k <= 0)
            return [];

        options ??= new EmbeddingSearchOptions();
        var dimensions = query.ToArray().Length;

        var embeddings = ApplyFilters(db.Embeddings.AsNoTracking(), options)
            .Where(embedding => embedding.Dim == dimensions);

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true)
        {
            var ranked = await embeddings
                .OrderBy(embedding => embedding.Vector.CosineDistance(query))
                .Take(k)
                .Select(embedding => new
                {
                    Embedding = embedding,
                    Distance = embedding.Vector.CosineDistance(query),
                })
                .ToListAsync(cancellationToken);

            return ranked
                .Select(item => new EmbeddingSearchResult(item.Embedding, (float)item.Distance))
                .ToList();
        }

        var candidates = await embeddings.ToListAsync(cancellationToken);
        return candidates
            .Select(embedding => new EmbeddingSearchResult(embedding, ComputeCosineDistance(embedding.Vector, query)))
            .OrderBy(result => result.Distance)
            .Take(k)
            .ToList();
    }

    private static IQueryable<Embedding> ApplyFilters(IQueryable<Embedding> query, EmbeddingSearchOptions options)
    {
        if (options.HostType.HasValue)
            query = query.Where(embedding => embedding.HostType == options.HostType.Value);

        if (options.HostId.HasValue)
            query = query.Where(embedding => embedding.HostId == options.HostId.Value);

        if (!string.IsNullOrWhiteSpace(options.Kind))
            query = query.Where(embedding => embedding.Kind == options.Kind);

        if (!string.IsNullOrWhiteSpace(options.KindFamily))
            query = query.Where(embedding => embedding.KindFamily == options.KindFamily);

        if (options.Modality.HasValue)
            query = query.Where(embedding => embedding.Modality == options.Modality.Value);

        if (options.IsSemantic.HasValue)
            query = query.Where(embedding => embedding.IsSemantic == options.IsSemantic.Value);

        if (!string.IsNullOrWhiteSpace(options.SourceKey))
            query = query.Where(embedding => embedding.SourceKey == options.SourceKey);

        return query;
    }

    private static float ComputeCosineDistance(Vector left, Vector right)
    {
        var leftValues = left.ToArray();
        var rightValues = right.ToArray();

        if (leftValues.Length != rightValues.Length)
            throw new InvalidOperationException("Embedding dimensions do not match.");

        if (leftValues.Length == 0)
            return 1f;

        var dot = 0f;
        var leftNorm = 0f;
        var rightNorm = 0f;

        for (var index = 0; index < leftValues.Length; index++)
        {
            dot += leftValues[index] * rightValues[index];
            leftNorm += leftValues[index] * leftValues[index];
            rightNorm += rightValues[index] * rightValues[index];
        }

        if (leftNorm <= 0f || rightNorm <= 0f)
            return 1f;

        var similarity = dot / (MathF.Sqrt(leftNorm) * MathF.Sqrt(rightNorm));
        return 1f - similarity;
    }
}