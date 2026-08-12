using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class TagBuilder
{
    private readonly List<string> _aliases = [];
    private readonly List<int> _childIds = [];
    private readonly List<int> _parentIds = [];
    private string? _description;
    private bool _favorite;
    private string _name = $"API test tag {Guid.NewGuid():N}";

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
        SortName: null,
        Description: _description,
        Favorite: _favorite,
        Aliases: [.. _aliases],
        ParentIds: [.. _parentIds],
        ChildIds: [.. _childIds]);
}
