using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Cove.Core.Interfaces;

namespace Cove.Data;

/// <summary>
/// Manages a self-contained PostgreSQL instance that starts/stops with the app.
/// On first run, downloads portable PostgreSQL binaries automatically.
/// </summary>
public class PostgresManagerService : IHostedService
{
    private readonly PostgresConfig _config;
    private readonly ILogger<PostgresManagerService> _logger;
    private bool _started;

    // PostgreSQL 18.3 - latest stable release
    private const string PgMajor = "18";
    private const string PgFullVersion = "18.3";

    // Windows: EDB portable binaries (still available for Windows/macOS)
    private const string WinUrl = "https://sbp.enterprisedb.com/getfile.jsp?fileid=1260146";
    // macOS: EDB portable binaries
    private const string MacUrl = "https://sbp.enterprisedb.com/getfile.jsp?fileid=1260163";

    public PostgresManagerService(IOptions<PostgresConfig> config, ILogger<PostgresManagerService> logger)
    {
        _config = config.Value;
        _logger = logger;
    }

    /// <summary>Root directory for all managed postgres files (binaries + data).</summary>
    private string CoveDir => _config.DataPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove");

    private string BinDir => Path.Combine(CoveDir, "pgsql", "bin");
    private string DataDir => Path.Combine(CoveDir, "pgdata");
    private string LogFile => Path.Combine(CoveDir, "pg.log");

    private string Exe(string name) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Path.Combine(BinDir, $"{name}.exe")
                                                            : Path.Combine(BinDir, name);

    // ─── Lifecycle ──────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken ct)
    {
        if (!_config.Managed)
        {
            _logger.LogInformation("Managed PostgreSQL disabled — using external connection string");
            return;
        }

        _logger.LogInformation("Managed PostgreSQL mode enabled");

        // 1. On Linux/macOS, check if a system postgres is already available in PATH
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var systemPgCtl = FindSystemPgCtl();
            if (systemPgCtl != null && !File.Exists(Exe("pg_ctl")))
            {
                _logger.LogInformation("Found system PostgreSQL at {Path} — symlinking to managed bin dir", systemPgCtl);
                LinkSystemPostgresBinDir(systemPgCtl);
            }
        }

        // 2. Ensure binaries exist (download if needed)
        if (!File.Exists(Exe("pg_ctl")))
        {
            _logger.LogInformation("PostgreSQL binaries not found — downloading portable {Version}…", PgFullVersion);
            await DownloadPostgresAsync(ct);
        }
        else
        {
            _logger.LogInformation("PostgreSQL binaries found at {BinDir}", BinDir);
        }

        // 2. Check if a stale instance exists from a previous crash
        await StopStaleInstanceAsync(ct);

        // 3. Init data directory if needed
        if (!File.Exists(Path.Combine(DataDir, "PG_VERSION")))
        {
            _logger.LogInformation("Initializing data directory at {DataDir}", DataDir);
            await InitDbAsync(ct);
        }

        // 4. Start PostgreSQL
        _logger.LogInformation("Starting PostgreSQL on port {Port}", _config.Port);
        await PgCtlAsync($"start -D \"{DataDir}\" -l \"{LogFile}\" -w -o \"-p {_config.Port}\"", ct);
        _started = true;

        // 5. Wait for ready
        await WaitForReadyAsync(ct);

        // 6. Create database if it doesn't exist
        await EnsureDatabaseAsync(ct);

        _logger.LogInformation("Managed PostgreSQL is ready (port {Port}, database '{Db}')", _config.Port, _config.Database);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_config.Managed || !_started) return;

        _logger.LogInformation("Stopping managed PostgreSQL");
        try
        {
            await PgCtlAsync($"stop -D \"{DataDir}\" -m fast", ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during PostgreSQL shutdown — it may already be stopped");
        }
        _started = false;
    }

    // ─── Download ───────────────────────────────────────────────────

    private async Task DownloadPostgresAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(CoveDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            await DownloadAndExtractArchiveAsync(WinUrl, ".zip", ct);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            await DownloadAndExtractArchiveAsync(MacUrl, ".zip", ct);
        }
        else
        {
            // Linux: EDB no longer provides portable binaries.
            // Install from PGDG APT repository packages extracted locally.
            await InstallLinuxPostgresAsync(ct);
        }

        if (!File.Exists(Exe("pg_ctl")))
            throw new FileNotFoundException(
                $"Installation succeeded but pg_ctl not found at expected path: {Exe("pg_ctl")}. " +
                $"Contents of {CoveDir}: {string.Join(", ", Directory.GetDirectories(CoveDir))}");

        _logger.LogInformation("PostgreSQL {Version} binaries ready at {BinDir}", PgFullVersion, BinDir);
    }

    private async Task DownloadAndExtractArchiveAsync(string url, string ext, CancellationToken ct)
    {
        string archivePath = Path.Combine(CoveDir, $"postgresql{ext}");

        await DownloadFileAsync(url, archivePath, ct);

        _logger.LogInformation("Extracting…");

        if (ext == ".zip")
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var exitCode = await RunAsync("/usr/bin/unzip", $"-q -o \"{archivePath}\" -d \"{CoveDir}\"", CoveDir, ct);
                if (exitCode != 0)
                    throw new InvalidOperationException("Failed to extract PostgreSQL archive");
            }
            else
            {
                ZipFile.ExtractToDirectory(archivePath, CoveDir, overwriteFiles: true);
            }
        }
        else
        {
            var exitCode = await RunAsync("/bin/tar", $"xzf \"{archivePath}\" -C \"{CoveDir}\"", CoveDir, ct);
            if (exitCode != 0)
                throw new InvalidOperationException("Failed to extract PostgreSQL archive");
            await RunAsync("/bin/chmod", $"-R +x \"{BinDir}\"", CoveDir, ct);
        }

        File.Delete(archivePath);
    }

    /// <summary>Find pg_ctl in common system paths.</summary>
    private string? FindSystemPgCtl()
    {
        // Common system install locations
        var candidates = new[]
        {
            $"/usr/lib/postgresql/{PgMajor}/bin/pg_ctl",
            $"/usr/lib/postgresql/{int.Parse(PgMajor) - 1}/bin/pg_ctl", // one version back
            "/usr/bin/pg_ctl",
            "/usr/local/bin/pg_ctl",
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Symlink (or copy) system postgres bin dir so our managed wrapper can find it.</summary>
    private void LinkSystemPostgresBinDir(string pgCtlPath)
    {
        var systemBinDir = Path.GetDirectoryName(pgCtlPath)!;
        var pgsqlDir = Path.Combine(CoveDir, "pgsql");
        Directory.CreateDirectory(pgsqlDir);
        try
        {
            Directory.CreateSymbolicLink(BinDir, systemBinDir);
        }
        catch
        {
            // Symlink failed (permissions?) — just note the path; we set BinDir to the system dir
            _logger.LogWarning("Could not symlink {SystemBin} to {BinDir} — will use system path directly", systemBinDir, BinDir);
        }
    }

    private async Task InstallLinuxPostgresAsync(CancellationToken ct)
    {
        // Strategy 1: try apt-get (works on Debian/Ubuntu without root if postgresql is already
        // in the package cache, otherwise tries with sudo).  If apt-get is not available or
        // fails, fall back to downloading .deb packages manually.

        // Check if apt-get is available
        var hasAptGet = File.Exists("/usr/bin/apt-get");
        if (hasAptGet)
        {
            _logger.LogInformation("Attempting to install PostgreSQL {Version} via apt-get…", PgMajor);
            // Add PGDG repo key + source if not already present
            await TryAddPgdgRepoAsync(ct);

            var installCode = await RunAsync(
                "/usr/bin/apt-get",
                $"install -y postgresql-{PgMajor} postgresql-client-{PgMajor}",
                CoveDir, ct);

            if (installCode == 0)
            {
                // Link the system-installed binaries
                var sysPgCtl = FindSystemPgCtl();
                if (sysPgCtl != null)
                    LinkSystemPostgresBinDir(sysPgCtl);

                // If still not found, make explicit symlinks
                if (!File.Exists(Exe("pg_ctl")))
                {
                    var systemBin = $"/usr/lib/postgresql/{PgMajor}/bin";
                    if (Directory.Exists(systemBin))
                    {
                        var pgsqlDir = Path.Combine(CoveDir, "pgsql");
                        Directory.CreateDirectory(pgsqlDir);
                        try { Directory.CreateSymbolicLink(BinDir, systemBin); } catch { }
                    }
                }
                return;
            }
            _logger.LogWarning("apt-get install failed (exit {Code}) — falling back to .deb extraction", installCode);
        }

        // Strategy 2: Download .deb packages from the PGDG APT repository and extract locally.
        var tempDir = Path.Combine(CoveDir, "_pg_install_tmp");
        var extractDir = Path.Combine(CoveDir, "_pg_extract_tmp");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            // Detect distro codename for PGDG repo (default to noble/Ubuntu 24.04)
            var codename = "noble";
            if (File.Exists("/etc/os-release"))
            {
                var osRelease = await File.ReadAllTextAsync("/etc/os-release", ct);
                foreach (var line in osRelease.Split('\n'))
                {
                    if (line.StartsWith("VERSION_CODENAME="))
                    {
                        codename = line.Split('=')[1].Trim().Trim('"');
                        break;
                    }
                }
            }

            // Map distro codename to PGDG numeric suffix (e.g. noble=24.04, jammy=22.04)
            var pgdgSuffix = codename switch
            {
                "noble" => "24.04",
                "jammy" => "22.04",
                "focal" => "20.04",
                "bookworm" => "12",
                "bullseye" => "11",
                "buster" => "10",
                _ => "24.04", // default to latest
            };

            var arch = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "amd64";
            var pgdgBase = $"https://apt.postgresql.org/pub/repos/apt/pool/main/p/postgresql-{PgMajor}";

            // Try both naming conventions: with and without pgdg codename suffix
            var packageNames = new[]
            {
                $"postgresql-{PgMajor}_{PgFullVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"postgresql-{PgMajor}_{PgFullVersion}-1_{arch}.deb",
            };

            foreach (var (baseName, isServer) in new[] { (packageNames, true) })
            {
                string? downloaded = null;
                foreach (var pkgName in baseName)
                {
                    var pkgUrl = $"{pgdgBase}/{pkgName}";
                    var pkgPath = Path.Combine(tempDir, pkgName);
                    _logger.LogInformation("Trying {Url}…", pkgUrl);
                    try
                    {
                        await DownloadFileAsync(pkgUrl, pkgPath, ct);
                        downloaded = pkgPath;
                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        _logger.LogWarning(ex, "Not found at {Url}", pkgUrl);
                    }
                }
                if (downloaded == null)
                    throw new InvalidOperationException(
                        $"Could not download postgresql-{PgMajor} for {codename}/{arch}. " +
                        "Please install PostgreSQL manually (apt-get install postgresql) or configure an external connection string.");
            }

            // Also try to get the client package (best-effort, not required)
            foreach (var pkgName in new[]
            {
                $"postgresql-client-{PgMajor}_{PgFullVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"postgresql-client-{PgMajor}_{PgFullVersion}-1_{arch}.deb",
            })
            {
                try
                {
                    await DownloadFileAsync($"{pgdgBase}/{pkgName}", Path.Combine(tempDir, pkgName), ct);
                    break;
                }
                catch { /* client package is optional */ }
            }

            // Extract .deb packages
            foreach (var debFile in Directory.GetFiles(tempDir, "*.deb"))
            {
                _logger.LogInformation("Extracting {File}", Path.GetFileName(debFile));
                var exitCode = await RunAsync("/usr/bin/dpkg-deb", $"-x \"{debFile}\" \"{extractDir}\"", tempDir, ct);
                if (exitCode != 0)
                {
                    exitCode = await RunAsync("/usr/bin/ar", $"x \"{debFile}\"", tempDir, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException($"Failed to extract {debFile}");

                    var dataTar = Directory.GetFiles(tempDir, "data.tar.*").FirstOrDefault()
                        ?? throw new FileNotFoundException("data.tar not found in .deb package");
                    exitCode = await RunAsync("/bin/tar", $"xf \"{dataTar}\" -C \"{extractDir}\"", tempDir, ct);
                    if (exitCode != 0)
                        throw new InvalidOperationException($"Failed to extract {dataTar}");
                }
            }

            // Move extracted PG binaries to expected location
            var pgBinSrc = Path.Combine(extractDir, "usr", "lib", "postgresql", PgMajor, "bin");
            var pgLibSrc = Path.Combine(extractDir, "usr", "lib", "postgresql", PgMajor, "lib");
            var pgShareSrc = Path.Combine(extractDir, "usr", "share", "postgresql", PgMajor);
            var pgsqlDir = Path.Combine(CoveDir, "pgsql");
            Directory.CreateDirectory(pgsqlDir);

            if (Directory.Exists(pgBinSrc))
                Directory.Move(pgBinSrc, BinDir);
            if (Directory.Exists(pgLibSrc))
                Directory.Move(pgLibSrc, Path.Combine(pgsqlDir, "lib"));
            if (Directory.Exists(pgShareSrc))
                Directory.Move(pgShareSrc, Path.Combine(pgsqlDir, "share"));

            await RunAsync("/bin/chmod", $"-R +x \"{BinDir}\"", CoveDir, ct);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        }
    }

    private async Task TryAddPgdgRepoAsync(CancellationToken ct)
    {
        const string pgdgListPath = "/etc/apt/sources.list.d/pgdg.list";
        if (File.Exists(pgdgListPath)) return; // Already configured

        try
        {
            // Download and add PGDG signing key
            await RunAsync("/bin/bash", "-c \"curl -fsSL https://www.postgresql.org/media/keys/ACCC4CF8.asc | gpg --dearmor -o /etc/apt/trusted.gpg.d/pgdg.gpg\"", CoveDir, ct);

            // Detect codename
            var codename = "noble";
            if (File.Exists("/etc/os-release"))
            {
                var osRelease = await File.ReadAllTextAsync("/etc/os-release", ct);
                foreach (var line in osRelease.Split('\n'))
                {
                    if (line.StartsWith("VERSION_CODENAME="))
                    {
                        codename = line.Split('=')[1].Trim().Trim('"');
                        break;
                    }
                }
            }

            await File.WriteAllTextAsync(pgdgListPath,
                $"deb https://apt.postgresql.org/pub/repos/apt {codename}-pgdg main\n", ct);
            await RunAsync("/usr/bin/apt-get", "update -qq", CoveDir, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not add PGDG repo — will try default apt packages");
        }
    }

    private async Task DownloadFileAsync(string url, string destPath, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _logger.LogInformation("Downloading {Url}", url);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);

        var buffer = new byte[81920];
        long totalRead = 0;
        int lastPct = -1;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            totalRead += bytesRead;
            if (totalBytes > 0)
            {
                int pct = (int)(totalRead * 100 / totalBytes);
                if (pct / 10 > lastPct / 10)
                {
                    _logger.LogInformation("  Download progress: {Pct}% ({MB:F0} MB)",
                        pct, totalRead / 1048576.0);
                    lastPct = pct;
                }
            }
        }
        await fileStream.FlushAsync(ct);
        fileStream.Close();
        _logger.LogInformation("Download complete ({MB:F1} MB)", totalRead / 1048576.0);
    }

    // ─── Init / Start / Stop helpers ────────────────────────────────

    private async Task InitDbAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(DataDir);
        var exitCode = await RunAsync(Exe("initdb"),
            $"-D \"{DataDir}\" -U postgres --encoding=UTF8 --locale=C --auth=trust",
            BinDir, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"initdb failed (exit code {exitCode}). Check {LogFile}");

        // Write pg_hba.conf — local-only trust auth
        await File.WriteAllTextAsync(Path.Combine(DataDir, "pg_hba.conf"),
            """
            # TYPE  DATABASE  USER  ADDRESS       METHOD
            local   all       all                 trust
            host    all       all   127.0.0.1/32  trust
            host    all       all   ::1/128       trust
            """, ct);

        // Append to postgresql.conf
        await File.AppendAllTextAsync(Path.Combine(DataDir, "postgresql.conf"),
            $"""

            # ── Cove managed ──
            port = {_config.Port}
            listen_addresses = '127.0.0.1'
            max_connections = 20
            shared_buffers = 128MB
            log_destination = 'stderr'
            logging_collector = off
            """, ct);
    }

    private async Task PgCtlAsync(string args, CancellationToken ct)
    {
        var exitCode = await RunAsync(Exe("pg_ctl"), args, BinDir, ct);
        if (exitCode != 0)
        {
            // Read log for diagnostics
            var logContent = File.Exists(LogFile) ? await File.ReadAllTextAsync(LogFile, ct) : "(no log file)";
            var lastLines = string.Join('\n', logContent.Split('\n').TakeLast(20));
            throw new InvalidOperationException(
                $"pg_ctl failed (exit code {exitCode}). Last log lines:\n{lastLines}");
        }
    }

    private async Task StopStaleInstanceAsync(CancellationToken ct)
    {
        var pidFile = Path.Combine(DataDir, "postmaster.pid");
        if (!File.Exists(pidFile)) return;

        _logger.LogInformation("Found stale postmaster.pid — stopping previous instance");
        try
        {
            await RunAsync(Exe("pg_ctl"), $"stop -D \"{DataDir}\" -m fast", BinDir, ct);
        }
        catch
        {
            // If it fails (process already dead), just remove the pid file
            try { File.Delete(pidFile); } catch { }
        }
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        for (int i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            var exitCode = await RunAsync(Exe("pg_isready"),
                $"-h 127.0.0.1 -p {_config.Port} -U postgres", BinDir, ct);
            if (exitCode == 0)
            {
                _logger.LogDebug("PostgreSQL is accepting connections");
                return;
            }
            await Task.Delay(500, ct);
        }

        var logContent = File.Exists(LogFile) ? await File.ReadAllTextAsync(LogFile, ct) : "(no log)";
        throw new TimeoutException(
            $"PostgreSQL did not become ready within 15 seconds. Log:\n{string.Join('\n', logContent.Split('\n').TakeLast(30))}");
    }

    private async Task EnsureDatabaseAsync(CancellationToken ct)
    {
        // Check if database exists via psql
        var (exitCode, stdout) = await RunWithOutputAsync(Exe("psql"),
            $"-h 127.0.0.1 -p {_config.Port} -U postgres -tAc \"SELECT 1 FROM pg_database WHERE datname='{_config.Database}'\"",
            BinDir, ct);

        if (stdout.Trim() == "1")
        {
            _logger.LogDebug("Database '{Db}' already exists", _config.Database);

            // Ensure pgvector extension is created
            await RunAsync(Exe("psql"),
                $"-h 127.0.0.1 -p {_config.Port} -U postgres -d {_config.Database} -c \"CREATE EXTENSION IF NOT EXISTS vector\"",
                BinDir, ct);
            return;
        }

        _logger.LogInformation("Creating database '{Db}'", _config.Database);
        exitCode = await RunAsync(Exe("createdb"),
            $"-h 127.0.0.1 -p {_config.Port} -U postgres {_config.Database}", BinDir, ct);

        if (exitCode != 0)
            throw new InvalidOperationException($"createdb failed (exit code {exitCode})");

        // Try to create pgvector extension (will fail silently if not available)
        var extResult = await RunAsync(Exe("psql"),
            $"-h 127.0.0.1 -p {_config.Port} -U postgres -d {_config.Database} -c \"CREATE EXTENSION IF NOT EXISTS vector\"",
            BinDir, ct);

        if (extResult != 0)
            _logger.LogWarning("pgvector extension not available — vector search features will be disabled");
    }

    // ─── Process helpers ────────────────────────────────────────────

    private async Task<int> RunAsync(string exe, string args, string workDir, CancellationToken ct)
    {
        _logger.LogDebug("Exec: {Exe} {Args}", Path.GetFileName(exe), args);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        // Ensure the PG bin dir is on PATH so sub-processes can find each other
        var path = psi.Environment.TryGetValue("PATH", out var existing) ? existing : "";
        psi.Environment["PATH"] = $"{BinDir}{Path.PathSeparator}{path}";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        await proc.WaitForExitAsync(ct);
        return proc.ExitCode;
    }

    private async Task<(int exitCode, string stdout)> RunWithOutputAsync(
        string exe, string args, string workDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var path = psi.Environment.TryGetValue("PATH", out var existing) ? existing : "";
        psi.Environment["PATH"] = $"{BinDir}{Path.PathSeparator}{path}";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, stdout);
    }
}
