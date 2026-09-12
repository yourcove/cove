using System.Globalization;
using System.Text.RegularExpressions;
using Cove.Core.Interfaces;

namespace Cove.Data.Repositories;

/// <summary>One clock reading for an entire query, including related and expression branches.</summary>
public sealed class RelativeDateEvaluation : IDisposable
{
    private static readonly Regex RelativePattern = new(@"^(?<sign>[+-]?)(?<parts>(?:\d+[ymwdh])+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PartPattern = new(@"(?<amount>\d+)(?<unit>[ymwdh])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly AsyncLocal<DateTime?> Current = new();
    private readonly DateTime? previous;
    public static DateTime UtcNow => Current.Value ?? TimeProvider.System.GetUtcNow().UtcDateTime;

    private RelativeDateEvaluation(TimeProvider clock)
    {
        previous = Current.Value;
        Current.Value ??= clock.GetUtcNow().UtcDateTime;
    }

    public static RelativeDateEvaluation Begin(TimeProvider? clock = null) => new(clock ?? TimeProvider.System);
    public void Dispose() => Current.Value = previous;

    public static DateCriterion Resolve(DateCriterion criterion)
    {
        var value = ResolveDateValue(criterion.Value);
        var value2 = ResolveDateValue(criterion.Value2);
        if (value == criterion.Value && value2 == criterion.Value2) return criterion;
        return new DateCriterion { Modifier = criterion.Modifier, Value = value ?? "", Value2 = value2 };
    }

    public static TimestampCriterion Resolve(TimestampCriterion criterion)
    {
        var value = ResolveTimestampValue(criterion.Value);
        var value2 = ResolveTimestampValue(criterion.Value2);
        if (value == criterion.Value && value2 == criterion.Value2) return criterion;
        return new TimestampCriterion { Modifier = criterion.Modifier, Value = value ?? "", Value2 = value2 };
    }

    public static bool IsRelativeExpression(string? value) => TryParse(value, allowHours: true, out _, out _);

    public static bool IsRelativeExpressionCandidate(string? value)
    {
        if (value?.Trim() is not { Length: > 0 } trimmed) return false;
        return trimmed[0] is '+' or '-' || Regex.IsMatch(trimmed, @"^\d+[ymwdh][\dymwdh+-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string? ResolveDateValue(string? value)
    {
        if (!IsRelativeExpressionCandidate(value)) return value;
        if (!TryParse(value, allowHours: false, out var sign, out var parts)) throw InvalidExpression();
        try
        {
            var date = Add(DateOnly.FromDateTime(UtcNow), sign, parts);
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException) { throw OutOfRange(); }
        catch (OverflowException) { throw OutOfRange(); }
    }

    private static string? ResolveTimestampValue(string? value)
    {
        if (!IsRelativeExpressionCandidate(value)) return value;
        if (!TryParse(value, allowHours: true, out var sign, out var parts)) throw InvalidExpression();
        try { return Add(UtcNow, sign, parts).ToString("O", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { throw OutOfRange(); }
        catch (OverflowException) { throw OutOfRange(); }
    }

    private static bool TryParse(string? value, bool allowHours, out int sign, out RelativePart[] parts)
    {
        sign = 1;
        parts = [];
        if (value == null) return false;
        var match = RelativePattern.Match(value.Trim());
        if (!match.Success) return false;

        var parsed = new List<RelativePart>();
        var previousRank = int.MaxValue;
        foreach (Match partMatch in PartPattern.Matches(match.Groups["parts"].Value))
        {
            if (!int.TryParse(partMatch.Groups["amount"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var amount)) return false;
            var unit = char.ToLowerInvariant(partMatch.Groups["unit"].Value[0]);
            if (unit == 'h' && !allowHours) return false;
            var rank = UnitRank(unit);
            if (rank >= previousRank) return false;
            parsed.Add(new(amount, unit));
            previousRank = rank;
        }

        var signText = match.Groups["sign"].Value;
        if (signText.Length == 0 && (parsed.Count != 1 || parsed[0] != new RelativePart(0, 'd'))) return false;
        sign = signText == "-" ? -1 : 1;
        parts = [.. parsed];
        return true;
    }

    private static DateOnly Add(DateOnly value, int sign, RelativePart[] parts)
    {
        foreach (var part in parts) value = part.Unit switch
        {
            'd' => value.AddDays(checked(sign * part.Amount)),
            'w' => value.AddDays(checked(sign * part.Amount * 7)),
            'm' => value.AddMonths(checked(sign * part.Amount)),
            'y' => value.AddYears(checked(sign * part.Amount)),
            _ => throw InvalidExpression(),
        };
        return value;
    }

    private static DateTime Add(DateTime value, int sign, RelativePart[] parts)
    {
        foreach (var part in parts) value = part.Unit switch
        {
            'd' => value.AddDays(checked(sign * part.Amount)),
            'w' => value.AddDays(checked(sign * part.Amount * 7)),
            'm' => value.AddMonths(checked(sign * part.Amount)),
            'y' => value.AddYears(checked(sign * part.Amount)),
            'h' => value.AddHours(checked(sign * part.Amount)),
            _ => throw InvalidExpression(),
        };
        return value;
    }

    private static int UnitRank(char unit) => unit switch { 'y' => 5, 'm' => 4, 'w' => 3, 'd' => 2, 'h' => 1, _ => 0 };
    private readonly record struct RelativePart(int Amount, char Unit);

    private static RelativeDateFilterException InvalidExpression() => new("Relative date values must start with + or -, followed by whole-number y, m, w, or d parts in largest-to-smallest order; timestamp filters also support h after d (for example -1y3m, +2w, or -6h). 0d means the current date or time.");
    private static RelativeDateFilterException OutOfRange() => new("Relative date value exceeds the supported date range.");
}

public sealed class RelativeDateFilterException(string message) : ArgumentException(message);
