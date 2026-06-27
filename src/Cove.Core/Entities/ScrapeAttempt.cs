namespace Cove.Core.Entities;

/// <summary>
/// Well-known values for <see cref="ScrapeAttempt.Status"/>.
/// </summary>
public static class ScrapeAttemptStatuses
{
    public const string Success = "Success";
    public const string Failure = "Failure";

    /// <summary>
    /// The scrape completed normally but produced no results (e.g. the title isn't on this site,
    /// or the site returned 404 for an unknown title). This is an expected, non-error outcome and
    /// should be surfaced as "no match" / skipped rather than a hard failure.
    /// </summary>
    public const string NoMatch = "NoMatch";

    public const string Applied = "Applied";
    public const string AppliedPartial = "AppliedPartial";
}

public class ScrapeAttempt
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ScraperId { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public int? EntityId { get; set; }
    public string InputKind { get; set; } = string.Empty;
    public string InputJson { get; set; } = "{}";
    public string? ResultJson { get; set; }
    public string? CandidateResultsJson { get; set; }
    public string? EntitySnapshotJson { get; set; }
    public string Status { get; set; } = "Success";
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AppliedAt { get; set; }
    public string? AppliedByUser { get; set; }
}