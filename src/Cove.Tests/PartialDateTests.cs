using Cove.Core.Enums;
using Cove.Core.Helpers;

namespace Cove.Tests;

public sealed class PartialDateTests
{
    [Theory]
    [InlineData("1986", 1986, 1, 1, DatePrecision.Year)]
    [InlineData("1986-02", 1986, 2, 1, DatePrecision.Month)]
    [InlineData("1986-02-14", 1986, 2, 14, DatePrecision.Day)]
    public void TryParse_ValidPartialDate_NormalizesAndPreservesPrecision(
        string input, int year, int month, int day, DatePrecision precision)
    {
        var parsed = PartialDate.TryParse(input, out var result);

        Assert.True(parsed);
        Assert.Equal(new DateOnly(year, month, day), result.Value);
        Assert.Equal(precision, result.Precision);
        Assert.Equal(input, result.ToString());
    }

    [Theory]
    [InlineData("1986-13")]
    [InlineData("1986-02-30")]
    [InlineData("1986-2")]
    [InlineData("not-a-date")]
    public void TryParse_InvalidPartialDate_ReturnsFalse(string input)
        => Assert.False(PartialDate.TryParse(input, out _));
}
