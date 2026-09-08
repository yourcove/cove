using System.Data;
using System.Globalization;
using Cove.Core.Auth;
using Cove.Core.Common;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

public sealed class ImageDuplicateSearchService(
    CoveContext db,
    IJobService jobService,
    IServiceScopeFactory scopeFactory,
    CoveConfiguration configuration,
    CustomFieldService customFields,
    EntityHostDependencyService hostDependencies,
    Cove.Core.Auth.IAuthorizationService authorizationService,
    ISegmentSpanCacheInvalidator? segmentSpanCacheInvalidator = null)
{
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    public async Task<ImageDuplicateSearchStartDto> StartAsync(JobOwner? owner, CovePrincipal? principal, ImageDuplicateSearchRequestDto request, CancellationToken ct)
    {
        var search = new ImageDuplicateSearch
        {
            OwnerKey = owner?.Key,
            MinimumBytes = Math.Max(0, request.MinimumBytes),
            ExpiresAt = DateTime.UtcNow.Add(Retention),
        };
        db.ImageDuplicateSearches.Add(search);
        await db.SaveChangesAsync(ct);
        async Task Work(IJobProgress progress, CancellationToken jobCt)
        {
            using var scope = scopeFactory.CreateScope();
            var accessor = scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>();
            accessor.Set(principal);
            await scope.ServiceProvider.GetRequiredService<ImageDuplicateSearchService>().ExecuteAsync(search.Id, progress, jobCt);
        }
        var resultUrl = $"/duplicates?kind=images&imageSearch={search.Id:D}";
        var jobId = owner is null
            ? jobService.EnqueueWithResult("image-duplicate-search", "Finding exact duplicate images", Work, resultUrl)
            : jobService.EnqueueOwned(owner, "image-duplicate-search", "Finding exact duplicate images", Work, resultUrl);
        search.JobId = jobId;
        await db.SaveChangesAsync(CancellationToken.None);
        return new ImageDuplicateSearchStartDto(search.Id, jobId, 0);
    }

    private async Task ExecuteAsync(Guid searchId, IJobProgress progress, CancellationToken ct)
    {
        try
        {
            var search = await db.ImageDuplicateSearches.SingleAsync(item => item.Id == searchId, ct);
            search.Status = DuplicateSearchStatus.Running;
            search.StartedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            progress.Report(.05, "Loading exact image pHashes");
            var rows = await db.Images
                .SelectMany(image => image.Files.SelectMany(file => file.Fingerprints
                    .Where(fingerprint => fingerprint.Type == "phash" && fingerprint.Value != "")
                    .Select(fingerprint => new ImageHashRow(
                        fingerprint.Value, file.Id, image.Id, file.Width, file.Height, file.Size,
                        file.Path, file.Basename, file.ZipFileId))))
                .AsNoTracking()
                .ToListAsync(ct);
            search.CandidateCount = rows.Count;
            var groups = rows.GroupBy(row => row.Hash, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Select(row => row.FileId).Distinct().Count() > 1)
                .Select(group =>
                {
                    var files = group.DistinctBy(row => row.FileId)
                        .OrderByDescending(row => row.Protected)
                        .ThenByDescending(row => (long)row.Width * row.Height)
                        .ThenByDescending(row => row.Size)
                        .ThenBy(row => row.FileId)
                        .ToArray();
                    var keeper = files[0];
                    var freeable = files.Where(row => row.FileId != keeper.FileId && !row.Protected).Sum(row => row.Size);
                    return new ImageGroupDefinition(group.Key, keeper.FileId, freeable, files);
                })
                .Where(group => group.FreeableBytes >= search.MinimumBytes)
                .OrderByDescending(group => group.FreeableBytes)
                .ThenBy(group => group.Hash, StringComparer.OrdinalIgnoreCase)
                .Take(25_000)
                .ToArray();
            progress.Report(.75, "Saving image duplicate groups");
            for (var position = 0; position < groups.Length; position++)
            {
                var definition = groups[position];
                var entity = new ImageDuplicateSearchGroup
                {
                    SearchId = searchId,
                    Position = position,
                    Hash = definition.Hash,
                    KeeperFileId = definition.KeeperFileId,
                    FreeableBytes = definition.FreeableBytes,
                    Items = definition.Files.Select(row => new ImageDuplicateSearchItem
                    {
                        FileId = row.FileId,
                        ImageId = row.ImageId,
                        Protected = row.Protected,
                    }).ToList(),
                };
                db.ImageDuplicateSearchGroups.Add(entity);
                if (position % 250 == 249) { await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); }
            }
            await db.SaveChangesAsync(ct);
            search = await db.ImageDuplicateSearches.SingleAsync(item => item.Id == searchId, ct);
            search.Status = DuplicateSearchStatus.Completed;
            search.GroupCount = groups.Length;
            search.FileCount = groups.Sum(group => group.Files.Length);
            search.FreeableBytes = groups.Sum(group => group.FreeableBytes);
            search.CompletedAt = DateTime.UtcNow;
            search.ExpiresAt = DateTime.UtcNow.Add(Retention);
            await db.SaveChangesAsync(ct);
            progress.Report(1, $"Found {groups.Length.ToString(CultureInfo.InvariantCulture)} duplicate image groups");
        }
        catch (OperationCanceledException)
        {
            await SetFailedAsync(searchId, DuplicateSearchStatus.Cancelled, null);
            throw;
        }
        catch (Exception ex)
        {
            await SetFailedAsync(searchId, DuplicateSearchStatus.Failed, ex.Message);
            throw;
        }
    }

    private async Task SetFailedAsync(Guid searchId, DuplicateSearchStatus status, string? error)
    {
        db.ChangeTracker.Clear();
        var search = await db.ImageDuplicateSearches.SingleOrDefaultAsync(item => item.Id == searchId, CancellationToken.None);
        if (search is null) return;
        search.Status = status;
        search.Error = error?[..Math.Min(error.Length, 2_000)];
        search.CompletedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(CancellationToken.None);
    }

    public BulkDeletionJobStart StartCleanup(CovePrincipal? principal, Guid searchId, bool copyMetadata, bool deleteGenerated)
    {
        var owner = JobOwner.FromPrincipal(principal);
        async Task Work(IJobProgress progress, CancellationToken ct)
        {
            var physicalContext = new BulkDeletionExecutionContext();
            using var countScope = scopeFactory.CreateScope();
            var countDb = countScope.ServiceProvider.GetRequiredService<CoveContext>();
            var groupIds = await countDb.ImageDuplicateSearchGroups.Where(group => group.SearchId == searchId).OrderBy(group => group.Position).Select(group => group.Id).ToArrayAsync(ct);
            progress.DeclareUnitCount(groupIds.Length);
            var completed = 0;
            try
            {
                foreach (var groupId in groupIds)
                {
                    ct.ThrowIfCancellationRequested();
                    using var scope = scopeFactory.CreateScope();
                    scope.ServiceProvider.GetRequiredService<ICurrentPrincipalAccessor>().Set(principal);
                    var service = scope.ServiceProvider.GetRequiredService<ImageDuplicateSearchService>();
                    if (await service.CleanupGroupAsync(groupId, physicalContext, copyMetadata, deleteGenerated, principal, ct)) completed++;
                    progress.Report((double)completed / Math.Max(1, groupIds.Length), $"Cleaned {completed.ToString(CultureInfo.InvariantCulture)} of {groupIds.Length.ToString(CultureInfo.InvariantCulture)} image groups");
                }
            }
            finally
            {
                using var scope = scopeFactory.CreateScope();
                var cleanupDb = scope.ServiceProvider.GetRequiredService<CoveContext>();
                await cleanupDb.ImageDuplicateKeeperReservations.IgnoreQueryFilters().Where(item => item.SearchId == searchId).ExecuteDeleteAsync(CancellationToken.None);
                var deletion = scope.ServiceProvider.GetRequiredService<BulkEntityDeletionService>();
                await deletion.DeleteTrackedPhysicalFilesAsync(BulkDeletionEntityKind.Image, physicalContext,
                    BulkDeletionJobService.ResolveMaxParallelism(configuration, Environment.ProcessorCount), CancellationToken.None);
            }
        }
        var description = "Merging metadata and cleaning duplicate images";
        var jobId = owner is null ? jobService.Enqueue("image-bulk-delete", description, Work) : jobService.EnqueueOwned(owner, "image-bulk-delete", description, Work);
        return new BulkDeletionJobStart(jobId, 0);
    }

    private async Task<bool> CleanupGroupAsync(int groupId, BulkDeletionExecutionContext physicalContext, bool copyMetadata, bool deleteGenerated, CovePrincipal? principal, CancellationToken ct)
    {
        var generatedIds = new List<int>();
        var strategy = db.Database.CreateExecutionStrategy();
        var changed = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var group = await db.ImageDuplicateSearchGroups.IgnoreQueryFilters().Include(item => item.Items).SingleOrDefaultAsync(item => item.Id == groupId, ct);
            if (group is null) return false;
            var keeperItem = group.Items.SingleOrDefault(item => item.FileId == group.KeeperFileId);
            if (keeperItem is null)
            {
                if (group.Items.Count <= 1) { await transaction.CommitAsync(ct); return false; }
                keeperItem = group.Items.OrderByDescending(item => item.Protected).ThenBy(item => item.FileId).First();
                group.KeeperFileId = keeperItem.FileId;
            }
            var drop = group.Items.Where(item => item.FileId != keeperItem.FileId && !item.Protected).ToArray();
            if (drop.Length == 0) { await transaction.CommitAsync(ct); return false; }
            var targetImageId = keeperItem.ImageId;
            var globallyKeptImageIds = (await db.ImageDuplicateSearchGroups
                .Where(candidate => candidate.SearchId == group.SearchId)
                .SelectMany(candidate => candidate.Items
                    .Where(item => item.FileId == candidate.KeeperFileId)
                    .Select(item => item.ImageId))
                .ToArrayAsync(ct)).ToHashSet();
            var sourceImageIds = drop.Select(item => item.ImageId)
                .Where(id => id != targetImageId && !globallyKeptImageIds.Contains(id))
                .Distinct()
                .ToArray();
            authorizationService.Require(principal, Permissions.ImagesDeleteFile);
            await RequireEntityAccessAsync(principal, Permissions.ImagesWrite, targetImageId, ct);
            foreach (var sourceImageId in sourceImageIds)
                await RequireEntityAccessAsync(principal, Permissions.ImagesDelete, sourceImageId, ct);
            foreach (var sourceImageId in sourceImageIds)
            {
                if (copyMetadata) await StageImageTransferAsync(db, targetImageId, sourceImageId, ct);
                else
                {
                    foreach (var file in await db.ImageFiles.Where(file => file.ImageId == sourceImageId && !drop.Select(item => item.FileId).Contains(file.Id)).ToListAsync(ct))
                        file.ImageId = targetImageId;
                }
                // Persist re-parented metadata within this still-uncommitted transaction so the
                // host cleanup queries no longer identify those rows as source dependencies.
                await db.SaveChangesAsync(ct);
                var hostCleanup = await hostDependencies.StageDeleteAsync(AffinityHostType.Image, sourceImageId, ct);
                await customFields.StageDeleteValuesForEntityAsync(CustomFieldEntityTypes.Image, sourceImageId, ct);
                foreach (var videoId in hostCleanup.SegmentVideoIds) segmentSpanCacheInvalidator?.InvalidateVideo(videoId);
                db.Images.Remove(await db.Images.IgnoreQueryFilters().SingleAsync(image => image.Id == sourceImageId, ct));
                generatedIds.Add(sourceImageId);
            }
            var fileIds = drop.Select(item => item.FileId).ToArray();
            var files = await db.ImageFiles.Where(file => fileIds.Contains(file.Id)).ToArrayAsync(ct);
            if (files.Any(file => IsProtected(file))) throw new InvalidOperationException("Archive-backed image files cannot be deleted.");
            var paths = files.Select(file => file.Path).Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            db.ImageFiles.RemoveRange(files);
            physicalContext.StagePhysicalFiles(db, paths);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            foreach (var path in paths) physicalContext.TrackPhysicalFile(path);
            return true;
        });
        if (deleteGenerated)
        {
            using var cleanupScope = scopeFactory.CreateScope();
            var thumbnails = cleanupScope.ServiceProvider.GetRequiredService<IThumbnailService>();
            foreach (var id in generatedIds) await thumbnails.DeleteImageGeneratedFilesAsync(id, ct);
        }
        return changed;
    }

    private async Task RequireEntityAccessAsync(CovePrincipal? principal, string permission, int imageId, CancellationToken ct)
    {
        var result = await authorizationService.AuthorizeAsync(principal, permission, EntityRef.Of(EntityKinds.Image, imageId), ct);
        if (!result.Allowed)
            throw new UnauthorizedAccessException(result.Reason ?? $"Missing permission '{permission}' for image {imageId}.");
    }

    private static async Task StageImageTransferAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        var images = await db.Images.IgnoreQueryFilters()
            .Include(image => image.Files).Include(image => image.ImageTags).Include(image => image.ImagePerformers)
            .Include(image => image.ImageGalleries).Include(image => image.Urls)
            .Where(image => image.Id == targetId || image.Id == sourceId).ToArrayAsync(ct);
        var target = images.Single(image => image.Id == targetId);
        var source = images.Single(image => image.Id == sourceId);
        var targetDateWasMissing = target.Date is null;
        target.Title ??= source.Title; target.Code ??= source.Code; target.Details ??= source.Details;
        target.Photographer ??= source.Photographer; target.StudioId ??= source.StudioId; target.Date ??= source.Date;
        if (targetDateWasMissing && source.Date is not null) target.DatePrecision = source.DatePrecision;
        target.Organized |= source.Organized;
        AddMissing(target.ImageTags, source.ImageTags, row => row.TagId, row => new ImageTag { ImageId = targetId, TagId = row.TagId });
        AddMissing(target.ImagePerformers, source.ImagePerformers, row => row.PerformerId, row => new ImagePerformer { ImageId = targetId, PerformerId = row.PerformerId });
        AddMissing(target.ImageGalleries, source.ImageGalleries, row => row.GalleryId, row => new ImageGallery { ImageId = targetId, GalleryId = row.GalleryId });
        AddMissing(target.Urls, source.Urls, row => row.Url, row => new ImageUrl { ImageId = targetId, Url = row.Url }, StringComparer.OrdinalIgnoreCase);
        target.TagIds = target.ImageTags.Select(row => row.TagId).Concat(source.ImageTags.Select(row => row.TagId)).Distinct().Order().ToArray();
        target.PerformerIds = target.ImagePerformers.Select(row => row.PerformerId).Concat(source.ImagePerformers.Select(row => row.PerformerId)).Distinct().Order().ToArray();
        target.TagCount = target.TagIds.Length; target.PerformerCount = target.PerformerIds.Length;
        target.GalleryCount = target.ImageGalleries.Select(row => row.GalleryId).Concat(source.ImageGalleries.Select(row => row.GalleryId)).Distinct().Count();
        foreach (var file in source.Files) file.ImageId = targetId;
        await MergeImageCustomFieldsAsync(db, targetId, sourceId, ct);
        await MergeImageHostRowsAsync(db, targetId, sourceId, ct);
        await DuplicateMetadataTransferService.MergeEngagementAsync(db, AffinityHostType.Image, RatingHostType.Image, InteractionHostType.Image, targetId, sourceId, false, ct);
        target.UpdatedAt = DateTime.UtcNow;
    }

    private static async Task MergeImageCustomFieldsAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        var values = await db.CustomFieldValues.IgnoreQueryFilters()
            .Where(value => value.EntityType == CustomFieldEntityTypes.Image && (value.EntityId == targetId || value.EntityId == sourceId)).ToListAsync(ct);
        var targetDefinitions = values.Where(value => value.EntityId == targetId).Select(value => value.DefinitionId).ToHashSet();
        foreach (var value in values.Where(value => value.EntityId == sourceId && !targetDefinitions.Contains(value.DefinitionId)))
            db.CustomFieldValues.Add(new CustomFieldValue
            {
                DefinitionId = value.DefinitionId, EntityType = CustomFieldEntityTypes.Image, EntityId = targetId,
                Position = value.Position, TextValue = value.TextValue, NumberValue = value.NumberValue,
                BoolValue = value.BoolValue, DateValue = value.DateValue, TimestampValue = value.TimestampValue,
                IntegerValue = value.IntegerValue,
            });
    }

    private static async Task MergeImageHostRowsAsync(CoveContext db, int targetId, int sourceId, CancellationToken ct)
    {
        var targetGroupIds = (await db.GroupItems.IgnoreQueryFilters().Where(row => row.HostType == "image" && row.HostId == targetId && row.Kind == GroupItemKind.Image).ToListAsync(ct)).Select(row => row.GroupId).ToHashSet();
        foreach (var row in await db.GroupItems.IgnoreQueryFilters().Where(row => row.HostType == "image" && row.HostId == sourceId && row.Kind == GroupItemKind.Image).ToListAsync(ct))
            if (targetGroupIds.Add(row.GroupId)) { row.HostId = targetId; row.ImageId = targetId; } else db.GroupItems.Remove(row);

        foreach (var row in await db.Segments.IgnoreQueryFilters().Where(row => row.HostType == SegmentHostType.Image && row.HostId == sourceId).ToListAsync(ct)) row.HostId = targetId;
        foreach (var row in await db.Detections.IgnoreQueryFilters().Where(row => row.HostType == DetectionHostType.Image && row.HostId == sourceId).ToListAsync(ct)) row.HostId = targetId;
        foreach (var row in await db.FaceAppearances.IgnoreQueryFilters().Where(row => row.HostType == FaceAppearanceHostType.Image && row.HostId == sourceId).ToListAsync(ct)) row.HostId = targetId;

        var fieldKeys = (await db.FieldProvenance.IgnoreQueryFilters().Where(row => row.HostType == AffinityHostType.Image && row.HostId == targetId).ToListAsync(ct)).Select(row => (row.FieldKey, row.SourceKey, row.SourceRunId, row.ModelKey)).ToHashSet();
        foreach (var row in await db.FieldProvenance.IgnoreQueryFilters().Where(row => row.HostType == AffinityHostType.Image && row.HostId == sourceId).ToListAsync(ct))
            if (fieldKeys.Add((row.FieldKey, row.SourceKey, row.SourceRunId, row.ModelKey))) row.HostId = targetId; else db.FieldProvenance.Remove(row);
        var tagKeys = (await db.TagApplications.IgnoreQueryFilters().Where(row => row.HostType == AffinityHostType.Image && row.HostId == targetId).ToListAsync(ct)).Select(row => (row.ContextType, row.ContextId, row.TagId, row.SourceKey, row.SourceRunId, row.ModelKey)).ToHashSet();
        foreach (var row in await db.TagApplications.IgnoreQueryFilters().Where(row => row.HostType == AffinityHostType.Image && row.HostId == sourceId).ToListAsync(ct))
            if (tagKeys.Add((row.ContextType, row.ContextId, row.TagId, row.SourceKey, row.SourceRunId, row.ModelKey))) row.HostId = targetId; else db.TagApplications.Remove(row);
    }

    private static void AddMissing<T, TKey>(ICollection<T> target, IEnumerable<T> source, Func<T, TKey> key, Func<T, T> clone, IEqualityComparer<TKey>? comparer = null)
    {
        var existing = target.Select(key).ToHashSet(comparer);
        foreach (var item in source) if (existing.Add(key(item))) target.Add(clone(item));
    }

    internal static bool IsProtected(ImageFile file) => file.ZipFileId.HasValue || IsArchivePath(file.Path);
    internal static bool IsArchivePath(string? path)
    {
        var value = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        return value.Contains(".zip#", StringComparison.Ordinal) || value.Contains(".cbz#", StringComparison.Ordinal)
            || value.Contains(".rar#", StringComparison.Ordinal) || value.Contains(".7z#", StringComparison.Ordinal);
    }

    private sealed record ImageHashRow(string Hash, int FileId, int ImageId, int Width, int Height, long Size, string Path, string Basename, int? ZipFileId)
    {
        public bool Protected => ZipFileId.HasValue || IsArchivePath(Path);
    }
    private sealed record ImageGroupDefinition(string Hash, int KeeperFileId, long FreeableBytes, ImageHashRow[] Files);
}
