using Cove.Core.Entities;

namespace Cove.Core.Interfaces;

/// <summary>Filter criteria for querying faces.</summary>
public sealed class FaceFilter
{
    /// <summary>Match faces by PrimarySourceKey (exact, case-insensitive).</summary>
    public IReadOnlyList<string>? PrimarySourceKeys { get; init; }
    /// <summary>Match faces by ID.</summary>
    public IReadOnlyList<int>? Ids { get; init; }
    public bool? HasPerformer { get; init; }
    public bool? Ignored { get; init; }
    /// <summary>When false, only returns faces that have not been merged (MergedIntoFaceId == null).</summary>
    public bool? IsMerged { get; init; }
    /// <summary>When true, includes the Performer navigation (with RemoteIds) in results.</summary>
    public bool IncludePerformer { get; init; } = false;
}

/// <summary>Filter criteria for querying face appearances.</summary>
public sealed class FaceAppearanceFilter
{
    public FaceAppearanceHostType? HostType { get; init; }
    public int? HostId { get; init; }
    public string? SourceKey { get; init; }
    /// <summary>When set, only appearances whose FaceId is in this list are returned.</summary>
    public IReadOnlyList<int>? FaceIds { get; init; }
}

/// <summary>
/// Generic repository for face identity and face appearance CRUD.
/// Available to any extension that reads or writes face cluster data, appearances, and identity links.
/// All Add/Remove calls are change-tracked; call SaveChangesAsync once at the end.
/// </summary>
public interface IFaceRepository
{
    // ── Faces ────────────────────────────────────────────────────────────────
    Task<IReadOnlyList<Face>> FindFacesAsync(FaceFilter filter, bool tracking = false, CancellationToken ct = default);
    Task<Face?> GetFaceAsync(int faceId, bool tracking = true, CancellationToken ct = default);
    Task<bool> FaceExistsAsync(int faceId, CancellationToken ct = default);
    void AddFace(Face face);

    // ── Face Appearances ─────────────────────────────────────────────────────
    Task<IReadOnlyList<FaceAppearance>> FindAppearancesAsync(FaceAppearanceFilter filter, CancellationToken ct = default);
    void AddAppearance(FaceAppearance appearance);
    void RemoveAppearances(IEnumerable<FaceAppearance> appearances);

    /// <summary>Re-points appearances from <paramref name="oldFaceIds"/> to <paramref name="newFaceId"/>.</summary>
    Task UpdateAppearanceFaceIdAsync(string sourceKey, IReadOnlyList<int> oldFaceIds, int newFaceId, CancellationToken ct = default);

    /// <summary>Re-points the appearances of <paramref name="oldFaceId"/> that came from the given runs
    /// (host-scoped, since one run is one processed host) to <paramref name="newFaceId"/>. Returns the
    /// number of appearances moved.</summary>
    Task<int> ReassignAppearancesByRunAsync(string sourceKey, int oldFaceId, IReadOnlyCollection<string> runIds, int newFaceId, CancellationToken ct = default);

    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
