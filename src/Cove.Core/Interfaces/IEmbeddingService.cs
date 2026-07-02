using Cove.Core.Entities;
using Pgvector;

namespace Cove.Core.Interfaces;

public sealed class EmbeddingSearchOptions
{
    public EmbeddingHostType? HostType { get; init; }
    public int? HostId { get; init; }
    public string? Kind { get; init; }
    public string? KindFamily { get; init; }
    public EmbeddingModality? Modality { get; init; }
    public bool? IsSemantic { get; init; }
    public string? SourceKey { get; init; }

    /// <summary>Restrict the search to a single section index (e.g. 0 = asset-level only). Greatly reduces
    /// the rows scanned when only whole-item similarity is wanted and section-level rows exist.</summary>
    public int? SectionIndex { get; init; }
}

public sealed record EmbeddingSearchResult(Embedding Embedding, float Distance);

public interface IEmbeddingService
{
    Task<IReadOnlyList<EmbeddingSearchResult>> KnnAsync(
        Vector query,
        int k,
        EmbeddingSearchOptions? options = null,
        CancellationToken cancellationToken = default);
}

public interface ITextEncoder
{
    string KindFamily { get; }

    Task<Vector> EncodeAsync(string text, CancellationToken cancellationToken = default);
}

public interface ITextEncoderRegistry
{
    ITextEncoder? Resolve(string kindFamily);
}