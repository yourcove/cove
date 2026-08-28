using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Cove.Api.Services;
using Microsoft.Win32.SafeHandles;

namespace Cove.Tests;

public class FileReadRaceTests
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusDeletePending = unchecked((int)0xC0000056);
    private const uint DeleteAccess = 0x00010000;

    [Theory]
    [InlineData(true, AccessDeniedHResult, true, true)]
    [InlineData(false, AccessDeniedHResult, true, false)]
    [InlineData(true, AccessDeniedHResult, false, false)]
    [InlineData(true, unchecked((int)0x80070020), true, false)]
    public void IsWindowsDeletionRace_RequiresAccessDeniedAndADeletionSpecificProbe(
        bool isWindows,
        int hResult,
        bool isMissingOrDeletePending,
        bool expected)
    {
        Assert.Equal(expected, FileReadRace.IsWindowsDeletionRace(isWindows, hResult, isMissingOrDeletePending));
    }

    [Theory]
    [InlineData(2, StatusAccessDenied, true)]
    [InlineData(3, StatusAccessDenied, true)]
    [InlineData(5, StatusDeletePending, true)]
    [InlineData(5, StatusAccessDenied, false)]
    [InlineData(32, StatusDeletePending, false)]
    public void IsMissingOrDeletePending_RequiresNotFoundOrTheDeletePendingStatus(
        int errorCode,
        int ntStatus,
        bool expected)
    {
        Assert.Equal(expected, FileReadRace.IsMissingOrDeletePending(errorCode, ntStatus));
    }

    [Fact]
    public void TryOpenAfterObservation_ReturnsNullForAWindowsDeletionSpecificFailure()
    {
        var observations = new Queue<bool>([true, true]);

        var result = FileReadRace.TryOpenAfterObservation<object>(
            observations.Dequeue,
            static () => throw new UnauthorizedAccessException(),
            observations.Dequeue,
            isWindows: true);

        Assert.Null(result);
        Assert.Empty(observations);
    }

    [Fact]
    public void TryOpenAfterObservation_OpensAndTreatsNotFoundAsMissingWhenTheWindowsPathIsInitiallyMissing()
    {
        var openCalled = false;

        var result = FileReadRace.TryOpenAfterObservation<object>(
            static () => false,
            () =>
            {
                openCalled = true;
                throw new FileNotFoundException();
            },
            static () => true,
            isWindows: true);

        Assert.Null(result);
        Assert.True(openCalled);
    }

    [Fact]
    public void TryOpenAfterObservation_PreservesAccessDeniedWhenTheWindowsPathWasNotObserved()
    {
        var afterFailureProbeCalled = false;

        Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenAfterObservation<object>(
            static () => false,
            static () => throw new UnauthorizedAccessException(),
            () =>
            {
                afterFailureProbeCalled = true;
                return false;
            },
            isWindows: true));

        Assert.False(afterFailureProbeCalled);
    }

    [Fact]
    public void TryOpenAfterObservation_PreservesAccessDeniedOutsideWindowsWithoutProbingPathState()
    {
        var observationCount = 0;

        Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenAfterObservation<object>(
            () =>
            {
                observationCount++;
                return false;
            },
            static () => throw new UnauthorizedAccessException(),
            () =>
            {
                observationCount++;
                return false;
            },
            isWindows: false));

        Assert.Equal(0, observationCount);
    }

    [Fact]
    public void TryOpenAfterObservation_PreservesAccessDeniedWhenTheProbeDoesNotIdentifyDeletion()
    {
        var observations = new Queue<bool>([true, false]);

        Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenAfterObservation<object>(
            observations.Dequeue,
            static () => throw new UnauthorizedAccessException(),
            observations.Dequeue,
            isWindows: true));

        Assert.Empty(observations);
    }

    [Fact]
    public void TryOpenAfterObservation_PreservesAnObservationAccessFailureOnWindows()
    {
        var openCalled = false;

        Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenAfterObservation<object>(
            static () => throw new UnauthorizedAccessException(),
            () =>
            {
                openCalled = true;
                return new object();
            },
            static () => false,
            isWindows: true));

        Assert.False(openCalled);
    }

    [Fact]
    public void TryOpenAfterObservation_PreservesOtherIoFailures()
    {
        var observations = new Queue<bool>([true]);

        Assert.Throws<IOException>(() => FileReadRace.TryOpenAfterObservation<object>(
            observations.Dequeue,
            static () => throw new IOException("sharing violation"),
            static () => false,
            isWindows: true));

        Assert.Empty(observations);
    }

    [Theory]
    [InlineData(typeof(FileNotFoundException))]
    [InlineData(typeof(DirectoryNotFoundException))]
    public void TryOpenAfterObservation_TreatsNotFoundFailuresAsMissing(Type exceptionType)
    {
        var exception = (Exception)Activator.CreateInstance(exceptionType)!;

        var result = FileReadRace.TryOpenAfterObservation<object>(
            static () => true,
            () => throw exception,
            static () => false,
            isWindows: false);

        Assert.Null(result);
    }

    [Fact]
    public void TryOpenRead_ReturnsNullForAMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.bin");

        var stream = FileReadRace.TryOpenRead(path);

        Assert.Null(stream);
    }

    [Fact]
    public void TryOpenRead_PreservesDirectoryAccessFailureOutsideWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenRead(directory));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task TryOpenRead_ReturnsAReadableSeekableStreamForAnExistingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            await using var stream = FileReadRace.TryOpenRead(path, FileShare.Read | FileShare.Delete);

            Assert.NotNull(stream);
            Assert.True(stream.CanRead);
            Assert.True(stream.CanSeek);
            Assert.Equal([1, 2, 3, 4], await ReadAllBytesAsync(stream));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryOpenRead_TreatsWindowsFileDeletionOutcomesAsMissing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            await using var deleteHoldingStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            File.Delete(path);
            Assert.False(File.Exists(path));

            // Windows documents two valid reopen outcomes: traditional pending deletion reports
            // access denied, while POSIX deletion removes the name immediately and reports not found.
            var rawOpenFailure = Record.Exception(() =>
            {
                using var unexpectedStream = File.OpenRead(path);
            });
            Assert.True(
                rawOpenFailure is FileNotFoundException or UnauthorizedAccessException,
                $"Expected a documented Windows deletion outcome, but got {rawOpenFailure?.GetType().FullName ?? "no exception"}.");
            if (rawOpenFailure is UnauthorizedAccessException accessDenied)
            {
                Assert.Equal(AccessDeniedHResult, accessDenied.HResult);
                Assert.True(FileReadRace.IsWindowsDeletionRace(accessDenied, path));
            }
            else
            {
                var notFound = Assert.IsType<FileNotFoundException>(rawOpenFailure);
                Assert.Equal(FileNotFoundHResult, notFound.HResult);
                Assert.Null(FileReadRace.TryOpenRead(path));
            }

            var classifiedOpen = FileReadRace.TryOpenAfterObservation(
                static () => true,
                () => File.OpenRead(path),
                static () => true,
                isWindows: true);
            Assert.Null(classifiedOpen);

            Assert.Null(FileReadRace.TryOpenRead(path, pathWasObserved: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryOpenRead_TreatsATraditionalWindowsDeletePendingFileAsMissing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        try
        {
            await using var deletionHoldingStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using (var deleteHandle = CreateFileW(
                       path,
                       DeleteAccess,
                       FileShare.ReadWrite | FileShare.Delete,
                       IntPtr.Zero,
                       FileMode.Open,
                       FileAttributes.Normal,
                       IntPtr.Zero))
            {
                if (deleteHandle.IsInvalid)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());

                var disposition = new FileDispositionInfo { DeleteFile = true };
                if (!SetFileInformationByHandle(
                        deleteHandle,
                        FileInfoByHandleClass.FileDispositionInfo,
                        ref disposition,
                        (uint)Marshal.SizeOf<FileDispositionInfo>()))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            // Closing the deletion-requesting handle completes the delete operation. The shared
            // read handle keeps the file object alive in the traditional delete-pending state.
            var rawOpenFailure = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var unexpectedStream = File.OpenRead(path);
            });
            Assert.Equal(AccessDeniedHResult, rawOpenFailure.HResult);
            Assert.True(FileReadRace.IsWindowsDeletionRace(rawOpenFailure, path));

            if (File.Exists(path))
                Assert.Null(FileReadRace.TryOpenRead(path));
            else
                Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenRead(path));
            Assert.Null(FileReadRace.TryOpenRead(path, pathWasObserved: true));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task TryOpenRead_PreservesAWindowsFileReadAclFailure()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var path = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var file = new FileInfo(path);
        var originalSecurity = file.GetAccessControl();

        try
        {
            using var currentIdentity = WindowsIdentity.GetCurrent();
            var currentUser = currentIdentity.User;
            Assert.NotNull(currentUser);

            var deniedSecurity = new FileSecurity();
            deniedSecurity.SetSecurityDescriptorBinaryForm(originalSecurity.GetSecurityDescriptorBinaryForm());
            deniedSecurity.AddAccessRule(new FileSystemAccessRule(
                currentUser,
                FileSystemRights.ReadData,
                AccessControlType.Deny));
            file.SetAccessControl(deniedSecurity);

            Assert.True(File.Exists(path));
            var rawOpenFailure = Assert.Throws<UnauthorizedAccessException>(() =>
            {
                using var unexpectedStream = File.OpenRead(path);
            });
            Assert.Equal(AccessDeniedHResult, rawOpenFailure.HResult);
            Assert.False(FileReadRace.IsWindowsDeletionRace(rawOpenFailure, path));
            Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenRead(path));
            Assert.Throws<UnauthorizedAccessException>(() => FileReadRace.TryOpenRead(path, pathWasObserved: true));
        }
        finally
        {
            try
            {
                file.SetAccessControl(originalSecurity);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task TryReadAllTextAsync_ReturnsNullForAMissingFileAndContentForAnExistingFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"cove-read-race-{Guid.NewGuid():N}");
        var path = Path.Combine(directory, "content.txt");
        Directory.CreateDirectory(directory);

        try
        {
            Assert.Null(await FileReadRace.TryReadAllTextAsync(path, TestContext.Current.CancellationToken));

            await File.WriteAllTextAsync(path, "one\ntwo", TestContext.Current.CancellationToken);

            Assert.Equal("one\ntwo", await FileReadRace.TryReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var lines = await FileReadRace.TryReadAllLinesAsync(path, TestContext.Current.CancellationToken);
            Assert.NotNull(lines);
            Assert.Equal(["one", "two"], lines);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, TestContext.Current.CancellationToken);
        return output.ToArray();
    }

    private enum FileInfoByHandleClass
    {
        FileDispositionInfo = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        FileInfoByHandleClass fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);
}
