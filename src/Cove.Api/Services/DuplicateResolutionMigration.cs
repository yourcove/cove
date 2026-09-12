namespace Cove.Api.Services;

internal static class DuplicateResolutionMigration
{
    /// <summary>The migration that introduces per-group duplicate review state and ignored pairs.</summary>
    public const string Id = "20260911141852_AddDuplicateReviewWorkflow";
}
