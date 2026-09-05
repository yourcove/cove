using Cove.Core.Common;
using Cove.Core.Entities;

namespace Cove.Tests;

public class CountryCatalogTests
{
    [Theory]
    [InlineData("us", "US")]
    [InlineData("United States", "US")]
    [InlineData("United States of America", "US")]
    [InlineData("UK", "GB")]
    [InlineData("Great Britain", "GB")]
    [InlineData("Czech Republic", "CZ")]
    [InlineData("South Korea", "KR")]
    public void Normalize_RecognizedCountry_ReturnsIsoCode(string input, string expected)
    {
        Assert.Equal(expected, CountryCatalog.Normalize(input));
    }

    [Theory]
    [InlineData("  Atlantis  ", "Atlantis")]
    [InlineData("ZZ", "ZZ")]
    [InlineData("Jewish", "Jewish")]
    [InlineData("American", "American")]
    [InlineData("British", "British")]
    [InlineData("Canadian", "Canadian")]
    [InlineData("German", "German")]
    [InlineData("Ukrainian", "Ukrainian")]
    [InlineData("England", "England")]
    public void Normalize_CustomValue_PreservesTrimmedText(string input, string expected)
    {
        Assert.Equal(expected, CountryCatalog.Normalize(input));
        Assert.Null(CountryCatalog.FindByCode(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_BlankValue_ReturnsNull(string? input)
    {
        Assert.Null(CountryCatalog.Normalize(input));
    }

    [Fact]
    public void Countries_ContainRecognizedIsoEntries()
    {
        var unitedStates = Assert.Single(CountryCatalog.Countries, country => country.Code == "US");

        Assert.Equal("United States", unitedStates.Name);
        Assert.True(CountryCatalog.Countries.Count >= 240);
    }

    [Fact]
    public void PerformerCountry_NormalizesEveryAssignment()
    {
        var performer = new Performer { Country = "  United Kingdom  " };
        Assert.Equal("GB", performer.Country);

        performer.Country = "  Atlantis  ";
        Assert.Equal("Atlantis", performer.Country);
    }
}
