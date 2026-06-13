using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>
/// Filter criteria for querying embeddings.
/// </summary>
public sealed class EmbeddingFilter
{
    public EmbeddingHostType? HostType { get; init; }
    public int? HostId { get; init; }
    public IReadOnlyList<int>? HostIds { get; init; }
    public string? SourceKey { get; init; }
    public string? Kind { get; init; }
    public string? KindFamily { get; init; }
    public EmbeddingModality? Modality { get; init; }
    public bool? IsSemantic { get; init; }
    public int? SectionIndexGreaterThan { get; init; }
}

/// <summary>
/// Generic repository for vector embedding CRUD. Available to any extension that stores
/// or queries vector embeddings (similarity search, AI artifacts, etc.).
/// All Add/RemoveRange calls are change-tracked; call SaveChangesAsync once at the end.
/// </summary>
public interface IEmbeddingRepository
{
    Task<IReadOnlyList<Embedding>> FindAsync(EmbeddingFilter filter, CancellationToken ct = default);

    /// <summary>Cheap existence check for the given filter — does not materialize vectors. Used to decide
    /// UI affordances (e.g. whether to show a visual-similarity tab) without running a full search.</summary>
    Task<bool> ExistsAsync(EmbeddingFilter filter, CancellationToken ct = default);

    void Add(Embedding embedding);
    void RemoveRange(IEnumerable<Embedding> embeddings);

    /// <summary>Re-points embeddings from <paramref name="oldHostIds"/> to <paramref name="newHostId"/>
    /// where HostType and SourceKey match. Useful for deduplication and merge scenarios.</summary>
    Task UpdateHostIdAsync(EmbeddingHostType hostType, string sourceKey,
        IReadOnlyList<int> oldHostIds, int newHostId, CancellationToken ct = default);

    /// <summary>Re-points the embeddings hosted on <paramref name="oldHostId"/> that came from the given
    /// runs to <paramref name="newHostId"/>, for the given host type and source.</summary>
    Task ReassignHostByRunAsync(EmbeddingHostType hostType, string sourceKey, int oldHostId,
        IReadOnlyCollection<string> runIds, int newHostId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
