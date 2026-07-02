namespace Cove.Core.Entities;

/// <summary>
/// Canonical, Cove-owned embedding kinds. These are a shared contract, not an extension's private detail:
/// any extension that produces one of these embedding types MUST reuse these <c>KindFamily</c> values so the
/// host and every extension share a single, indexed representation. Because they are core-owned, core owns the
/// ANN indexes for them (see the EmbeddingsHnsw* migrations).
/// </summary>
public static class EmbeddingKinds
{
    /// <summary>Pure-visual embedding (e.g. DINOv3) — best for visual-similarity neighbor quality. PRIMARY
    /// visual signal for recommenders/similarity; prefer this over <see cref="VisualSemanticFamily"/> when only
    /// one is present.</summary>
    public const string VisualFeatureFamily = "feature.v1";
    public const string VisualFeatureKind = "visual.feature.v1";

    /// <summary>Joint vision-language embedding (e.g. MetaCLIP2) — complementary "related but not visually
    /// identical" signal. Secondary to <see cref="VisualFeatureFamily"/> for visual similarity.</summary>
    public const string VisualSemanticFamily = "semantic.v1";
    public const string VisualSemanticKind = "visual.semantic.v1";
}
