using System.Reflection;
using Cove.Api.Controllers;
using Cove.Core.Common;

namespace Cove.Tests;

public class FileOpsControllerTests
{
    [Fact]
    public void TryNormalizeMoveDestination_PreservesFilesystemRoots()
    {
        var root = Path.GetPathRoot(Environment.CurrentDirectory);
        Assert.False(string.IsNullOrEmpty(root));

        var normalized = FileOpsController.TryNormalizeMoveDestination(root);

        Assert.NotNull(normalized);
        Assert.Equal(root, normalized.Value.NativePath);
        Assert.Equal(FilesystemPaths.ToStoredPath(root), normalized.Value.StoredPath);
    }

    [Fact]
    public void TryNormalizeMoveDestination_CanonicalizesRelativeAndTrailingPaths()
    {
        var input = Path.Combine("relative", "nested") + Path.DirectorySeparatorChar;

        var normalized = FileOpsController.TryNormalizeMoveDestination(input);

        Assert.NotNull(normalized);
        Assert.Equal(Path.GetFullPath(Path.Combine("relative", "nested")), normalized.Value.NativePath);
        Assert.Equal(FilesystemPaths.ToStoredPath(normalized.Value.NativePath), normalized.Value.StoredPath);
        Assert.False(normalized.Value.StoredPath.EndsWith('/'));
    }

    [Fact]
    public void TryNormalizeMoveDestination_RejectsInvalidPaths()
    {
        Assert.Null(FileOpsController.TryNormalizeMoveDestination("invalid\0path"));
    }

    [Fact]
    public void NormalizeLocalPath_OnWindows_RepairsDriveRelativeImportedPaths()
    {
        var method = typeof(FileOpsController).GetMethod("NormalizeLocalPath", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        var normalized = Assert.IsType<string>(method.Invoke(null, ["E:test/Content/video.mp4"]));

        if (OperatingSystem.IsWindows())
        {
            // Only the shape is asserted: which volume the tail lands on is not part of the contract.
            Assert.True(Path.IsPathFullyQualified(normalized));
            Assert.Equal(
                Path.Combine("Content", "video.mp4"),
                Path.GetRelativePath(Path.GetPathRoot(normalized)!, normalized));
        }
        else
        {
            Assert.Equal(Path.GetFullPath("E:test/Content/video.mp4"), normalized);
        }
    }
}
