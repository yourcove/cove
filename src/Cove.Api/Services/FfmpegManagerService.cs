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
///
/// On Windows and Linux the downloaded build is the BtbN "gpl-shared" variant which
/// includes the FFmpeg shared libraries (.dll / .so) alongside the binaries.
/// FFmpeg.AutoGen's DynamicallyLoadedBindings needs to dlopen those files for
/// in-process frame extraction; the static CLI-only builds do NOT provide them.
/// macOS uses evermeet.cx standalone binaries (no shared build available) and falls
/// back to the process-spawn path in FingerprintService / ThumbnailService.
/// </summary>
public class FfmpegManagerService(CoveConfiguration config, ILogger<FfmpegManagerService> logger) : IHostedService
{
    // BtbN GPL-SHARED builds — includes shared libraries (.dll/.so) required by FFmpeg.AutoGen.
    private const string WinUrl      = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl-shared.zip";
    private const string LinuxUrl    = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl-shared.tar.xz";
    private const string LinuxArm64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl-shared.tar.xz";
    // macOS: evermeet.cx static builds (no shared-library build publicly available)
    private const string MacUrl      = "https://evermeet.cx/ffmpeg/getrelease/zip";
    private const string MacProbeUrl = "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip";

    // Marker written to ManagedDir after a successful shared-library download.
    // If the binary exists but this marker is absent the install is a legacy standalone
    // build and we re-download to get the shared libraries.
    private const string SharedMarker = "_cove_shared";

    private static string ManagedDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cove", "ffmpeg");

    private static string FfmpegExe  => OperatingSystem.IsWindows() ? "ffmpeg.exe"  : "ffmpeg";
    private static string FfprobeExe => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    public async Task StartAsync(CancellationToken ct)
    {
        // 1. Explicitly configured path — trust the user, skip download logic.
        if (!string.IsNullOrEmpty(config.FfmpegPath) && File.Exists(config.FfmpegPath))
        {
            logger.LogInformation("FFmpeg configured at {Path}", config.FfmpegPath);
            EnsureFfprobePath(Path.GetDirectoryName(config.FfmpegPath)!);
            return;
        }

        // 2. In PATH — use it but don't try to replace it.
        var pathResult = FindInPath(FfmpegExe);
        if (pathResult != null)
        {
            config.FfmpegPath = pathResult;
            EnsureFfprobePath(Path.GetDirectoryName(pathResult)!);
            logger.LogInformation("FFmpeg found in PATH at {Path}", pathResult);
            Console.WriteLine($"[FfmpegManagerService] FFmpeg found in PATH at {pathResult}");
            return;
        }

        var managedFfmpeg = Path.Combine(ManagedDir, FfmpegExe);
        var sharedMarkerPath = Path.Combine(ManagedDir, SharedMarker);

        // 3. Already downloaded — but check that it is the shared-library build.
        //    On Windows and Linux a standalone build lacks the .dll/.so files that
        //    FFmpeg.AutoGen needs; re-download if the marker is absent.
        if (File.Exists(managedFfmpeg))
        {
            var needsSharedLibs = !OperatingSystem.IsMacOS(); // macOS has no shared build
            if (needsSharedLibs && !File.Exists(sharedMarkerPath))
            {
                logger.LogInformation(
                    "Managed FFmpeg is a standalone build — re-downloading shared-library build " +
                    "so in-process frame extraction (FFmpeg.AutoGen) works correctly...");
                Console.WriteLine("[FfmpegManagerService] Managed FFmpeg exists but is legacy standalone — triggering re-download of shared-library build");
                try { Directory.Delete(ManagedDir, recursive: true); } catch { /* best effort */ }
            }
            else
            {
                config.FfmpegPath = managedFfmpeg;
                EnsureFfprobePath(ManagedDir);
                logger.LogInformation("FFmpeg found at managed location {Path}", managedFfmpeg);
                Console.WriteLine($"[FfmpegManagerService] FFmpeg found at managed location {managedFfmpeg}");
                return;
            }
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
            logger.LogWarning(ex,
                "Failed to download FFmpeg — transcoding and thumbnail generation will be unavailable. " +
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
            await DownloadAndExtractZipAsync(WinUrl, ct);
            // Mark as a shared-library install
            await File.WriteAllTextAsync(Path.Combine(ManagedDir, SharedMarker), "shared", ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await DownloadMacAsync(ct);
            // macOS is always standalone — no marker written
        }
        else
        {
            var url = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? LinuxArm64Url : LinuxUrl;
            await DownloadAndExtractTarAsync(url, ct);
            await File.WriteAllTextAsync(Path.Combine(ManagedDir, SharedMarker), "shared", ct);
        }

        // Verify
        var ffmpeg = Path.Combine(ManagedDir, FfmpegExe);
        if (!File.Exists(ffmpeg))
            throw new FileNotFoundException($"Download completed but ffmpeg not found at {ffmpeg}");
    }

    /// <summary>
    /// Extracts the BtbN Windows zip (gpl-shared). The archive contains:
    ///   ffmpeg-master-latest-win64-gpl-shared/bin/ffmpeg.exe
    ///   ffmpeg-master-latest-win64-gpl-shared/bin/ffprobe.exe
    ///   ffmpeg-master-latest-win64-gpl-shared/bin/avcodec-61.dll  ← needed by AutoGen
    ///   ffmpeg-master-latest-win64-gpl-shared/bin/avformat-61.dll ← etc.
    /// All bin/ entries are extracted flat into ManagedDir.
    /// </summary>
    private async Task DownloadAndExtractZipAsync(string url, CancellationToken ct)
    {
        var archivePath = Path.Combine(ManagedDir, "ffmpeg-download.zip");
        try
        {
            await DownloadFileAsync(url, archivePath, ct);
            using var zip = ZipFile.OpenRead(archivePath);
            foreach (var entry in zip.Entries)
            {
                // Extract everything under bin/ (executables + DLLs)
                var parts = entry.FullName.Split('/');
                if (parts.Length >= 3 && parts[1] == "bin" && !string.IsNullOrEmpty(parts[2]))
                {
                    var dest = Path.Combine(ManagedDir, parts[2]);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Extracts the BtbN Linux tar.xz (gpl-shared). The archive contains:
    ///   ffmpeg-master-latest-linux64-gpl-shared/bin/ffmpeg      ← CLI binary
    ///   ffmpeg-master-latest-linux64-gpl-shared/bin/ffprobe
    ///   ffmpeg-master-latest-linux64-gpl-shared/lib/libavcodec.so.61  ← needed by AutoGen
    ///   ffmpeg-master-latest-linux64-gpl-shared/lib/libavformat.so.61 ← etc.
    /// Both bin/ and lib/ contents are extracted flat into ManagedDir so that
    /// DynamicallyLoadedBindings.LibrariesPath (set to ManagedDir) finds the .so files.
    /// </summary>
    private async Task DownloadAndExtractTarAsync(string url, CancellationToken ct)
    {
        var archivePath = Path.Combine(ManagedDir, "ffmpeg-download.tar.xz");
        var tempExtract = Path.Combine(ManagedDir, "_extract");
        try
        {
            await DownloadFileAsync(url, archivePath, ct);

            Directory.CreateDirectory(tempExtract);
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/tar",
                Arguments = $"xf \"{archivePath}\" -C \"{tempExtract}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                if (proc != null) await proc.WaitForExitAsync(ct);
            }

            // Copy bin/ executables and lib/ shared libraries flat into ManagedDir
            foreach (var subDir in new[] { "bin", "lib" })
            {
                foreach (var binDir in Directory.GetDirectories(tempExtract, subDir, SearchOption.AllDirectories))
                {
                    foreach (var file in Directory.GetFiles(binDir))
                    {
                        var dest = Path.Combine(ManagedDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                        var chmod = System.Diagnostics.Process.Start("/bin/chmod", $"+x \"{dest}\"");
                        chmod?.WaitForExit();
                    }
                    break; // only the first match per subDir name
                }
            }
        }
        finally
        {
            try { File.Delete(archivePath); } catch { /* best effort */ }
            try { Directory.Delete(tempExtract, recursive: true); } catch { /* best effort */ }
        }
    }

    private async Task DownloadMacAsync(CancellationToken ct)
    {
        // macOS: evermeet.cx only provides standalone static binaries (no shared .dylib build).
        // In-process FFmpeg.AutoGen will not work; FingerprintService / ThumbnailService
        // fall back to spawning the ffmpeg process automatically.
        var ffmpegZip  = Path.Combine(ManagedDir, "ffmpeg.zip");
        var ffprobeZip = Path.Combine(ManagedDir, "ffprobe.zip");
        try
        {
            await DownloadFileAsync(MacUrl, ffmpegZip, ct);
            ZipFile.ExtractToDirectory(ffmpegZip, ManagedDir, overwriteFiles: true);

            await DownloadFileAsync(MacProbeUrl, ffprobeZip, ct);
            ZipFile.ExtractToDirectory(ffprobeZip, ManagedDir, overwriteFiles: true);

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
            try { File.Delete(ffmpegZip); }  catch { /* best effort */ }
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
        await using var file   = File.Create(dest);
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

