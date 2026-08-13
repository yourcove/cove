using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Builders;

public sealed class MetadataServiceSceneBuilder
{
    private readonly List<MetadataServiceTag> _tags = [];
    private string _id = Guid.NewGuid().ToString("N");
    private string _title = $"API test metadata scene {Guid.NewGuid():N}";

    public MetadataServiceSceneBuilder WithId(string id)
    {
        _id = id;
        return this;
    }

    public MetadataServiceSceneBuilder WithTitle(string title)
    {
        _title = title;
        return this;
    }

    public MetadataServiceSceneBuilder WithTag(string name)
    {
        _tags.Add(new MetadataServiceTag(Guid.NewGuid().ToString("N"), name));
        return this;
    }

    public MetadataServiceSceneBuilder WithTag(string id, string name)
    {
        _tags.Add(new MetadataServiceTag(id, name));
        return this;
    }

    public MetadataServiceScene Build()
        => new(_id, _title, [.. _tags]);
}
