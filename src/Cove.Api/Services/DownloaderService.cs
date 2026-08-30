using System.Collections.Concurrent;
using System.Text.Json;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Events;
using Cove.Core.Interfaces;
using Cove.Plugins;
using Cove.Data;
using Cove.Data.Services;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed record DownloaderBatchExecutionSummary(
    int TotalCount,
    int SucceededCount,
    int SkippedCount,
    int FailedCount,
    string? FollowUpJobId,
    IReadOnlyList<string> Issues);

public sealed record DownloaderBatchPreflightResult(
    IReadOnlyList<DownloaderBatchItemDto> ItemsToQueue,
    IReadOnlyList<DownloaderBatchStartIssueDto> Issues);

public sealed record DownloaderMetadataApplyOptions(
    bool CreateMissingTags = false,
    bool CreateMissingPerformers = false,
    bool CreateMissingStudio = false,
    bool MarkOrganized = false);

public partial class DownloaderService(
    ExtensionManager extensionManager,
    IHttpClientFactory httpClientFactory,
    ILoggerFactory loggerFactory,
    CoveConfiguration config,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<DownloaderService> logger,
    PhysicalFileAccessCoordinator? physicalFileAccessCoordinator = null)
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "cove", "downloaders");
    private readonly Lock _downloadSlotLock = new();
    private readonly Lock _libraryMoveLock = new();
    private SemaphoreSlim? _downloadSlots;
    private int _downloadSlotCapacity;
    private readonly PhysicalFileAccessCoordinator _physicalFileAccessCoordinator =
        physicalFileAccessCoordinator ?? PhysicalFileAccessCoordinator.Shared;

    [LoggerMessage(EventId = 2501, Level = LogLevel.Trace,
        Message = "Starting downloader {DownloaderId} for {Entity} URL {Url}; quality={QualityId}")]
    private partial void TraceDownloadStarted(string downloaderId, DownloaderEntity entity, string url, string? qualityId);

    [LoggerMessage(EventId = 2502, Level = LogLevel.Trace,
        Message = "Downloader {DownloaderId} completed for {Entity} URL {Url}; file={LocalPath}, originalFilename={OriginalFilename}")]
    private partial void TraceDownloadCompleted(string downloaderId, DownloaderEntity entity, string url, string localPath, string? originalFilename);

    [LoggerMessage(EventId = 2503, Level = LogLevel.Trace,
        Message = "Imported {Entity} download from {Url} into entity {EntityId} using downloader {DownloaderId}")]
    private partial void TraceDownloadImported(DownloaderEntity entity, string url, int entityId, string downloaderId);

    [LoggerMessage(EventId = 2504, Level = LogLevel.Trace,
        Message = "Downloader {DownloaderId} returned no result for {Entity} URL {Url}")]
    private partial void TraceDownloadReturnedNoResult(string downloaderId, DownloaderEntity entity, string url);

    [LoggerMessage(EventId = 2505, Level = LogLevel.Trace,
        Message = "Moved download for {DownloaderId} {Entity} URL {Url} into library path {LibraryPath}")]
    private partial void TraceDownloadMoved(string downloaderId, DownloaderEntity entity, string url, string libraryPath);

    public IReadOnlyList<DownloaderDescriptorDto> GetDownloaders()
    {
        Directory.CreateDirectory(_tempRoot);

        return extensionManager.GetDownloaderProviders()
            .SelectMany(provider => extensionManager.ExecuteExtension(provider, provider.GetDownloaders))
            .OrderBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
    }

    public async Task<IReadOnlyList<DownloaderMatchDto>> MatchUrlAsync(string url, CancellationToken ct)
    {
        var failures = new List<(string ProviderId, string Message)>();
        var matches = await MatchUrlAsync(url, ct, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0, failures);
        if (failures.Count > 0)
            logger.LogWarning("Downloader matching encountered {FailureCount} provider failure(s)", failures.Count);

        if (matches.Count == 0 && failures.Count > 0)
            throw new InvalidOperationException(BuildMatchFailureMessage(failures));

        return matches;
    }

    private async Task<IReadOnlyList<DownloaderMatchDto>> MatchUrlAsync(string url, CancellationToken ct, IReadOnlySet<string> excludedProviderIds, int diversionDepth, List<(string ProviderId, string Message)> failures)
    {
        if (string.IsNullOrWhiteSpace(url))
            return [];

        if (diversionDepth > 4)
            return [];

        var results = new List<DownloaderMatchDto>();
        var providers = extensionManager.GetDownloaderProviders()
            .Where(provider => !excludedProviderIds.Contains(provider.Id))
            .ToList();
        var providerExecutions = providers.ToDictionary(
            provider => provider,
            extensionManager.CaptureExtensionExecution);
        var descriptorLookup = providers
            .SelectMany(provider => extensionManager.ExecuteExtension(providerExecutions[provider], provider.GetDownloaders))
            .GroupBy(descriptor => descriptor.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var provider in providers)
        {
            try
            {
                if (!await extensionManager.EnsureExtensionInitializedAsync(provider.Id, ct))
                {
                    failures.Add((provider.Id, $"Downloader provider failed to initialize: {provider.Id}"));
                    continue;
                }

                var matches = await extensionManager.ExecuteExtensionAsync(
                    providerExecutions[provider],
                    () => provider.MatchAllAsync(url, ct));
                if (matches.Count == 0)
                    continue;

                foreach (var match in matches)
                {
                    if (match.Divert)
                    {
                        var nextExcludedProviderIds = excludedProviderIds
                            .Append(provider.Id)
                            .ToHashSet(StringComparer.OrdinalIgnoreCase);
                        var divertedMatches = await MatchUrlAsync(match.NormalizedUrl, ct, nextExcludedProviderIds, diversionDepth + 1, failures);
                        foreach (var divertedMatch in divertedMatches)
                        {
                            results.Add(divertedMatch with
                            {
                                Label = string.IsNullOrWhiteSpace(match.Label) ? divertedMatch.Label : match.Label,
                                SourceUrl = string.IsNullOrWhiteSpace(divertedMatch.SourceUrl) ? match.SourceUrl : divertedMatch.SourceUrl,
                            });
                        }

                        continue;
                    }

                    if (!descriptorLookup.TryGetValue(match.DownloaderId, out var descriptor))
                    {
                        logger.LogDebug("Downloader provider {ProviderId} returned unknown downloader id {DownloaderId}", provider.Id, match.DownloaderId);
                        failures.Add((provider.Id, $"Downloader provider returned unknown downloader id: {match.DownloaderId}"));
                        continue;
                    }

                    results.Add(ToDto(descriptor, match));
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Downloader provider {ProviderId} failed URL match for {Url}", provider.Id, url);
                failures.Add((provider.Id, GetMatchFailureMessage(ex)));
            }
        }

        return results
            .OrderBy(result => result.DownloaderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.DownloaderId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string GetMatchFailureMessage(Exception ex)
    {
        if (!string.IsNullOrWhiteSpace(ex.Message))
            return ex.Message.Trim();

        return ex.GetBaseException().Message.Trim();
    }

    private static string BuildMatchFailureMessage(IReadOnlyList<(string ProviderId, string Message)> failures)
    {
        var uniqueFailures = failures
            .Where(failure => !string.IsNullOrWhiteSpace(failure.Message))
            .GroupBy(failure => $"{failure.ProviderId}\n{failure.Message}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        if (uniqueFailures.Count == 0)
            return "No downloader could be matched because provider checks failed.";

        if (uniqueFailures.Count == 1)
            return uniqueFailures[0].Message;

        return string.Join(
            Environment.NewLine,
            ["No downloader could be matched because provider checks failed:", .. uniqueFailures.Select(failure => $"- {failure.ProviderId}: {failure.Message}")]);
    }

    public async Task<DownloaderResult?> DownloadAsync(DownloaderRequest request, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        var registration = extensionManager.GetDownloaderProviders()
            .Select(provider => new
            {
                Provider = provider,
                Execution = extensionManager.CaptureExtensionExecution(provider),
            })
            .Select(registration => new
            {
                registration.Provider,
                registration.Execution,
                Descriptor = extensionManager.ExecuteExtension(registration.Execution, registration.Provider.GetDownloaders)
                    .FirstOrDefault(descriptor => string.Equals(descriptor.Id, request.DownloaderId, StringComparison.OrdinalIgnoreCase))
            })
            .FirstOrDefault(item => item.Descriptor != null);

        if (registration?.Descriptor == null)
            throw new InvalidOperationException($"Downloader not found: {request.DownloaderId}");

        if (!await extensionManager.EnsureExtensionInitializedAsync(registration.Provider.Id, ct))
            throw new InvalidOperationException($"Downloader is available but failed to initialize: {registration.Provider.Id}");

        Directory.CreateDirectory(_tempRoot);
        var tempDirectory = Path.Combine(_tempRoot, SanitizePathSegment(registration.Descriptor.Id), Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(tempDirectory);

        TraceDownloadStarted(request.DownloaderId, request.Entity, request.Url, request.QualityId);

        var retainTempDirectory = false;
        try
        {
            var host = new DownloaderHost(tempDirectory, httpClientFactory, loggerFactory, progress);
            using var downloadSlotLease = await AcquireDownloadSlotAsync(progress, ct);
            var result = await extensionManager.ExecuteExtensionAsync(
                registration.Execution,
                () => registration.Provider.DownloadAsync(request, host, ct));
            if (result == null)
            {
                TraceDownloadReturnedNoResult(request.DownloaderId, request.Entity, request.Url);
                return null;
            }

            var localPath = Path.IsPathRooted(result.LocalPath)
                ? result.LocalPath
                : Path.GetFullPath(Path.Combine(tempDirectory, result.LocalPath));

            if (!File.Exists(localPath))
                throw new InvalidOperationException($"Downloader {registration.Descriptor.Id} completed without producing a file at {localPath}");

            retainTempDirectory = IsPathWithinDirectory(localPath, tempDirectory);
            TraceDownloadCompleted(request.DownloaderId, request.Entity, request.Url, localPath, result.OriginalFilename);

            return result with { LocalPath = localPath };
        }
        finally
        {
            if (!retainTempDirectory)
                TryDeleteDirectory(tempDirectory);
        }
    }

    public async Task<(DownloaderResult? Result, int? ImportedEntityId)> DownloadAndIngestAsync(
        DownloaderRequest request,
        int? entityId,
        Cove.Core.Interfaces.IJobProgress? progress,
        CancellationToken ct,
        bool autoApplyMetadata = false,
        DownloaderMetadataApplyOptions? metadataApplyOptions = null,
        bool allowDuplicateDownload = false)
    {
        if (!allowDuplicateDownload)
            await EnsureDownloadAllowedAsync(request, entityId, ct);

        var result = await DownloadAsync(request, progress, ct);
        if (result == null)
            return (null, null);

        // A physical deletion must not enter between placing the file in the library and the scan
        // committing its BaseFileEntity reference.
        using var fileProductionLease = await _physicalFileAccessCoordinator.AcquireReadAsync(ct);
        var libraryPath = MoveDownloadedFileToLibrary(result, request.Entity, request.DownloaderId, request.Url);
        TraceDownloadMoved(request.DownloaderId, request.Entity, request.Url, libraryPath);

        using var scope = serviceScopeFactory.CreateScope();
        var scanService = scope.ServiceProvider.GetRequiredService<IScanService>();

        var importedEntityId = request.Entity switch
        {
            DownloaderEntity.Video => await ImportVideoAsync(scanService, libraryPath, entityId, progress, ct),
            DownloaderEntity.Image => await ImportImageAsync(scanService, libraryPath, entityId, progress, ct),
            DownloaderEntity.Gallery => await ImportGalleryAsync(scanService, libraryPath, entityId, progress, ct),
            DownloaderEntity.Audio => await ImportAudioAsync(scanService, libraryPath, entityId, progress, ct),
            DownloaderEntity.Text => await ImportTextAsync(scanService, libraryPath, entityId, progress, ct),
            _ => entityId,
        };

        if (importedEntityId.HasValue)
        {
            await AttachDownloadedUrlAsync(request.Entity, importedEntityId.Value, request.Url, ct);
            if (!string.IsNullOrWhiteSpace(request.SourceUrl)
                && !string.Equals(request.SourceUrl, request.Url, StringComparison.OrdinalIgnoreCase))
            {
                await AttachDownloadedUrlAsync(request.Entity, importedEntityId.Value, request.SourceUrl, ct);
            }

            TraceDownloadImported(request.Entity, request.Url, importedEntityId.Value, request.DownloaderId);
        }

        if (autoApplyMetadata && importedEntityId.HasValue)
            await ApplyAutoMetadataAsync(scope.ServiceProvider, request, result, importedEntityId.Value, metadataApplyOptions ?? new DownloaderMetadataApplyOptions(), progress, ct);

        return (result with { LocalPath = libraryPath }, importedEntityId);
    }

    public async Task<DownloaderBatchExecutionSummary> DownloadAndIngestBatchAsync(
        IReadOnlyList<DownloaderBatchItemDto> items,
        DownloaderBatchFollowUpDto? followUp,
        Cove.Core.Interfaces.IJobProgress? progress,
        CancellationToken ct)
    {
        var expansion = await ExpandBatchItemsAsync(items, ct);
        items = expansion.ItemsToQueue;

        if (items.Count == 0)
            return new DownloaderBatchExecutionSummary(0, 0, expansion.Issues.Count, 0, null, expansion.Issues.Select(issue => $"{issue.Label}: {issue.Reason}").ToList());

        followUp ??= new DownloaderBatchFollowUpDto();

        var batchItems = items.Select((item, index) => new IndexedBatchItem(item, index)).ToList();
        var issues = new ConcurrentQueue<string>(expansion.Issues.Select(issue => $"{issue.Label}: {issue.Reason}"));
        var importedPaths = new ConcurrentBag<string>();
        var successfulItems = new ConcurrentBag<DownloaderBatchItemDto>();
        var reservedDownloads = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var processed = 0;
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;

        logger.LogInformation("Starting batch download of {ItemCount} item(s); maxConcurrency={MaxConcurrency}", batchItems.Count, ResolveMaxConcurrentDownloads());

        progress?.Report(0d, $"Preparing {batchItems.Count} download{(batchItems.Count == 1 ? string.Empty : "s")}...");

        await Parallel.ForEachAsync(
            batchItems,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = ResolveMaxConcurrentDownloads(),
                CancellationToken = ct,
            },
            async (batchItem, token) =>
            {
                var label = BuildBatchItemLabel(batchItem.Item, batchItem.Index);
                try
                {
                    var resolvedItem = await ResolveBatchItemAsync(batchItem.Item, batchItem.Index, followUp, reservedDownloads, token);
                    label = resolvedItem.Label;

                    var (result, importedEntityId) = await DownloadAndIngestAsync(
                        resolvedItem.Request,
                        resolvedItem.EntityId,
                        progress: null,
                        token,
                        autoApplyMetadata: resolvedItem.AutoApplyMetadata || (followUp.ScrapeVideos && resolvedItem.Request.Entity == DownloaderEntity.Video),
                        metadataApplyOptions: resolvedItem.MetadataApplyOptions,
                        allowDuplicateDownload: followUp.AllowDuplicateDownloads);

                    if (result != null)
                    {
                        importedPaths.Add(result.LocalPath);
                        if (importedEntityId.HasValue)
                            await AttachBatchRelationshipsAsync(resolvedItem.Request.Entity, importedEntityId.Value, batchItem.Item, token);
                        successfulItems.Add(batchItem.Item);
                        Interlocked.Increment(ref succeeded);
                    }
                    else
                    {
                        Interlocked.Increment(ref skipped);
                        issues.Enqueue($"{label}: downloader returned no result.");
                    }
                }
                catch (InvalidOperationException ex) when (!followUp.AllowDuplicateDownloads && IsDuplicateDownloadMessage(ex.Message))
                {
                    Interlocked.Increment(ref skipped);
                    issues.Enqueue($"{label}: {ex.Message}");
                }
                catch (InvalidOperationException ex) when (IsBatchSkipMessage(ex.Message))
                {
                    Interlocked.Increment(ref skipped);
                    issues.Enqueue($"{label}: {ex.Message}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    issues.Enqueue($"{label}: {ex.Message}");
                    logger.LogDebug(ex, "Batch download failed for {Label}", label);
                }
                finally
                {
                    var completed = Interlocked.Increment(ref processed);
                    var percent = batchItems.Count == 0 ? 0.95d : (completed / (double)batchItems.Count) * 0.95d;
                    progress?.Report(percent, BuildBatchProgressMessage(completed, batchItems.Count, label));
                }
            });

        await ApplyBatchGroupMetadataAsync(successfulItems.ToList(), followUp, progress, issues, ct);

        var followUpJobId = TryQueueFollowUpGenerateJob(followUp.Generate, importedPaths, progress);
        var summary = new DownloaderBatchExecutionSummary(
            batchItems.Count,
            succeeded,
            skipped,
            failed,
            followUpJobId,
            issues.ToArray());

        progress?.Report(1d, BuildBatchCompletionMessage(summary));
        if (summary.FailedCount > 0)
            logger.LogWarning("Batch download completed with failures; total={TotalCount}, succeeded={SucceededCount}, skipped={SkippedCount}, failed={FailedCount}", summary.TotalCount, summary.SucceededCount, summary.SkippedCount, summary.FailedCount);
        else
            logger.LogInformation("Batch download completed; total={TotalCount}, succeeded={SucceededCount}, skipped={SkippedCount}", summary.TotalCount, summary.SucceededCount, summary.SkippedCount);
        return summary;
    }

    public async Task<DownloaderBatchPreflightResult> PreflightBatchAsync(
        IReadOnlyList<DownloaderBatchItemDto> items,
        DownloaderBatchFollowUpDto? followUp,
        CancellationToken ct)
    {
        if (items.Count == 0)
            return new DownloaderBatchPreflightResult([], []);

        followUp ??= new DownloaderBatchFollowUpDto();
        var expansion = await ExpandBatchItemsAsync(items, ct);
        items = expansion.ItemsToQueue;
        var issues = expansion.Issues.ToList();

        if (followUp.AllowDuplicateDownloads)
            return new DownloaderBatchPreflightResult(items, issues);

        var entities = items
            .Select(item => Enum.TryParse<DownloaderEntity>(item.Entity, true, out var entity) ? entity : (DownloaderEntity?)null)
            .OfType<DownloaderEntity>()
            .Distinct()
            .ToArray();
        var existingUrls = await LoadExistingDownloadUrlLookupAsync(entities, ct);
        var downloadedEntityIds = await LoadDownloadedEntityIdLookupAsync(entities, ct);
        var reservedDownloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsToQueue = new List<DownloaderBatchItemDto>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var label = BuildBatchItemLabel(item, index);

            if (!Enum.TryParse<DownloaderEntity>(item.Entity, true, out var entity))
            {
                itemsToQueue.Add(item);
                continue;
            }

            if (item.EntityId.HasValue
                && downloadedEntityIds.TryGetValue(entity, out var downloadedIds)
                && downloadedIds.Contains(item.EntityId.Value))
            {
                issues.Add(new DownloaderBatchStartIssueDto("skipped", label, $"{entity} {item.EntityId.Value} already has downloaded files."));
                continue;
            }

            var normalizedUrl = NormalizeUrlForLookup(item.Url);
            if (!string.IsNullOrWhiteSpace(normalizedUrl)
                && existingUrls.TryGetValue(entity, out var entityLookup)
                && entityLookup.TryGetValue(normalizedUrl, out var duplicateTargets)
                && duplicateTargets.FirstOrDefault(target => !item.EntityId.HasValue || target.EntityId != item.EntityId.Value) is { } duplicate)
            {
                issues.Add(new DownloaderBatchStartIssueDto("skipped", label, $"This URL is already downloaded for {duplicate.Label}."));
                continue;
            }

            var reservationKey = $"{entity}:{normalizedUrl}";
            if (!string.IsNullOrWhiteSpace(normalizedUrl) && !reservedDownloads.Add(reservationKey))
            {
                issues.Add(new DownloaderBatchStartIssueDto("skipped", label, "This URL is already queued elsewhere in this batch."));
                continue;
            }

            itemsToQueue.Add(item);
        }

        return new DownloaderBatchPreflightResult(itemsToQueue, issues);
    }

    private async Task<DownloaderBatchPreflightResult> ExpandBatchItemsAsync(IReadOnlyList<DownloaderBatchItemDto> items, CancellationToken ct)
    {
        var expandedItems = new List<DownloaderBatchItemDto>();
        var issues = new List<DownloaderBatchStartIssueDto>();

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            if (!string.IsNullOrWhiteSpace(item.DownloaderId)
                || !Enum.TryParse<DownloaderEntity>(item.Entity, true, out var entity)
                || string.IsNullOrWhiteSpace(item.Url))
            {
                expandedItems.Add(item);
                continue;
            }

            var matches = (await MatchUrlAsync(item.Url, ct))
                .Where(match => string.Equals(match.SupportedEntity, entity.ToString(), StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                expandedItems.Add(item);
                continue;
            }

            foreach (var match in matches)
                expandedItems.Add(ApplyDownloaderMatch(item, match, useMatchLabel: matches.Count > 1 || !string.IsNullOrWhiteSpace(match.SourceUrl)));
        }

        return new DownloaderBatchPreflightResult(expandedItems, issues);
    }

    private static DownloaderBatchItemDto ApplyDownloaderMatch(DownloaderBatchItemDto item, DownloaderMatchDto match, bool useMatchLabel)
    {
        var label = string.IsNullOrWhiteSpace(match.Label) ? item.Label : match.Label;
        if (!useMatchLabel && !string.IsNullOrWhiteSpace(item.Title))
            label = item.Label;

        var title = string.IsNullOrWhiteSpace(item.Title) ? label : item.Title;
        return item with
        {
            DownloaderId = match.DownloaderId,
            Url = string.IsNullOrWhiteSpace(match.NormalizedUrl) ? item.Url : match.NormalizedUrl,
            QualityId = item.QualityId ?? match.QualityOptions.FirstOrDefault()?.Id,
            SourceUrl = string.IsNullOrWhiteSpace(match.SourceUrl) ? item.SourceUrl : match.SourceUrl,
            Label = label,
            Title = title,
        };
    }

    internal static ScrapedVideoDto? ConvertScrapeResultToVideoMetadata(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        var dto = new ScrapedVideoDto
        {
            SourceScraperId = sourceScraperId,
            Title = GetScrapeResultString(result, "Title", "title", "Name", "name"),
            Code = GetScrapeResultString(result, "Code", "code"),
            Details = GetScrapeResultString(result, "Details", "details", "Description", "description", "Synopsis", "synopsis"),
            Director = GetScrapeResultString(result, "Director", "director"),
            Date = GetScrapeResultString(result, "Date", "date", "ReleaseDate", "releaseDate"),
            ImageUrl = GetScrapeResultString(result, "Image", "image", "ImageUrl", "imageUrl"),
            StudioName = GetScrapeResultString(result, "Studio", "studio", "StudioName", "studioName"),
            Urls = MergeDistinctStrings(GetScrapeResultStringList(result, "URLs", "urls", "URL", "url", "Links", "links"), sourceUrl),
            TagNames = GetScrapeResultStringList(result, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
            PerformerNames = GetScrapeResultStringList(result, "Performers", "performers", "Performer", "performer", "PerformerNames", "performerNames"),
        };

        return HasMetadataContent(dto.Title, dto.Code, dto.Details, dto.Date, dto.StudioName)
            || dto.PerformerNames.Count > 0
            || dto.TagNames.Count > 0
            || dto.Urls.Count > 0
            ? dto
            : null;
    }

    internal static ScrapedImageDto? ConvertScrapeResultToImageMetadata(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        var dto = new ScrapedImageDto
        {
            SourceScraperId = sourceScraperId,
            Title = GetScrapeResultString(result, "Title", "title", "Name", "name"),
            Date = GetScrapeResultString(result, "Date", "date", "ReleaseDate", "releaseDate"),
            Details = GetScrapeResultString(result, "Details", "details", "Description", "description", "Synopsis", "synopsis"),
            Photographer = GetScrapeResultString(result, "Photographer", "photographer", "Artist", "artist"),
            ImageUrl = GetScrapeResultString(result, "Image", "image", "ImageUrl", "imageUrl"),
            Urls = MergeDistinctStrings(GetScrapeResultStringList(result, "URLs", "urls", "URL", "url", "Links", "links"), sourceUrl),
            StudioName = GetScrapeResultString(result, "Studio", "studio", "StudioName", "studioName"),
            PerformerNames = GetScrapeResultStringList(result, "Performers", "performers", "Performer", "performer", "PerformerNames", "performerNames"),
            TagNames = GetScrapeResultStringList(result, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
            GalleryTitle = GetScrapeResultString(result, "GalleryTitle", "galleryTitle", "Gallery", "gallery"),
        };

        return HasMetadataContent(dto.Title, dto.Details, dto.Date, dto.Photographer, dto.StudioName, dto.GalleryTitle)
            || dto.PerformerNames.Count > 0
            || dto.TagNames.Count > 0
            || dto.Urls.Count > 0
            ? dto
            : null;
    }

    internal static ScrapedAudioMetadata? ConvertScrapeResultToAudioMetadata(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        var metadata = new ScrapedAudioMetadata
        {
            SourceScraperId = sourceScraperId,
            Title = GetScrapeResultString(result, "Title", "title", "Name", "name"),
            Code = GetScrapeResultString(result, "Code", "code"),
            Details = GetScrapeResultString(result, "Details", "details", "Description", "description", "Synopsis", "synopsis"),
            Date = GetScrapeResultString(result, "Date", "date", "ReleaseDate", "releaseDate"),
            StudioName = GetScrapeResultString(result, "Studio", "studio", "StudioName", "studioName"),
            Urls = MergeDistinctStrings(GetScrapeResultStringList(result, "URLs", "urls", "URL", "url", "Links", "links"), sourceUrl),
            TagNames = GetScrapeResultStringList(result, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
            PerformerNames = MergeDistinctStrings(
                GetScrapeResultStringList(result, "Performers", "performers", "Performer", "performer", "PerformerNames", "performerNames"),
                GetScrapeResultStringList(result, "Artist", "artist", "Creator", "creator", "Author", "author")),
        };

        return HasMetadataContent(metadata.Title, metadata.Code, metadata.Details, metadata.Date, metadata.StudioName)
            || metadata.PerformerNames.Count > 0
            || metadata.TagNames.Count > 0
            || metadata.Urls.Count > 0
            ? metadata
            : null;
    }

    internal static ScrapedTextMetadata? ConvertScrapeResultToTextMetadata(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        var metadata = new ScrapedTextMetadata
        {
            SourceScraperId = sourceScraperId,
            Title = GetScrapeResultString(result, "Title", "title", "Name", "name"),
            Code = GetScrapeResultString(result, "Code", "code"),
            Details = GetScrapeResultString(result, "Details", "details", "Description", "description", "Synopsis", "synopsis"),
            Date = GetScrapeResultString(result, "Date", "date", "ReleaseDate", "releaseDate"),
            StudioName = GetScrapeResultString(result, "Studio", "studio", "StudioName", "studioName"),
            Urls = MergeDistinctStrings(GetScrapeResultStringList(result, "URLs", "urls", "URL", "url", "Links", "links"), sourceUrl),
            TagNames = GetScrapeResultStringList(result, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
            PerformerNames = MergeDistinctStrings(
                GetScrapeResultStringList(result, "Performers", "performers", "Performer", "performer", "PerformerNames", "performerNames"),
                GetScrapeResultStringList(result, "Author", "author", "Creator", "creator", "Artist", "artist")),
        };

        return HasMetadataContent(metadata.Title, metadata.Code, metadata.Details, metadata.Date, metadata.StudioName)
            || metadata.PerformerNames.Count > 0
            || metadata.TagNames.Count > 0
            || metadata.Urls.Count > 0
            ? metadata
            : null;
    }

    internal static ScrapedGroupDto? ConvertScrapeResultToGroupMetadata(IReadOnlyDictionary<string, object> result, string sourceUrl, string? sourceScraperId = null)
    {
        if (result.Count == 0)
            return null;

        var dto = new ScrapedGroupDto
        {
            SourceScraperId = sourceScraperId,
            Name = GetScrapeResultString(result, "Name", "name", "Title", "title"),
            Aliases = GetScrapeResultStringList(result, "Aliases", "aliases", "Alias", "alias"),
            Duration = GetScrapeResultInt(result, "Duration", "duration", "DurationSeconds", "durationSeconds"),
            Date = GetScrapeResultString(result, "Date", "date", "ReleaseDate", "releaseDate"),
            Director = GetScrapeResultString(result, "Director", "director"),
            Details = GetScrapeResultString(result, "Details", "details", "Description", "description", "Synopsis", "synopsis"),
            Synopsis = GetScrapeResultString(result, "Synopsis", "synopsis", "Description", "description", "Details", "details"),
            Rating = GetScrapeResultInt(result, "Rating", "rating"),
            ImageUrl = GetScrapeResultString(result, "Image", "image", "ImageUrl", "imageUrl", "FrontImage", "frontImage", "FrontImageUrl", "frontImageUrl"),
            Urls = MergeDistinctStrings(GetScrapeResultStringList(result, "URLs", "urls", "URL", "url", "Links", "links"), sourceUrl),
            StudioName = GetScrapeResultString(result, "Studio", "studio", "StudioName", "studioName"),
            TagNames = GetScrapeResultStringList(result, "Tags", "tags", "Tag", "tag", "TagNames", "tagNames"),
        };

        return HasMetadataContent(dto.Name, dto.Details, dto.Synopsis, dto.Date, dto.Director, dto.StudioName, dto.ImageUrl)
            || dto.Aliases.Count > 0
            || dto.Duration.HasValue
            || dto.Rating.HasValue
            || dto.TagNames.Count > 0
            || dto.Urls.Count > 0
            ? dto
            : null;
    }

    internal static ScrapedAudioMetadata? MergeAudioMetadata(ScrapedAudioMetadata? primary, ScrapedAudioMetadata? secondary)
    {
        if (primary == null)
            return secondary;

        if (secondary == null)
            return primary;

        return new ScrapedAudioMetadata
        {
            SourceScraperId = primary.SourceScraperId ?? secondary.SourceScraperId,
            Title = ChooseValue(primary.Title, secondary.Title),
            Code = ChooseValue(primary.Code, secondary.Code),
            Details = ChooseValue(primary.Details, secondary.Details),
            Date = ChooseValue(primary.Date, secondary.Date),
            StudioName = ChooseValue(primary.StudioName, secondary.StudioName),
            Urls = MergeDistinctStrings(primary.Urls, secondary.Urls),
            TagNames = ChoosePreferredNames(primary.TagNames, secondary.TagNames),
            PerformerNames = MergeDistinctStrings(primary.PerformerNames, secondary.PerformerNames),
        };
    }

    internal static ScrapedVideoDto? MergeVideoMetadata(ScrapedVideoDto? primary, ScrapedVideoDto? secondary)
    {
        if (primary == null)
            return secondary;

        if (secondary == null)
            return primary;

        return primary with
        {
            SourceScraperId = primary.SourceScraperId ?? secondary.SourceScraperId,
            Title = ChooseValue(primary.Title, secondary.Title),
            Code = ChooseValue(primary.Code, secondary.Code),
            Details = ChooseValue(primary.Details, secondary.Details),
            Director = ChooseValue(primary.Director, secondary.Director),
            Date = ChooseValue(primary.Date, secondary.Date),
            ImageUrl = ChooseValue(primary.ImageUrl, secondary.ImageUrl),
            StudioName = ChooseValue(primary.StudioName, secondary.StudioName),
            Urls = MergeDistinctStrings(primary.Urls, secondary.Urls),
            TagNames = ChoosePreferredNames(primary.TagNames, secondary.TagNames),
            PerformerNames = MergeDistinctStrings(primary.PerformerNames, secondary.PerformerNames),
        };
    }

    internal static ScrapedTextMetadata? MergeTextMetadata(ScrapedTextMetadata? primary, ScrapedTextMetadata? secondary)
    {
        if (primary == null)
            return secondary;

        if (secondary == null)
            return primary;

        return new ScrapedTextMetadata
        {
            SourceScraperId = primary.SourceScraperId ?? secondary.SourceScraperId,
            Title = ChooseValue(primary.Title, secondary.Title),
            Code = ChooseValue(primary.Code, secondary.Code),
            Details = ChooseValue(primary.Details, secondary.Details),
            Date = ChooseValue(primary.Date, secondary.Date),
            StudioName = ChooseValue(primary.StudioName, secondary.StudioName),
            Urls = MergeDistinctStrings(primary.Urls, secondary.Urls),
            TagNames = ChoosePreferredNames(primary.TagNames, secondary.TagNames),
            PerformerNames = MergeDistinctStrings(primary.PerformerNames, secondary.PerformerNames),
        };
    }

    internal static ScrapedImageDto? MergeImageMetadata(ScrapedImageDto? primary, ScrapedImageDto? secondary)
    {
        if (primary == null)
            return secondary;

        if (secondary == null)
            return primary;

        return primary with
        {
            SourceScraperId = primary.SourceScraperId ?? secondary.SourceScraperId,
            Title = ChooseValue(primary.Title, secondary.Title),
            Date = ChooseValue(primary.Date, secondary.Date),
            Details = ChooseValue(primary.Details, secondary.Details),
            Photographer = ChooseValue(primary.Photographer, secondary.Photographer),
            ImageUrl = ChooseValue(primary.ImageUrl, secondary.ImageUrl),
            Urls = MergeDistinctStrings(primary.Urls, secondary.Urls),
            StudioName = ChooseValue(primary.StudioName, secondary.StudioName),
            PerformerNames = MergeDistinctStrings(primary.PerformerNames, secondary.PerformerNames),
            TagNames = ChoosePreferredNames(primary.TagNames, secondary.TagNames),
            GalleryTitle = ChooseValue(primary.GalleryTitle, secondary.GalleryTitle),
        };
    }

    internal static ScrapedGroupDto? MergeGroupMetadata(ScrapedGroupDto? primary, ScrapedGroupDto? secondary)
    {
        if (primary == null)
            return secondary;

        if (secondary == null)
            return primary;

        return primary with
        {
            SourceScraperId = primary.SourceScraperId ?? secondary.SourceScraperId,
            Name = ChooseValue(primary.Name, secondary.Name),
            Aliases = MergeDistinctStrings(primary.Aliases, secondary.Aliases),
            Duration = primary.Duration ?? secondary.Duration,
            Date = ChooseValue(primary.Date, secondary.Date),
            Director = ChooseValue(primary.Director, secondary.Director),
            Details = ChooseValue(primary.Details, secondary.Details),
            Synopsis = ChooseValue(primary.Synopsis, secondary.Synopsis),
            Rating = primary.Rating ?? secondary.Rating,
            ImageUrl = ChooseValue(primary.ImageUrl, secondary.ImageUrl),
            Urls = MergeDistinctStrings(primary.Urls, secondary.Urls),
            StudioName = ChooseValue(primary.StudioName, secondary.StudioName),
            TagNames = ChoosePreferredNames(primary.TagNames, secondary.TagNames),
        };
    }

    private async Task ApplyAutoMetadataAsync(
        IServiceProvider services,
        DownloaderRequest request,
        DownloaderResult result,
        int importedEntityId,
        DownloaderMetadataApplyOptions options,
        Cove.Core.Interfaces.IJobProgress? progress,
        CancellationToken ct)
    {
        switch (request.Entity)
        {
            case DownloaderEntity.Video:
            {
                var metadata = result.InlineVideoMetadata;
                if (metadata == null)
                {
                    progress?.Report(0.97d, "Looking up downloaded video metadata...");
                    metadata = await BuildMergedVideoMetadataAsync(services, request, ct);
                }

                if (metadata != null)
                {
                    metadata = metadata with { Urls = ResolveDownloadedMetadataUrls(metadata.Urls, request) };
                    progress?.Report(0.99d, "Applying downloaded video metadata...");
                    var metadataApplyService = services.GetRequiredService<IVideoMetadataApplyService>();
                    await metadataApplyService.ApplyAsync(importedEntityId, metadata, options, ct);
                }

                break;
            }
            case DownloaderEntity.Image:
            {
                ScrapedImageDto? metadata = result.InlineImageMetadata;
                if (metadata == null)
                {
                    progress?.Report(0.97d, "Looking up downloaded image metadata...");
                    metadata = await BuildMergedImageMetadataAsync(services, request, ct);
                }

                if (metadata != null)
                {
                    metadata = metadata with { Urls = ResolveDownloadedMetadataUrls(metadata.Urls, request) };
                    progress?.Report(0.99d, "Applying downloaded image metadata...");
                    await ApplyImageMetadataAsync(importedEntityId, metadata, ct, options);
                }

                break;
            }
            case DownloaderEntity.Audio:
            {
                progress?.Report(0.97d, "Looking up downloaded audio metadata...");
                var metadata = await BuildMergedAudioMetadataAsync(services, request, ct);
                if (metadata != null)
                {
                    metadata = metadata with { Urls = ResolveDownloadedMetadataUrls(metadata.Urls, request) };
                    progress?.Report(0.99d, "Applying downloaded audio metadata...");
                    await ApplyAudioMetadataAsync(importedEntityId, metadata, ct, options);
                }

                break;
            }
            case DownloaderEntity.Text:
            {
                progress?.Report(0.97d, "Looking up downloaded text metadata...");
                var metadata = await BuildMergedTextMetadataAsync(services, request, ct);
                if (metadata != null)
                {
                    metadata = metadata with { Urls = ResolveDownloadedMetadataUrls(metadata.Urls, request) };
                    progress?.Report(0.99d, "Applying downloaded text metadata...");
                    await ApplyTextMetadataAsync(importedEntityId, metadata, ct, options);
                }

                break;
            }
        }
    }

    private async Task<ScrapedImageDto?> BuildMergedImageMetadataAsync(IServiceProvider services, DownloaderRequest request, CancellationToken ct)
    {
        var scraperService = services.GetRequiredService<ScraperService>();
        var primaryUrl = request.Url;
        var secondaryUrl = ResolveSourceMetadataUrl(request, primaryUrl);
        var primaryScrape = await ScrapeMetadataAsync(scraperService, primaryUrl, "image", ct);
        var primary = ConvertScrapeResultToImageMetadata(primaryScrape?.Result ?? [], primaryUrl, primaryScrape?.ScraperId);
        var secondary = secondaryUrl == null
            ? null
            : await ConvertScrapedImageMetadataAsync(scraperService, secondaryUrl, ct);
        return MergeImageMetadata(primary, secondary);
    }

    private async Task<ScrapedVideoDto?> BuildMergedVideoMetadataAsync(IServiceProvider services, DownloaderRequest request, CancellationToken ct)
    {
        var scraperService = services.GetRequiredService<ScraperService>();
        var primaryUrl = request.Url;
        var secondaryUrl = ResolveSourceMetadataUrl(request, primaryUrl);
        var primaryScrape = await ScrapeMetadataAsync(scraperService, primaryUrl, "video", ct);
        var primary = ConvertScrapeResultToVideoMetadata(primaryScrape?.Result ?? [], primaryUrl, primaryScrape?.ScraperId);
        var secondary = secondaryUrl == null
            ? null
            : await ConvertScrapedVideoMetadataAsync(scraperService, secondaryUrl, ct);
        return MergeVideoMetadata(primary, secondary);
    }

    private async Task<ScrapedAudioMetadata?> BuildMergedAudioMetadataAsync(IServiceProvider services, DownloaderRequest request, CancellationToken ct)
    {
        var scraperService = services.GetRequiredService<ScraperService>();
        var primaryUrl = request.Url;
        var secondaryUrl = ResolveSourceMetadataUrl(request, primaryUrl);
        var primaryScrape = await ScrapeMetadataAsync(scraperService, primaryUrl, "audio", ct);
        var primary = ConvertScrapeResultToAudioMetadata(primaryScrape?.Result ?? [], primaryUrl, primaryScrape?.ScraperId);
        var secondary = secondaryUrl == null
            ? null
            : await ConvertScrapedAudioMetadataAsync(scraperService, secondaryUrl, ct);
        return MergeAudioMetadata(primary, secondary);
    }

    private async Task<ScrapedTextMetadata?> BuildMergedTextMetadataAsync(IServiceProvider services, DownloaderRequest request, CancellationToken ct)
    {
        var scraperService = services.GetRequiredService<ScraperService>();
        var primaryUrl = request.Url;
        var secondaryUrl = ResolveSourceMetadataUrl(request, primaryUrl);
        var primaryScrape = await ScrapeMetadataAsync(scraperService, primaryUrl, "text", ct);
        var primary = ConvertScrapeResultToTextMetadata(primaryScrape?.Result ?? [], primaryUrl, primaryScrape?.ScraperId);
        var secondary = secondaryUrl == null
            ? null
            : await ConvertScrapedTextMetadataAsync(scraperService, secondaryUrl, ct);
        return MergeTextMetadata(primary, secondary);
    }

    private static async Task<ScrapedImageDto?> ConvertScrapedImageMetadataAsync(ScraperService scraperService, string url, CancellationToken ct)
    {
        var scrape = await ScrapeMetadataAsync(scraperService, url, "image", ct);
        return ConvertScrapeResultToImageMetadata(scrape?.Result ?? [], url, scrape?.ScraperId);
    }

    private static async Task<ScrapedVideoDto?> ConvertScrapedVideoMetadataAsync(ScraperService scraperService, string url, CancellationToken ct)
    {
        var scrape = await ScrapeMetadataAsync(scraperService, url, "video", ct);
        return ConvertScrapeResultToVideoMetadata(scrape?.Result ?? [], url, scrape?.ScraperId);
    }

    private static async Task<ScrapedAudioMetadata?> ConvertScrapedAudioMetadataAsync(ScraperService scraperService, string url, CancellationToken ct)
    {
        var scrape = await ScrapeMetadataAsync(scraperService, url, "audio", ct);
        return ConvertScrapeResultToAudioMetadata(scrape?.Result ?? [], url, scrape?.ScraperId);
    }

    private static async Task<ScrapedTextMetadata?> ConvertScrapedTextMetadataAsync(ScraperService scraperService, string url, CancellationToken ct)
    {
        var scrape = await ScrapeMetadataAsync(scraperService, url, "text", ct);
        return ConvertScrapeResultToTextMetadata(scrape?.Result ?? [], url, scrape?.ScraperId);
    }

    private async Task<ScrapedGroupDto?> BuildMergedGroupMetadataAsync(IServiceProvider services, IReadOnlyList<string> urls, CancellationToken ct)
    {
        var scraperService = services.GetRequiredService<ScraperService>();
        ScrapedGroupDto? merged = null;
        foreach (var url in urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var scrape = await ScrapeMetadataAsync(scraperService, url, "group", ct);
            var scraped = ConvertScrapeResultToGroupMetadata(scrape?.Result ?? [], url, scrape?.ScraperId);
            merged = MergeGroupMetadata(merged, scraped);
        }

        return merged;
    }

    private async Task ApplyBatchGroupMetadataAsync(
        IReadOnlyList<DownloaderBatchItemDto> items,
        DownloaderBatchFollowUpDto followUp,
        Cove.Core.Interfaces.IJobProgress? progress,
        ConcurrentQueue<string> issues,
        CancellationToken ct)
    {
        var autoApplyItems = items
            .Where(item => item.AutoApplyMetadata || followUp.AutoApplyMetadata)
            .Where(item => item.GroupIds is { Count: > 0 })
            .ToList();

        if (autoApplyItems.Count == 0)
            return;

        var groupCounts = autoApplyItems
            .SelectMany(item => item.GroupIds!.Select(group => group.GroupId))
            .Where(groupId => groupId > 0)
            .GroupBy(groupId => groupId)
            .Where(group => group.Count() > 1)
            .ToDictionary(group => group.Key, group => group.Count());

        if (groupCounts.Count == 0)
            return;

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var metadataApplyService = scope.ServiceProvider.GetRequiredService<IGroupMetadataApplyService>();
        var groups = await db.Groups
            .AsNoTracking()
            .Include(group => group.Urls)
            .Where(group => groupCounts.Keys.Contains(group.Id))
            .ToListAsync(ct);

        foreach (var group in groups)
        {
            var urls = group.Urls
                .Select(url => url.Url)
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (urls.Count == 0)
                continue;

            try
            {
                progress?.Report(0.965d, $"Looking up group metadata for {group.Name}...");
                var metadata = await BuildMergedGroupMetadataAsync(scope.ServiceProvider, urls, ct);
                if (metadata == null)
                    continue;

                var options = BuildBatchGroupMetadataApplyOptions(group.Id, autoApplyItems, followUp);
                progress?.Report(0.975d, $"Applying group metadata for {group.Name}...");
                await metadataApplyService.ApplyAsync(group.Id, metadata, options, ct: ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                issues.Enqueue($"{group.Name}: failed to apply group metadata ({ex.Message}).");
                logger.LogWarning(ex, "Batch group metadata apply failed for group {GroupId}", group.Id);
            }
        }
    }

    private static DownloaderMetadataApplyOptions BuildBatchGroupMetadataApplyOptions(int groupId, IReadOnlyList<DownloaderBatchItemDto> items, DownloaderBatchFollowUpDto followUp)
    {
        var groupItems = items.Where(item => item.GroupIds?.Any(group => group.GroupId == groupId) == true).ToList();
        return new DownloaderMetadataApplyOptions(
            CreateMissingTags: groupItems.Any(item => item.CreateMissingTags) || followUp.CreateMissingTags,
            CreateMissingPerformers: false,
            CreateMissingStudio: groupItems.Any(item => item.CreateMissingStudio) || followUp.CreateMissingStudio,
            MarkOrganized: groupItems.Any(item => item.MarkOrganized) || followUp.MarkOrganized);
    }

    internal async Task<bool> ApplyAudioMetadataAsync(int audioId, ScrapedAudioMetadata metadata, CancellationToken ct, DownloaderMetadataApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new DownloaderMetadataApplyOptions();

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var tagProvenanceService = scope.ServiceProvider.GetService<ITagProvenanceService>();
        var fieldProvenanceService = scope.ServiceProvider.GetService<IFieldProvenanceService>();
        var eventBus = scope.ServiceProvider.GetService<IEventBus>();

        var audio = await db.Audios
            .Include(item => item.Urls)
            .Include(item => item.AudioTags).ThenInclude(item => item.Tag)
            .Include(item => item.AudioPerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .FirstOrDefaultAsync(item => item.Id == audioId, ct);

        if (audio == null)
            return false;

        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(metadata.SourceScraperId);

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            audio.Title = metadata.Title.Trim();
            fieldProvenance["title"] = audio.Title;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Code))
        {
            audio.Code = metadata.Code.Trim();
            fieldProvenance["code"] = audio.Code;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Details))
        {
            audio.Details = metadata.Details.Trim();
            fieldProvenance["details"] = audio.Details;
        }

        if (ScrapedVideoDateParser.TryParse(metadata.Date, out var parsedDate))
        {
            audio.Date = parsedDate;
            audio.DatePrecision = DatePrecision.Day;
            fieldProvenance["date"] = parsedDate.ToString("yyyy-MM-dd");
        }

        if (options.MarkOrganized)
            audio.Organized = true;

        var urls = NormalizeNames(metadata.Urls);
        if (urls.Count > 0)
        {
            ApplyAudioUrls(audio, urls);
            fieldProvenance["urls"] = urls;
        }

        var tagNames = NormalizeNames(metadata.TagNames);
        if (tagNames.Count > 0)
        {
            await ApplyAudioTagsAsync(db, audio, tagNames, options.CreateMissingTags, tagProvenanceService, sourceKey, ct);
            fieldProvenance["tags"] = tagNames;
        }

        var performerNames = NormalizeNames(metadata.PerformerNames);
        if (performerNames.Count > 0)
        {
            await ApplyAudioPerformersAsync(db, audio, performerNames, options.CreateMissingPerformers, ct);
            fieldProvenance["performers"] = performerNames;
        }

        var studioName = NormalizeOptionalValue(metadata.StudioName);
        if (!string.IsNullOrWhiteSpace(studioName))
        {
            await ApplyStudioAsync(db, audio, studioName, options.CreateMissingStudio, ct);
            fieldProvenance["studio"] = studioName;
        }

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Audio, audio.Id, fieldProvenance, sourceKey, cancellationToken: ct);

        await db.SaveChangesAsync(ct);
        await RefreshAudioArraysAsync(db, audio, ct);
        eventBus?.Publish(new EntityEvent(EventType.AudioUpdated, "Audio", audio.Id));
        return true;
    }

    private async Task<bool> ApplyImageMetadataAsync(int imageId, ScrapedImageDto metadata, CancellationToken ct, DownloaderMetadataApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new DownloaderMetadataApplyOptions();

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var tagProvenanceService = scope.ServiceProvider.GetService<ITagProvenanceService>();
        var fieldProvenanceService = scope.ServiceProvider.GetService<IFieldProvenanceService>();
        var eventBus = scope.ServiceProvider.GetService<IEventBus>();

        var image = await db.Images
            .Include(item => item.Urls)
            .Include(item => item.ImageTags).ThenInclude(item => item.Tag)
            .Include(item => item.ImagePerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .FirstOrDefaultAsync(item => item.Id == imageId, ct);

        if (image == null)
            return false;

        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(metadata.SourceScraperId);

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            image.Title = metadata.Title.Trim();
            fieldProvenance["title"] = image.Title;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Details))
        {
            image.Details = metadata.Details.Trim();
            fieldProvenance["details"] = image.Details;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Photographer))
        {
            image.Photographer = metadata.Photographer.Trim();
            fieldProvenance["photographer"] = image.Photographer;
        }

        if (ScrapedVideoDateParser.TryParse(metadata.Date, out var parsedDate))
        {
            image.Date = parsedDate;
            image.DatePrecision = DatePrecision.Day;
            fieldProvenance["date"] = parsedDate.ToString("yyyy-MM-dd");
        }

        if (options.MarkOrganized)
            image.Organized = true;

        var urls = NormalizeNames(metadata.Urls);
        if (urls.Count > 0)
        {
            ApplyImageUrls(image, urls);
            fieldProvenance["urls"] = urls;
        }

        var tagNames = NormalizeNames(metadata.TagNames);
        if (tagNames.Count > 0)
        {
            await ApplyImageTagsAsync(db, image, tagNames, options.CreateMissingTags, tagProvenanceService, sourceKey, ct);
            fieldProvenance["tags"] = tagNames;
        }

        var performerNames = NormalizeNames(metadata.PerformerNames);
        if (performerNames.Count > 0)
        {
            await ApplyImagePerformersAsync(db, image, performerNames, options.CreateMissingPerformers, ct);
            fieldProvenance["performers"] = performerNames;
        }

        var studioName = NormalizeOptionalValue(metadata.StudioName);
        if (!string.IsNullOrWhiteSpace(studioName))
        {
            await ApplyStudioAsync(db, image, studioName, options.CreateMissingStudio, ct);
            fieldProvenance["studio"] = studioName;
        }

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Image, image.Id, fieldProvenance, sourceKey, cancellationToken: ct);

        await db.SaveChangesAsync(ct);
        eventBus?.Publish(new EntityEvent(EventType.ImageUpdated, "Image", image.Id));
        return true;
    }

    private async Task<bool> ApplyTextMetadataAsync(int textDocumentId, ScrapedTextMetadata metadata, CancellationToken ct, DownloaderMetadataApplyOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        options ??= new DownloaderMetadataApplyOptions();

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var tagProvenanceService = scope.ServiceProvider.GetService<ITagProvenanceService>();
        var fieldProvenanceService = scope.ServiceProvider.GetService<IFieldProvenanceService>();
        var eventBus = scope.ServiceProvider.GetService<IEventBus>();

        var textDocument = await db.TextDocuments
            .Include(item => item.Urls)
            .Include(item => item.TextTags).ThenInclude(item => item.Tag)
            .Include(item => item.TextPerformers).ThenInclude(item => item.Performer)
            .Include(item => item.Studio)
            .FirstOrDefaultAsync(item => item.Id == textDocumentId, ct);

        if (textDocument == null)
            return false;

        var fieldProvenance = new Dictionary<string, object?>();
        var sourceKey = BuildScraperSourceKey(metadata.SourceScraperId);

        if (!string.IsNullOrWhiteSpace(metadata.Title))
        {
            textDocument.Title = metadata.Title.Trim();
            fieldProvenance["title"] = textDocument.Title;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Code))
        {
            textDocument.Code = metadata.Code.Trim();
            fieldProvenance["code"] = textDocument.Code;
        }

        if (!string.IsNullOrWhiteSpace(metadata.Details))
        {
            textDocument.Details = metadata.Details.Trim();
            fieldProvenance["details"] = textDocument.Details;
        }

        if (ScrapedVideoDateParser.TryParse(metadata.Date, out var parsedDate))
        {
            textDocument.Date = parsedDate;
            textDocument.DatePrecision = DatePrecision.Day;
            fieldProvenance["date"] = parsedDate.ToString("yyyy-MM-dd");
        }

        if (options.MarkOrganized)
            textDocument.Organized = true;

        var urls = NormalizeNames(metadata.Urls);
        if (urls.Count > 0)
        {
            ApplyTextUrls(textDocument, urls);
            fieldProvenance["urls"] = urls;
        }

        var tagNames = NormalizeNames(metadata.TagNames);
        if (tagNames.Count > 0)
        {
            await ApplyTextTagsAsync(db, textDocument, tagNames, options.CreateMissingTags, tagProvenanceService, sourceKey, ct);
            fieldProvenance["tags"] = tagNames;
        }

        var performerNames = NormalizeNames(metadata.PerformerNames);
        if (performerNames.Count > 0)
        {
            await ApplyTextPerformersAsync(db, textDocument, performerNames, options.CreateMissingPerformers, ct);
            fieldProvenance["performers"] = performerNames;
        }

        var studioName = NormalizeOptionalValue(metadata.StudioName);
        if (!string.IsNullOrWhiteSpace(studioName))
        {
            await ApplyStudioAsync(db, textDocument, studioName, options.CreateMissingStudio, ct);
            fieldProvenance["studio"] = studioName;
        }

        if (fieldProvenance.Count > 0 && fieldProvenanceService != null)
            await fieldProvenanceService.RecordManyAsync(AffinityHostType.Text, textDocument.Id, fieldProvenance, sourceKey, cancellationToken: ct);

        await db.SaveChangesAsync(ct);
        await RefreshTextArraysAsync(db, textDocument, ct);
        eventBus?.Publish(new EntityEvent(EventType.TextUpdated, "Text", textDocument.Id));
        return true;
    }

    private static async Task<(string ScraperId, Dictionary<string, object> Result)?> ScrapeMetadataAsync(ScraperService scraperService, string? url, string entityType, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        return await scraperService.ScrapeUrlAutoAsync(url, entityType, ct);
    }

    private static string BuildScraperSourceKey(string? scraperId)
        => string.IsNullOrWhiteSpace(scraperId) ? "scraper" : $"scraper:{scraperId.Trim()}";

    private static string? ResolveSourceMetadataUrl(DownloaderRequest request, string primaryUrl)
    {
        return string.IsNullOrWhiteSpace(request.SourceUrl) || string.Equals(primaryUrl, request.SourceUrl, StringComparison.OrdinalIgnoreCase)
            ? null
            : request.SourceUrl;
    }

    private static List<string> ResolveDownloadedMetadataUrls(IEnumerable<string>? metadataUrls, DownloaderRequest request)
    {
        var sourceUrl = ResolveSourceMetadataUrl(request, request.Url);
        return sourceUrl == null
            ? MergeDistinctStrings(metadataUrls, request.Url)
            : MergeDistinctStrings(null, request.Url, sourceUrl);
    }

    private static string? GetScrapeResultString(IReadOnlyDictionary<string, object> result, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (entryKey, entryValue) in result)
            {
                if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entryValue is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();

                if (entryValue is JsonElement element)
                {
                    var elementValue = GetJsonElementString(element);
                    if (!string.IsNullOrWhiteSpace(elementValue))
                        return elementValue;
                }

                if (entryValue is not null && entryValue is not System.Collections.IEnumerable)
                    return entryValue.ToString();
            }
        }

        return null;
    }

    private static List<string> GetScrapeResultStringList(IReadOnlyDictionary<string, object> result, params string[] keys)
    {
        var values = new List<string>();
        foreach (var key in keys)
        {
            foreach (var (entryKey, entryValue) in result)
            {
                if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                switch (entryValue)
                {
                    case string s when !string.IsNullOrWhiteSpace(s):
                        foreach (var part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            values.Add(part);
                        break;
                    case JsonElement element:
                        AddJsonElementStringListValues(values, element);
                        break;
                    case System.Collections.IEnumerable list:
                        foreach (var item in list)
                        {
                            switch (item)
                            {
                                case string str when !string.IsNullOrWhiteSpace(str):
                                    values.Add(str.Trim());
                                    break;
                                case JsonElement element:
                                    AddJsonElementStringListValues(values, element);
                                    break;
                                case IDictionary<string, string> map:
                                    if (map.TryGetValue("Name", out var name) || map.TryGetValue("name", out name))
                                        values.Add(name);
                                    break;
                                case System.Collections.IDictionary genericMap:
                                    var nameValue = genericMap["Name"] ?? genericMap["name"] ?? genericMap["Title"] ?? genericMap["title"];
                                    if (nameValue is string nameStr && !string.IsNullOrWhiteSpace(nameStr))
                                        values.Add(nameStr.Trim());
                                    break;
                            }
                        }
                        break;
                }
            }
        }

        return NormalizeNames(values);
    }

    private static int? GetScrapeResultInt(IReadOnlyDictionary<string, object> result, params string[] keys)
    {
        foreach (var key in keys)
        {
            foreach (var (entryKey, entryValue) in result)
            {
                if (!string.Equals(entryKey, key, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (entryValue is JsonElement element)
                {
                    if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var intValue))
                        return intValue;
                    if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsedValue))
                        return parsedValue;
                }
                else if (entryValue is int intValue)
                {
                    return intValue;
                }
                else if (entryValue != null && int.TryParse(entryValue.ToString(), out var parsedValue))
                {
                    return parsedValue;
                }
            }
        }

        return null;
    }

    private static void AddJsonElementStringListValues(List<string> values, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                AddJsonElementStringListValues(values, item);
            return;
        }

        var value = GetJsonElementString(element);
        if (!string.IsNullOrWhiteSpace(value))
            values.Add(value);
    }

    private static string? GetJsonElementString(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return element.GetString()?.Trim();

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "Name", "name", "Title", "title" })
            {
                if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
                    return property.GetString()?.Trim();
            }
        }

        return element.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False
            ? element.ToString()
            : null;
    }

    private static bool HasMetadataContent(params string?[] values)
    {
        return values.Any(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? ChooseValue(string? primary, string? secondary)
    {
        return string.IsNullOrWhiteSpace(primary) ? NormalizeOptionalValue(secondary) : NormalizeOptionalValue(primary);
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static List<string> MergeDistinctStrings(IEnumerable<string>? first, IEnumerable<string>? second)
    {
        return NormalizeNames((first ?? []).Concat(second ?? []));
    }

    private static List<string> ChoosePreferredNames(IEnumerable<string>? primary, IEnumerable<string>? secondary)
    {
        var preferred = NormalizeNames(primary ?? []);
        return preferred.Count > 0 ? preferred : NormalizeNames(secondary ?? []);
    }

    private static List<string> MergeDistinctStrings(IEnumerable<string>? first, params string?[] extraValues)
    {
        return NormalizeNames((first ?? []).Concat(extraValues.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)));
    }

    private static void ApplyAudioUrls(Audio audio, IReadOnlyList<string> urls)
    {
        var existing = audio.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (existing.Add(url))
                audio.Urls.Add(new AudioUrl { AudioId = audio.Id, Url = url });
        }
    }

    private static void ApplyImageUrls(Image image, IReadOnlyList<string> urls)
    {
        var existing = image.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (existing.Add(url))
                image.Urls.Add(new ImageUrl { ImageId = image.Id, Url = url });
        }
    }

    private static void ApplyTextUrls(TextDocument textDocument, IReadOnlyList<string> urls)
    {
        var existing = textDocument.Urls.Select(item => item.Url).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var url in urls)
        {
            if (existing.Add(url))
                textDocument.Urls.Add(new TextUrl { TextDocumentId = textDocument.Id, Url = url });
        }
    }

    private static async Task ApplyAudioTagsAsync(CoveContext db, Audio audio, IReadOnlyList<string> tagNames, bool createMissing, ITagProvenanceService? tagProvenanceService, string sourceKey, CancellationToken ct)
    {
        var tagLookup = await LoadTagsByNameAsync(db, tagNames, createMissing, ct);
        var existing = audio.AudioTags
            .Where(item => item.Tag != null)
            .Select(item => item.Tag!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in tagNames)
        {
            if (!tagLookup.TryGetValue(tagName, out var tag))
                continue;

            if (tagProvenanceService != null)
                await tagProvenanceService.RecordAsync(AffinityHostType.Audio, audio.Id, tag, sourceKey, cancellationToken: ct);

            if (!existing.Add(tag.Name))
                continue;

            audio.AudioTags.Add(new AudioTag { Audio = audio, Tag = tag });
        }
    }

    private static async Task ApplyImageTagsAsync(CoveContext db, Image image, IReadOnlyList<string> tagNames, bool createMissing, ITagProvenanceService? tagProvenanceService, string sourceKey, CancellationToken ct)
    {
        var tagLookup = await LoadTagsByNameAsync(db, tagNames, createMissing, ct);
        var existing = image.ImageTags
            .Where(item => item.Tag != null)
            .Select(item => item.Tag!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in tagNames)
        {
            if (!tagLookup.TryGetValue(tagName, out var tag))
                continue;

            if (tagProvenanceService != null)
                await tagProvenanceService.RecordAsync(AffinityHostType.Image, image.Id, tag, sourceKey, cancellationToken: ct);

            if (!existing.Add(tag.Name))
                continue;

            image.ImageTags.Add(new ImageTag { Image = image, Tag = tag });
        }
    }

    private static async Task ApplyTextTagsAsync(CoveContext db, TextDocument textDocument, IReadOnlyList<string> tagNames, bool createMissing, ITagProvenanceService? tagProvenanceService, string sourceKey, CancellationToken ct)
    {
        var tagLookup = await LoadTagsByNameAsync(db, tagNames, createMissing, ct);
        var existing = textDocument.TextTags
            .Where(item => item.Tag != null)
            .Select(item => item.Tag!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var tagName in tagNames)
        {
            if (!tagLookup.TryGetValue(tagName, out var tag))
                continue;

            if (tagProvenanceService != null)
                await tagProvenanceService.RecordAsync(AffinityHostType.Text, textDocument.Id, tag, sourceKey, cancellationToken: ct);

            if (!existing.Add(tag.Name))
                continue;

            textDocument.TextTags.Add(new TextTag { TextDocument = textDocument, Tag = tag });
        }
    }

    private static async Task ApplyAudioPerformersAsync(CoveContext db, Audio audio, IReadOnlyList<string> performerNames, bool createMissing, CancellationToken ct)
    {
        var performerLookup = await LoadPerformersByNameAsync(db, performerNames, createMissing, ct);
        var existing = audio.AudioPerformers
            .Where(item => item.Performer != null)
            .Select(item => item.Performer!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var performerName in performerNames)
        {
            if (!performerLookup.TryGetValue(performerName, out var performer))
                continue;

            if (existing.Add(performer.Name))
                audio.AudioPerformers.Add(new AudioPerformer { Audio = audio, Performer = performer });
        }
    }

    private static async Task ApplyImagePerformersAsync(CoveContext db, Image image, IReadOnlyList<string> performerNames, bool createMissing, CancellationToken ct)
    {
        var performerLookup = await LoadPerformersByNameAsync(db, performerNames, createMissing, ct);
        var existing = image.ImagePerformers
            .Where(item => item.Performer != null)
            .Select(item => item.Performer!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var performerName in performerNames)
        {
            if (!performerLookup.TryGetValue(performerName, out var performer))
                continue;

            if (existing.Add(performer.Name))
                image.ImagePerformers.Add(new ImagePerformer { Image = image, Performer = performer });
        }
    }

    private static async Task ApplyTextPerformersAsync(CoveContext db, TextDocument textDocument, IReadOnlyList<string> performerNames, bool createMissing, CancellationToken ct)
    {
        var performerLookup = await LoadPerformersByNameAsync(db, performerNames, createMissing, ct);
        var existing = textDocument.TextPerformers
            .Where(item => item.Performer != null)
            .Select(item => item.Performer!.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var performerName in performerNames)
        {
            if (!performerLookup.TryGetValue(performerName, out var performer))
                continue;

            if (existing.Add(performer.Name))
                textDocument.TextPerformers.Add(new TextPerformer { TextDocument = textDocument, Performer = performer });
        }
    }

    private static async Task ApplyStudioAsync(CoveContext db, Audio audio, string studioName, bool createMissing, CancellationToken ct)
    {
        var studio = await FindOrCreateStudioAsync(db, studioName, createMissing, ct);
        if (studio == null)
            return;

        audio.Studio = studio;
        audio.StudioId = studio.Id == 0 ? null : studio.Id;
    }

    private static async Task ApplyStudioAsync(CoveContext db, Image image, string studioName, bool createMissing, CancellationToken ct)
    {
        var studio = await FindOrCreateStudioAsync(db, studioName, createMissing, ct);
        if (studio == null)
            return;

        image.Studio = studio;
        image.StudioId = studio.Id == 0 ? null : studio.Id;
    }

    private static async Task ApplyStudioAsync(CoveContext db, TextDocument textDocument, string studioName, bool createMissing, CancellationToken ct)
    {
        var studio = await FindOrCreateStudioAsync(db, studioName, createMissing, ct);
        if (studio == null)
            return;

        textDocument.Studio = studio;
        textDocument.StudioId = studio.Id == 0 ? null : studio.Id;
    }

    private static async Task<Dictionary<string, Tag>> LoadTagsByNameAsync(CoveContext db, IReadOnlyList<string> tagNames, bool createMissing, CancellationToken ct)
    {
        var tagLookup = await RelationNameResolver.ResolveTagsAsync(db, tagNames, ct);

        foreach (var tagName in tagNames)
        {
            if (tagLookup.ContainsKey(tagName))
                continue;

            if (!createMissing)
                continue;

            var tag = new Tag { Name = tagName };
            db.Tags.Add(tag);
            tagLookup[tagName] = tag;
        }

        return tagLookup;
    }

    private static async Task<Dictionary<string, Performer>> LoadPerformersByNameAsync(CoveContext db, IReadOnlyList<string> performerNames, bool createMissing, CancellationToken ct)
    {
        var performerLookup = await RelationNameResolver.ResolvePerformersAsync(db, performerNames, ct);

        foreach (var performerName in performerNames)
        {
            if (performerLookup.ContainsKey(performerName))
                continue;

            if (!createMissing)
                continue;

            var performer = new Performer { Name = performerName };
            db.Performers.Add(performer);
            performerLookup[performerName] = performer;
        }

        return performerLookup;
    }

    private static async Task<Studio?> FindOrCreateStudioAsync(CoveContext db, string studioName, bool createMissing, CancellationToken ct)
    {
        var normalizedStudioName = studioName.Trim();
        var studio = await RelationNameResolver.ResolveStudioAsync(db, normalizedStudioName, ct);
        if (studio == null && !createMissing)
            return null;

        studio ??= new Studio { Name = normalizedStudioName };

        if (studio.Id == 0)
            db.Studios.Add(studio);

        return studio;
    }

    private static async Task RefreshAudioArraysAsync(CoveContext db, Audio audio, CancellationToken ct)
    {
        var nextTagIds = audio.AudioTags
            .Select(item => item.TagId != 0 ? item.TagId : item.Tag?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var nextPerformerIds = audio.AudioPerformers
            .Select(item => item.PerformerId != 0 ? item.PerformerId : item.Performer?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (audio.TagIds.SequenceEqual(nextTagIds) && audio.PerformerIds.SequenceEqual(nextPerformerIds))
            return;

        audio.TagIds = nextTagIds;
        audio.PerformerIds = nextPerformerIds;
        await db.SaveChangesAsync(ct);
    }

    private static async Task RefreshTextArraysAsync(CoveContext db, TextDocument textDocument, CancellationToken ct)
    {
        var nextTagIds = textDocument.TextTags
            .Select(item => item.TagId != 0 ? item.TagId : item.Tag?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var nextPerformerIds = textDocument.TextPerformers
            .Select(item => item.PerformerId != 0 ? item.PerformerId : item.Performer?.Id ?? 0)
            .Where(id => id > 0)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();

        if (textDocument.TagIds.SequenceEqual(nextTagIds) && textDocument.PerformerIds.SequenceEqual(nextPerformerIds))
            return;

        textDocument.TagIds = nextTagIds;
        textDocument.PerformerIds = nextPerformerIds;
        await db.SaveChangesAsync(ct);
    }

    private static List<string> NormalizeNames(IEnumerable<string> values)
    {
        return values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => NormalizeBracketWrappedValue(value.Trim()))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeBracketWrappedValue(string value)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']')
            return value[1..^1].Trim();

        return value;
    }

    private async Task<IDisposable> AcquireDownloadSlotAsync(Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        var slots = GetDownloadSemaphore();
        if (!await slots.WaitAsync(0, ct))
        {
            progress?.Report(0.01d, $"Waiting for a download slot ({ResolveMaxConcurrentDownloads()} max concurrent downloads)...");
            await slots.WaitAsync(ct);
        }

        return new DownloadSlotLease(slots);
    }

    private SemaphoreSlim GetDownloadSemaphore()
    {
        var desiredCapacity = ResolveMaxConcurrentDownloads();

        lock (_downloadSlotLock)
        {
            if (_downloadSlots == null)
            {
                _downloadSlots = new SemaphoreSlim(desiredCapacity, desiredCapacity);
                _downloadSlotCapacity = desiredCapacity;
                return _downloadSlots;
            }

            if (_downloadSlotCapacity != desiredCapacity && _downloadSlots.CurrentCount == _downloadSlotCapacity)
            {
                _downloadSlots.Dispose();
                _downloadSlots = new SemaphoreSlim(desiredCapacity, desiredCapacity);
                _downloadSlotCapacity = desiredCapacity;
            }

            return _downloadSlots;
        }
    }

    private int ResolveMaxConcurrentDownloads()
    {
        var configured = config.MaxConcurrentDownloads;
        return Math.Clamp(configured <= 0 ? 3 : configured, 1, 16);
    }

    private static DownloaderDescriptorDto ToDto(DownloaderDescriptor descriptor)
    {
        return new DownloaderDescriptorDto(
            descriptor.Id,
            descriptor.Name,
            descriptor.SupportedEntity.ToString(),
            descriptor.SupportedUrlPatterns.ToList(),
            GetCapabilityNames(descriptor.Capabilities));
    }

    private static DownloaderMatchDto ToDto(DownloaderDescriptor descriptor, DownloaderUrlMatch match)
    {
        return new DownloaderMatchDto(
            descriptor.Id,
            descriptor.Name,
            descriptor.SupportedEntity.ToString(),
            match.NormalizedUrl,
            match.Label,
            match.QualityOptions?.Select(option => new DownloaderQualityOptionDto(option.Id, option.Label, option.Description)).ToList() ?? [],
            match.SourceUrl);
    }

    private static List<string> GetCapabilityNames(DownloaderCapabilities capabilities)
    {
        return Enum.GetValues<DownloaderCapabilities>()
            .Where(capability => capability != DownloaderCapabilities.None && capabilities.HasFlag(capability))
            .Select(capability => capability.ToString())
            .ToList();
    }

    private static string SanitizePathSegment(string value)
    {
        foreach (var invalidChar in Path.GetInvalidFileNameChars())
            value = value.Replace(invalidChar, '_');

        return value.Replace(Path.DirectorySeparatorChar, '_').Replace(Path.AltDirectorySeparatorChar, '_');
    }

    public async Task<string?> GetDuplicateDownloadReasonAsync(DownloaderEntity entity, int? entityId, string url, CancellationToken ct)
    {
        return await FindDuplicateDownloadReasonAsync(entity, entityId, url, ct);
    }

    private async Task EnsureDownloadAllowedAsync(DownloaderRequest request, int? entityId, CancellationToken ct)
    {
        var duplicateReason = await GetDuplicateDownloadReasonAsync(request.Entity, entityId, request.Url, ct);
        if (!string.IsNullOrWhiteSpace(duplicateReason))
            throw new InvalidOperationException(duplicateReason);
    }

    private async Task<string?> FindDuplicateDownloadReasonAsync(DownloaderEntity entity, int? entityId, string url, CancellationToken ct)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<CoveContext>();
        if (db == null)
            return null;

        if (entityId.HasValue)
        {
            var currentHasFiles = entity switch
            {
                DownloaderEntity.Video => await db.VideoFiles.AnyAsync(item => item.VideoId == entityId.Value, ct),
                DownloaderEntity.Image => await db.ImageFiles.AnyAsync(item => item.ImageId == entityId.Value, ct),
                DownloaderEntity.Gallery => await db.GalleryFiles.AnyAsync(item => item.GalleryId == entityId.Value, ct),
                DownloaderEntity.Audio => await db.AudioFiles.AnyAsync(item => item.AudioId == entityId.Value, ct),
                DownloaderEntity.Text => await db.TextFiles.AnyAsync(item => item.TextDocumentId == entityId.Value, ct),
                _ => false,
            };

            if (currentHasFiles)
                return $"{entity} {entityId.Value} already has downloaded files.";
        }

        var normalizedUrl = NormalizeUrlForLookup(url);
        if (string.IsNullOrWhiteSpace(normalizedUrl))
            return null;

        var duplicateLabel = entity switch
        {
            DownloaderEntity.Video => await FindDuplicateVideoLabelAsync(db, entityId, normalizedUrl, ct),
            DownloaderEntity.Image => await FindDuplicateImageLabelAsync(db, entityId, normalizedUrl, ct),
            DownloaderEntity.Gallery => await FindDuplicateGalleryLabelAsync(db, entityId, normalizedUrl, ct),
            DownloaderEntity.Audio => await FindDuplicateAudioLabelAsync(db, entityId, normalizedUrl, ct),
            DownloaderEntity.Text => await FindDuplicateTextLabelAsync(db, entityId, normalizedUrl, ct),
            _ => null,
        };

        return string.IsNullOrWhiteSpace(duplicateLabel)
            ? null
            : $"This URL is already downloaded for {duplicateLabel}.";
    }

    private async Task<Dictionary<DownloaderEntity, Dictionary<string, List<ExistingDownloadTarget>>>> LoadExistingDownloadUrlLookupAsync(
        IReadOnlyCollection<DownloaderEntity> entities,
        CancellationToken ct)
    {
        var result = new Dictionary<DownloaderEntity, Dictionary<string, List<ExistingDownloadTarget>>>();
        if (entities.Count == 0)
            return result;

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<CoveContext>();
        if (db == null)
            return result;

        if (entities.Contains(DownloaderEntity.Video))
        {
            var rows = await db.Set<Cove.Core.Entities.VideoUrl>()
                .AsNoTracking()
                .Select(item => new { item.VideoId, item.Url, item.Video!.Title })
                .ToListAsync(ct);
            result[DownloaderEntity.Video] = BuildExistingUrlLookup(rows.Select(item => new ExistingUrlRow(item.VideoId, item.Url, item.Title ?? $"Video {item.VideoId}")));
        }

        if (entities.Contains(DownloaderEntity.Image))
        {
            var rows = await db.Set<Cove.Core.Entities.ImageUrl>()
                .AsNoTracking()
                .Select(item => new { item.ImageId, item.Url, item.Image!.Title })
                .ToListAsync(ct);
            result[DownloaderEntity.Image] = BuildExistingUrlLookup(rows.Select(item => new ExistingUrlRow(item.ImageId, item.Url, item.Title ?? $"Image {item.ImageId}")));
        }

        if (entities.Contains(DownloaderEntity.Gallery))
        {
            var rows = await db.Set<Cove.Core.Entities.GalleryUrl>()
                .AsNoTracking()
                .Select(item => new { item.GalleryId, item.Url, item.Gallery!.Title })
                .ToListAsync(ct);
            result[DownloaderEntity.Gallery] = BuildExistingUrlLookup(rows.Select(item => new ExistingUrlRow(item.GalleryId, item.Url, item.Title ?? $"Gallery {item.GalleryId}")));
        }

        if (entities.Contains(DownloaderEntity.Audio))
        {
            var rows = await db.Set<Cove.Core.Entities.AudioUrl>()
                .AsNoTracking()
                .Select(item => new { item.AudioId, item.Url, item.Audio!.Title })
                .ToListAsync(ct);
            result[DownloaderEntity.Audio] = BuildExistingUrlLookup(rows.Select(item => new ExistingUrlRow(item.AudioId, item.Url, item.Title ?? $"Audio {item.AudioId}")));
        }

        if (entities.Contains(DownloaderEntity.Text))
        {
            var rows = await db.Set<Cove.Core.Entities.TextUrl>()
                .AsNoTracking()
                .Select(item => new { item.TextDocumentId, item.Url, item.TextDocument!.Title })
                .ToListAsync(ct);
            result[DownloaderEntity.Text] = BuildExistingUrlLookup(rows.Select(item => new ExistingUrlRow(item.TextDocumentId, item.Url, item.Title ?? $"Text {item.TextDocumentId}")));
        }

        return result;
    }

    private async Task<Dictionary<DownloaderEntity, HashSet<int>>> LoadDownloadedEntityIdLookupAsync(
        IReadOnlyCollection<DownloaderEntity> entities,
        CancellationToken ct)
    {
        var result = new Dictionary<DownloaderEntity, HashSet<int>>();
        if (entities.Count == 0)
            return result;

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetService<CoveContext>();
        if (db == null)
            return result;

        if (entities.Contains(DownloaderEntity.Video))
        {
            var ids = await db.VideoFiles
                .AsNoTracking()
                .Where(item => item.VideoId != null)
                .Select(item => item.VideoId!.Value)
                .Distinct()
                .ToListAsync(ct);
            result[DownloaderEntity.Video] = ids.ToHashSet();
        }

        if (entities.Contains(DownloaderEntity.Image))
        {
            var ids = await db.ImageFiles
                .AsNoTracking()
                .Where(item => item.ImageId != null)
                .Select(item => item.ImageId!.Value)
                .Distinct()
                .ToListAsync(ct);
            result[DownloaderEntity.Image] = ids.ToHashSet();
        }

        if (entities.Contains(DownloaderEntity.Gallery))
        {
            var ids = await db.GalleryFiles
                .AsNoTracking()
                .Where(item => item.GalleryId != null)
                .Select(item => item.GalleryId!.Value)
                .Distinct()
                .ToListAsync(ct);
            result[DownloaderEntity.Gallery] = ids.ToHashSet();
        }

        if (entities.Contains(DownloaderEntity.Audio))
        {
            var ids = await db.AudioFiles
                .AsNoTracking()
                .Where(item => item.AudioId != null)
                .Select(item => item.AudioId!.Value)
                .Distinct()
                .ToListAsync(ct);
            result[DownloaderEntity.Audio] = ids.ToHashSet();
        }

        if (entities.Contains(DownloaderEntity.Text))
        {
            var ids = await db.TextFiles
                .AsNoTracking()
                .Where(item => item.TextDocumentId != null)
                .Select(item => item.TextDocumentId!.Value)
                .Distinct()
                .ToListAsync(ct);
            result[DownloaderEntity.Text] = ids.ToHashSet();
        }

        return result;
    }

    private static Dictionary<string, List<ExistingDownloadTarget>> BuildExistingUrlLookup(IEnumerable<ExistingUrlRow> rows)
    {
        var lookup = new Dictionary<string, List<ExistingDownloadTarget>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var normalizedUrl = NormalizeUrlForLookup(row.Url);
            if (string.IsNullOrWhiteSpace(normalizedUrl))
                continue;

            if (!lookup.TryGetValue(normalizedUrl, out var targets))
            {
                targets = [];
                lookup[normalizedUrl] = targets;
            }

            targets.Add(new ExistingDownloadTarget(row.EntityId, row.Label));
        }

        return lookup;
    }

    private string MoveDownloadedFileToLibrary(DownloaderResult result, DownloaderEntity entity, string? downloaderId, string? sourceUrl)
    {
        var sourcePath = result.LocalPath;
        var (destinationRoot, useEntitySubdirectory) = ResolveLibraryRoot(entity, downloaderId, sourceUrl);
        var destinationDirectory = useEntitySubdirectory
            ? Path.Combine(destinationRoot, "_downloads", GetEntityDownloadFolder(entity))
            : destinationRoot;
        Directory.CreateDirectory(destinationDirectory);

        var preferredFileName = string.IsNullOrWhiteSpace(result.OriginalFilename)
            ? Path.GetFileName(sourcePath)
            : result.OriginalFilename;
        var sanitizedFileName = SanitizePathSegment(string.IsNullOrWhiteSpace(preferredFileName) ? Path.GetFileName(sourcePath) : preferredFileName);
        if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedFileName)))
            sanitizedFileName += Path.GetExtension(sourcePath);

        string destinationPath;
        lock (_libraryMoveLock)
        {
            destinationPath = GetUniquePath(destinationDirectory, sanitizedFileName);
            File.Move(sourcePath, destinationPath);
        }

        TryDeleteParentDirectory(sourcePath);
        return destinationPath;
    }

    private (string Root, bool UseEntitySubdirectory) ResolveLibraryRoot(DownloaderEntity entity, string? downloaderId = null, string? sourceUrl = null)
    {
        var overrideRoot = ResolveDownloaderOverrideRoot(downloaderId, sourceUrl);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
        {
            Directory.CreateDirectory(overrideRoot);
            return (Path.GetFullPath(overrideRoot), false);
        }

        var root = entity switch
        {
            DownloaderEntity.Video => config.CovePaths.FirstOrDefault(path => !path.ExcludeVideo)?.Path,
            DownloaderEntity.Image => config.CovePaths.FirstOrDefault(path => !path.ExcludeImage)?.Path,
            DownloaderEntity.Gallery => config.CovePaths.FirstOrDefault(path => !path.ExcludeImage)?.Path,
            DownloaderEntity.Audio => config.CovePaths.FirstOrDefault(path => !path.ExcludeAudio)?.Path,
            DownloaderEntity.Text => config.CovePaths.FirstOrDefault(path => !path.ExcludeText)?.Path,
            _ => null,
        };

        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException($"No Cove library path is configured for {entity} downloads");

        Directory.CreateDirectory(root);
        return (Path.GetFullPath(root), true);
    }

    private string? ResolveDownloaderOverrideRoot(string? downloaderId, string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(downloaderId))
            return null;

        var matchingOverrides = config.DownloaderPathOverrides
            .Where(overridePath => !string.IsNullOrWhiteSpace(overridePath.Path))
            .Where(overridePath => string.Equals(overridePath.DownloaderId, downloaderId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matchingOverrides.Count == 0)
            return null;

        var normalizedSite = NormalizeOverrideSite(sourceUrl);
        if (!string.IsNullOrWhiteSpace(normalizedSite))
        {
            var siteOverride = matchingOverrides.FirstOrDefault(overridePath =>
                string.Equals(NormalizeOverrideSite(overridePath.Site), normalizedSite, StringComparison.OrdinalIgnoreCase));
            if (siteOverride != null)
                return siteOverride.Path;
        }

        return matchingOverrides.FirstOrDefault(overridePath => string.IsNullOrWhiteSpace(overridePath.Site))?.Path;
    }

    internal static string NormalizeUrlForLookup(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return trimmed.TrimEnd('/').ToLowerInvariant();

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..]
            : uri.Host;
        host = host.ToLowerInvariant();

        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            path = "/";

        return string.Concat(host, port, path.ToLowerInvariant(), NormalizeQueryForLookup(uri.Query));
    }

    private static string NormalizeQueryForLookup(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query == "?")
            return string.Empty;

        var parts = query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseQueryPart)
            .Where(part => !IsTrackingQueryParameter(part.Key))
            .OrderBy(part => part.Key, StringComparer.Ordinal)
            .ThenBy(part => part.Value, StringComparer.Ordinal)
            .Select(part => string.IsNullOrEmpty(part.Value) ? part.Key : $"{part.Key}={part.Value}")
            .ToList();

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }

    private static (string Key, string Value) ParseQueryPart(string part)
    {
        var separator = part.IndexOf('=');
        var rawKey = separator < 0 ? part : part[..separator];
        var rawValue = separator < 0 ? string.Empty : part[(separator + 1)..];
        return (NormalizeQueryToken(rawKey), NormalizeQueryToken(rawValue));
    }

    private static string NormalizeQueryToken(string value)
    {
        var plusAsSpace = value.Replace('+', ' ');
        try
        {
            return Uri.UnescapeDataString(plusAsSpace).Trim().ToLowerInvariant();
        }
        catch
        {
            return plusAsSpace.Trim().ToLowerInvariant();
        }
    }

    private static bool IsTrackingQueryParameter(string key)
        => key.StartsWith("utm_", StringComparison.Ordinal)
            || key is "fbclid" or "gclid" or "dclid" or "gbraid" or "wbraid" or "msclkid" or "mc_cid" or "mc_eid" or "igshid";

    private string? TryQueueFollowUpGenerateJob(GenerateOptionsDto? generate, IEnumerable<string> importedPaths, Cove.Core.Interfaces.IJobProgress? progress)
    {
        if (generate == null || !HasGenerateFollowUp(generate))
            return null;

        var paths = importedPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(FilesystemPaths.PathComparer)
            .ToList();

        if (paths.Count == 0)
            return null;

        if (generate.Segments)
            logger.LogInformation("Batch download generate follow-up does not currently support segment generation; skipping segment option.");

        using var scope = serviceScopeFactory.CreateScope();
        var scanService = scope.ServiceProvider.GetRequiredService<IScanService>();
        progress?.Report(0.98d, "Queueing follow-up generation scan...");

        return scanService.StartScan(new ScanOperationOptions
        {
            Paths = paths,
            IncludeUnchangedFilesInAssetGeneration = true,
            GenerateCovers = generate.Thumbnails,
            GeneratePreviews = generate.Previews,
            GenerateSprites = generate.Sprites,
            GeneratePhashes = generate.Phashes,
            GenerateMd5 = generate.Md5,
            GenerateImageThumbnails = generate.ImageThumbnails,
            GenerateImagePhashes = generate.ImagePhashes,
            GenerateAudioPhashes = generate.AudioPhashes,
            GenerateTextPhashes = generate.TextPhashes,
            Rescan = generate.Overwrite,
        });
    }

    private static bool HasGenerateFollowUp(GenerateOptionsDto generate)
    {
        return generate.Thumbnails
            || generate.Previews
            || generate.Sprites
            || generate.Phashes
            || generate.Md5
            || generate.ImageThumbnails
                || generate.ImagePhashes
                || generate.AudioPhashes
                || generate.TextPhashes;
    }

    private static bool IsDuplicateDownloadMessage(string message)
    {
        return message.Contains("already downloaded", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already has downloaded files", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBatchSkipMessage(string message)
    {
        return message.Contains("No compatible", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is missing a URL", StringComparison.OrdinalIgnoreCase)
            || message.Contains("is missing an entity id", StringComparison.OrdinalIgnoreCase)
            || message.Contains("unsupported entity type", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already queued elsewhere in this batch", StringComparison.OrdinalIgnoreCase)
            || message.Contains("do not support creating new", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildBatchProgressMessage(int completed, int total, string label)
    {
        return $"Processed {completed}/{total}: {label}";
    }

    private static string BuildBatchCompletionMessage(DownloaderBatchExecutionSummary summary)
    {
        var parts = new List<string>
        {
            $"Downloaded {summary.SucceededCount} of {summary.TotalCount} item{(summary.TotalCount == 1 ? string.Empty : "s")}."
        };

        if (summary.SkippedCount > 0)
            parts.Add($"Skipped {summary.SkippedCount}.");

        if (summary.FailedCount > 0)
            parts.Add($"Failed {summary.FailedCount}.");

        if (!string.IsNullOrWhiteSpace(summary.FollowUpJobId))
            parts.Add($"Queued follow-up generate job {summary.FollowUpJobId}.");

        if (summary.Issues.Count > 0)
        {
            parts.Add(string.Join(' ', summary.Issues.Take(2)));
            if (summary.Issues.Count > 2)
                parts.Add($"+{summary.Issues.Count - 2} more issue(s).");
        }

        return string.Join(' ', parts);
    }

    private async Task<ResolvedBatchItem> ResolveBatchItemAsync(
        DownloaderBatchItemDto item,
        int index,
        DownloaderBatchFollowUpDto followUp,
        ConcurrentDictionary<string, byte> reservedDownloads,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(item.Url))
            throw new InvalidOperationException($"Batch download item {index + 1} is missing a URL.");

        if (!Enum.TryParse<DownloaderEntity>(item.Entity, true, out var entity))
            throw new InvalidOperationException($"Batch download item {index + 1} has an unsupported entity type '{item.Entity}'.");

        var normalizedUrl = item.Url.Trim();
        var label = BuildBatchItemLabel(item, index);
        var matched = await ResolveBatchMatchAsync(item, entity, normalizedUrl, ct);
        var effectiveUrl = matched.NormalizedUrl;

        if (!followUp.AllowDuplicateDownloads)
        {
            var normalizedEffectiveUrl = NormalizeUrlForLookup(effectiveUrl);
            var reservationKey = string.Empty;
            var reservationAdded = false;

            if (!string.IsNullOrWhiteSpace(normalizedEffectiveUrl))
            {
                reservationKey = $"{entity}:{normalizedEffectiveUrl}";
                if (!reservedDownloads.TryAdd(reservationKey, 0))
                    throw new InvalidOperationException("This URL is already queued elsewhere in this batch.");

                reservationAdded = true;
            }

            try
            {
                var duplicateReason = await GetDuplicateDownloadReasonAsync(entity, item.EntityId, effectiveUrl, ct);
                if (!string.IsNullOrWhiteSpace(duplicateReason))
                    throw new InvalidOperationException(duplicateReason);
            }
            catch
            {
                if (reservationAdded)
                    reservedDownloads.TryRemove(reservationKey, out _);

                throw;
            }
        }

        var entityId = item.EntityId;
        var resolvedLabel = string.IsNullOrWhiteSpace(matched.Label) ? label : matched.Label.Trim();
        if (!entityId.HasValue && item.CreateEntityIfMissing)
            entityId = await CreatePlaceholderEntityAsync(entity, effectiveUrl, ResolvePlaceholderTitle(item, effectiveUrl, resolvedLabel), ct);

        if (!entityId.HasValue && entity is DownloaderEntity.Video or DownloaderEntity.Image or DownloaderEntity.Gallery)
            throw new InvalidOperationException($"Batch download item {index + 1} is missing an entity id.");

        return new ResolvedBatchItem(
            new DownloaderRequest(matched.DownloaderId, effectiveUrl, entity, BuildDownloaderPermissions(effectiveUrl), matched.QualityId, matched.SourceUrl ?? item.SourceUrl),
            entityId,
            resolvedLabel,
            item.AutoApplyMetadata || followUp.AutoApplyMetadata,
            BuildMetadataApplyOptions(item, followUp));
    }

    private static DownloaderMetadataApplyOptions BuildMetadataApplyOptions(DownloaderBatchItemDto item, DownloaderBatchFollowUpDto followUp)
    {
        return new DownloaderMetadataApplyOptions(
            item.CreateMissingTags || followUp.CreateMissingTags,
            item.CreateMissingPerformers || followUp.CreateMissingPerformers,
            item.CreateMissingStudio || followUp.CreateMissingStudio,
            item.MarkOrganized || followUp.MarkOrganized);
    }

    private async Task<ResolvedBatchMatch> ResolveBatchMatchAsync(DownloaderBatchItemDto item, DownloaderEntity entity, string url, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(item.DownloaderId))
            return new ResolvedBatchMatch(item.DownloaderId.Trim(), url, item.QualityId, SourceUrl: item.SourceUrl);

        var selectedMatch = (await MatchUrlAsync(url, ct))
            .FirstOrDefault(match => string.Equals(match.SupportedEntity, entity.ToString(), StringComparison.OrdinalIgnoreCase));

        if (selectedMatch == null)
            throw new InvalidOperationException($"No compatible {entity.ToString().ToLowerInvariant()} downloader matched this URL.");

        return new ResolvedBatchMatch(
            selectedMatch.DownloaderId,
            string.IsNullOrWhiteSpace(selectedMatch.NormalizedUrl) ? url : selectedMatch.NormalizedUrl,
            item.QualityId ?? selectedMatch.QualityOptions.FirstOrDefault()?.Id,
            selectedMatch.Label,
            selectedMatch.SourceUrl);
    }

    private async Task<int> CreatePlaceholderEntityAsync(DownloaderEntity entity, string url, string title, CancellationToken ct)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        switch (entity)
        {
            case DownloaderEntity.Video:
            {
                var video = new Video
                {
                    Title = title,
                    Organized = false,
                    Urls = [new VideoUrl { Url = url }],
                };
                db.Videos.Add(video);
                await db.SaveChangesAsync(ct);
                return video.Id;
            }
            case DownloaderEntity.Image:
            {
                var image = new Image
                {
                    Title = title,
                    Organized = false,
                    Urls = [new ImageUrl { Url = url }],
                };
                db.Images.Add(image);
                await db.SaveChangesAsync(ct);
                return image.Id;
            }
            case DownloaderEntity.Gallery:
            {
                var gallery = new Gallery
                {
                    Title = title,
                    Organized = false,
                    Urls = [new GalleryUrl { Url = url }],
                };
                db.Galleries.Add(gallery);
                await db.SaveChangesAsync(ct);
                return gallery.Id;
            }
            case DownloaderEntity.Audio:
            {
                var audio = new Audio
                {
                    Title = title,
                    Organized = false,
                    Urls = [new AudioUrl { Url = url }],
                };
                db.Audios.Add(audio);
                await db.SaveChangesAsync(ct);
                return audio.Id;
            }
            case DownloaderEntity.Text:
            {
                var text = new TextDocument
                {
                    Title = title,
                    Organized = false,
                    Urls = [new TextUrl { Url = url }],
                };
                db.TextDocuments.Add(text);
                await db.SaveChangesAsync(ct);
                return text.Id;
            }
            default:
                throw new InvalidOperationException($"Batch imports do not support creating new {entity.ToString().ToLowerInvariant()} records.");
        }
    }

    private async Task AttachDownloadedUrlAsync(DownloaderEntity entity, int entityId, string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();

        switch (entity)
        {
            case DownloaderEntity.Video:
                if (!await db.Set<Cove.Core.Entities.VideoUrl>().AnyAsync(item => item.VideoId == entityId && item.Url == url, ct))
                    db.Set<Cove.Core.Entities.VideoUrl>().Add(new Cove.Core.Entities.VideoUrl { VideoId = entityId, Url = url });
                break;
            case DownloaderEntity.Image:
                if (!await db.Set<Cove.Core.Entities.ImageUrl>().AnyAsync(item => item.ImageId == entityId && item.Url == url, ct))
                    db.Set<Cove.Core.Entities.ImageUrl>().Add(new Cove.Core.Entities.ImageUrl { ImageId = entityId, Url = url });
                break;
            case DownloaderEntity.Gallery:
                if (!await db.Set<Cove.Core.Entities.GalleryUrl>().AnyAsync(item => item.GalleryId == entityId && item.Url == url, ct))
                    db.Set<Cove.Core.Entities.GalleryUrl>().Add(new Cove.Core.Entities.GalleryUrl { GalleryId = entityId, Url = url });
                break;
            case DownloaderEntity.Audio:
                if (!await db.Set<Cove.Core.Entities.AudioUrl>().AnyAsync(item => item.AudioId == entityId && item.Url == url, ct))
                    db.Set<Cove.Core.Entities.AudioUrl>().Add(new Cove.Core.Entities.AudioUrl { AudioId = entityId, Url = url });
                break;
            case DownloaderEntity.Text:
                if (!await db.Set<Cove.Core.Entities.TextUrl>().AnyAsync(item => item.TextDocumentId == entityId && item.Url == url, ct))
                    db.Set<Cove.Core.Entities.TextUrl>().Add(new Cove.Core.Entities.TextUrl { TextDocumentId = entityId, Url = url });
                break;
        }

        await db.SaveChangesAsync(ct);
    }

    private async Task AttachBatchRelationshipsAsync(DownloaderEntity entity, int entityId, DownloaderBatchItemDto item, CancellationToken ct)
    {
        var galleryIds = item.GalleryIds?
            .Where(id => id > 0)
            .Distinct()
            .ToList() ?? [];
        var groupInputs = item.GroupIds?
            .Where(group => group is { GroupId: > 0 })
            .GroupBy(group => group.GroupId)
            .Select(group => group.First())
            .ToList() ?? [];

        if ((entity != DownloaderEntity.Image || galleryIds.Count == 0) && groupInputs.Count == 0)
            return;

        using var scope = serviceScopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CoveContext>();
        var changed = false;

        if (entity == DownloaderEntity.Image && galleryIds.Count > 0)
        {
            var existingGalleryIds = await db.Set<ImageGallery>()
                .Where(link => link.ImageId == entityId && galleryIds.Contains(link.GalleryId))
                .Select(link => link.GalleryId)
                .ToListAsync(ct);
            var existingGalleryIdSet = existingGalleryIds.ToHashSet();
            var linksToAdd = galleryIds
                .Where(galleryId => !existingGalleryIdSet.Contains(galleryId))
                .Select(galleryId => new ImageGallery { ImageId = entityId, GalleryId = galleryId })
                .ToList();
            if (linksToAdd.Count > 0)
            {
                db.Set<ImageGallery>().AddRange(linksToAdd);
                changed = true;
            }
        }

        if (groupInputs.Count > 0 && TryResolveGroupItemTarget(entity, out var groupTarget))
        {
            var targetGroupIds = groupInputs.Select(group => group.GroupId).ToList();
            var existingGroupIds = await LoadExistingBatchGroupIdsAsync(db, entity, entityId, groupTarget.Kind, groupTarget.HostType, targetGroupIds, ct);
            var groupItemsToAdd = groupInputs
                .Where(group => !existingGroupIds.Contains(group.GroupId))
                .Select(group => CreateBatchGroupItem(groupTarget.Kind, groupTarget.HostType, entityId, group.GroupId, group.VideoIndex, item.Title ?? item.Label))
                .ToList();
            if (groupItemsToAdd.Count > 0)
            {
                db.GroupItems.AddRange(groupItemsToAdd);
                changed = true;
            }
        }

        if (changed)
            await db.SaveChangesAsync(ct);
    }

    private static async Task<HashSet<int>> LoadExistingBatchGroupIdsAsync(
        CoveContext db,
        DownloaderEntity entity,
        int entityId,
        GroupItemKind kind,
        string hostType,
        IReadOnlyCollection<int> groupIds,
        CancellationToken ct)
    {
        if (groupIds.Count == 0)
            return [];

        IQueryable<GroupItem> query = db.GroupItems.Where(item => groupIds.Contains(item.GroupId) && item.Kind == kind);
        query = entity switch
        {
            DownloaderEntity.Video => query.Where(item => (item.HostType == hostType && item.HostId == entityId) || item.VideoId == entityId),
            DownloaderEntity.Image => query.Where(item => (item.HostType == hostType && item.HostId == entityId) || item.ImageId == entityId),
            _ => query.Where(item => item.HostType == hostType && item.HostId == entityId),
        };

        var existing = await query.Select(item => item.GroupId).ToListAsync(ct);
        return existing.ToHashSet();
    }

    private static GroupItem CreateBatchGroupItem(GroupItemKind kind, string hostType, int entityId, int groupId, int orderIndex, string? title)
    {
        var item = new GroupItem
        {
            GroupId = groupId,
            OrderIndex = orderIndex,
            Kind = kind,
            HostType = hostType,
            HostId = entityId,
            Title = string.IsNullOrWhiteSpace(title) ? null : title.Trim(),
        };

        if (kind == GroupItemKind.Video)
            item.VideoId = entityId;
        else if (kind == GroupItemKind.Image)
            item.ImageId = entityId;

        return item;
    }

    private static bool TryResolveGroupItemTarget(DownloaderEntity entity, out (GroupItemKind Kind, string HostType) target)
    {
        target = entity switch
        {
            DownloaderEntity.Video => (GroupItemKind.Video, "video"),
            DownloaderEntity.Image => (GroupItemKind.Image, "image"),
            DownloaderEntity.Gallery => (GroupItemKind.Gallery, "gallery"),
            DownloaderEntity.Audio => (GroupItemKind.Audio, "audio"),
            DownloaderEntity.Text => (GroupItemKind.Text, "text"),
            _ => default,
        };

        return target != default;
    }

    private static string ResolvePlaceholderTitle(DownloaderBatchItemDto item, string url, string label)
    {
        if (!string.IsNullOrWhiteSpace(item.Title))
            return item.Title.Trim();

        if (!string.IsNullOrWhiteSpace(item.Label))
            return item.Label.Trim();

        return DeriveTitleFromUrl(url, label);
    }

    private static string BuildBatchItemLabel(DownloaderBatchItemDto item, int index)
    {
        if (!string.IsNullOrWhiteSpace(item.Label))
            return item.Label.Trim();

        if (!string.IsNullOrWhiteSpace(item.Title))
            return item.Title.Trim();

        return string.IsNullOrWhiteSpace(item.Url)
            ? $"Batch item {index + 1}"
            : DeriveTitleFromUrl(item.Url, item.Url.Trim());
    }

    private static string DeriveTitleFromUrl(string url, string fallback)
    {
        try
        {
            var parsed = new Uri(url, UriKind.Absolute);
            var fileName = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return Uri.UnescapeDataString(fileName)
                    .Replace('_', ' ')
                    .Replace('-', ' ')
                    .Trim();
            }

            return parsed.Host;
        }
        catch
        {
            return fallback;
        }
    }

    private static DownloaderPermissions BuildDownloaderPermissions(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
            return new DownloaderPermissions([uri.Host]);

        return new DownloaderPermissions();
    }

    internal sealed record ScrapedAudioMetadata
    {
        public string? SourceScraperId { get; init; }
        public string? Title { get; init; }
        public string? Code { get; init; }
        public string? Details { get; init; }
        public string? Date { get; init; }
        public List<string> Urls { get; init; } = [];
        public string? StudioName { get; init; }
        public List<string> PerformerNames { get; init; } = [];
        public List<string> TagNames { get; init; } = [];
    }

    internal sealed record ScrapedTextMetadata
    {
        public string? SourceScraperId { get; init; }
        public string? Title { get; init; }
        public string? Code { get; init; }
        public string? Details { get; init; }
        public string? Date { get; init; }
        public List<string> Urls { get; init; } = [];
        public string? StudioName { get; init; }
        public List<string> PerformerNames { get; init; } = [];
        public List<string> TagNames { get; init; } = [];
    }

    private sealed record ResolvedBatchItem(DownloaderRequest Request, int? EntityId, string Label, bool AutoApplyMetadata, DownloaderMetadataApplyOptions MetadataApplyOptions);

    private sealed record ResolvedBatchMatch(string DownloaderId, string NormalizedUrl, string? QualityId, string? Label = null, string? SourceUrl = null);

    private sealed record IndexedBatchItem(DownloaderBatchItemDto Item, int Index);

    private sealed record ExistingUrlRow(int EntityId, string Url, string Label);

    private sealed record ExistingDownloadTarget(int EntityId, string Label);

    private static string? NormalizeOverrideSite(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var absoluteUri))
            trimmed = absoluteUri.Host;

        trimmed = trimmed.ToLowerInvariant();
        return trimmed.StartsWith("www.", StringComparison.Ordinal) ? trimmed[4..] : trimmed;
    }

    private static async Task<string?> FindDuplicateVideoLabelAsync(CoveContext db, int? entityId, string normalizedUrl, CancellationToken ct)
    {
        var candidateUrls = await db.Set<Cove.Core.Entities.VideoUrl>()
            .Where(item => !entityId.HasValue || item.VideoId != entityId.Value)
            .Select(item => new { item.VideoId, item.Url })
            .AsNoTracking()
            .ToListAsync(ct);
        var duplicateId = candidateUrls
            .Where(item => NormalizeUrlForLookup(item.Url) == normalizedUrl)
            .Select(item => item.VideoId)
            .FirstOrDefault();

        if (duplicateId == 0)
            return null;

        var duplicate = await db.Videos.FirstOrDefaultAsync(item => item.Id == duplicateId, ct);
        return duplicate == null ? null : duplicate.Title ?? $"Video {duplicate.Id}";
    }

    private static async Task<string?> FindDuplicateImageLabelAsync(CoveContext db, int? entityId, string normalizedUrl, CancellationToken ct)
    {
        var candidateUrls = await db.Set<Cove.Core.Entities.ImageUrl>()
            .Where(item => !entityId.HasValue || item.ImageId != entityId.Value)
            .Select(item => new { item.ImageId, item.Url })
            .AsNoTracking()
            .ToListAsync(ct);
        var duplicateId = candidateUrls
            .Where(item => NormalizeUrlForLookup(item.Url) == normalizedUrl)
            .Select(item => item.ImageId)
            .FirstOrDefault();

        if (duplicateId == 0)
            return null;

        var duplicate = await db.Images.FirstOrDefaultAsync(item => item.Id == duplicateId, ct);
        return duplicate == null ? null : duplicate.Title ?? $"Image {duplicate.Id}";
    }

    private static async Task<string?> FindDuplicateGalleryLabelAsync(CoveContext db, int? entityId, string normalizedUrl, CancellationToken ct)
    {
        var candidateUrls = await db.Set<Cove.Core.Entities.GalleryUrl>()
            .Where(item => !entityId.HasValue || item.GalleryId != entityId.Value)
            .Select(item => new { item.GalleryId, item.Url })
            .AsNoTracking()
            .ToListAsync(ct);
        var duplicateId = candidateUrls
            .Where(item => NormalizeUrlForLookup(item.Url) == normalizedUrl)
            .Select(item => item.GalleryId)
            .FirstOrDefault();

        if (duplicateId == 0)
            return null;

        var duplicate = await db.Galleries.FirstOrDefaultAsync(item => item.Id == duplicateId, ct);
        return duplicate == null ? null : duplicate.Title ?? $"Gallery {duplicate.Id}";
    }

    private static async Task<string?> FindDuplicateAudioLabelAsync(CoveContext db, int? entityId, string normalizedUrl, CancellationToken ct)
    {
        var candidateUrls = await db.Set<Cove.Core.Entities.AudioUrl>()
            .Where(item => !entityId.HasValue || item.AudioId != entityId.Value)
            .Select(item => new { item.AudioId, item.Url })
            .AsNoTracking()
            .ToListAsync(ct);
        var duplicateId = candidateUrls
            .Where(item => NormalizeUrlForLookup(item.Url) == normalizedUrl)
            .Select(item => item.AudioId)
            .FirstOrDefault();

        if (duplicateId == 0)
            return null;

        var duplicate = await db.Audios.FirstOrDefaultAsync(item => item.Id == duplicateId, ct);
        return duplicate == null ? null : duplicate.Title ?? $"Audio {duplicate.Id}";
    }

    private static async Task<string?> FindDuplicateTextLabelAsync(CoveContext db, int? entityId, string normalizedUrl, CancellationToken ct)
    {
        var candidateUrls = await db.Set<Cove.Core.Entities.TextUrl>()
            .Where(item => !entityId.HasValue || item.TextDocumentId != entityId.Value)
            .Select(item => new { item.TextDocumentId, item.Url })
            .AsNoTracking()
            .ToListAsync(ct);
        var duplicateId = candidateUrls
            .Where(item => NormalizeUrlForLookup(item.Url) == normalizedUrl)
            .Select(item => item.TextDocumentId)
            .FirstOrDefault();

        if (duplicateId == 0)
            return null;

        var duplicate = await db.TextDocuments.FirstOrDefaultAsync(item => item.Id == duplicateId, ct);
        return duplicate == null ? null : duplicate.Title ?? $"Text {duplicate.Id}";
    }

    private static string GetUniquePath(string directory, string fileName)
    {
        var safeFileName = string.IsNullOrWhiteSpace(fileName) ? "download" : fileName;
        var extension = Path.GetExtension(safeFileName);
        var baseName = Path.GetFileNameWithoutExtension(safeFileName);
        var candidate = Path.Combine(directory, safeFileName);
        var counter = 1;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(directory, $"{baseName}-{counter}{extension}");
            counter++;
        }

        return candidate;
    }

    private static string GetEntityDownloadFolder(DownloaderEntity entity)
    {
        return entity switch
        {
            DownloaderEntity.Video => "videos",
            DownloaderEntity.Image => "images",
            DownloaderEntity.Gallery => "galleries",
            DownloaderEntity.Audio => "audio",
            DownloaderEntity.Text => "texts",
            _ => entity.ToString().ToLowerInvariant() + "s",
        };
    }

    private static async Task<int> ImportVideoAsync(IScanService scanService, string libraryPath, int? entityId, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        progress?.Report(0.98d, entityId.HasValue ? "Importing downloaded video..." : "Creating video from download...");
        return scanService is ScanService coreScanService
            ? await coreScanService.ImportDownloadedVideoWithinProducerLeaseAsync(libraryPath, entityId, ct)
            : await scanService.ImportDownloadedVideoAsync(libraryPath, entityId, ct);
    }

    private static async Task<int> ImportImageAsync(IScanService scanService, string libraryPath, int? entityId, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        progress?.Report(0.98d, entityId.HasValue ? "Importing downloaded image..." : "Creating image from download...");
        return scanService is ScanService coreScanService
            ? await coreScanService.ImportDownloadedImageWithinProducerLeaseAsync(libraryPath, entityId, ct)
            : await scanService.ImportDownloadedImageAsync(libraryPath, entityId, ct);
    }

    private static async Task<int> ImportGalleryAsync(IScanService scanService, string libraryPath, int? entityId, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        progress?.Report(0.98d, entityId.HasValue ? "Importing downloaded gallery..." : "Creating gallery from download...");
        return scanService is ScanService coreScanService
            ? await coreScanService.ImportDownloadedGalleryWithinProducerLeaseAsync(libraryPath, entityId, ct)
            : await scanService.ImportDownloadedGalleryAsync(libraryPath, entityId, ct);
    }

    private static async Task<int> ImportAudioAsync(IScanService scanService, string libraryPath, int? entityId, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        progress?.Report(0.98d, entityId.HasValue ? "Importing downloaded audio..." : "Creating audio from download...");
        return scanService is ScanService coreScanService
            ? await coreScanService.ImportDownloadedAudioWithinProducerLeaseAsync(libraryPath, entityId, ct)
            : await scanService.ImportDownloadedAudioAsync(libraryPath, entityId, ct);
    }

    private static async Task<int> ImportTextAsync(IScanService scanService, string libraryPath, int? entityId, Cove.Core.Interfaces.IJobProgress? progress, CancellationToken ct)
    {
        progress?.Report(0.98d, entityId.HasValue ? "Importing downloaded text..." : "Creating text from download...");
        return scanService is ScanService coreScanService
            ? await coreScanService.ImportDownloadedTextWithinProducerLeaseAsync(libraryPath, entityId, ct)
            : await scanService.ImportDownloadedTextAsync(libraryPath, entityId, ct);
    }

    private static void TryDeleteParentDirectory(string filePath)
    {
        try
        {
            var parent = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                Directory.Delete(parent, recursive: false);
        }
        catch
        {
            // Best-effort cleanup for the downloader temp directory.
        }
    }

    private static bool IsPathWithinDirectory(string path, string directory)
    {
        var relativePath = Path.GetRelativePath(Path.GetFullPath(directory), Path.GetFullPath(path));
        return !Path.IsPathRooted(relativePath)
            && relativePath != ".."
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for a downloader attempt that did not return a usable temp file.
        }
    }

    private sealed class DownloaderHost(
        string tempDirectory,
        IHttpClientFactory httpClientFactory,
        ILoggerFactory loggerFactory,
        Cove.Core.Interfaces.IJobProgress? progress) : IDownloaderHost
    {
        public string TempDirectory { get; } = tempDirectory;
        public IHttpClientFactory HttpClients { get; } = httpClientFactory;

        public ILogger CreateLogger(string categoryName) => loggerFactory.CreateLogger(categoryName);

        public void ReportProgress(double progressValue, string? message = null)
        {
            progress?.Report(progressValue, message);
        }
    }

    private sealed class DownloadSlotLease(SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose()
        {
            semaphore.Release();
        }
    }
}
