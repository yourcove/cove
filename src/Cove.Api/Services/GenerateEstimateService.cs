using System.Linq.Expressions;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Sizes a Generate run before it starts, for the options to show. Totals, and the hashes Generate stores in the
/// database, are counted there. Generated files are looked for on disk only for an evenly spread sample of each
/// kind of item, and the missing share of the sample is scaled to the total: looking for every file of a large
/// library takes minutes, and the options need only the rough size of a run.
/// </summary>
public sealed class GenerateEstimateService(CoveContext db, IThumbnailService thumbnails, IVideoAssetGenerator videoAssets)
{
    /// <summary>The most items of one kind looked for on disk.</summary>
    internal const int SampleSize = 400;

    private class SampleRow
    {
        public int Id { get; init; }
    }

    private sealed class VideoRow : SampleRow
    {
        public int VideoId { get; init; }
    }

    private sealed class SegmentRow : SampleRow
    {
        public int SourceId { get; init; }
        public int HostId { get; init; }
        public double StartSec { get; init; }
        public string Kind { get; init; } = "";
    }

    private sealed class ImageRow : SampleRow
    {
        public int ImageId { get; init; }
    }

    private sealed class Md5Tally
    {
        public int Missing, Total;
        public long MissingBytes, TotalBytes;

        public GenerateWorkCountDto ToDto()
            => new() { Missing = Missing, Total = Total, MissingBytes = MissingBytes, TotalBytes = TotalBytes };
    }

    /// <summary>
    /// Counts what Generate would make in the scope of these options (their paths and ids, not what they tick).
    /// Unlike a run, ids narrow the count to the media they name, as a dialog for some images has no use for
    /// every video's. Media the scope does not reach stay null.
    /// </summary>
    public async Task<GenerateEstimateDto> EstimateAsync(GenerateOptionsDto scope, CancellationToken ct)
    {
        var videoIds = scope.VideoIds is { Count: > 0 } ? scope.VideoIds : null;
        var imageIds = scope.ImageIds is { Count: > 0 } ? scope.ImageIds : null;
        var audioIds = scope.AudioIds is { Count: > 0 } ? scope.AudioIds : null;
        var textIds = scope.TextIds is { Count: > 0 } ? scope.TextIds : null;
        var namesIds = videoIds != null || imageIds != null || audioIds != null || textIds != null;
        // Compared without case, as GeneratePathFilter does.
        var roots = GeneratePathFilter.Normalize(scope.Paths).Select(path => path.ToLowerInvariant()).ToList();

        var md5 = new Md5Tally();
        var estimate = new GenerateEstimateDto();
        if (!namesIds || videoIds != null)
            // A run with picked videos ignores the paths for them.
            estimate = await EstimateVideosAsync(videoIds, videoIds == null ? roots : [], md5, ct);
        if (videoIds != null && roots.Count == 0)
            return estimate with { Md5 = md5.ToDto() };

        if (!namesIds || imageIds != null)
        {
            var files = db.ImageFiles.AsNoTracking().Where(UnderAny<ImageFile>(file => file.Path, roots));
            if (imageIds != null)
                files = files.Where(file => file.ImageId.HasValue && imageIds.Contains(file.ImageId.Value));
            var images = files.Where(file => file.ImageId.HasValue).Select(file => new ImageRow { Id = file.Id, ImageId = file.ImageId!.Value });
            // A run reads hashes only from a file of its own, so not from inside an archive.
            var own = files.Where(file => file.ZipFileId == null);
            estimate = estimate with
            {
                ImageThumbnails = await SampleAsync(images, row => !thumbnails.HasImageThumbnail(row.ImageId), ct),
                ImagePhashes = await LackingAsync(own, "phash", ct),
            };
            await AddMd5Async(md5, own, ct);
        }

        if (!namesIds)
        {
            var folders = db.Set<Folder>().AsNoTracking().Where(UnderAny<Folder>(folder => folder.Path, roots)).Select(folder => folder.Id);
            var galleryFiles = db.GalleryFiles.AsNoTracking().Where(UnderAny<GalleryFile>(file => file.Path, roots));
            var galleries = db.Galleries.AsNoTracking().Where(gallery => roots.Count == 0
                || (gallery.FolderId.HasValue && folders.Contains(gallery.FolderId.Value))
                || galleryFiles.Any(file => file.GalleryId == gallery.Id));
            var covers = galleries
                .Select(gallery => new ImageRow
                {
                    Id = gallery.Id,
                    ImageId = gallery.CoverImageId
                        ?? db.Set<ImageGallery>().Where(link => link.GalleryId == gallery.Id).Min(link => (int?)link.ImageId)
                        ?? 0,
                })
                .Where(row => row.ImageId != 0);
            estimate = estimate with { GalleryThumbnails = await SampleAsync(covers, row => !thumbnails.HasImageThumbnail(row.ImageId), ct) };
            var galleryIds = galleries.Select(gallery => gallery.Id);
            await AddMd5Async(md5, db.GalleryFiles.AsNoTracking()
                .Where(file => file.ZipFileId == null && file.GalleryId.HasValue && galleryIds.Contains(file.GalleryId.Value)), ct);
        }

        if (!namesIds || audioIds != null)
        {
            var files = db.AudioFiles.AsNoTracking().Where(UnderAny<AudioFile>(file => file.Path, roots));
            if (audioIds != null)
                files = files.Where(file => file.AudioId.HasValue && audioIds.Contains(file.AudioId.Value));
            estimate = estimate with { AudioPhashes = await LackingAsync(files, "phash", ct) };
            await AddMd5Async(md5, files, ct);
        }

        if (!namesIds || textIds != null)
        {
            var files = db.TextFiles.AsNoTracking().Where(UnderAny<TextFile>(file => file.Path, roots));
            if (textIds != null)
                files = files.Where(file => file.TextDocumentId.HasValue && textIds.Contains(file.TextDocumentId.Value));
            estimate = estimate with { TextPhashes = await LackingAsync(files, "phash", ct) };
            await AddMd5Async(md5, files, ct);
        }

        return estimate with { Md5 = md5.ToDto() };
    }

    private async Task<GenerateEstimateDto> EstimateVideosAsync(
        List<int>? videoIds, IReadOnlyList<string> roots, Md5Tally md5, CancellationToken ct)
    {
        // Generate works on each video's primary file. Joined by key from the video, which PostgreSQL plans well;
        // comparing the file's own video's PrimaryFileId misleads its row estimate into nested loops.
        var primaries =
            from video in db.Videos.AsNoTracking()
            join file in db.VideoFiles.AsNoTracking() on video.PrimaryFileId equals (int?)file.Id
            select file;
        var files = primaries.Where(UnderAny<VideoFile>(file => file.Path, roots));
        if (videoIds != null)
            files = files.Where(file => file.VideoId.HasValue && videoIds.Contains(file.VideoId.Value));
        await AddMd5Async(md5, files, ct);

        // A run decodes nothing from a file it already found unreadable (VideoFile.IsSourceKnownUnreadable).
        static IQueryable<VideoFile> Decodable(IQueryable<VideoFile> query)
            => query.Where(file => file.SourceUnreadableAt == null || file.SourceUnreadableSize != file.Size);
        var decodable = Decodable(files);
        IQueryable<VideoRow> Rows(IQueryable<VideoFile> query)
            => query.Select(file => new VideoRow { Id = file.Id, VideoId = file.VideoId!.Value });

        var withoutChosenCover = decodable.Where(file => file.Video!.ImageBlobId == null || file.Video.ImageBlobId.Trim() == "");
        var vr = decodable.Where(file => file.Video!.IsVr);

        // A clip's segments run on its source's timeline, so their frames are made from the source's file.
        // Picking a clip reaches its own segments; picking a video reaches its own and its clips'. A start at or
        // past the end of the file has no frame.
        var sourceFiles = videoIds != null ? Decodable(primaries) : decodable;
        var segments =
            from segment in db.Segments.AsNoTracking().ThatCanHaveGeneratedCovers()
            join host in db.Videos.AsNoTracking() on segment.HostId equals host.Id
            join source in db.Videos.AsNoTracking() on host.ParentVideoId ?? host.Id equals source.Id
            join file in sourceFiles on source.PrimaryFileId equals (int?)file.Id
            where file.Duration <= 0 || segment.StartSec < file.Duration
            select new SegmentRow
            {
                Id = segment.Id,
                SourceId = host.ParentVideoId ?? host.Id,
                HostId = host.Id,
                StartSec = segment.StartSec,
                Kind = segment.Kind == null ? "" : segment.Kind.Trim().ToLower(),
            };
        if (videoIds != null)
            segments = segments.Where(row => videoIds.Contains(row.HostId) || videoIds.Contains(row.SourceId));
        var kinds = await segments.Select(row => row.Kind).Distinct().OrderBy(kind => kind).ToListAsync(ct);
        var segmentCovers = new List<GenerateSegmentKindCountDto>(kinds.Count);
        foreach (var kind in kinds)
        {
            // Segments starting at the same moment share one frame file.
            var frames = segments.Where(row => row.Kind == kind)
                .GroupBy(row => new { row.SourceId, row.StartSec })
                .Select(group => new SegmentRow { Id = group.Min(row => row.Id), SourceId = group.Key.SourceId, StartSec = group.Key.StartSec, Kind = kind });
            var count = await SampleAsync(
                frames,
                row => !File.Exists(thumbnails.GetTimestampedThumbnailPath(row.SourceId, row.StartSec)),
                ct);
            segmentCovers.Add(new GenerateSegmentKindCountDto { Kind = kind, Missing = count.Missing, Total = count.Total, Estimated = count.Estimated });
        }

        return new GenerateEstimateDto
        {
            Thumbnails = await SampleAsync(Rows(withoutChosenCover), row => !File.Exists(thumbnails.GetThumbnailPathForVideo(row.VideoId)), ct),
            Previews = await SampleAsync(Rows(decodable), row => !File.Exists(thumbnails.GetPreviewPath(row.VideoId)), ct),
            Sprites = await SampleAsync(
                Rows(decodable),
                row => !File.Exists(thumbnails.GetSpritePath(row.VideoId)) || !File.Exists(thumbnails.GetSpriteVttPath(row.VideoId)),
                ct),
            VrStereo = await SampleAsync(Rows(vr), row => !videoAssets.HasVrCard(row.VideoId) || !videoAssets.HasVrPreview(row.VideoId), ct),
            SegmentCovers = segmentCovers,
            Phashes = await LackingAsync(decodable, "phash", ct),
        };
    }

    /// <summary>
    /// Counts the rows, and how many are missing their generated file, looking on disk only for an evenly spread
    /// sample of them; the missing share of the sample is then scaled to the total.
    /// </summary>
    private static async Task<GenerateWorkCountDto> SampleAsync<TRow>(IQueryable<TRow> rows, Func<TRow, bool> isMissing, CancellationToken ct)
        where TRow : SampleRow
    {
        var total = await rows.CountAsync(ct);
        if (total == 0)
            return new GenerateWorkCountDto();
        // Every step-th id, so the sample spreads across the library instead of covering its oldest part.
        var step = Math.Max(1, total / SampleSize);
        var sample = await rows.Where(row => row.Id % step == 0).OrderBy(row => row.Id).Take(SampleSize).ToListAsync(ct);
        if (sample.Count == 0)
            sample = await rows.OrderBy(row => row.Id).Take(SampleSize).ToListAsync(ct);
        var missing = sample.Count(isMissing);
        return sample.Count >= total
            ? new GenerateWorkCountDto { Missing = missing, Total = total }
            : new GenerateWorkCountDto { Missing = (int)Math.Round((double)missing * total / sample.Count), Total = total, Estimated = true };
    }

    /// <summary>The files, and how many lack a stored hash of this type.</summary>
    private static async Task<GenerateWorkCountDto> LackingAsync<TFile>(IQueryable<TFile> files, string type, CancellationToken ct)
        where TFile : BaseFileEntity
        => new()
        {
            Total = await files.CountAsync(ct),
            Missing = await Lacking(files, type).CountAsync(ct),
        };

    private static async Task AddMd5Async<TFile>(Md5Tally md5, IQueryable<TFile> files, CancellationToken ct)
        where TFile : BaseFileEntity
    {
        var lacking = Lacking(files, "md5");
        md5.Total += await files.CountAsync(ct);
        md5.TotalBytes += await files.SumAsync(file => file.Size, ct);
        md5.Missing += await lacking.CountAsync(ct);
        md5.MissingBytes += await lacking.SumAsync(file => file.Size, ct);
    }

    private static IQueryable<TFile> Lacking<TFile>(IQueryable<TFile> files, string type) where TFile : BaseFileEntity
        => files.Where(file => !file.Fingerprints.Any(fingerprint => fingerprint.Type == type && fingerprint.Value != ""));

    /// <summary>
    /// Rows whose path is one of the roots or under one, compared without case; every row when there are none.
    /// The same test as GeneratePathFilter.Contains, made in the database.
    /// </summary>
    private static Expression<Func<T, bool>> UnderAny<T>(Expression<Func<T, string>> path, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0)
            return _ => true;
        var lowered = Expression.Call(path.Body, typeof(string).GetMethod(nameof(string.ToLower), Type.EmptyTypes)!);
        var startsWith = typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;
        var body = roots
            .Select(root => (Expression)Expression.OrElse(
                Expression.Equal(lowered, Expression.Constant(root)),
                Expression.Call(lowered, startsWith, Expression.Constant(root + "/"))))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<T, bool>>(body, path.Parameters);
    }
}
