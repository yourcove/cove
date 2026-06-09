using Cove.Core.Entities;
using Cove.Core.Interfaces;

using Microsoft.EntityFrameworkCore;

namespace Cove.Data.Services;

/// <summary>
/// Merges source performers into a target ("primary") performer. Single-value fields keep the target's
/// value and are only filled from a source when the target left them empty; list-style data (scene/image/
/// gallery links, tags, URLs, remote ids, aliases) is unioned. Each source's name is added to the target
/// as an alias, linked faces are repointed to the target, and the sources are deleted.
///
/// The denormalized Video/Image/Gallery PerformerIds arrays are rebuilt automatically by
/// <see cref="CoveContext"/> on save, so this service only manages the join rows.
/// </summary>
public sealed class PerformerMergeService(CoveContext db) : IPerformerMergeService
{
    private readonly CoveContext _db = db;

    public async Task<Performer?> MergeAsync(int targetId, IReadOnlyCollection<int> sourceIds, CancellationToken ct = default)
    {
        var distinctSourceIds = sourceIds.Where(id => id != targetId).Distinct().ToArray();

        var target = await LoadWithRelationsAsync(targetId, ct);
        if (target is null)
            return null;

        if (distinctSourceIds.Length == 0)
            return target;

        var sources = await _db.Performers
            .Include(p => p.Urls)
            .Include(p => p.Aliases)
            .Include(p => p.RemoteIds)
            .Include(p => p.PerformerTags)
            .Include(p => p.VideoPerformers)
            .Include(p => p.ImagePerformers)
            .Include(p => p.GalleryPerformers)
            .Where(p => distinctSourceIds.Contains(p.Id))
            .ToListAsync(ct);

        foreach (var source in sources)
        {
            MoveLinks(target, source);
            MoveTags(target, source);
            UnionUrls(target, source);
            UnionRemoteIds(target, source);
            UnionAliases(target, source);
            FillScalarGaps(target, source);

            // Faces use OnDelete(SetNull); repoint them so the merge keeps the linkage instead of
            // orphaning faces that pointed at a source. Tracked (not ExecuteUpdate) so the repoint and the
            // source delete commit together in the single SaveChanges below.
            var linkedFaces = await _db.Faces.Where(face => face.PerformerId == source.Id).ToListAsync(ct);
            foreach (var linkedFace in linkedFaces)
                linkedFace.PerformerId = target.Id;

            _db.Performers.Remove(source);
        }

        await _db.SaveChangesAsync(ct);
        return await LoadWithRelationsAsync(targetId, ct);
    }

    private Task<Performer?> LoadWithRelationsAsync(int id, CancellationToken ct)
        => _db.Performers
            .Include(p => p.Urls)
            .Include(p => p.Aliases)
            .Include(p => p.RemoteIds)
            .Include(p => p.PerformerTags)
            .Include(p => p.VideoPerformers)
            .Include(p => p.ImagePerformers)
            .Include(p => p.GalleryPerformers)
            .FirstOrDefaultAsync(p => p.Id == id, ct);

    private static void MoveLinks(Performer target, Performer source)
    {
        foreach (var link in source.VideoPerformers)
            if (!target.VideoPerformers.Any(existing => existing.VideoId == link.VideoId))
                target.VideoPerformers.Add(new VideoPerformer { VideoId = link.VideoId, PerformerId = target.Id });

        foreach (var link in source.ImagePerformers)
            if (!target.ImagePerformers.Any(existing => existing.ImageId == link.ImageId))
                target.ImagePerformers.Add(new ImagePerformer { ImageId = link.ImageId, PerformerId = target.Id });

        foreach (var link in source.GalleryPerformers)
            if (!target.GalleryPerformers.Any(existing => existing.GalleryId == link.GalleryId))
                target.GalleryPerformers.Add(new GalleryPerformer { GalleryId = link.GalleryId, PerformerId = target.Id });
    }

    private static void MoveTags(Performer target, Performer source)
    {
        foreach (var tag in source.PerformerTags)
            if (!target.PerformerTags.Any(existing => existing.TagId == tag.TagId))
                target.PerformerTags.Add(new PerformerTag { TagId = tag.TagId, PerformerId = target.Id });
    }

    private static void UnionUrls(Performer target, Performer source)
    {
        foreach (var url in source.Urls)
            if (!string.IsNullOrWhiteSpace(url.Url)
                && !target.Urls.Any(existing => string.Equals(existing.Url, url.Url, StringComparison.OrdinalIgnoreCase)))
                target.Urls.Add(new PerformerUrl { Url = url.Url, PerformerId = target.Id });
    }

    private static void UnionRemoteIds(Performer target, Performer source)
    {
        foreach (var remote in source.RemoteIds)
            if (!target.RemoteIds.Any(existing =>
                    string.Equals(existing.Endpoint, remote.Endpoint, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.RemoteId, remote.RemoteId, StringComparison.OrdinalIgnoreCase)))
                target.RemoteIds.Add(new PerformerRemoteId { Endpoint = remote.Endpoint, RemoteId = remote.RemoteId, PerformerId = target.Id });
    }

    private static void UnionAliases(Performer target, Performer source)
    {
        bool HasAlias(string value) =>
            string.Equals(target.Name, value, StringComparison.OrdinalIgnoreCase)
            || target.Aliases.Any(existing => string.Equals(existing.Alias, value, StringComparison.OrdinalIgnoreCase));

        void AddAlias(string? value)
        {
            var trimmed = value?.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed) && !HasAlias(trimmed))
                target.Aliases.Add(new PerformerAlias { Alias = trimmed, PerformerId = target.Id });
        }

        AddAlias(source.Name);
        foreach (var alias in source.Aliases)
            AddAlias(alias.Alias);
    }

    // Single-value fields: keep the primary's value, only borrow from a source where the primary is empty.
    private static void FillScalarGaps(Performer target, Performer source)
    {
        target.Disambiguation = Coalesce(target.Disambiguation, source.Disambiguation);
        target.Gender ??= source.Gender;
        target.Birthdate ??= source.Birthdate;
        target.DeathDate ??= source.DeathDate;
        target.Ethnicity = Coalesce(target.Ethnicity, source.Ethnicity);
        target.Country = Coalesce(target.Country, source.Country);
        target.EyeColor = Coalesce(target.EyeColor, source.EyeColor);
        target.HairColor = Coalesce(target.HairColor, source.HairColor);
        target.HeightCm ??= source.HeightCm;
        target.Weight ??= source.Weight;
        target.Measurements = Coalesce(target.Measurements, source.Measurements);
        target.FakeTits = Coalesce(target.FakeTits, source.FakeTits);
        target.PenisLength ??= source.PenisLength;
        target.Circumcised ??= source.Circumcised;
        target.CareerStart ??= source.CareerStart;
        target.CareerEnd ??= source.CareerEnd;
        target.Tattoos = Coalesce(target.Tattoos, source.Tattoos);
        target.Piercings = Coalesce(target.Piercings, source.Piercings);
        target.Details = Coalesce(target.Details, source.Details);
        target.ImageBlobId = Coalesce(target.ImageBlobId, source.ImageBlobId);
        target.ImageOverrideBlobId = Coalesce(target.ImageOverrideBlobId, source.ImageOverrideBlobId);
    }

    private static string? Coalesce(string? primary, string? fallback)
        => string.IsNullOrWhiteSpace(primary) ? fallback : primary;
}
