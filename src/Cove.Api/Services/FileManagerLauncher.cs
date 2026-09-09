using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Cove.Api.Services;

public interface IFileManagerLauncher
{
    void RevealFile(string filePath);

    void RevealFolder(string folderPath);
}

public sealed class FileManagerLauncher : IFileManagerLauncher
{
    private readonly string? _customCommand;
    private readonly IReadOnlyList<string> _customArguments;

    public FileManagerLauncher()
    {
        _customArguments = [];
    }

    public FileManagerLauncher(IConfiguration configuration)
    {
        _customCommand = configuration.GetValue<string>("Cove:FileManager:Command")?.Trim();
        _customArguments = configuration
            .GetSection("Cove:FileManager:Arguments")
            .Get<string[]>() ?? [];
    }

    public void RevealFile(string filePath)
    {
        if (TryLaunchCustom("file", filePath))
            return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var startInfo = CreateStartInfo("explorer.exe");
            startInfo.ArgumentList.Add("/select,");
            startInfo.ArgumentList.Add(filePath);
            Start(startInfo);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var startInfo = CreateStartInfo("open");
            startInfo.ArgumentList.Add("-R");
            startInfo.ArgumentList.Add(filePath);
            Start(startInfo);
        }
        else
        {
            var startInfo = CreateStartInfo("xdg-open");
            startInfo.ArgumentList.Add(Path.GetDirectoryName(filePath) ?? filePath);
            Start(startInfo);
        }
    }

    public void RevealFolder(string folderPath)
    {
        if (TryLaunchCustom("folder", folderPath))
            return;

        var command = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "explorer.exe"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "open"
                : "xdg-open";
        var startInfo = CreateStartInfo(command);
        startInfo.ArgumentList.Add(folderPath);
        Start(startInfo);
    }

    private bool TryLaunchCustom(string targetKind, string path)
    {
        if (string.IsNullOrWhiteSpace(_customCommand))
            return false;

        var startInfo = CreateStartInfo(_customCommand);
        foreach (var argument in _customArguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add(targetKind);
        startInfo.ArgumentList.Add(path);
        Start(startInfo);
        return true;
    }

    private static ProcessStartInfo CreateStartInfo(string command) => new(command)
    {
        UseShellExecute = false,
    };

    private static void Start(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"The file-manager command '{startInfo.FileName}' did not start.");
    }
}
