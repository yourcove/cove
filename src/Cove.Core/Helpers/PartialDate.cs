using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Cove.Core.Enums;

namespace Cove.Core.Helpers;

public readonly record struct PartialDate(DateOnly? Value, DatePrecision Precision)
{
    public static bool TryParse(string? input, out PartialDate result)
    {
        result = new PartialDate(null, DatePrecision.Day);
        if (string.IsNullOrWhiteSpace(input))
            return true;

        var value = input.Trim();
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            result = new PartialDate(day, DatePrecision.Day);
            return true;
        }

        if (value.Length == 7
            && int.TryParse(value.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var monthYear)
            && value[4] == '-'
            && int.TryParse(value.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            && month is >= 1 and <= 12)
        {
            result = new PartialDate(new DateOnly(monthYear, month, 1), DatePrecision.Month);
            return true;
        }

        if (value.Length == 4
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year is >= 1 and <= 9999)
        {
            result = new PartialDate(new DateOnly(year, 1, 1), DatePrecision.Year);
            return true;
        }

        return false;
    }

    public static PartialDate Parse(string? input)
        => TryParse(input, out var result)
            ? result
            : throw new FormatException($"Date must use YYYY, YYYY-MM, or YYYY-MM-DD format: {input}");

    public override string? ToString() => Format(Value, Precision);

    public static string? Format(DateOnly? value, DatePrecision precision)
    {
        if (!value.HasValue)
            return null;

        return precision switch
        {
            DatePrecision.Year => value.Value.ToString("yyyy", CultureInfo.InvariantCulture),
            DatePrecision.Month => value.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            _ => value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
    }
}

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Parameter)]
public sealed class PartialDateAttribute : ValidationAttribute
{
    public PartialDateAttribute() : base("The {0} field must use YYYY, YYYY-MM, or YYYY-MM-DD format.") { }

    public override bool IsValid(object? value)
        => value is null || value is string text && PartialDate.TryParse(text, out _);
}
