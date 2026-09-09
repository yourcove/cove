using System.Diagnostics;

namespace Cove.ApiTests.Infrastructure;

public sealed class ApiTestFileManagerRecorder
{
    private readonly string _command;
    private readonly IReadOnlyList<string> _arguments;
    private readonly string _logPath;

    private ApiTestFileManagerRecorder(
        string command,
        IReadOnlyList<string> arguments,
        string logPath)
    {
        _command = command;
        _arguments = arguments;
        _logPath = logPath;
    }

    internal static ApiTestFileManagerRecorder Create(string dataRoot)
    {
        var recorderDirectory = Path.Combine(dataRoot, "file-manager-recorder");
        Directory.CreateDirectory(recorderDirectory);
        var logPath = Path.Combine(recorderDirectory, "invocations.tsv");

        if (OperatingSystem.IsWindows())
        {
            var scriptPath = Path.Combine(recorderDirectory, "record.ps1");
            File.WriteAllText(scriptPath, """
                param([string]$TargetKind, [string]$TargetPath)
                Add-Content -LiteralPath $env:COVE_API_TEST_FILE_MANAGER_LOG -Value ($TargetKind + "`t" + $TargetPath)
                """);
            return new ApiTestFileManagerRecorder(
                "powershell.exe",
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-File", scriptPath],
                logPath);
        }

        var shellScriptPath = Path.Combine(recorderDirectory, "record.sh");
        File.WriteAllText(shellScriptPath, """
            set -eu
            printf '%s\t%s\n' "$1" "$2" >> "$COVE_API_TEST_FILE_MANAGER_LOG"
            """);
        return new ApiTestFileManagerRecorder("/bin/sh", [shellScriptPath], logPath);
    }

    internal void Configure(ProcessStartInfo startInfo)
    {
        startInfo.Environment["COVE__FileManager__Command"] = _command;
        for (var index = 0; index < _arguments.Count; index++)
            startInfo.Environment[$"COVE__FileManager__Arguments__{index}"] = _arguments[index];
        startInfo.Environment["COVE_API_TEST_FILE_MANAGER_LOG"] = _logPath;
    }

    internal void Reset()
    {
        if (File.Exists(_logPath))
            File.Delete(_logPath);
    }

    internal IReadOnlyList<FileManagerInvocation> ReadInvocations()
    {
        if (!File.Exists(_logPath))
            return [];

        try
        {
            return File.ReadAllLines(_logPath)
                .Select(ParseInvocation)
                .ToList();
        }
        catch (IOException)
        {
            return [];
        }
    }

    internal async Task<IReadOnlyList<FileManagerInvocation>> WaitForInvocationsAsync(
        int expectedCount,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            var invocations = ReadInvocations();
            if (invocations.Count >= expectedCount)
                return invocations;
            await Task.Delay(25, timeout.Token);
        }
    }

    private static FileManagerInvocation ParseInvocation(string line)
    {
        var separator = line.IndexOf('\t');
        if (separator <= 0 || separator == line.Length - 1)
            throw new InvalidOperationException("The file-manager recorder wrote an invalid invocation.");
        return new FileManagerInvocation(line[..separator], line[(separator + 1)..]);
    }
}

public sealed record FileManagerInvocation(string TargetKind, string TargetPath);
