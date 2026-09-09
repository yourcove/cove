using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Cove.Api.Services;

/// <summary>
/// Opens files whose callers already define a vanished path as a normal "not found" outcome. Windows
/// reports an open against a delete-pending file as access denied; that result is treated as missing
/// only after the same path was observed and a metadata-only native reopen identifies deletion.
/// </summary>
internal static class FileReadRace
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int StatusDeletePending = unchecked((int)0xC0000056);
    private static readonly IntPtr InvalidHandleValue = new(-1);

    /// <param name="pathWasObserved">
    /// Set only when the caller has already positively resolved this same path. That observation is
    /// required before a later Windows access-denied open can be classified as a deletion race.
    /// </param>
    public static FileStream? TryOpenRead(
        string path,
        FileShare share = FileShare.Read,
        int bufferSize = 81920,
        FileOptions options = FileOptions.Asynchronous,
        bool pathWasObserved = false)
        => TryOpenAfterObservation(
            () => pathWasObserved || File.Exists(path),
            () => new FileStream(
                path,
                new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = share,
                    BufferSize = bufferSize,
                    Options = options,
                }),
            () => IsMissingOrDeletePending(path),
            OperatingSystem.IsWindows());

    internal static T? TryOpenAfterObservation<T>(
        Func<bool> observeBefore,
        Func<T> open,
        Func<bool> isMissingOrDeletePending,
        bool isWindows)
        where T : class
    {
        // Other platforms open directly so their permission behavior is unchanged. On Windows, the
        // positive observation prevents an inaccessible path from being mistaken for a vanished one.
        var observedBefore = isWindows && observeBefore();

        try
        {
            return open();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (UnauthorizedAccessException ex) when (
            isWindows
            && observedBefore
            && ex.HResult == AccessDeniedHResult
            && isMissingOrDeletePending())
        {
            return null;
        }
    }

    public static async Task<string?> TryReadAllTextAsync(
        string path,
        CancellationToken ct = default,
        bool pathWasObserved = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var stream = TryOpenRead(path, pathWasObserved: pathWasObserved);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task<string[]?> TryReadAllLinesAsync(
        string path,
        CancellationToken ct = default,
        bool pathWasObserved = false)
    {
        ct.ThrowIfCancellationRequested();
        await using var stream = TryOpenRead(path, pathWasObserved: pathWasObserved);
        if (stream == null)
            return null;

        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(ct) is { } line)
            lines.Add(line);
        return lines.ToArray();
    }

    /// <summary>
    /// Classifies an access-denied failure after the caller has already positively resolved
    /// <paramref name="path"/>.
    /// </summary>
    public static bool IsWindowsDeletionRace(UnauthorizedAccessException exception, string path)
        => OperatingSystem.IsWindows()
            && exception.HResult == AccessDeniedHResult
            && IsMissingOrDeletePending(path);

    internal static bool IsWindowsDeletionRace(bool isWindows, int hResult, bool isMissingOrDeletePending)
        => isWindows && hResult == AccessDeniedHResult && isMissingOrDeletePending;

    private static bool IsMissingOrDeletePending(string path)
    {
        // Resolve this P/Invoke before CreateFileW. Lazy native symbol resolution can otherwise
        // replace the thread's last NT status before the first RtlGetLastNtStatus call observes it.
        _ = RtlGetLastNtStatus();

        var rawHandle = CreateFileW(
            path,
            desiredAccess: 0,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            FileMode.Open,
            FileAttributes.Normal,
            IntPtr.Zero);

        if (rawHandle != InvalidHandleValue)
        {
            using var handle = new SafeFileHandle(rawHandle, ownsHandle: true);
            return false;
        }

        // Win32 maps STATUS_DELETE_PENDING to ERROR_ACCESS_DENIED. Capture the more specific NT
        // status before constructing any managed handle so a real permission failure remains distinct.
        var errorCode = Marshal.GetLastPInvokeError();
        var ntStatus = RtlGetLastNtStatus();
        return IsMissingOrDeletePending(errorCode, ntStatus);
    }

    internal static bool IsMissingOrDeletePending(int errorCode, int ntStatus)
        => errorCode is ErrorFileNotFound or ErrorPathNotFound
            || errorCode == ErrorAccessDenied && ntStatus == StatusDeletePending;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        FileMode creationDisposition,
        FileAttributes flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlGetLastNtStatus();
}
