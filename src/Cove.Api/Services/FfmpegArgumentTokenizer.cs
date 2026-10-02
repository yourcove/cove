using System.Text;

namespace Cove.Api.Services;

/// <summary>
/// Splits an administrator-entered ffmpeg argument string (the <c>FfmpegInputArgs</c> and
/// <c>FfmpegOutputArgs</c> settings) into the separate arguments ffmpeg receives.
///
/// The rules are exactly the ones those settings were parsed with on Linux and macOS when they were
/// spliced into <see cref="System.Diagnostics.ProcessStartInfo"/>'s argument string (.NET's own parser,
/// modelled on the Microsoft C runtime's). Windows handed that string to ffmpeg's C runtime, which agrees
/// except for <c>""</c> inside a quoted section, where it also ends the quoted section.
/// <list type="bullet">
/// <item>Arguments are separated by runs of spaces and tabs. Any other character, a newline included,
/// is part of an argument.</item>
/// <item>A double quote starts or ends a quoted section, in which spaces and tabs do not separate.
/// The quote itself is removed, and a quoted section can sit inside a larger argument
/// (<c>-vf "scale=1280:-2"</c> and <c>-vf scale="1280:-2"</c> both pass <c>scale=1280:-2</c>).
/// <c>""</c> is an empty argument.</item>
/// <item>Inside a quoted section, two double quotes in a row produce one literal double quote.</item>
/// <item>Backslashes are literal, except directly before a double quote: there each pair becomes one
/// backslash, and an odd one left over makes the quote literal (<c>\"</c> is a quote character).</item>
/// <item>Single quotes have no meaning here. They are passed through to ffmpeg, whose filter-graph
/// parser gives them their own meaning.</item>
/// </list>
/// Nothing is expanded or substituted: there is no shell, so <c>$VAR</c>, <c>~</c>, globs and
/// redirections reach ffmpeg as literal text.
/// </summary>
internal static class FfmpegArgumentTokenizer
{
    public static IReadOnlyList<string> Split(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var results = new List<string>();
        var i = 0;
        while (i < arguments.Length)
        {
            while (i < arguments.Length && IsSeparator(arguments[i]))
                i++;
            if (i == arguments.Length)
                break;
            results.Add(NextArgument(arguments, ref i));
        }

        return results;
    }

    private static string NextArgument(string arguments, ref int i)
    {
        var current = new StringBuilder();
        var inQuotes = false;

        while (i < arguments.Length)
        {
            var backslashes = 0;
            while (i < arguments.Length && arguments[i] == '\\')
            {
                i++;
                backslashes++;
            }

            if (backslashes > 0)
            {
                if (i >= arguments.Length || arguments[i] != '"')
                {
                    current.Append('\\', backslashes);
                }
                else
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 != 0)
                    {
                        current.Append('"');
                        i++;
                    }
                }

                continue;
            }

            var c = arguments[i];
            if (c == '"')
            {
                if (inQuotes && i < arguments.Length - 1 && arguments[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }

                i++;
                continue;
            }

            if (IsSeparator(c) && !inQuotes)
                break;

            current.Append(c);
            i++;
        }

        return current.ToString();
    }

    private static bool IsSeparator(char c) => c is ' ' or '\t';
}
