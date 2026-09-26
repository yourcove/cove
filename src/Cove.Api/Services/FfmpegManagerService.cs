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
/// Cove only ever invokes the ffmpeg/ffprobe binaries, so the self-contained "gpl" builds are
/// enough. Earlier versions pulled the larger BtbN "gpl-shared" variant because the in-process
/// FFmpeg.AutoGen decoder had to dlopen the shared libraries; that decoder has been removed.
/// </summary>
public class FfmpegManagerService(CoveConfiguration config, ILogger<FfmpegManagerService> logger) : IHostedService
{
    // BtbN GPL builds — self-contained binaries; no shared libraries needed.
    private const string WinUrl      = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    private const string LinuxUrl    = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl.tar.xz";
    private const string LinuxArm64Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl.tar.xz";
    // macOS: evermeet.cx static builds
    private const string MacUrl      = "https://evermeet.cx/ffmpeg/getrelease/zip";
    private const string MacProbeUrl = "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip";

    // Marker written to ManagedDir after a successful shared-library download.
    // If the binary exists but this marker is absent the install is a legacy standalone
    // build and we re-download to get the shared libraries.

    private static readonly TimeSpan ExtractTimeout = TimeSpan.FromMinutes(5);

    private static string ManagedDir => CoveDefaultPaths.GetDataSubdirectory("ffmpeg");

    private static string FfmpegExe  => FfmpegExecutableLocator.FfmpegFileName;
    private static string FfprobeExe => FfmpegExecutableLocator.FfprobeFileName;

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
        var pathResult = FfmpegExecutableLocator.FindOnPath(FfmpegExe);
        if (pathResult != null)
        {
            config.FfmpegPath = pathResult;
            EnsureFfprobePath(Path.GetDirectoryName(pathResult)!);
            logger.LogInformation("FFmpeg found in PATH at {Path}", pathResult);
            return;
        }

        var managedFfmpeg = Path.Combine(ManagedDir, FfmpegExe);

        // 3. Already downloaded. Any build whose binaries run is fine now that nothing loads the
        //    FFmpeg shared libraries in-process, so an existing install is never re-downloaded
        //    just because of which variant it is.
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
        }
        else if (OperatingSystem.IsMacOS())
        {
            await DownloadMacAsync(ct);
        }
        else
        {
            var url = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? LinuxArm64Url : LinuxUrl;
            await DownloadAndExtractTarAsync(url, ct);
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
    /// Extracts the BtbN Linux tar.xz. The archive nests everything under a versioned directory:
    ///   ffmpeg-master-latest-linux64-gpl/bin/ffmpeg      ← CLI binary
    ///   ffmpeg-master-latest-linux64-gpl/bin/ffprobe
    /// Contents are extracted flat into ManagedDir so the binaries land at a predictable path.
    /// </summary>
    private async Task DownloadAndExtractTarAsync(string url, CancellationToken ct)
    {
        var archivePath = Path.Combine(ManagedDir, "ffmpeg-download.tar.xz");
        var tempExtract = Path.Combine(ManagedDir, "_extract");
        try
        {
            await DownloadFileAsync(url, archivePath, ct);

            Directory.CreateDirectory(tempExtract);
            var tar = await FfmpegProcessRunner.RunSystemToolAsync("/bin/tar", ["xf", archivePath, "-C", tempExtract], ExtractTimeout, ct);
            if (tar.TimedOut || tar.ExitCode != 0)
                throw new InvalidOperationException($"Extracting the FFmpeg archive failed (exit {tar.ExitCode}): {tar.StandardError.Trim()}");

            // Copy bin/ executables and lib/ shared libraries flat into ManagedDir
            foreach (var subDir in new[] { "bin", "lib" })
            {
                foreach (var binDir in Directory.GetDirectories(tempExtract, subDir, SearchOption.AllDirectories))
                {
                    foreach (var file in Directory.GetFiles(binDir))
                    {
                        var dest = Path.Combine(ManagedDir, Path.GetFileName(file));
                        File.Copy(file, dest, overwrite: true);
                        MakeExecutable(dest);
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
                    MakeExecutable(path);
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

    /// <summary>Adds execute permission wherever read permission is granted, like <c>chmod +x</c> under the usual umask.</summary>
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        var mode = File.GetUnixFileMode(path);
        if (mode.HasFlag(UnixFileMode.UserRead)) mode |= UnixFileMode.UserExecute;
        if (mode.HasFlag(UnixFileMode.GroupRead)) mode |= UnixFileMode.GroupExecute;
        if (mode.HasFlag(UnixFileMode.OtherRead)) mode |= UnixFileMode.OtherExecute;
        File.SetUnixFileMode(path, mode);
    }
}

