namespace Cove.Core.Entities;

public static class FaceSuggestionDecisionValues
{
    public const string Accept = "accept";
    public const string Reject = "reject";
    // Combine two or more competing reference matches into a single performer (the primary), folding the
    // others in as aliases/links/remote ids. Handled by the reference-suggestion provider.
    public const string Merge = "merge";
}

public class FaceSuggestionDecision : BaseEntity
{
    public int FaceId { get; set; }
    public int PerformerId { get; set; }
    public int UserId { get; set; }
    public string Decision { get; set; } = FaceSuggestionDecisionValues.Reject;

    public Face? Face { get; set; }
    public Performer? Performer { get; set; }
}