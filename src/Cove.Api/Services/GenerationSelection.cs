using System.Runtime.CompilerServices;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Data;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Services;

/// <summary>
/// Resolve path scope using bounded scalar projections before loading entity graphs.
/// Managed path comparison preserves the existing separator, case and boundary rules
/// independently of the database provider's collation.
/// </summary>
internal static class GenerationSelection
{
    internal const int BatchSize = 256;

    private sealed class Candidate
    {
        public int Id { get; init; }
        public string? Path { get; init; }
        public List<string>? Paths { get; init; }
    }

    internal static IAsyncEnumerable<int[]> VideosAsync(CoveContext db, GenerateOptionsDto options, CancellationToken ct)
    {
        var query = db.Videos.AsNoTracking();
        var explicitIds = options.VideoIds is { Count: > 0 };
        if (explicitIds)
            query = query.Where(video => options.VideoIds!.Contains(video.Id));
        var paths = explicitIds ? [] : GeneratePathFilter.Normalize(options.Paths);
        return ReadAsync(paths.Count == 0
            ? query.Select(video => new Candidate { Id = video.Id })
            : query.Select(video => new Candidate { Id = video.Id, Paths = video.Files.Select(file => file.Path).ToList() }), paths, ct);
    }

    internal static IAsyncEnumerable<int[]> FilesAsync<TFile>(
        IQueryable<TFile> query, IEnumerable<string>? paths, CancellationToken ct) where TFile : BaseFileEntity
        => ReadAsync(query.AsNoTracking().Select(file => new Candidate { Id = file.Id, Path = file.Path }),
            GeneratePathFilter.Normalize(paths), ct);

    internal static IAsyncEnumerable<int[]> GalleriesAsync(CoveContext db, IEnumerable<string>? paths, CancellationToken ct)
    {
        var normalized = GeneratePathFilter.Normalize(paths);
        var query = db.Galleries.AsNoTracking();
        return ReadAsync(normalized.Count == 0
            ? query.Select(gallery => new Candidate { Id = gallery.Id })
            : query.Select(gallery => new Candidate
            {
                Id = gallery.Id,
                Path = gallery.Folder == null ? null : gallery.Folder.Path,
                Paths = gallery.Files.Select(file => file.Path).ToList(),
            }), normalized, ct);
    }

    private static async IAsyncEnumerable<int[]> ReadAsync(
        IQueryable<Candidate> query, IReadOnlyList<string> paths, [EnumeratorCancellation] CancellationToken ct)
    {
        int? afterId = null;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await query.Where(candidate => afterId == null || candidate.Id > afterId)
                .OrderBy(candidate => candidate.Id).Take(BatchSize).ToListAsync(ct);
            if (page.Count == 0)
                yield break;
            afterId = page[^1].Id;
            var ids = page.Where(candidate => paths.Count == 0
                    || (candidate.Path != null && GeneratePathFilter.Contains(candidate.Path, paths))
                    || (candidate.Paths != null && candidate.Paths.Any(path => GeneratePathFilter.Contains(path, paths))))
                .Select(candidate => candidate.Id).ToArray();
            if (ids.Length > 0)
                yield return ids;
        }
    }
}
