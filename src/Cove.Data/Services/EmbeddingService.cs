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

        if (db.Database.ProviderName?.Contains("Npgsql", StringComparison.Ordinal) == true
            && db.CanUseUnfilteredEmbeddingAnn(options.HostType))
        {
            try
            {
                return await NpgsqlKnnAsync(query, k, options, dimensions, cancellationToken);
            }
            catch
            {
                // Safety net: any issue with the cast/index path → the proven pgvector EF path (correct,
                // just without the ANN index).
                var ranked = await embeddings
                    .OrderBy(embedding => embedding.Vector.CosineDistance(query))
                    .Take(k)
                    .Select(embedding => new { Embedding = embedding, Distance = embedding.Vector.CosineDistance(query) })
                    .ToListAsync(cancellationToken);

                return ranked
                    .Select(item => new EmbeddingSearchResult(item.Embedding, SanitizeDistance((float)item.Distance)))
                    .ToList();
            }
        }

        var candidates = await embeddings.ToListAsync(cancellationToken);
        return candidates
            .Select(embedding => new EmbeddingSearchResult(embedding, SanitizeDistance(ComputeCosineDistance(embedding.Vector, query))))
            .OrderBy(result => result.Distance)
            .Take(k)
            .ToList();
    }

    // KNN via raw SQL with an explicit vector(N) cast on both column and parameter, so the planner can
    // use a matching partial HNSW index (the Vector column is untyped, so EF's "Vector" <=> q can't).
    // ef_search is set per query (transaction-scoped) to cover the requested k plus the host-type/section
    // post-filter. Returns identical results to the EF path; just much faster when an index matches.
    private async Task<IReadOnlyList<EmbeddingSearchResult>> NpgsqlKnnAsync(
        Vector query, int k, EmbeddingSearchOptions options, int dimensions, CancellationToken cancellationToken)
    {
        var conditions = new List<string> { $"\"Dim\" = {dimensions}" };
        var args = new List<object> { query }; // {0} is the query vector
        void Add(string column, object value)
        {
            conditions.Add($"\"{column}\" = {{{args.Count}}}");
            args.Add(value);
        }

        if (options.HostType.HasValue) Add("HostType", (int)options.HostType.Value);
        if (options.HostId.HasValue) Add("HostId", options.HostId.Value);
        if (!string.IsNullOrWhiteSpace(options.Kind)) Add("Kind", options.Kind!);
        if (options.IsSemantic.HasValue) Add("IsSemantic", options.IsSemantic.Value);
        if (!string.IsNullOrWhiteSpace(options.SourceKey)) Add("SourceKey", options.SourceKey!);

        // Modality, KindFamily and SectionIndex are the predicate columns of the partial HNSW ANN
        // indexes (see the 20260707 migration). They MUST be emitted as SQL literals, not parameters:
        // Postgres only uses a partial index when it can prove, at plan time, that the query predicate
        // implies the index predicate — which it cannot do against a parameter (@p). Parameterizing
        // these silently defeated the index and made this KNN fall back to a full sequential scan +
        // exact sort, which times out on a large embeddings table. These are small, controlled internal
        // values (a modality int, a fixed KindFamily like 'feature.v1', a section index), so inlining is
        // safe; KindFamily is still quote-escaped defensively.
        if (!string.IsNullOrWhiteSpace(options.KindFamily))
            conditions.Add($"\"KindFamily\" = '{options.KindFamily!.Replace("'", "''")}'");
        if (options.Modality.HasValue)
            conditions.Add($"\"Modality\" = {(int)options.Modality.Value}");
        if (options.SectionIndex.HasValue)
            conditions.Add($"\"SectionIndex\" = {options.SectionIndex.Value}");

        var sql = $"SELECT * FROM embeddings WHERE {string.Join(" AND ", conditions)} " +
                  $"ORDER BY (\"Vector\")::vector({dimensions}) <=> {{0}}::vector({dimensions}) LIMIT {k}";

        // ef_search must be set transaction-locally; the configured retrying execution strategy forbids
        // user-initiated transactions, so wrap the whole unit in the strategy (its retriable boundary).
        var efSearch = Math.Clamp(k * 3, 100, 1000);
        var strategy = db.Database.CreateExecutionStrategy();
        var rows = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT set_config('hnsw.ef_search', {efSearch.ToString(System.Globalization.CultureInfo.InvariantCulture)}, true)",
                cancellationToken);
            var list = await db.Embeddings.FromSqlRaw(sql, args.ToArray()).AsNoTracking().ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return list;
        });

        return rows
            .Select(embedding => new EmbeddingSearchResult(embedding, SanitizeDistance(ComputeCosineDistance(embedding.Vector, query))))
            .ToList();
    }

    // A zero-norm / degenerate embedding vector yields an undefined (NaN) cosine distance. Map any
    // non-finite distance to the maximum cosine distance so it ranks last and never propagates into
    // downstream similarity math or JSON serialization (System.Text.Json rejects NaN/Infinity).
    private const float MaxCosineDistance = 2f;

    private static float SanitizeDistance(float distance) => float.IsFinite(distance) ? distance : MaxCosineDistance;

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

        if (options.SectionIndex.HasValue)
            query = query.Where(embedding => embedding.SectionIndex == options.SectionIndex.Value);

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
