using System.Text;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Runs Cove's identify pass as a host job: match each video against the configured metadata servers,
/// then against the URL-capable scrapers, and apply a match only where it clears the configured
/// auto-apply thresholds.
/// </summary>
public sealed class IdentifyJobService(
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration config,
    IEventBus eventBus,
    ILogger<IdentifyJobService> logger) : IIdentifyService
{
    public string Start(IdentifyOptionsDto? opts)
    {
        var jobId = jobService.Enqueue("identify", "Identifying videos", async (progress, ct) =>
        {
            using var scope = scopeFactory.CreateScope();
            var dbCtx = scope.ServiceProvider.GetRequiredService<CoveContext>();
            var metadataServerSvc = scope.ServiceProvider.GetService<MetadataServerService>();
            var scraperSvc = scope.ServiceProvider.GetService<ScraperService>();
            var scrapeAttemptSvc = scope.ServiceProvider.GetService<ScrapeAttemptService>();

            var videoIds = opts?.VideoIds?.Count > 0
                ? await dbCtx.Videos
                    .Where(video => opts.VideoIds.Contains(video.Id))
                    .Select(video => video.Id)
                    .ToListAsync(ct)
                : await dbCtx.Videos
                    .Select(video => video.Id)
                    .ToListAsync(ct);

            var identifyDefaults = config.Scraping.IdentifyDefaults;
            var sourceEndpoints = ResolveIdentifyMetadataServerEndpoints(opts?.Sources, config.Scraping.MetadataServers);
            var sourceOrder = sourceEndpoints?
                .Select((endpoint, index) => new { endpoint, index })
                .ToDictionary(item => item.endpoint, item => item.index, StringComparer.OrdinalIgnoreCase);

            // Which URL-capable video scrapers are enabled as identify sources (null = all eligible;
            // empty = the caller selected only metadata servers, so the scraper stage is skipped).
            var enabledScraperIds = ResolveIdentifyScraperIds(opts?.Sources, scraperSvc);

            // Build import config from identify options
            var importConfig = new MetadataServerVideoImportRequestDto
            {
                SetCoverImage = opts?.SetCoverImage ?? true,
                SetTags = opts?.SetTags ?? true,
                SetPerformers = opts?.SetPerformers ?? true,
                SetStudio = opts?.SetStudio ?? true,
                OnlyExistingTags = !(opts?.CreateTags ?? identifyDefaults.CreateTags),
                OnlyExistingPerformers = !(opts?.CreatePerformers ?? identifyDefaults.CreatePerformers),
                OnlyExistingStudio = !(opts?.CreateStudios ?? identifyDefaults.CreateStudios),
                MarkOrganized = opts?.MarkOrganized ?? false,
                FieldStrategies = opts?.FieldStrategies,
                PerformerGenders = opts?.PerformerGenders,
                SkipSingleNamePerformers = opts?.SkipSingleNamePerformers ?? true,
            };

            var total = videoIds.Count;
            var identifiedCount = 0;
            var failedCount = 0;
            var warningCount = 0;
            // Distinct error messages per source, so a run whose queries failed does not read like one
            // that simply found no matches.
            var sourceErrors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var sourceNames = config.Scraping.MetadataServers
                .GroupBy(server => server.Endpoint, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First().Name, StringComparer.OrdinalIgnoreCase);
            progress.DeclareUnitCount(total);
            for (var i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress.Report((double)(i + 1) / total, $"Identifying video {i + 1}/{total}");
                using var unit = progress.StartUnit(videoIds[i].ToString(System.Globalization.CultureInfo.InvariantCulture));
                var videoFailures = new List<string>();
                void RecordFailure(string source, string message)
                {
                    videoFailures.Add($"{source}: {message}");
                    if (!sourceErrors.TryGetValue(source, out var messages))
                        sourceErrors[source] = messages = [];
                    if (!messages.Contains(message, StringComparer.Ordinal))
                        messages.Add(message);
                }

                dbCtx.ChangeTracker.Clear();
                metadataServerSvc?.ResetTrackedIdentityState();
                var video = await dbCtx.Videos
                    .Include(entity => entity.Files).ThenInclude(file => file.Fingerprints)
                    .Include(entity => entity.VideoTags)
                    .Include(entity => entity.VideoPerformers)
                    .Include(entity => entity.RemoteIds)
                    .Include(entity => entity.Urls)
                    .AsSplitQuery()
                    .SingleAsync(entity => entity.Id == videoIds[i], ct);
                var fingerprints = video.Files.SelectMany(f => f.Fingerprints).ToList();
                var identified = false;
                // Set when a metadata server returned candidates but none was applied: the video is left
                // alone rather than handed to the scrapers.
                var candidatesDeclined = false;
                string? mergeSource = null;

                // Attempt MetadataServer identification (needs fingerprints to auto-match a candidate).
                if (fingerprints.Count > 0 && metadataServerSvc != null && (sourceEndpoints == null || sourceEndpoints.Count > 0))
                {
                    try
                    {
                        IReadOnlyList<MetadataServerVideoMatchDto> matches;
                        if (sourceEndpoints == null)
                        {
                            matches = await metadataServerSvc.SearchVideosAsync(video, null, null, null, ct);
                        }
                        else
                        {
                            var orderedMatches = new List<MetadataServerVideoMatchDto>();
                            foreach (var endpoint in sourceEndpoints)
                            {
                                // One failing source must not keep the remaining ones from being tried.
                                try
                                {
                                    orderedMatches.AddRange(await metadataServerSvc.SearchVideosAsync(video, null, endpoint, null, ct));
                                }
                                catch (Exception ex) when (!ct.IsCancellationRequested)
                                {
                                    logger.LogWarning(ex, "MetadataServer identify query to {Endpoint} failed for video {VideoId}", endpoint, video.Id);
                                    RecordFailure(sourceNames.GetValueOrDefault(endpoint) ?? endpoint, ex.Message);
                                }
                            }
                            matches = orderedMatches;
                        }

                        logger.LogTrace(
                            "Identify video {VideoId}: metadata servers returned {MatchCount} candidate match(es)",
                            video.Id, matches.Count);

                        if (matches.Count > 0)
                        {
                            // Evaluate every candidate once, capturing its scores and whether it cleared
                            // the auto-apply thresholds (and which guard rejected it). This is purely for
                            // diagnostics; the ranking/selection below is unchanged.
                            var evaluatedCandidates = matches
                                .Select(match =>
                                {
                                    var durationDifferenceSeconds = GetDurationDifferenceSeconds(video, match);
                                    var phashDistance = GetBestPhashDistance(video, match);
                                    var passed = MeetsIdentifyAutoApplyThresholds(match.MatchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out var failureReason);
                                    return new
                                    {
                                        Match = match,
                                        DurationDifferenceSeconds = durationDifferenceSeconds,
                                        PhashDistance = phashDistance,
                                        Passed = passed,
                                        FailureReason = failureReason,
                                    };
                                })
                                .ToList();

                            foreach (var candidate in evaluatedCandidates)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} => {Result}; failureReason={FailureReason}",
                                    video.Id,
                                    candidate.Match.Id,
                                    candidate.Match.Title,
                                    candidate.Match.Endpoint,
                                    candidate.Match.MetadataServerName,
                                    candidate.Match.MatchCount,
                                    candidate.DurationDifferenceSeconds,
                                    candidate.PhashDistance,
                                    candidate.Passed ? "PASSED" : "FAILED",
                                    candidate.FailureReason);
                            }

                            var rankedMatches = evaluatedCandidates
                                .Where(candidate => candidate.Passed)
                                .OrderBy(candidate => sourceOrder != null && sourceOrder.TryGetValue(candidate.Match.Endpoint, out var index) ? index : int.MaxValue)
                                .ThenByDescending(candidate => candidate.Match.MatchCount)
                                .ThenBy(candidate => candidate.PhashDistance ?? int.MaxValue)
                                .ThenBy(candidate => candidate.DurationDifferenceSeconds ?? double.MaxValue)
                                .ToList();

                            if (rankedMatches.Count == 0)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: {MatchCount} candidate(s) returned, 0 passed thresholds",
                                    video.Id, matches.Count);
                                candidatesDeclined = true;
                            }
                            // Skip a video with several passing candidates unless the caller opts out:
                            // applying the top-ranked one is a guess that can attach the wrong scene.
                            else if ((opts?.SkipMultipleMatches ?? true) && rankedMatches.Count > 1)
                            {
                                logger.LogTrace(
                                    "Identify video {VideoId}: skipping because {PassedCount} candidates passed thresholds and SkipMultipleMatches is enabled",
                                    video.Id, rankedMatches.Count);
                                candidatesDeclined = true;
                            }
                            else
                            {
                                var bestCandidate = rankedMatches[0];
                                var best = bestCandidate.Match;
                                logger.LogTrace(
                                    "Identify video {VideoId}: selected candidate {CandidateId} '{CandidateTitle}' from {Endpoint} ({ServerName}) - matchCount={MatchCount}, durationDiff={DurationDiff}, phashDistance={PhashDistance} (best of {PassedCount} passing of {TotalCount} returned)",
                                    video.Id,
                                    best.Id,
                                    best.Title,
                                    best.Endpoint,
                                    best.MetadataServerName,
                                    best.MatchCount,
                                    bestCandidate.DurationDifferenceSeconds,
                                    bestCandidate.PhashDistance,
                                    rankedMatches.Count,
                                    matches.Count);

                                mergeSource = best.MetadataServerName;
                                var imported = await metadataServerSvc.MergeVideoWithWarningsAsync(video, best.Endpoint, best.Id, importConfig, ct);
                                if (imported.Imported)
                                {
                                    await dbCtx.SaveChangesAsync(ct);
                                    if (imported.Warnings.Count > 0)
                                    {
                                        warningCount++;
                                        logger.LogWarning(
                                            "MetadataServer identify partially updated video {VideoId}: {Warnings}",
                                            video.Id,
                                            string.Join(" ", imported.Warnings));
                                    }
                                    eventBus.Publish(new EntityEvent(EventType.VideoUpdated, "Video", video.Id));
                                    identified = true;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "MetadataServer identify failed for video {VideoId}", video.Id);
                        RecordFailure(mergeSource ?? "Metadata servers", ex.Message);
                        dbCtx.ChangeTracker.Clear();
                        metadataServerSvc.ResetTrackedIdentityState();
                        video = await dbCtx.Videos
                            .Include(entity => entity.Files).ThenInclude(file => file.Fingerprints)
                            .Include(entity => entity.VideoTags)
                            .Include(entity => entity.VideoPerformers)
                            .Include(entity => entity.RemoteIds)
                            .Include(entity => entity.Urls)
                            .AsSplitQuery()
                            .SingleAsync(entity => entity.Id == video.Id, ct);
                    }
                }

                // Attempt scraper identification from the video's existing URL(s). The URL is the
                // identity, so unlike metadata servers this needs no fingerprint match and also runs
                // for fingerprint-less videos. Skipped if a metadata server already identified this one.
                if (!identified && !candidatesDeclined && scraperSvc != null && scrapeAttemptSvc != null
                    && (enabledScraperIds == null || enabledScraperIds.Count > 0))
                {
                    try
                    {
                        identified = await TryScraperIdentifyVideoAsync(
                            video,
                            enabledScraperIds,
                            opts,
                            identifyDefaults,
                            scraperSvc,
                            scrapeAttemptSvc,
                            ct);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Scraper identify failed for video {VideoId}", video.Id);
                        RecordFailure("Scrapers", ex.Message);
                        dbCtx.ChangeTracker.Clear();
                        metadataServerSvc?.ResetTrackedIdentityState();
                    }
                }

                if (identified)
                {
                    await dbCtx.SaveChangesAsync(ct);
                    identifiedCount++;
                    unit.Complete(JobUnitOutcome.Succeeded);
                }
                else if (videoFailures.Count > 0)
                {
                    // A video only counts as unmatched when every query it needed actually ran.
                    failedCount++;
                    unit.Complete(JobUnitOutcome.Failed, $"Video {video.Id}: {string.Join("; ", videoFailures)}");
                }
                else
                {
                    unit.Complete(JobUnitOutcome.Skipped);
                }
            }

            await dbCtx.SaveChangesAsync(ct);
            var unmatchedCount = total - identifiedCount - failedCount;
            progress.SetSummary(BuildIdentifySummary(identifiedCount, failedCount, unmatchedCount, total, warningCount, sourceErrors));
            if (failedCount > 0 || sourceErrors.Count > 0)
            {
                logger.LogWarning(
                    "Identify completed: {Identified} identified, {Failed} failed, {Unmatched} unmatched of {Total} videos; source errors: {SourceErrors}",
                    identifiedCount,
                    failedCount,
                    unmatchedCount,
                    total,
                    string.Join("; ", sourceErrors.Select(pair => $"{pair.Key}: {string.Join(" | ", pair.Value)}")));
            }
            else
            {
                logger.LogInformation(
                    "Identify completed: {Identified} identified, {Unmatched} unmatched of {Total} videos",
                    identifiedCount,
                    unmatchedCount,
                    total);
            }
        }, exclusive: false);

        return jobId;
    }

    // The finished Identify job's summary. Failed videos are those a source error left unidentified;
    // source errors are listed even when another source still identified the video.
    internal static string BuildIdentifySummary(
        int identified,
        int failed,
        int unmatched,
        int total,
        int warnings,
        IReadOnlyDictionary<string, List<string>> sourceErrors)
    {
        const int maxMessagesPerSource = 3;
        const int maxMessageLength = 300;

        var summary = new StringBuilder($"Identified {identified} of {total} videos");
        if (failed > 0)
            summary.Append($", {failed} failed");
        summary.Append($", {unmatched} unmatched");
        if (warnings > 0)
            summary.Append($"; {warnings} saved with skipped conflicting tag names or aliases");
        summary.Append('.');
        if (warnings > 0)
            summary.Append(" See server logs for details.");

        if (sourceErrors.Count > 0)
        {
            summary.Append(" Source errors: ");
            summary.AppendJoin("; ", sourceErrors.Select(pair =>
            {
                var messages = pair.Value
                    .Take(maxMessagesPerSource)
                    .Select(message => message.Length > maxMessageLength ? message[..maxMessageLength] + "…" : message);
                var more = pair.Value.Count > maxMessagesPerSource ? $" (+{pair.Value.Count - maxMessagesPerSource} more)" : string.Empty;
                return $"{pair.Key}: {string.Join(" | ", messages)}{more}";
            }));
        }

        return summary.ToString();
    }

    private static List<string>? ResolveIdentifyMetadataServerEndpoints(List<string>? sources, IReadOnlyList<MetadataServerInstance> metadataServers)
    {
        if (sources == null || sources.Count == 0)
            return null;

        var endpoints = new List<string>();
        foreach (var source in sources.Select(source => source.Trim()).Where(source => source.Length > 0))
        {
            var server = metadataServers.FirstOrDefault(candidate =>
                string.Equals(candidate.Endpoint, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Name, source, StringComparison.OrdinalIgnoreCase));

            if (server == null)
                continue;

            if (!endpoints.Contains(server.Endpoint, StringComparer.OrdinalIgnoreCase))
                endpoints.Add(server.Endpoint);
        }

        return endpoints;
    }

    // Resolves which URL-capable video scrapers are enabled as identify sources. Returns null when no
    // explicit sources were given (all scrapers eligible), or the set of scraper ids named in the
    // sources list (empty when the caller selected only metadata servers, so the scraper stage skips).
    private static HashSet<string>? ResolveIdentifyScraperIds(List<string>? sources, ScraperService? scraperSvc)
    {
        if (scraperSvc == null)
            return [];

        var videoScrapers = scraperSvc.GetScrapers()
            .Where(scraper => string.Equals(scraper.EntityType, EntityKinds.Video, StringComparison.OrdinalIgnoreCase)
                && scraper.SupportedScrapes.Any(kind => string.Equals(kind, "URL", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (sources == null || sources.Count == 0)
            return null;

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources.Select(source => source.Trim()).Where(source => source.Length > 0))
        {
            var match = videoScrapers.FirstOrDefault(scraper =>
                string.Equals(scraper.Id, source, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(scraper.Name, source, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                enabled.Add(match.Id);
        }

        return enabled;
    }

    // Tries each URL-matching, enabled scraper for the video's URLs in priority order, applying the
    // first that returns data (honoring the identify options), and returns whether one was applied.
    private async Task<bool> TryScraperIdentifyVideoAsync(
        Video video,
        HashSet<string>? enabledScraperIds,
        IdentifyOptionsDto? opts,
        IdentifyDefaultsConfig identifyDefaults,
        ScraperService scraperSvc,
        ScrapeAttemptService scrapeAttemptSvc,
        CancellationToken ct)
    {
        var urls = video.Urls
            .Select(item => item.Url)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .ToList();

        foreach (var url in urls)
        {
            foreach (var candidate in scraperSvc.FindScrapersForUrl(url, EntityKinds.Video))
            {
                if (enabledScraperIds != null && !enabledScraperIds.Contains(candidate.Id))
                    continue;

                var attempt = await scrapeAttemptSvc.CreateAttemptAsync(
                    new CreateScrapeAttemptDto(candidate.Id, EntityKinds.Video, video.Id, "url", url, null, null),
                    ct);

                if (attempt.Status != ScrapeAttemptStatuses.Success)
                    continue;

                var applied = await scrapeAttemptSvc.ApplyAttemptAsync(attempt.Id, BuildScraperIdentifyApplyDto(opts, identifyDefaults), ct);
                if (applied == null)
                    continue;

                logger.LogTrace(
                    "Identify video {VideoId}: applied scraper {ScraperId} from URL {Url}",
                    video.Id, candidate.Id, url);
                return true;
            }
        }

        return false;
    }

    // Translates the identify options into a scraper apply plan: per-field overwrite/merge/ignore plus
    // the create-missing / mark-organized toggles, mirroring the metadata-server import config.
    private static ApplyVideoScrapeAttemptDto BuildScraperIdentifyApplyDto(IdentifyOptionsDto? opts, IdentifyDefaultsConfig identifyDefaults)
    {
        var strategies = opts?.FieldStrategies;
        string Strategy(string key) => strategies != null && strategies.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.Trim().ToLowerInvariant()
            : "merge";

        static string ModeFor(string strategy) => strategy switch
        {
            "ignore" => "skip",
            "overwrite" => "replace",
            _ => "merge",
        };

        var replaceFields = new List<string>();
        foreach (var field in new[] { "title", "code", "details", "director", "date" })
        {
            if (Strategy(field) == "overwrite")
                replaceFields.Add(field);
        }
        if (opts?.SetCoverImage ?? true)
            replaceFields.Add("image");

        var collectionModes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["urls"] = ModeFor(Strategy("urls")),
            ["tags"] = (opts?.SetTags ?? true) ? ModeFor(Strategy("tags")) : "skip",
            ["performers"] = (opts?.SetPerformers ?? true) ? ModeFor(Strategy("performers")) : "skip",
            ["studio"] = (opts?.SetStudio ?? true) ? ModeFor(Strategy("studio")) : "skip",
        };

        return new ApplyVideoScrapeAttemptDto(
            ReplaceFields: replaceFields,
            CollectionModes: collectionModes,
            CreateMissingTags: opts?.CreateTags ?? identifyDefaults.CreateTags,
            CreateMissingPerformers: opts?.CreatePerformers ?? identifyDefaults.CreatePerformers,
            CreateMissingStudio: opts?.CreateStudios ?? identifyDefaults.CreateStudios,
            MarkOrganized: opts?.MarkOrganized ?? false);
    }

    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults)
        => MeetsIdentifyAutoApplyThresholds(matchCount, durationDifferenceSeconds, phashDistance, identifyDefaults, out _);

    // Same threshold logic, but also reports which specific guard rejected the candidate so the
    // identify loop can log it. The boolean result is identical to the parameterless overload.
    private static bool MeetsIdentifyAutoApplyThresholds(int matchCount, double? durationDifferenceSeconds, int? phashDistance, IdentifyDefaultsConfig identifyDefaults, out string? failureReason)
    {
        failureReason = null;

        // Primary signal: require enough matching fingerprint submissions. MatchCount already
        // counts oshash, md5, and phash (incl. close phash) matches, so this works for metadata
        // servers that don't publish phashes.
        if (identifyDefaults.AutoApplyMinFingerprintMatches is int minFingerprintMatches)
        {
            if (matchCount < minFingerprintMatches)
            {
                failureReason = $"matchCount {matchCount} < AutoApplyMinFingerprintMatches {minFingerprintMatches}";
                return false;
            }
        }

        // Secondary guard: only reject when both durations are known and disagree by more than the
        // tolerance. A missing duration must never block a match that cleared the fingerprint bar.
        if (identifyDefaults.AutoApplyMaxDurationDifferenceSeconds is int maxDurationDifferenceSeconds)
        {
            if (durationDifferenceSeconds.HasValue && durationDifferenceSeconds.Value > maxDurationDifferenceSeconds)
            {
                failureReason = $"durationDiff {durationDifferenceSeconds.Value:0.##}s > AutoApplyMaxDurationDifferenceSeconds {maxDurationDifferenceSeconds}";
                return false;
            }
        }

        // Optional phash tightness guard: only applies when a phash distance is actually computable.
        if (identifyDefaults.AutoApplyMaxPhashDistance is int maxPhashDistance)
        {
            if (phashDistance.HasValue && phashDistance.Value > maxPhashDistance)
            {
                failureReason = $"phashDistance {phashDistance.Value} > AutoApplyMaxPhashDistance {maxPhashDistance}";
                return false;
            }
        }

        return true;
    }

    private static double? GetDurationDifferenceSeconds(Video video, MetadataServerVideoMatchDto match)
    {
        var localDuration = video.Files.Select(file => (double?)file.Duration).Max();
        return localDuration.HasValue && match.Duration.HasValue
            ? Math.Abs(localDuration.Value - match.Duration.Value)
            : null;
    }

    private static int? GetBestPhashDistance(Video video, MetadataServerVideoMatchDto match)
    {
        var localPhashes = video.Files
            .SelectMany(file => file.Fingerprints)
            .Where(fingerprint => string.Equals(fingerprint.Type, "phash", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Value))
            .Select(fingerprint => fingerprint.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var remotePhashes = match.Fingerprints
            .Where(fingerprint => string.Equals(fingerprint.Algorithm, "PHASH", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(fingerprint.Hash))
            .Select(fingerprint => fingerprint.Hash)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (localPhashes.Count == 0 || remotePhashes.Count == 0)
            return null;

        int? bestDistance = null;
        foreach (var localPhash in localPhashes)
        {
            foreach (var remotePhash in remotePhashes)
            {
                var distance = MetadataServerService.ComputePhashHammingDistance(localPhash, remotePhash);
                bestDistance = bestDistance.HasValue ? Math.Min(bestDistance.Value, distance) : distance;
            }
        }

        return bestDistance;
    }
}
