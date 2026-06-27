using System.Collections.Concurrent;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed record VideoBatchScrapeExecutionSummary(
    int TotalCount,
    int ScrapedCount,
    int AppliedCount,
    int PartialAppliedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<string> Issues);

public class VideoBatchScrapeService(
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<VideoBatchScrapeService> logger)
{
    public async Task<VideoBatchScrapeExecutionSummary> RunAsync(BatchVideoScrapeStartRequestDto request, IJobProgress? progress, CancellationToken ct)
    {
        var normalizedInputKind = request.InputKind?.Trim().ToLowerInvariant();
        if (normalizedInputKind is not ("url" or "name"))
            throw new InvalidOperationException($"Unsupported batch video scrape input kind '{request.InputKind}'.");

        var videoIds = request.VideoIds.Where(id => id > 0).Distinct().ToList();
        if (videoIds.Count == 0)
            return new VideoBatchScrapeExecutionSummary(0, 0, 0, 0, 0, 0, []);

        var issues = new ConcurrentQueue<string>();
        var processed = 0;
        var scraped = 0;
        var applied = 0;
        var partialApplied = 0;
        var skipped = 0;
        var failed = 0;

        await Parallel.ForEachAsync(
            videoIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveParallelism(),
                CancellationToken = ct,
            },
            async (videoId, token) =>
            {
                string label = $"Video {videoId}";

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    var scrapeAttemptService = scope.ServiceProvider.GetRequiredService<ScrapeAttemptService>();

                    var video = await db.Videos
                        .AsNoTracking()
                        .Include(item => item.Urls)
                        .FirstOrDefaultAsync(item => item.Id == videoId, token);

                    if (video == null)
                    {
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"Video {videoId}: video not found.");
                        return;
                    }

                    label = string.IsNullOrWhiteSpace(video.Title) ? $"Video {video.Id}" : video.Title;
                    var input = normalizedInputKind == "url"
                        ? video.Urls.Select(item => item.Url).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
                        : video.Title;

                    if (string.IsNullOrWhiteSpace(input))
                    {
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"{label}: no {(normalizedInputKind == "url" ? "URL" : "title")} available.");
                        return;
                    }

                    var attempt = await scrapeAttemptService.CreateAttemptAsync(
                        new CreateScrapeAttemptDto(
                            request.ScraperId,
                            "video",
                            video.Id,
                            normalizedInputKind,
                            normalizedInputKind == "url" ? input : null,
                            normalizedInputKind == "name" ? input : null,
                            null),
                        token);

                    if (string.Equals(attempt.Status, ScrapeAttemptStatuses.NoMatch, StringComparison.OrdinalIgnoreCase))
                    {
                        // Expected "title isn't on this site" outcome - report as skipped, not failed.
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"{label}: {attempt.Error ?? "no match found."}");
                        return;
                    }

                    if (string.Equals(attempt.Status, ScrapeAttemptStatuses.Failure, StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref failed);
                        issues.Enqueue($"{label}: {attempt.Error ?? "scrape failed."}");
                        return;
                    }

                    Interlocked.Increment(ref scraped);

                    if (!request.AutoApply)
                        return;

                    var appliedAttempt = await scrapeAttemptService.ApplyVideoAttemptWithDefaultPlanAsync(
                        attempt.Id,
                        new ApplyVideoScrapeAttemptDto(
                            ReplaceFields: null,
                            CollectionModes: null,
                            CreateMissingTags: request.CreateMissingTags,
                            CreateMissingPerformers: request.CreateMissingPerformers,
                            CreateMissingStudio: request.CreateMissingStudio,
                            MarkOrganized: request.MarkOrganized,
                            HydratePerformers: request.HydratePerformers),
                        token);

                    if (appliedAttempt == null)
                    {
                        Interlocked.Increment(ref failed);
                        issues.Enqueue($"{label}: failed to apply the scraped result.");
                        return;
                    }

                    if (string.Equals(appliedAttempt.Status, ScrapeAttemptStatuses.AppliedPartial, StringComparison.OrdinalIgnoreCase))
                        Interlocked.Increment(ref partialApplied);
                    else
                        Interlocked.Increment(ref applied);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    issues.Enqueue($"{label}: {ex.Message}");
                    logger.LogWarning(ex, "Batch video scrape failed for {VideoLabel}", label);
                }
                finally
                {
                    var completed = Interlocked.Increment(ref processed);
                    progress?.Report(completed / (double)videoIds.Count, $"Processed {completed}/{videoIds.Count}: {label}");
                }
            });

        return new VideoBatchScrapeExecutionSummary(
            videoIds.Count,
            scraped,
            applied,
            partialApplied,
            skipped,
            failed,
            issues.ToArray());
    }

    private int ResolveParallelism()
    {
        var configured = config.MaxParallelTasks;
        var desired = configured <= 0 ? Environment.ProcessorCount : configured;
        return Math.Clamp(desired, 1, 8);
    }
}
