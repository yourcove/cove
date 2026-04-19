using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Cove.Core.Interfaces;

namespace Cove.Api.Services;

/// <summary>
/// Ensures FFmpeg is available on startup. Checks PATH and configured paths first;
/// if not found, downloads a portable build automatically.
/// Sets <see cref="CoveConfiguration.FfmpegPath"/> and <see cref="CoveConfiguration.FfprobePath"/>
/// so all services discover ffmpeg without their own search logic.
/// </summary>
public class FfmpegManagerService(CoveConfiguration config, ILogger<FfmpegManagerService> logger) : IHostedService
{
    // BtbN GPL builds — multi-platform, includes hwaccel support
    // These are stable snapshot releases, updated regularly.
    private const string WinUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string LinuxUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz";
    private const string LinuxArm64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz";
    // macOS: use evermeet.cx static builds (widely used, GPL)
    private const string MacUrl = "https://evermeet.cx/ffmpeg/getrelease/zip";
    private const string MacProbeUrl = "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip";

    private static string ManagedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cove", "ffmpeg");

    private static string FfmpegExe => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    private static string FfprobeExe => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    public async Task StartAsync(CancellationToken ct)
    {
        // 1. Already configured and exists?
        if (!string.IsNullOrEmpty(config.FfmpegPath) && File.Exists(config.FfmpegPath))
        {
            logger.LogInformation("FFmpeg configured at {Path}", config.FfmpegPath);
            EnsureFfprobePath(Path.GetDirectoryName(config.FfmpegPath)!);
            return;
        }

        // 2. In PATH?
        var pathResult = FindInPath(FfmpegExe);
        if (pathResult != null)
        {
            config.FfmpegPath = pathResult;
            EnsureFfprobePath(Path.GetDirectoryName(pathResult)!);
            logger.LogInformation("FFmpeg found in PATH at {Path}", pathResult);
            return;
        }

        // 3. Already downloaded to managed directory?
        var managedFfmpeg = Path.Combine(ManagedDir, FfmpegExe);
        if (File.Exists(managedFfmpeg))
        {
            config.FfmpegPath = managedFfmpeg;
            EnsureFfprobePath(ManagedDir);
            logger.LogInformation("FFmpeg found at managed location {Path}", managedFfmpeg);
            return;
        }

        // 4. Download
        logger.LogInformation("FFmpeg not found — downloading portable build...");
        try
        {
            await DownloadFfmpegAsync(ct);
            config.FfmpegPath = managedFfmpeg;
            EnsureFfprobePath(ManagedDir);
            logger.LogInformation("FFmpeg downloaded to {Path}", ManagedDir);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download FFmpeg — transcoding and thumbnail generation will be unavailable. " +
                                  "Install FFmpeg manually or set Cove.FfmpegPath in configuration.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private void EnsureFfprobePath(string directory)
    {
        if (!string.IsNullOrEmpty(config.FfprobePath) && File.Exists(config.FfprobePath))
            return;

        var probe = Path.Combine(directory, FfprobeExe);
        if (File.Exists(probe))
            config.FfprobePath = probe;
    }

    private async Task DownloadFfmpegAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(ManagedDir);

        if (OperatingSystem.IsWindows())
        {
            await DownloadAndExtractAsync(WinUrl, ".zip", ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await DownloadMacAsync(ct);
        }
        else
        {
            var url = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? LinuxArm64Url : LinuxUrl;
            await DownloadAndExtractAsync(url, ".tar.xz", ct);
        }

        // Verify
        var ffmpeg = Path.Combine(ManagedDir, FfmpegExe);
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException($"Download completed but ffmpeg not found at {ffmpeg}");
    }

    private async Task DownloadAndExtractAsync(string url, string ext, CancellationToken ct)
    {
        var archivePath = Path.Combine(ManagedDir, $"ffmpeg-download{ext}");

        try
        {
            await DownloadFileAsync(url, archivePath, ct);

            if (ext == ".zip")
            {
                // BtbN zip has a top-level directory like ffmpeg-master-latest-win64-gpl/bin/
                using var zip = ZipFile.OpenRead(archivePath);
                foreach (var entry in zip.Entries)
                {
                    // Only extract the bin/ contents — ffmpeg.exe, ffprobe.exe
                    var parts = entry.FullName.Split('/');
                    if (parts.Length >= 3 && parts[1] == "bin" && !string.IsNullOrEmpty(parts[2]))
                    {
                        var dest = Path.Combine(ManagedDir, parts[2]);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }
            }
            else
            {
                // tar.xz — extract bin/ contents
                var tempExtract = Path.Combine(ManagedDir, "_extract");
                Directory.CreateDirectory(tempExtract);

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/bin/tar",
                    Arguments = $"xf \"{archivePath}\" -C \"{tempExtract}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = System.Diagnostics.Process.Start(psi);
                if (proc != null)
                {
                    await proc.WaitForExitAsync(ct);
                }

                // Find and move the bin/ contents
                foreach (var binDir in Directory.GetDirectories(tempExtract, "bin", SearchOption.AllDirectories))
                {
                    foreach (var file in Directory.GetFiles(binDir))
                    {
                        var dest = Path.Combine(ManagedDir, Path.GetFileName(file));
                        File.Move(file, dest, overwrite: true);
                        // Make executable on Unix
                        if (!OperatingSystem.IsWindows())
                        {
                            var chmod = System.Diagnostics.Process.Start("/bin/chmod", $"+x \"{dest}\"");
                            chmod?.WaitForExit();
                        }
                    }
                    break;
                }

                // Cleanup temp
                try { Directory.Delete(tempExtract, recursive: true); } catch { /* best effort */ }
            }
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* best effort */ }
        }
    }

    private async Task DownloadMacAsync(CancellationToken ct)
    {
        // macOS: separate downloads for ffmpeg and ffprobe
        var ffmpegZip = Path.Combine(ManagedDir, "ffmpeg.zip");
        var ffprobeZip = Path.Combine(ManagedDir, "ffprobe.zip");

        try
        {
            await DownloadFileAsync(MacUrl, ffmpegZip, ct);
            ZipFile.ExtractToDirectory(ffmpegZip, ManagedDir, overwriteFiles: true);

            await DownloadFileAsync(MacProbeUrl, ffprobeZip, ct);
            ZipFile.ExtractToDirectory(ffprobeZip, ManagedDir, overwriteFiles: true);

            // Make executable
            foreach (var name in new[] { "ffmpeg", "ffprobe" })
            {
                var path = Path.Combine(ManagedDir, name);
                if (File.Exists(path))
                {
                    var chmod = System.Diagnostics.Process.Start("/bin/chmod", $"+x \"{path}\"");
                    chmod?.WaitForExit();
                }
            }
        }
        finally
        {
            try { File.Delete(ffmpegZip); } catch { /* best effort */ }
            try { File.Delete(ffprobeZip); } catch { /* best effort */ }
        }
    }

    private static async Task DownloadFileAsync(string url, string dest, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Cove/1.0");

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file = File.Create(dest);
        await stream.CopyToAsync(file, ct);
    }

    private static string? FindInPath(string exe)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            try
            {
                var candidate = Path.Combine(dir, exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* skip invalid path entries */ }
        }
        return null;
    }
}
