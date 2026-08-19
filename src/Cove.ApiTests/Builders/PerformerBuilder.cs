using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class PerformerBuilder
{
    private readonly List<string> _aliases = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _details;
    private bool _favorite;
    private string _name = $"API test performer {Guid.NewGuid():N}";

    public PerformerBuilder WithName(string name)
    {
        _name = name;
        return this;
    }

    public PerformerBuilder WithDetails(string details)
    {
        _details = details;
        return this;
    }

    public PerformerBuilder WithAlias(string alias)
    {
        _aliases.Add(alias);
        return this;
    }

    public PerformerBuilder WithUrl(string url)
    {
        _urls.Add(url);
        return this;
    }

    public PerformerBuilder WithTag(TagDetailDto tag)
    {
        _tagIds.Add(tag.Id);
        return this;
    }

    public PerformerBuilder AsFavorite()
    {
        _favorite = true;
        return this;
    }

    public PerformerCreateDto Build() => new(
        Name: _name,
        Disambiguation: null,
        Gender: null,
        Birthdate: null,
        DeathDate: null,
        Ethnicity: null,
        Country: null,
        EyeColor: null,
        HairColor: null,
        HeightCm: null,
        Weight: null,
        Measurements: null,
        FakeTits: null,
        PenisLength: null,
        Circumcised: null,
        CareerStart: null,
        CareerEnd: null,
        Tattoos: null,
        Piercings: null,
        Favorite: _favorite,
        Rating: null,
        Details: _details,
        Urls: [.. _urls],
        Aliases: [.. _aliases],
        TagIds: [.. _tagIds]);
}
