using System.Globalization;
using Cove.Api.Services;

namespace Cove.Tests;

/// <summary>
/// Guards against the locale bug where ffmpeg seek/duration arguments were formatted with the current
/// culture: on a comma-decimal locale (de-DE, pt-BR, …) "-ss 697.91" became "-ss 697,91", which ffmpeg
/// rejects ("Invalid duration for option ss", exit -22), breaking generation for every video.
/// </summary>
public class FfmpegArgFormattingTests
{
    [Theory]
    [InlineData("de-DE")]   // comma decimal separator
    [InlineData("pt-BR")]   // comma decimal separator
    [InlineData("fr-FR")]   // comma decimal separator
    [InlineData("en-US")]   // period — control
    public void FrameExtractArgs_AlwaysUseInvariantDecimalSeparator(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);

            var args = FfmpegProcessFrameExtractor.BuildExtractFrameArguments("/media/clip.mp4", 697.91, 320, "/tmp/frame.jpg");

            Assert.Contains("-ss 697.910", args);
            Assert.DoesNotContain("697,91", args);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}
