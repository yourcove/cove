using Cove.Core.DTOs;
using Cove.Core.Entities;

namespace Cove.Api.Controllers;

internal static class TagDtoMapping
{
    public static string EffectiveSortName(Tag tag) => tag.SortName ?? tag.Name;

    public static TagDto MapTagDto(Tag tag, List<TagProvenanceDto>? provenance = null)
        => new(
            tag.Id,
            tag.Name,
            tag.Description,
            tag.Favorite,
            tag.Aliases.Select(alias => alias.Alias).ToList(),
            tag.ShowAsSegment,
            tag.SegmentColorOverride,
            tag.SegmentLaneOverride,
            provenance,
            tag.Color,
            tag.TagGroupId,
            tag.TagGroup?.Name,
            tag.TagGroup?.Color,
            tag.MinOccurrenceSec,
            tag.MinOccurrencePercent);
}