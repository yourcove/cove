using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class TagBuilder
{
    private readonly List<string> _aliases = [];
    private readonly List<int> _childIds = [];
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<int> _parentIds = [];
    private readonly List<TagRemoteIdDto> _remoteIds = [];
    private string? _color;
    private string? _description;
    private bool _favorite;
    private double? _minOccurrencePercent;
    private double? _minOccurrenceSec;
    private string _name = $"API test tag {Guid.NewGuid():N}";
    private bool _organized;
    private string? _segmentColorOverride;
    private int? _segmentLaneOverride;
    private bool? _showAsSegment;
    private string? _sortName;
    private int? _tagGroupId;

    public TagBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public TagBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public TagBuilder WithSortName(string sortName) { _sortName = sortName; return this; }
    public TagBuilder WithColor(string color) { _color = color; return this; }
    public TagBuilder WithTagGroup(TagGroupDto tagGroup) { _tagGroupId = tagGroup.Id; return this; }
    public TagBuilder WithSegmentDisplay(string color, int lane) { _showAsSegment = true; _segmentColorOverride = color; _segmentLaneOverride = lane; return this; }
    public TagBuilder WithMinimumOccurrence(double seconds, double percent) { _minOccurrenceSec = seconds; _minOccurrencePercent = percent; return this; }
    public TagBuilder WithRemoteId(string endpoint, string remoteId) { _remoteIds.Add(new TagRemoteIdDto(endpoint, remoteId)); return this; }
    public TagBuilder WithCustomField(string key, object value) { _customFields[key] = value; return this; }
    public TagBuilder AsOrganized() { _organized = true; return this; }

    public TagBuilder WithAlias(string alias)
    {
        _aliases.Add(alias);
        return this;
    }

    public TagBuilder WithParent(TagDetailDto parent)
    {
        _parentIds.Add(parent.Id);
        return this;
    }

    public TagBuilder WithChild(TagDetailDto child)
    {
        _childIds.Add(child.Id);
        return this;
    }

    public TagBuilder AsFavorite()
    {
        _favorite = true;
        return this;
    }

    public TagCreateDto Build() => new(
        Name: _name,
        SortName: _sortName,
        Description: _description,
        Favorite: _favorite,
        Aliases: [.. _aliases],
        ParentIds: [.. _parentIds],
        ChildIds: [.. _childIds],
        ShowAsSegment: _showAsSegment,
        SegmentColorOverride: _segmentColorOverride,
        SegmentLaneOverride: _segmentLaneOverride,
        Color: _color,
        TagGroupId: _tagGroupId,
        MinOccurrenceSec: _minOccurrenceSec,
        MinOccurrencePercent: _minOccurrencePercent,
        CustomFields: new Dictionary<string, object>(_customFields),
        RemoteIds: [.. _remoteIds],
        Organized: _organized);
}
