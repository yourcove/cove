using Cove.Api.Controllers;
using Cove.Core.DTOs;

namespace Cove.Tests;

public sealed class TagDtoMappingTests
{
    [Fact]
    public void OrderForDisplay_UsesGroupPriorityThenTagNameAndPlacesUngroupedTagsLast()
    {
        var tags = new[]
        {
            Tag(1, "Alpha ungrouped"),
            Tag(2, "Zulu high priority", 10),
            Tag(3, "Beta low priority", 20),
            Tag(4, "Alpha high priority", 10),
            Tag(5, "Alpha in second high-priority group", 10, "Zulu Group"),
            Tag(6, "Grouped at maximum priority", int.MaxValue),
            Tag(7, "Zulu by name but alpha by sort name", 10, sortName: "Aardvark"),
        };

        var orderedIds = tags.OrderForDisplay().Select(tag => tag.Id).ToArray();

        Assert.Equal([7, 4, 2, 5, 3, 6, 1], orderedIds);
    }

    private static TagDto Tag(int id, string name, int? groupSortOrder = null, string? groupName = "Alpha Group", string? sortName = null)
        => new(id, name, null, false, [], TagGroupId: groupSortOrder.HasValue ? id : null, TagGroupName: groupSortOrder.HasValue ? groupName : null)
        {
            TagGroupSortOrder = groupSortOrder,
            SortName = sortName,
        };
}
