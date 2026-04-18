using System.Text.Json;
using System.Text.Json.Serialization;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;

namespace Cove.Tests;

public class ImageFilterDeserializationTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    [Fact]
    public void Deserialize_TagsCriterion_FromFrontendFormat()
    {
        // This is the exact JSON the frontend sends after normalizeCriterionPayload
        var json = """
        {
            "findFilter": {"page": 1, "perPage": 40, "direction": "desc"},
            "objectFilter": {
                "tagsCriterion": {
                    "value": [42],
                    "modifier": "includesAll"
                }
            }
        }
        """;

        var result = JsonSerializer.Deserialize<FilteredQueryRequest<ImageFilter>>(json, Options);

        Assert.NotNull(result);
        Assert.NotNull(result.FindFilter);
        Assert.Equal(1, result.FindFilter.Page);
        Assert.Equal(40, result.FindFilter.PerPage);

        Assert.NotNull(result.ObjectFilter);
        Assert.NotNull(result.ObjectFilter.TagsCriterion);
        Assert.Single(result.ObjectFilter.TagsCriterion.Value);
        Assert.Equal(42, result.ObjectFilter.TagsCriterion.Value[0]);
        Assert.Equal(CriterionModifier.IncludesAll, result.ObjectFilter.TagsCriterion.Modifier);
    }

    [Fact]
    public void Deserialize_TagsCriterion_Includes()
    {
        var json = """
        {
            "findFilter": {"page": 1, "perPage": 40},
            "objectFilter": {
                "tagsCriterion": {
                    "value": [1, 2, 3],
                    "modifier": "includes"
                }
            }
        }
        """;

        var result = JsonSerializer.Deserialize<FilteredQueryRequest<ImageFilter>>(json, Options);

        Assert.NotNull(result?.ObjectFilter?.TagsCriterion);
        Assert.Equal(3, result.ObjectFilter.TagsCriterion.Value.Count);
        Assert.Equal(CriterionModifier.Includes, result.ObjectFilter.TagsCriterion.Modifier);
    }

    [Fact]
    public void Deserialize_TagsCriterion_WithDepth()
    {
        var json = """
        {
            "findFilter": {},
            "objectFilter": {
                "tagsCriterion": {
                    "value": [42],
                    "modifier": "includesAll",
                    "depth": -1
                }
            }
        }
        """;

        var result = JsonSerializer.Deserialize<FilteredQueryRequest<ImageFilter>>(json, Options);

        Assert.NotNull(result?.ObjectFilter?.TagsCriterion);
        Assert.Equal(-1, result.ObjectFilter.TagsCriterion.Depth);
    }

    [Fact]
    public void Deserialize_EmptyObjectFilter_ReturnsEmptyImageFilter()
    {
        var json = """
        {
            "findFilter": {"page": 1, "perPage": 40},
            "objectFilter": {}
        }
        """;

        var result = JsonSerializer.Deserialize<FilteredQueryRequest<ImageFilter>>(json, Options);

        Assert.NotNull(result?.ObjectFilter);
        Assert.Null(result.ObjectFilter.TagsCriterion);
    }

    [Fact]
    public void Deserialize_SortDirection_Desc()
    {
        var json = """{"page": 1, "perPage": 40, "direction": "desc"}""";
        var result = JsonSerializer.Deserialize<FindFilter>(json, Options);
        Assert.NotNull(result);
        Assert.Equal(Cove.Core.Enums.SortDirection.Desc, result.Direction);
    }

    [Fact]
    public void Deserialize_AllCriterionModifiers()
    {
        // Verify all modifiers the frontend can send
        var modifiers = new Dictionary<string, CriterionModifier>
        {
            ["equals"] = CriterionModifier.Equals,
            ["notEquals"] = CriterionModifier.NotEquals,
            ["greaterThan"] = CriterionModifier.GreaterThan,
            ["lessThan"] = CriterionModifier.LessThan,
            ["includes"] = CriterionModifier.Includes,
            ["excludes"] = CriterionModifier.Excludes,
            ["includesAll"] = CriterionModifier.IncludesAll,
            ["excludesAll"] = CriterionModifier.ExcludesAll,
            ["isNull"] = CriterionModifier.IsNull,
            ["notNull"] = CriterionModifier.NotNull,
            ["between"] = CriterionModifier.Between,
            ["notBetween"] = CriterionModifier.NotBetween,
            ["matchesRegex"] = CriterionModifier.MatchesRegex,
            ["notMatchesRegex"] = CriterionModifier.NotMatchesRegex,
        };

        foreach (var (jsonValue, expected) in modifiers)
        {
            var json = $$"""{"value": [1], "modifier": "{{jsonValue}}"}""";
            var result = JsonSerializer.Deserialize<MultiIdCriterion>(json, Options);
            Assert.NotNull(result);
            Assert.Equal(expected, result.Modifier);
        }
    }
}
