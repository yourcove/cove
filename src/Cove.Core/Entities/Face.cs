namespace Cove.Core.Entities;

public class Face : BaseEntity
{
    public string? Label { get; set; }
    public int? PerformerId { get; set; }
    public string? CoverBlobId { get; set; }
    public bool Ignored { get; set; }
    public int? MergedIntoFaceId { get; set; }
    public int DetectionCount { get; set; }
    public int AppearanceCount { get; set; }
    public int FrameSampleCount { get; set; }
    public int VideoCount { get; set; }
    public int ImageCount { get; set; }
    public string? PrimarySourceKey { get; set; }
    public string? SearchText { get; set; }

    // Materialized "top suggestion" projection.
    //
    // The faces list lets users sort and filter by suggestion confidence and suggested performer.
    // Those are first-class query dimensions, so the value has to live in the database to be
    // filtered/sorted/paginated in SQL — otherwise the controller has to compute suggestions for the
    // whole candidate set on the request thread (which does not scale past a few thousand unlinked
    // faces). These columns hold the precomputed global top suggestion for an unlinked face; they are
    // populated off the request path by the background materializer and recomputed when inputs change.
    //
    // Semantics:
    //   - TopSuggestionComputedAt == null  => needs (re)compute; the materializer will pick it up.
    //   - TopSuggestionComputedAt != null && TopSuggestionPerformerId == null => computed, no suggestion.
    //   - These reflect the *global* best match and intentionally do not encode per-user reject
    //     decisions (a single shared projection cannot be per-user). Per-user filtering still applies
    //     on the single-face detail/suggestions endpoints, which stay compute-on-read.
    public int? TopSuggestionPerformerId { get; set; }
    public int? TopSuggestionLocalPerformerId { get; set; }
    public string? TopSuggestionPerformerName { get; set; }
    public float? TopSuggestionConfidence { get; set; }
    public string? TopSuggestionCoverImageUrl { get; set; }
    public string? TopSuggestionExternalUrl { get; set; }
    public bool TopSuggestionLocalPerformerHasImage { get; set; }
    public bool TopSuggestionLocalPerformerIsLocalOnly { get; set; }
    public DateTime? TopSuggestionComputedAt { get; set; }

    public Performer? Performer { get; set; }
    public Face? MergedIntoFace { get; set; }
    public ICollection<Face> MergedFaces { get; set; } = [];
    public ICollection<FaceAppearance> Appearances { get; set; } = [];
}
