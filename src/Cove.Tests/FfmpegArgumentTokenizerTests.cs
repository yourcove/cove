using System.Diagnostics;
using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// The FfmpegInputArgs/FfmpegOutputArgs settings are raw strings an administrator typed. They used to be
/// spliced into ProcessStartInfo.Arguments, so the rules .NET parsed that string with are the settings'
/// existing behavior, and the tokenizer must reproduce them exactly.
/// </summary>
public class FfmpegArgumentTokenizerTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    [Theory]
    [InlineData(null, new string[0])]
    [InlineData("", new string[0])]
    [InlineData("   \t ", new string[0])]
    [InlineData("-hwaccel cuda", new[] { "-hwaccel", "cuda" })]
    [InlineData("  -hwaccel\t\tcuda  ", new[] { "-hwaccel", "cuda" })]
    [InlineData("-c:v libx264 -preset veryfast -crf 23", new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "23" })]
    [InlineData("-vf \"scale=1280:-2, format=yuv420p\"", new[] { "-vf", "scale=1280:-2, format=yuv420p" })]
    [InlineData("-vf scale=\"1280:-2\"", new[] { "-vf", "scale=1280:-2" })]
    [InlineData("-metadata \"\"", new[] { "-metadata", "" })]
    [InlineData("-metadata \"title=a \"\"b\"\" c\"", new[] { "-metadata", "title=a \"b\" c" })]
    [InlineData("-metadata title=\\\"b\\\"", new[] { "-metadata", "title=\"b\"" })]
    [InlineData("C:\\ffmpeg\\filters", new[] { "C:\\ffmpeg\\filters" })]
    [InlineData("\"C:\\dir with space\\\\\"", new[] { "C:\\dir with space\\" })]
    [InlineData("-vf 'scale=w=1280:h=-2'", new[] { "-vf", "'scale=w=1280:h=-2'" })]
    [InlineData("-x264-params $HOME ~ *", new[] { "-x264-params", "$HOME", "~", "*" })]
    [InlineData("-a\n-b", new[] { "-a\n-b" })]
    public void Split_FollowsTheDocumentedRules(string? input, string[] expected)
    {
        Assert.Equal(expected, FfmpegArgumentTokenizer.Split(input));
    }

    /// <summary>
    /// Checks the tokenizer against the parser the settings actually went through before: .NET's own
    /// ProcessStartInfo.Arguments splitting, observed from the argv a child process receives.
    /// </summary>
    [Theory(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    [InlineData("-hwaccel cuda -hwaccel_output_format cuda")]
    [InlineData("-vf \"scale=1280:-2, format=yuv420p\" -c:v libx264")]
    [InlineData("-vf scale=\"1280:-2\"x \"\" end")]
    [InlineData("-metadata \"title=a \"\"b\"\" c\" -y")]
    [InlineData("a\\\\\"b c\" d\\\\\\\"e f\\g")]
    [InlineData("\"unterminated quote -y")]
    [InlineData("trailing backslash\\")]
    [InlineData("tab\tseparated\t\"and\tquoted\"")]
    [InlineData("'single quotes' stay \"as 'text'\"")]
    public void Split_MatchesHowTheSettingsWereParsedBefore(string input)
    {
        using var recorder = new ArgvRecordingExecutable();
        var startInfo = new ProcessStartInfo(recorder.ExecutablePath) { Arguments = input, UseShellExecute = false };
        using (var process = Process.Start(startInfo)!)
            process.WaitForExit();

        var argv = Assert.Single(recorder.Invocations);
        Assert.Equal(argv, FfmpegArgumentTokenizer.Split(input));
    }
}
