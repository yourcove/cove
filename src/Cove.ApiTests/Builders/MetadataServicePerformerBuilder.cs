using Cove.ApiTests.Infrastructure;

namespace Cove.ApiTests.Builders;

public sealed class MetadataServicePerformerBuilder
{
    private readonly List<string> _aliases = [];
    private readonly List<string> _urls = [];
    private string? _birthDate;
    private int? _careerStartYear;
    private string? _country;
    private string? _disambiguation;
    private string? _ethnicity;
    private string? _eyeColor;
    private string? _gender;
    private string? _hairColor;
    private int? _heightCm;
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = $"API test metadata performer {Guid.NewGuid():N}";

    public MetadataServicePerformerBuilder WithId(string id) { _id = id; return this; }
    public MetadataServicePerformerBuilder WithName(string name) { _name = name; return this; }
    public MetadataServicePerformerBuilder WithDisambiguation(string disambiguation) { _disambiguation = disambiguation; return this; }
    public MetadataServicePerformerBuilder WithAlias(string alias) { _aliases.Add(alias); return this; }
    public MetadataServicePerformerBuilder WithGender(string gender) { _gender = gender; return this; }
    public MetadataServicePerformerBuilder WithBirthDate(string birthDate) { _birthDate = birthDate; return this; }
    public MetadataServicePerformerBuilder WithEthnicity(string ethnicity) { _ethnicity = ethnicity; return this; }
    public MetadataServicePerformerBuilder WithCountry(string country) { _country = country; return this; }
    public MetadataServicePerformerBuilder WithEyeColor(string eyeColor) { _eyeColor = eyeColor; return this; }
    public MetadataServicePerformerBuilder WithHairColor(string hairColor) { _hairColor = hairColor; return this; }
    public MetadataServicePerformerBuilder WithHeightCm(int heightCm) { _heightCm = heightCm; return this; }
    public MetadataServicePerformerBuilder WithCareerStartYear(int careerStartYear) { _careerStartYear = careerStartYear; return this; }
    public MetadataServicePerformerBuilder WithUrl(string url) { _urls.Add(url); return this; }

    public MetadataServicePerformer Build() => new(
        Id: _id,
        Name: _name,
        Disambiguation: _disambiguation,
        Aliases: [.. _aliases],
        Gender: _gender,
        BirthDate: _birthDate,
        Ethnicity: _ethnicity,
        Country: _country,
        EyeColor: _eyeColor,
        HairColor: _hairColor,
        HeightCm: _heightCm,
        CareerStartYear: _careerStartYear,
        Urls: [.. _urls]);
}
