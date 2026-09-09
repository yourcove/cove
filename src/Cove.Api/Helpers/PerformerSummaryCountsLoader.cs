using Microsoft.EntityFrameworkCore;
using Cove.Core.Auth;
using Cove.Core.Entities;
using Cove.Data;

namespace Cove.Api.Helpers;

public sealed record PerformerSummaryCounts(int VideoCount, int ImageCount, int GalleryCount, int AudioCount, int TextCount);

public static class PerformerSummaryCountsLoader
{
    public static async Task<IReadOnlyDictionary<int, PerformerSummaryCounts>> LoadAsync(
        CoveContext db,
        IEnumerable<int> performerIds,
        CancellationToken cancellationToken,
        ICurrentPrincipalAccessor? principalAccessor = null)
    {
        var ids = performerIds.Where(id => id > 0).Distinct().ToArray();
        if (ids.Length == 0) return new Dictionary<int, PerformerSummaryCounts>();

        var principal = principalAccessor?.Current;
        bool CanRead(string permission) => principal == null || principal.Has(permission) || principal.HasReadGrant(permission);
        var videoCounts = CanRead(Permissions.VideosRead) ? await db.Set<VideoPerformer>().AsNoTracking().Where(link => ids.Contains(link.PerformerId)).GroupBy(link => link.PerformerId).Select(group => new { group.Key, Count = group.Select(link => link.VideoId).Distinct().Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken) : [];
        var imageCounts = CanRead(Permissions.ImagesRead) ? await db.Set<ImagePerformer>().AsNoTracking().Where(link => ids.Contains(link.PerformerId)).GroupBy(link => link.PerformerId).Select(group => new { group.Key, Count = group.Select(link => link.ImageId).Distinct().Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken) : [];
        var galleryCounts = CanRead(Permissions.GalleriesRead) ? await db.Set<GalleryPerformer>().AsNoTracking().Where(link => ids.Contains(link.PerformerId)).GroupBy(link => link.PerformerId).Select(group => new { group.Key, Count = group.Select(link => link.GalleryId).Distinct().Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken) : [];
        var audioCounts = CanRead(Permissions.AudiosRead) ? await db.Set<AudioPerformer>().AsNoTracking().Where(link => ids.Contains(link.PerformerId)).GroupBy(link => link.PerformerId).Select(group => new { group.Key, Count = group.Select(link => link.AudioId).Distinct().Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken) : [];
        var textCounts = CanRead(Permissions.TextsRead) ? await db.Set<TextPerformer>().AsNoTracking().Where(link => ids.Contains(link.PerformerId)).GroupBy(link => link.PerformerId).Select(group => new { group.Key, Count = group.Select(link => link.TextDocumentId).Distinct().Count() }).ToDictionaryAsync(item => item.Key, item => item.Count, cancellationToken) : [];

        return ids.ToDictionary(
            id => id,
            id => new PerformerSummaryCounts(
                videoCounts.GetValueOrDefault(id),
                imageCounts.GetValueOrDefault(id),
                galleryCounts.GetValueOrDefault(id),
                audioCounts.GetValueOrDefault(id),
                textCounts.GetValueOrDefault(id)));
    }

}
