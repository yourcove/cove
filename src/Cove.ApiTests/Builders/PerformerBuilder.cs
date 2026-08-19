using Cove.Core.DTOs;

namespace Cove.ApiTests.Builders;

public sealed class PerformerBuilder
{
    private readonly List<string> _aliases = [];
    private readonly Dictionary<string, object> _customFields = [];
    private readonly List<PerformerRemoteIdDto> _remoteIds = [];
    private readonly List<int> _tagIds = [];
    private readonly List<string> _urls = [];
    private string? _birthdate;
    private string? _careerEnd;
    private string? _careerStart;
    private string? _circumcised;
    private string? _country;
    private string? _deathDate;
    private string? _details;
    private string? _disambiguation;
    private string? _ethnicity;
    private string? _eyeColor;
    private string? _fakeTits;
    private bool _favorite;
    private string? _gender;
    private string? _hairColor;
    private int? _heightCm;
    private string? _measurements;
    private string _name = $"API test performer {Guid.NewGuid():N}";
    private double? _penisLength;
    private string? _piercings;
    private int? _rating;
    private string? _tattoos;
    private int? _weight;

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

    public PerformerBuilder WithDisambiguation(string disambiguation)
    {
        _disambiguation = disambiguation;
        return this;
    }

    public PerformerBuilder WithGender(string gender)
    {
        _gender = gender;
        return this;
    }

    public PerformerBuilder WithBirthdate(string birthdate)
    {
        _birthdate = birthdate;
        return this;
    }

    public PerformerBuilder WithDeathDate(string deathDate)
    {
        _deathDate = deathDate;
        return this;
    }

    public PerformerBuilder WithEthnicity(string ethnicity)
    {
        _ethnicity = ethnicity;
        return this;
    }

    public PerformerBuilder WithCountry(string country)
    {
        _country = country;
        return this;
    }

    public PerformerBuilder WithEyeColor(string eyeColor)
    {
        _eyeColor = eyeColor;
        return this;
    }

    public PerformerBuilder WithHairColor(string hairColor)
    {
        _hairColor = hairColor;
        return this;
    }

    public PerformerBuilder WithHeightCm(int heightCm)
    {
        _heightCm = heightCm;
        return this;
    }

    public PerformerBuilder WithWeight(int weight)
    {
        _weight = weight;
        return this;
    }

    public PerformerBuilder WithMeasurements(string measurements)
    {
        _measurements = measurements;
        return this;
    }

    public PerformerBuilder WithFakeTits(string fakeTits)
    {
        _fakeTits = fakeTits;
        return this;
    }

    public PerformerBuilder WithPenisLength(double penisLength)
    {
        _penisLength = penisLength;
        return this;
    }

    public PerformerBuilder WithCircumcised(string circumcised)
    {
        _circumcised = circumcised;
        return this;
    }

    public PerformerBuilder WithCareerStart(string careerStart)
    {
        _careerStart = careerStart;
        return this;
    }

    public PerformerBuilder WithCareerEnd(string careerEnd)
    {
        _careerEnd = careerEnd;
        return this;
    }

    public PerformerBuilder WithTattoos(string tattoos)
    {
        _tattoos = tattoos;
        return this;
    }

    public PerformerBuilder WithPiercings(string piercings)
    {
        _piercings = piercings;
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

    public PerformerBuilder WithRemoteId(string endpoint, string remoteId)
    {
        _remoteIds.Add(new PerformerRemoteIdDto(endpoint, remoteId));
        return this;
    }

    public PerformerBuilder WithCustomField(string key, object value)
    {
        _customFields[key] = value;
        return this;
    }

    public PerformerBuilder WithRating(int rating)
    {
        _rating = rating;
        return this;
    }

    public PerformerBuilder AsFavorite()
    {
        _favorite = true;
        return this;
    }

    public PerformerCreateDto Build() => new(
        Name: _name,
        Disambiguation: _disambiguation,
        Gender: _gender,
        Birthdate: _birthdate,
        DeathDate: _deathDate,
        Ethnicity: _ethnicity,
        Country: _country,
        EyeColor: _eyeColor,
        HairColor: _hairColor,
        HeightCm: _heightCm,
        Weight: _weight,
        Measurements: _measurements,
        FakeTits: _fakeTits,
        PenisLength: _penisLength,
        Circumcised: _circumcised,
        CareerStart: _careerStart,
        CareerEnd: _careerEnd,
        Tattoos: _tattoos,
        Piercings: _piercings,
        Favorite: _favorite,
        Rating: _rating,
        Details: _details,
        Urls: [.. _urls],
        Aliases: [.. _aliases],
        TagIds: [.. _tagIds],
        RemoteIds: [.. _remoteIds],
        CustomFields: new Dictionary<string, object>(_customFields));
}
