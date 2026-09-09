using System.Globalization;

namespace Cove.Core.Common;

public sealed record CountryDefinition(string Code, string Name);

public static class CountryCatalog
{
    private static readonly HashSet<string> NonIsoRegionCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AC", "DG", "EA", "IC", "TA",
    };

    private static readonly IReadOnlyDictionary<string, string> ExtraAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Great Britain"] = "GB",
            ["UK"] = "GB",
            ["USA"] = "US",
            ["United States of America"] = "US",
            ["Czech Republic"] = "CZ",
            ["North Korea"] = "KP",
            ["South Korea"] = "KR",
            ["Russia"] = "RU",
            ["Russian Federation"] = "RU",
            ["Slovak Republic"] = "SK",
            ["Taiwan"] = "TW",
            ["Vatican City"] = "VA",
            ["Vietnam"] = "VN",
        };

    private static readonly IReadOnlyDictionary<string, CountryDefinition> ByCode;
    private static readonly IReadOnlyDictionary<string, string> CodesByAlias;

    static CountryCatalog()
    {
        var byCode = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
            .Select(culture => TryCreateRegion(culture.Name))
            .Where(region => region is not null && region.TwoLetterISORegionName.Length == 2 && !NonIsoRegionCodes.Contains(region.TwoLetterISORegionName))
            .Select(region => new CountryDefinition(region!.TwoLetterISORegionName.ToUpperInvariant(), region.EnglishName))
            .GroupBy(country => country.Code, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(country => country.Name, StringComparer.Ordinal).First())
            .ToDictionary(country => country.Code, StringComparer.OrdinalIgnoreCase);

        foreach (var country in new[]
        {
            new CountryDefinition("AQ", "Antarctica"),
            new CountryDefinition("BV", "Bouvet Island"),
            new CountryDefinition("GS", "South Georgia and the South Sandwich Islands"),
            new CountryDefinition("HM", "Heard Island and McDonald Islands"),
            new CountryDefinition("PN", "Pitcairn"),
            new CountryDefinition("TF", "French Southern Territories"),
            new CountryDefinition("UM", "United States Minor Outlying Islands"),
        })
            byCode.TryAdd(country.Code, country);

        byCode.TryAdd("XK", new CountryDefinition("XK", "Kosovo"));
        ByCode = byCode;

        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var country in byCode.Values)
        {
            aliases[country.Code] = country.Code;
            aliases[country.Name] = country.Code;
        }

        foreach (var (alias, code) in ExtraAliases)
            aliases[alias] = code;

        CodesByAlias = aliases;
        NormalizationMappings = aliases
            .Where(pair => !pair.Key.Equals(pair.Value, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Countries = byCode.Values.OrderBy(country => country.Name, StringComparer.Ordinal).ToArray();
    }

    public static IReadOnlyList<CountryDefinition> Countries { get; }
    public static IReadOnlyList<KeyValuePair<string, string>> NormalizationMappings { get; }

    public static CountryDefinition? FindByCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return ByCode.GetValueOrDefault(value.Trim());
    }

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return CodesByAlias.GetValueOrDefault(trimmed) ?? trimmed;
    }

    private static RegionInfo? TryCreateRegion(string cultureName)
    {
        try
        {
            return new RegionInfo(cultureName);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
