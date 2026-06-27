using System.Collections.Concurrent;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed record ImageBatchScrapeExecutionSummary(
    int TotalCount,
    int ScrapedCount,
    int AppliedCount,
    int PartialAppliedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<string> Issues);

public class ImageBatchScrapeService(
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    ILogger<ImageBatchScrapeService> logger)
{
    public async Task<ImageBatchScrapeExecutionSummary> RunAsync(BatchImageScrapeStartRequestDto request, IJobProgress? progress, CancellationToken ct)
    {
        var normalizedInputKind = request.InputKind?.Trim().ToLowerInvariant();
        if (normalizedInputKind is not ("url" or "name"))
            throw new InvalidOperationException($"Unsupported batch image scrape input kind '{request.InputKind}'.");

        var imageIds = request.ImageIds.Where(id => id > 0).Distinct().ToList();
        if (imageIds.Count == 0)
            return new ImageBatchScrapeExecutionSummary(0, 0, 0, 0, 0, 0, []);

        var issues = new ConcurrentQueue<string>();
        var processed = 0;
        var scraped = 0;
        var applied = 0;
        var partialApplied = 0;
        var skipped = 0;
        var failed = 0;

        await Parallel.ForEachAsync(
            imageIds,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveParallelism(),
                CancellationToken = ct,
            },
            async (imageId, token) =>
            {
                string label = $"Image {imageId}";

                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
                    var scrapeAttemptService = scope.ServiceProvider.GetRequiredService<ScrapeAttemptService>();

                    var image = await db.Images
                        .AsNoTracking()
                        .Include(item => item.Urls)
                        .Include(item => item.Files)
                        .FirstOrDefaultAsync(item => item.Id == imageId, token);

                    if (image == null)
                    {
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"Image {imageId}: image not found.");
                        return;
                    }

                    label = !string.IsNullOrWhiteSpace(image.Title)
                        ? image.Title
                        : image.Files.Select(file => file.Basename).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? $"Image {image.Id}";
                    var input = normalizedInputKind == "url"
                        ? image.Urls.Select(item => item.Url).FirstOrDefault(url => !string.IsNullOrWhiteSpace(url))
                        : label;

                    if (string.IsNullOrWhiteSpace(input))
                    {
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"{label}: no {(normalizedInputKind == "url" ? "URL" : "title")} available.");
                        return;
                    }

                    var attempt = await scrapeAttemptService.CreateAttemptAsync(
                        new CreateScrapeAttemptDto(
                            request.ScraperId,
                            "image",
                            image.Id,
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

                    var appliedAttempt = await scrapeAttemptService.ApplyImageAttemptWithDefaultPlanAsync(
                        attempt.Id,
                        new ApplyVideoScrapeAttemptDto(
                            ReplaceFields: null,
                            CollectionModes: null,
                            CreateMissingTags: request.CreateMissingTags,
                            CreateMissingPerformers: request.CreateMissingPerformers,
                            CreateMissingStudio: request.CreateMissingStudio,
                            MarkOrganized: request.MarkOrganized),
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
                    logger.LogWarning(ex, "Batch image scrape failed for {ImageLabel}", label);
                }
                finally
                {
                    var completed = Interlocked.Increment(ref processed);
                    progress?.Report(completed / (double)imageIds.Count, $"Processed {completed}/{imageIds.Count}: {label}");
                }
            });

        return new ImageBatchScrapeExecutionSummary(
            imageIds.Count,
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
