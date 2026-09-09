using Cove.Core.Common;
using Cove.Core.Entities;

namespace Cove.Tests;

public class FilesystemPathsTests
{
    [Theory]
    [InlineData("", "", "")]
    [InlineData("video.mp4", "video.mp4", "video.mp4")]
    [InlineData("library/video.mp4", "library\\video.mp4", "library/video.mp4")]
    [InlineData("/library/video.mp4", "\\library\\video.mp4", "/library/video.mp4")]
    [InlineData("C:/library/video.mp4", "C:\\library\\video.mp4", "C:/library/video.mp4")]
    [InlineData("//server/share/video.mp4", "\\\\server\\share\\video.mp4", "//server/share/video.mp4")]
    [InlineData("library/nested/deep/video.mp4", "library\\nested\\deep\\video.mp4", "library/nested/deep/video.mp4")]
    [InlineData("library/video.mp4/", "library\\video.mp4\\", "library/video.mp4/")]
    [InlineData("library//video.mp4", "library\\\\video.mp4", "library//video.mp4")]
    [InlineData("library/./video.mp4", "library\\.\\video.mp4", "library/./video.mp4")]
    [InlineData("library/../video.mp4", "library\\..\\video.mp4", "library/../video.mp4")]
    [InlineData("library/a video.mp4", "library\\a video.mp4", "library/a video.mp4")]
    [InlineData("librarý/影片.mp4", "librarý\\影片.mp4", "librarý/影片.mp4")]
    public void ToNativePath_UsesThePlatformSeparator(string storedPath, string windowsExpected, string unixExpected)
    {
        var expected = OperatingSystem.IsWindows() ? windowsExpected : unixExpected;

        Assert.Equal(expected, FilesystemPaths.ToNativePath(storedPath));
    }

    [Theory]
    [InlineData("", "", "")]
    [InlineData("video.mp4", "video.mp4", "video.mp4")]
    [InlineData("library\\video.mp4", "library/video.mp4", "library\\video.mp4")]
    [InlineData("\\library\\video.mp4", "/library/video.mp4", "\\library\\video.mp4")]
    [InlineData("C:\\library\\video.mp4", "C:/library/video.mp4", "C:\\library\\video.mp4")]
    [InlineData("\\\\server\\share\\video.mp4", "//server/share/video.mp4", "\\\\server\\share\\video.mp4")]
    [InlineData("library\\nested\\deep\\video.mp4", "library/nested/deep/video.mp4", "library\\nested\\deep\\video.mp4")]
    [InlineData("library\\video.mp4\\", "library/video.mp4/", "library\\video.mp4\\")]
    [InlineData("library\\\\video.mp4", "library//video.mp4", "library\\\\video.mp4")]
    [InlineData("library\\.\\video.mp4", "library/./video.mp4", "library\\.\\video.mp4")]
    [InlineData("library\\..\\video.mp4", "library/../video.mp4", "library\\..\\video.mp4")]
    [InlineData("library\\a video.mp4", "library/a video.mp4", "library\\a video.mp4")]
    [InlineData("librarý\\影片.mp4", "librarý/影片.mp4", "librarý\\影片.mp4")]
    [InlineData("already/stored/video.mp4", "already/stored/video.mp4", "already/stored/video.mp4")]
    public void ToStoredPath_UsesTheCanonicalSeparator(string nativePath, string windowsExpected, string unixExpected)
    {
        var expected = OperatingSystem.IsWindows() ? windowsExpected : unixExpected;

        Assert.Equal(expected, FilesystemPaths.ToStoredPath(nativePath));
    }

    [Theory]
    [InlineData(null, "video.mp4", "video.mp4")]
    [InlineData("", "video.mp4", "video.mp4")]
    [InlineData("library", "video.mp4", "library/video.mp4")]
    [InlineData("library/", "video.mp4", "library/video.mp4")]
    [InlineData("/library", "video.mp4", "/library/video.mp4")]
    [InlineData("/", "video.mp4", "/video.mp4")]
    [InlineData("C:\\library", "video.mp4", "C:/library/video.mp4")]
    [InlineData("C:\\library\\", "video.mp4", "C:/library/video.mp4")]
    [InlineData("C:/library\\nested", "video.mp4", "C:/library/nested/video.mp4")]
    [InlineData("//server/share", "video.mp4", "//server/share/video.mp4")]
    [InlineData("librarý/影片", "a video.mp4", "librarý/影片/a video.mp4")]
    public void ComputePath_ProducesTheCanonicalStoredFilePath(string? folder, string basename, string expected)
    {
        Assert.Equal(expected, BaseFileEntity.ComputePath(folder, basename));
    }
}
