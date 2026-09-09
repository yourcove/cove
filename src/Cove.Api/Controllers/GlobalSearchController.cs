using System.Linq.Expressions;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Entities;
using Cove.Core.Enums;
using Cove.Core.Helpers;
using Cove.Data;
using Cove.Data.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OutputCaching;
using Microsoft.EntityFrameworkCore;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/search/global")]
[RequiresPermission(
    Permissions.VideosRead,
    Permissions.PerformersRead,
    Permissions.StudiosRead,
    Permissions.TagsRead,
    Permissions.GalleriesRead,
    Permissions.ImagesRead,
    Permissions.GroupsRead,
    Permissions.AudiosRead,
    Permissions.TextsRead,
    Mode = PermissionMode.Any)]
public sealed class GlobalSearchController(
    CoveContext db,
    ICurrentPrincipalAccessor principalAccessor,
    ILogger<GlobalSearchController> logger) : ControllerBase
{
    private sealed class SearchProjection
    {
        public int Id { get; init; }
        public string? Title { get; init; }
        public string? FallbackTitle { get; init; }
        public string? Subtitle { get; init; }
        public DateOnly? Date { get; init; }
        public DatePrecision DatePrecision { get; init; }
        public string? Alias { get; init; }
    }

    [HttpGet]
    [OutputCache(PolicyName = "ShortCache")]
    public async Task<ActionResult<GlobalSearchResponseDto>> Find(
        [FromQuery] string? q,
        [FromQuery] int perType = 8,
        CancellationToken ct = default)
    {
        var term = q?.Trim() ?? string.Empty;
        if (term.Length < 2)
            return Ok(new GlobalSearchResponseDto([], []));

        perType = Math.Clamp(perType, 1, 25);
        var groups = new List<GlobalSearchGroupDto>();
        var failedTypes = new List<string>();

        await AddGroupAsync<Video>(
            "video", EntityKinds.Video, Permissions.VideosRead, term, perType,
            query => ApplyVideoSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, video => video.Title),
            video => new SearchProjection
            {
                Id = video.Id,
                Title = video.Title,
                FallbackTitle = video.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                Subtitle = video.Studio != null ? video.Studio.Name : null,
                Date = video.Date,
                DatePrecision = video.DatePrecision,
            }, groups, failedTypes, ct);

        await AddGroupAsync<Performer>(
            "performer", EntityKinds.Performer, Permissions.PerformersRead, term, perType,
            query => ApplyPerformerSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, performer => performer.Name),
            performer => new SearchProjection
            {
                Id = performer.Id,
                Title = performer.Name,
                Subtitle = performer.Disambiguation,
                Alias = performer.Aliases.OrderBy(alias => alias.Alias).Select(alias => alias.Alias).FirstOrDefault(),
            }, groups, failedTypes, ct);

        await AddGroupAsync<Studio>(
            "studio", EntityKinds.Studio, Permissions.StudiosRead, term, perType,
            query => ApplyStudioSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, studio => studio.Name),
            studio => new SearchProjection
            {
                Id = studio.Id,
                Title = studio.Name,
                Subtitle = studio.Parent != null ? studio.Parent.Name : null,
                Alias = studio.Aliases.OrderBy(alias => alias.Alias).Select(alias => alias.Alias).FirstOrDefault(),
            }, groups, failedTypes, ct);

        await AddGroupAsync<Tag>(
            "tag", EntityKinds.Tag, Permissions.TagsRead, term, perType,
            query => ApplyTagSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, tag => tag.Name),
            tag => new SearchProjection
            {
                Id = tag.Id,
                Title = tag.Name,
                Subtitle = tag.Description,
                Alias = tag.Aliases.OrderBy(alias => alias.Alias).Select(alias => alias.Alias).FirstOrDefault(),
            }, groups, failedTypes, ct);

        await AddGroupAsync<Gallery>(
            "gallery", EntityKinds.Gallery, Permissions.GalleriesRead, term, perType,
            query => ApplyGallerySearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, gallery => gallery.Title),
            gallery => new SearchProjection
            {
                Id = gallery.Id,
                Title = gallery.Title,
                FallbackTitle = gallery.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault()
                    ?? (gallery.Folder != null ? gallery.Folder.Path : null),
                Subtitle = gallery.Studio != null ? gallery.Studio.Name : null,
                Date = gallery.Date,
                DatePrecision = gallery.DatePrecision,
            }, groups, failedTypes, ct);

        await AddGroupAsync<Image>(
            "image", EntityKinds.Image, Permissions.ImagesRead, term, perType,
            query => ApplyImageSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, image => image.Title),
            image => new SearchProjection
            {
                Id = image.Id,
                Title = image.Title,
                FallbackTitle = image.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                Subtitle = image.Studio != null ? image.Studio.Name : null,
                Date = image.Date,
                DatePrecision = image.DatePrecision,
            }, groups, failedTypes, ct);

        await AddGroupAsync<Group>(
            "group", EntityKinds.Group, Permissions.GroupsRead, term, perType,
            query => ApplyGroupSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, group => group.Name),
            group => new SearchProjection
            {
                Id = group.Id,
                Title = group.Name,
                Subtitle = group.Studio != null ? group.Studio.Name : null,
                Date = group.Date,
                DatePrecision = group.DatePrecision,
            }, groups, failedTypes, ct);

        await AddGroupAsync<Audio>(
            "audio", EntityKinds.Audio, Permissions.AudiosRead, term, perType,
            query => ApplyAudioSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, audio => audio.Title),
            audio => new SearchProjection
            {
                Id = audio.Id,
                Title = audio.Title,
                FallbackTitle = audio.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                Subtitle = audio.Studio != null ? audio.Studio.Name : null,
                Date = audio.Date,
                DatePrecision = audio.DatePrecision,
            }, groups, failedTypes, ct);

        await AddGroupAsync<TextDocument>(
            "text", EntityKinds.Text, Permissions.TextsRead, term, perType,
            query => ApplyTextSearch(query, term),
            query => FullTextSearchHelpers.OrderByExactThenRelevance(db, query, term, text => text.Title),
            text => new SearchProjection
            {
                Id = text.Id,
                Title = text.Title,
                FallbackTitle = text.Files.OrderBy(file => file.Id).Select(file => file.Basename).FirstOrDefault(),
                Subtitle = text.Studio != null ? text.Studio.Name : null,
                Date = text.Date,
                DatePrecision = text.DatePrecision,
            }, groups, failedTypes, ct);

        return Ok(new GlobalSearchResponseDto(groups, failedTypes));
    }

    private async Task AddGroupAsync<TEntity>(
        string type,
        string entityKind,
        string permission,
        string term,
        int limit,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> applySearch,
        Func<IQueryable<TEntity>, IQueryable<TEntity>> applyOrder,
        Expression<Func<TEntity, SearchProjection>> projection,
        ICollection<GlobalSearchGroupDto> groups,
        ICollection<string> failedTypes,
        CancellationToken ct)
        where TEntity : BaseEntity
    {
        if (!CanSearch(permission, entityKind))
            return;

        try
        {
            var baseQuery = await ReadScopeListOptimization.ApplyAsync<TEntity>(db, entityKind, permission, ct);
            var rows = await applyOrder(applySearch(baseQuery))
                .Take(limit)
                .Select(projection)
                .ToListAsync(ct);

            if (rows.Count == 0)
                return;

            var items = rows.Select(row => new GlobalSearchItemDto(
                row.Id,
                FirstNonEmpty(row.Title, LeafName(row.FallbackTitle)) ?? $"{TitleFor(type)} {row.Id}",
                !string.IsNullOrWhiteSpace(row.Alias)
                    ? $"Aliases: {row.Alias}"
                    : FirstNonEmpty(row.Subtitle, PartialDate.Format(row.Date, row.DatePrecision)))).ToList();
            groups.Add(new GlobalSearchGroupDto(type, items));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Global search failed for {EntityType} and term length {TermLength}", type, term.Length);
            failedTypes.Add(type);
        }
    }

    private bool CanSearch(string permission, string entityKind)
    {
        var principal = principalAccessor.Current;
        return principal is null || principal.Has(permission) || principal.ReadGrantedEntityKinds.Contains(entityKind);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? LeafName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator >= 0 ? normalized[(separator + 1)..] : normalized;
    }

    private static string TitleFor(string type) => type switch
    {
        "text" => "Text",
        _ => char.ToUpperInvariant(type[0]) + type[1..],
    };

    private IQueryable<Video> ApplyVideoSearch(IQueryable<Video> query, string term)
        => FullTextSearchHelpers.Apply(db, query, term,
            video => video.Title, video => video.Details, video => video.Code, video => video.FileSearchText, video => video.SearchText);

    private IQueryable<Performer> ApplyPerformerSearch(IQueryable<Performer> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term,
            performer => performer.Name, performer => performer.Disambiguation, performer => performer.Details, performer => performer.SearchText);
        var lower = term.ToLowerInvariant();
        var aliases = FullTextSearchHelpers.UnionMatchesById(
            query,
            text,
            query.Where(performer => performer.Aliases.Any(alias => alias.Alias.ToLower().Contains(lower))));
        return FullTextSearchHelpers.ApplyRelationalMatches(aliases, query, term,
            tagSelectors: [performer => performer.PerformerTags.Where(link => link.Tag != null).Select(link => link.Tag!)]);
    }

    private IQueryable<Studio> ApplyStudioSearch(IQueryable<Studio> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term, studio => studio.Name, studio => studio.Details, studio => studio.SearchText);
        var lower = term.ToLowerInvariant();
        var aliases = FullTextSearchHelpers.UnionMatchesById(
            query,
            text,
            query.Where(studio => studio.Aliases.Any(alias => alias.Alias.ToLower().Contains(lower))));
        return FullTextSearchHelpers.ApplyRelationalMatches(aliases, query, term,
            tagSelectors: [studio => studio.StudioTags.Where(link => link.Tag != null).Select(link => link.Tag!)]);
    }

    private IQueryable<Tag> ApplyTagSearch(IQueryable<Tag> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term, tag => tag.Name, tag => tag.SortName, tag => tag.Description, tag => tag.SearchText);
        var lower = term.ToLowerInvariant();
        return FullTextSearchHelpers.UnionMatchesById(
            query,
            text,
            query.Where(tag => tag.Aliases.Any(alias => alias.Alias.ToLower().Contains(lower))));
    }

    private IQueryable<Gallery> ApplyGallerySearch(IQueryable<Gallery> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term,
            gallery => gallery.Title, gallery => gallery.Code, gallery => gallery.Details, gallery => gallery.Photographer, gallery => gallery.SearchText);
        var relational = FullTextSearchHelpers.ApplyRelationalMatches(text, query, term,
            tagSelectors: [gallery => gallery.GalleryTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [gallery => gallery.GalleryPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        var path = term.ToLowerInvariant().Replace('\\', '/');
        return FullTextSearchHelpers.UnionMatchesById(
            query,
            relational,
            query.Where(gallery =>
                gallery.Files.Any(file => file.Path.ToLower().Contains(path))
                || (gallery.Folder != null && gallery.Folder.Path.ToLower().Contains(path))));
    }

    private IQueryable<Image> ApplyImageSearch(IQueryable<Image> query, string term)
        => FullTextSearchHelpers.Apply(db, query, term,
            image => image.Title, image => image.Details, image => image.Code, image => image.Photographer, image => image.FileSearchText, image => image.SearchText);

    private IQueryable<Group> ApplyGroupSearch(IQueryable<Group> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term,
            group => group.Name, group => group.Aliases, group => group.Director, group => group.Synopsis, group => group.SearchText);
        return FullTextSearchHelpers.ApplyRelationalMatches(text, query, term,
            tagSelectors: [group => group.GroupTags.Where(link => link.Tag != null).Select(link => link.Tag!)]);
    }

    private IQueryable<Audio> ApplyAudioSearch(IQueryable<Audio> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term,
            audio => audio.Title, audio => audio.Code, audio => audio.Details, audio => audio.FileSearchText, audio => audio.SearchText);
        var relational = FullTextSearchHelpers.ApplyRelationalMatches(text, query, term,
            tagSelectors: [audio => audio.AudioTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [audio => audio.AudioPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        return FullTextSearchHelpers.ApplyFilePathMatch(relational, query, term, audio => audio.Files);
    }

    private IQueryable<TextDocument> ApplyTextSearch(IQueryable<TextDocument> query, string term)
    {
        var text = FullTextSearchHelpers.Apply(db, query, term,
            document => document.Title, document => document.Code, document => document.Details, document => document.FileSearchText, document => document.SearchText);
        var relational = FullTextSearchHelpers.ApplyRelationalMatches(text, query, term,
            tagSelectors: [document => document.TextTags.Where(link => link.Tag != null).Select(link => link.Tag!)],
            performerSelectors: [document => document.TextPerformers.Where(link => link.Performer != null).Select(link => link.Performer!)]);
        return FullTextSearchHelpers.ApplyFilePathMatch(relational, query, term, document => document.Files);
    }
}
