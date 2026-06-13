using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
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
    private string? _binDirOverride;
    private bool _started;

    // PostgreSQL 18.3 - latest stable release
    private const string PgMajor = "18";
    private const string PgFullVersion = "18.3";
    private const string PgvectorVersion = "0.8.2";
    private const string EmbeddedPgvectorResourcePrefix = "cove.pgvector/";

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
    private string CoveDir => string.IsNullOrWhiteSpace(_config.DataPath)
        ? CoveDefaultPaths.GetDataRoot()
        : CoveDefaultPaths.ResolveDataPath(_config.DataPath);

    private string PgsqlDir => Path.Combine(CoveDir, "pgsql");
    private string BinDir => _binDirOverride ?? Path.Combine(PgsqlDir, "bin");
    private string PgLibDir => Path.Combine(PgsqlDir, "lib");
    private string PgShareDir => Path.Combine(PgsqlDir, "share");
    private string DataDir => Path.Combine(CoveDir, "pgdata");
    private string LogFile => Path.Combine(CoveDir, "pg.log");
    private string EmbeddedPgvectorExtractDir => Path.Combine(CoveDir, "_embedded_pgvector", $"pg{PgMajor}", CurrentRuntimeId());

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
            var systemPgCtl = await FindSystemPgCtlAsync(ct);
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

        await EnsurePgvectorInstalledAsync(ct);

        // 3. Init data directory if needed
        if (!File.Exists(Path.Combine(DataDir, "PG_VERSION")))
        {
            _logger.LogInformation("Initializing data directory at {DataDir}", DataDir);
            await InitDbAsync(ct);
        }

        await EnsureManagedConfigurationAsync(ct);

        // 4. Start PostgreSQL
        _logger.LogInformation("Starting PostgreSQL on port {Port}", _config.Port);
        await PgCtlAsync($"start -D \"{DataDir}\" -l \"{LogFile}\" -w -t 300 -o \"-p {_config.Port}\"", ct);
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
            _logger.LogWarning(ex, "Error during PostgreSQL shutdown — force-stopping by PID to avoid an orphaned process");
            TryKillPostmasterByPidFile();
        }
        _started = false;
    }

    /// <summary>
    /// Force-kills the postmaster recorded in postmaster.pid. Used only as a fallback when
    /// `pg_ctl stop` fails, so a managed postgres started detached isn't left running. Guards
    /// against recycled PIDs by checking the process name.
    /// </summary>
    private void TryKillPostmasterByPidFile()
    {
        try
        {
            var pidFile = Path.Combine(DataDir, "postmaster.pid");
            if (!File.Exists(pidFile)) return;

            var firstLine = File.ReadLines(pidFile).FirstOrDefault();
            if (!int.TryParse(firstLine?.Trim(), out var pid) || pid <= 0) return;

            using var postmaster = System.Diagnostics.Process.GetProcessById(pid);
            if (!postmaster.HasExited && postmaster.ProcessName.Contains("postgres", StringComparison.OrdinalIgnoreCase))
            {
                postmaster.Kill(entireProcessTree: true);
                postmaster.WaitForExit(10000);
                _logger.LogInformation("Force-stopped managed PostgreSQL postmaster (PID {Pid})", pid);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to force-stop managed PostgreSQL postmaster by PID");
        }
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
            // Linux: EDB no longer provides portable binaries, so Cove downloads PGDG
            // .deb packages and extracts them into CoveDir without installing system packages.
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

        _logger.LogInformation("Extracting PostgreSQL binaries to {BinDir}", BinDir);

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

    private sealed record LinuxOsRelease(string? Id, string? IdLike, string? VersionCodename, string? VersionId, string? PrettyName);

    private static async Task<LinuxOsRelease> ReadLinuxOsReleaseAsync(CancellationToken ct)
    {
        if (!File.Exists("/etc/os-release"))
            return new LinuxOsRelease(null, null, null, null, null);

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var osRelease = await File.ReadAllTextAsync("/etc/os-release", ct);
        foreach (var rawLine in osRelease.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex <= 0)
                continue;

            var key = line[..equalsIndex];
            var value = line[(equalsIndex + 1)..].Trim().Trim('"');
            values[key] = value;
        }

        return new LinuxOsRelease(
            values.GetValueOrDefault("ID"),
            values.GetValueOrDefault("ID_LIKE"),
            values.GetValueOrDefault("VERSION_CODENAME"),
            values.GetValueOrDefault("VERSION_ID"),
            values.GetValueOrDefault("PRETTY_NAME"));
    }

    private static bool IsDebianFamilyLinux(LinuxOsRelease osRelease)
    {
        var ids = new[] { osRelease.Id, osRelease.IdLike }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return ids.Any(value =>
            value.Equals("debian", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ubuntu", StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildUnsupportedLinuxManagedPostgresMessage(LinuxOsRelease osRelease)
    {
        var detected = osRelease.PrettyName
            ?? osRelease.Id
            ?? "this Linux distribution";

        return $"Managed PostgreSQL automatic download currently supports Debian/Ubuntu-family Linux only; detected {detected}. " +
            "Cove will not download Debian .deb packages on this distribution. " +
            $"Install PostgreSQL {PgMajor} and pgvector with your distribution packages so pg_ctl is on PATH, use Docker, " +
            "or set Cove__Postgres__Managed=false and provide Cove__Postgres__ConnectionString for an external pgvector-enabled PostgreSQL server.";
    }

    private static IEnumerable<string> FindExecutablesInPath(string executableName)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        foreach (var dir in pathDirs)
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;

            string candidate;
            try
            {
                candidate = Path.Combine(dir, executableName);
            }
            catch
            {
                continue;
            }

            if (File.Exists(candidate))
                yield return candidate;
        }
    }

    private async Task<bool> IsPostgresMajorAsync(string pgCtlPath, CancellationToken ct)
    {
        try
        {
            var workDir = Path.GetDirectoryName(pgCtlPath) ?? "/";
            var (exitCode, stdout) = await RunWithOutputAsync(pgCtlPath, "--version", workDir, ct);
            if (exitCode != 0)
                return false;

            return stdout.Contains($"PostgreSQL) {PgMajor}.", StringComparison.Ordinal)
                || stdout.Contains($"PostgreSQL {PgMajor}.", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect PostgreSQL version at {Path}", pgCtlPath);
            return false;
        }
    }

    /// <summary>Find a system pg_ctl for the exact PostgreSQL major Cove manages.</summary>
    private async Task<string?> FindSystemPgCtlAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        candidates.AddRange(FindExecutablesInPath("pg_ctl"));

        // Common distro package locations. Debian-family PGDG uses /usr/lib/postgresql;
        // RPM-family PGDG uses /usr/pgsql-*; Fedora's distro package commonly uses /usr/bin.
        candidates.AddRange(new[]
        {
            $"/usr/lib/postgresql/{PgMajor}/bin/pg_ctl",
            $"/usr/pgsql-{PgMajor}/bin/pg_ctl",
            "/usr/bin/pg_ctl",
            "/usr/local/bin/pg_ctl",
        });

        foreach (var candidate in DistinctPaths(candidates).Where(File.Exists))
        {
            if (await IsPostgresMajorAsync(candidate, ct))
                return candidate;
        }

        return null;
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
            // Symlink failed (permissions/filesystem restrictions?) — use the system bin dir directly.
            var intendedBinDir = BinDir;
            _binDirOverride = systemBinDir;
            _logger.LogWarning("Could not symlink {SystemBin} to {BinDir} — using system path directly", systemBinDir, intendedBinDir);
        }
    }

    private async Task InstallLinuxPostgresAsync(CancellationToken ct)
    {
        var osRelease = await ReadLinuxOsReleaseAsync(ct);
        if (!IsDebianFamilyLinux(osRelease))
            throw new InvalidOperationException(BuildUnsupportedLinuxManagedPostgresMessage(osRelease));

        // Download .deb packages from the PGDG APT repository and extract locally.
        var tempDir = Path.Combine(CoveDir, "_pg_install_tmp");
        var extractDir = Path.Combine(CoveDir, "_pg_extract_tmp");
        Directory.CreateDirectory(tempDir);
        Directory.CreateDirectory(extractDir);

        try
        {
            // Detect distro codename for PGDG repo (default to noble/Ubuntu 24.04)
            var codename = string.IsNullOrWhiteSpace(osRelease.VersionCodename) ? "noble" : osRelease.VersionCodename;

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

            var serverDownloaded = await TryDownloadDebPackageAsync(pgdgBase, tempDir, new[]
            {
                $"postgresql-{PgMajor}_{PgFullVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"postgresql-{PgMajor}_{PgFullVersion}-1_{arch}.deb",
            }, ct);

            if (!serverDownloaded)
                throw new InvalidOperationException(
                    $"Could not download postgresql-{PgMajor} for {codename}/{arch}. " +
                    "Configure Cove with an external PostgreSQL connection string or use the Docker package.");

            // Client utilities and libpq are required for initdb, readiness checks, and database creation.
            await TryDownloadDebPackageAsync(pgdgBase, tempDir, new[]
            {
                $"postgresql-client-{PgMajor}_{PgFullVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"postgresql-client-{PgMajor}_{PgFullVersion}-1_{arch}.deb",
            }, ct);

            await TryDownloadDebPackageAsync(pgdgBase, tempDir, new[]
            {
                $"libpq5_{PgFullVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"libpq5_{PgFullVersion}-1_{arch}.deb",
            }, ct);
            await TryDownloadLinuxRuntimeDependencyAsync("liburing2", codename, arch, tempDir, ct);

            var pgvectorBase = "https://apt.postgresql.org/pub/repos/apt/pool/main/p/pgvector";
            var pgvectorDownloaded = await TryDownloadDebPackageAsync(pgvectorBase, tempDir, new[]
            {
                $"postgresql-{PgMajor}-pgvector_{PgvectorVersion}-1.pgdg{pgdgSuffix}+1_{arch}.deb",
                $"postgresql-{PgMajor}-pgvector_{PgvectorVersion}-1_{arch}.deb",
            }, ct, required: false);

            if (!pgvectorDownloaded)
            {
                throw new InvalidOperationException(
                    $"Could not download postgresql-{PgMajor}-pgvector {PgvectorVersion} for {codename}/{arch}. " +
                    "Configure Cove with an external pgvector-enabled PostgreSQL connection string or use the Docker package.");
            }

            // Extract .deb packages
            foreach (var debFile in Directory.GetFiles(tempDir, "*.deb"))
            {
                _logger.LogDebug("Extracting {File}", Path.GetFileName(debFile));
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
            Directory.CreateDirectory(PgsqlDir);

            if (Directory.Exists(pgBinSrc))
                Directory.Move(pgBinSrc, BinDir);
            if (Directory.Exists(pgLibSrc))
                Directory.Move(pgLibSrc, PgLibDir);
            if (Directory.Exists(pgShareSrc))
                Directory.Move(pgShareSrc, PgShareDir);

            CopyLinuxRuntimeLibraries(extractDir, PgLibDir);

            await RunAsync("/bin/chmod", $"-R +x \"{BinDir}\"", CoveDir, ct);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true);
            if (Directory.Exists(extractDir)) Directory.Delete(extractDir, recursive: true);
        }
    }

    private async Task EnsurePgvectorInstalledAsync(CancellationToken ct)
    {
        if (await PgvectorFilesAvailableAsync(ct))
        {
            _logger.LogInformation("pgvector extension files are available for managed PostgreSQL");
            return;
        }

        if (await TryInstallBundledPgvectorAsync(ct))
        {
            _logger.LogInformation("Installed bundled pgvector extension files for managed PostgreSQL");
            return;
        }

        throw new InvalidOperationException(BuildPgvectorUnavailableMessage());
    }

    private async Task<bool> TryInstallBundledPgvectorAsync(CancellationToken ct)
    {
        var bundleRoot = FindBundledPgvectorRoot() ?? await ExtractEmbeddedPgvectorPayloadAsync(ct);
        if (bundleRoot == null)
        {
            _logger.LogInformation("Bundled pgvector payload was not found. Searched: {BundleDirs}", string.Join(Path.PathSeparator, BundledPgvectorCandidateDirs()));
            return false;
        }

        var pkglibDir = await ResolveManagedPgLibDirAsync(ct);
        var sharedDir = await ResolveManagedPgShareDirAsync(ct);

        var libraryPath = FindPgvectorLibrary(bundleRoot)
            ?? throw new InvalidOperationException($"Bundled pgvector payload at '{bundleRoot}' does not contain one of: {string.Join(", ", ExpectedPgvectorLibraryNames())}.");
        var controlPath = FindPgvectorControlFile(bundleRoot)
            ?? throw new InvalidOperationException($"Bundled pgvector payload at '{bundleRoot}' does not contain vector.control.");

        Directory.CreateDirectory(pkglibDir);
        File.Copy(libraryPath, Path.Combine(pkglibDir, Path.GetFileName(libraryPath)), overwrite: true);

        var extensionDir = Path.Combine(sharedDir, "extension");
        Directory.CreateDirectory(extensionDir);
        File.Copy(controlPath, Path.Combine(extensionDir, "vector.control"), overwrite: true);

        foreach (var sqlPath in Directory.EnumerateFiles(bundleRoot, "vector--*.sql", SearchOption.AllDirectories))
        {
            File.Copy(sqlPath, Path.Combine(extensionDir, Path.GetFileName(sqlPath)), overwrite: true);
        }

        await CopyBundledPgvectorHeadersAsync(bundleRoot, ct);

        if (await PgvectorFilesAvailableAsync(ct))
            return true;

        throw new InvalidOperationException($"Bundled pgvector payload at '{bundleRoot}' was copied but managed PostgreSQL still cannot see pgvector extension files.");
    }

    private string? FindBundledPgvectorRoot()
    {
        return BundledPgvectorCandidateDirs().FirstOrDefault(path =>
            Directory.Exists(path)
            && FindPgvectorLibrary(path) != null
            && FindPgvectorControlFile(path) != null);
    }

    private IEnumerable<string> BundledPgvectorCandidateDirs()
    {
        yield return EmbeddedPgvectorExtractDir;

        foreach (var baseDir in RuntimeBaseDirs())
        {
            yield return Path.Combine(baseDir, "runtimes", CurrentRuntimeId(), "native", "postgresql", $"pg{PgMajor}", "pgvector");
            yield return Path.Combine(baseDir, "postgresql", $"pg{PgMajor}", "pgvector", CurrentRuntimeId());
            yield return Path.Combine(baseDir, "pgvector", $"pg{PgMajor}", CurrentRuntimeId());
            yield return Path.Combine(baseDir, "pgvector");
        }
    }

    private static IEnumerable<string> RuntimeBaseDirs()
    {
        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);

        foreach (var path in new[]
        {
            AppContext.BaseDirectory,
            Path.GetDirectoryName(Environment.ProcessPath),
            Directory.GetCurrentDirectory(),
        })
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
                yield return fullPath;
        }
    }

    private static string CurrentRuntimeId()
    {
        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "osx"
                : "linux";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };

        return $"{os}-{arch}";
    }

    private static IReadOnlyList<string> ExpectedPgvectorLibraryNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return ["vector.dll"];

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return ["vector.so", "vector.dylib"];

        return ["vector.so"];
    }

    private static string? FindPgvectorLibrary(string root)
    {
        foreach (var fileName in ExpectedPgvectorLibraryNames())
        {
            var match = Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (match != null)
                return match;
        }

        return null;
    }

    private static string? FindPgvectorControlFile(string root)
        => Directory.EnumerateFiles(root, "vector.control", SearchOption.AllDirectories).FirstOrDefault();

    private async Task CopyBundledPgvectorHeadersAsync(string bundleRoot, CancellationToken ct)
    {
        var sourceHeadersDir = Path.Combine(bundleRoot, "include", "server", "extension", "vector");
        if (!Directory.Exists(sourceHeadersDir))
            return;

        var includeServerDir = await PgConfigPathAsync("--includedir-server", ct);
        if (string.IsNullOrWhiteSpace(includeServerDir))
            includeServerDir = Path.Combine(PgsqlDir, "include", "server");
        else if (!IsPathUnderCoveDir(includeServerDir))
            includeServerDir = Path.Combine(PgsqlDir, "include", "server");

        var targetHeadersDir = Path.Combine(includeServerDir, "extension", "vector");
        Directory.CreateDirectory(targetHeadersDir);
        foreach (var headerPath in Directory.EnumerateFiles(sourceHeadersDir, "*.h", SearchOption.TopDirectoryOnly))
        {
            File.Copy(headerPath, Path.Combine(targetHeadersDir, Path.GetFileName(headerPath)), overwrite: true);
        }
    }

    private async Task<bool> PgvectorFilesAvailableAsync(CancellationToken ct)
    {
        var hasControlFile = (await PgShareDirsAsync(ct))
            .Select(path => Path.Combine(path, "extension", "vector.control"))
            .Any(File.Exists);
        if (!hasControlFile)
            return false;

        return (await PgLibDirsAsync(ct))
            .Where(Directory.Exists)
            .SelectMany(path => ExpectedPgvectorLibraryNames().Select(name => Path.Combine(path, name)))
            .Any(File.Exists);
    }

    private async Task<string?> PgConfigPathAsync(string argument, CancellationToken ct)
    {
        var pgConfig = Exe("pg_config");
        if (!File.Exists(pgConfig))
            return null;

        var (exitCode, stdout) = await RunWithOutputAsync(pgConfig, argument, BinDir, ct);
        return exitCode == 0 ? stdout.Trim() : null;
    }

    private static string BuildPgvectorUnavailableMessage()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return "Managed PostgreSQL could not install pgvector automatically because this Cove build does not include the Windows pgvector payload for PostgreSQL " + PgMajor + ". Reinstall the full Cove native package, use Docker, or configure Cove with an external PostgreSQL server that already has pgvector installed.";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return "Managed PostgreSQL could not install pgvector automatically because this Cove build does not include the macOS pgvector payload for PostgreSQL " + PgMajor + ". Reinstall the full Cove native package, use Docker, or configure Cove with an external PostgreSQL server that already has pgvector installed.";
        }

        return "Managed PostgreSQL could not install pgvector automatically because this Cove build does not include the Linux pgvector payload for PostgreSQL " + PgMajor + ". Reinstall the full Cove native package, use Docker, or configure Cove with an external PostgreSQL server that already has pgvector installed.";
    }

    private static string BuildPgvectorCreateExtensionFailureMessage(string database)
        => $"Could not enable pgvector in database '{database}'. Install pgvector extension files for this PostgreSQL server and rerun Cove migrations.";

    private async Task<bool> TryDownloadDebPackageAsync(string baseUrl, string tempDir, IEnumerable<string> packageNames, CancellationToken ct, bool required = true)
    {
        foreach (var pkgName in packageNames)
        {
            var pkgUrl = $"{baseUrl}/{pkgName}";
            var pkgPath = Path.Combine(tempDir, pkgName);
            _logger.LogDebug("Trying package URL {Url}", pkgUrl);
            try
            {
                await DownloadFileAsync(pkgUrl, pkgPath, ct);
                return true;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Package {PackageName} was not available at {Url}", pkgName, pkgUrl);
            }
        }

        if (required)
            throw new InvalidOperationException($"Could not download any of these packages: {string.Join(", ", packageNames)}");

        return false;
    }

    private async Task TryDownloadLinuxRuntimeDependencyAsync(string packageName, string codename, string arch, string tempDir, CancellationToken ct)
    {
        var urls = LinuxRuntimeDependencyPackageUrls(packageName, codename, arch);
        if (urls.Length == 0)
            return;

        foreach (var url in urls)
        {
            var fileName = Path.GetFileName(new Uri(url).LocalPath);
            try
            {
                await DownloadFileAsync(url, Path.Combine(tempDir, fileName), ct);
                return;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogDebug(ex, "Optional Linux runtime dependency {PackageName} was not available at {Url}", packageName, url);
            }
        }
    }

    private static string[] LinuxRuntimeDependencyPackageUrls(string packageName, string codename, string arch)
    {
        if (!string.Equals(packageName, "liburing2", StringComparison.OrdinalIgnoreCase) || !string.Equals(arch, "amd64", StringComparison.OrdinalIgnoreCase))
            return [];

        return codename switch
        {
            "noble" => ["https://archive.ubuntu.com/ubuntu/pool/main/libu/liburing/liburing2_2.5-1build1_amd64.deb"],
            "jammy" => ["https://archive.ubuntu.com/ubuntu/pool/main/libu/liburing/liburing2_2.1-2build1_amd64.deb"],
            "bookworm" => ["https://deb.debian.org/debian/pool/main/libu/liburing/liburing2_2.3-3_amd64.deb"],
            _ => [],
        };
    }

    private static void CopyLinuxRuntimeLibraries(string extractDir, string pgLibDir)
    {
        Directory.CreateDirectory(pgLibDir);
        var runtimeLibraryNames = new[] { "libpq.so*", "liburing.so*" };
        var usrLibDir = Path.Combine(extractDir, "usr", "lib");
        if (!Directory.Exists(usrLibDir))
            return;

        foreach (var pattern in runtimeLibraryNames)
        {
            foreach (var libraryPath in Directory.EnumerateFiles(usrLibDir, pattern, SearchOption.AllDirectories))
            {
                File.Copy(libraryPath, Path.Combine(pgLibDir, Path.GetFileName(libraryPath)), overwrite: true);
            }
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
                    _logger.LogInformation("Download progress: {Pct}% ({MB:F0} MB)",
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
        var initDbArgs = $"-D \"{DataDir}\" -U postgres --encoding=UTF8 --locale=C --auth=trust";
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && Directory.Exists(PgShareDir))
            initDbArgs += $" -L \"{PgShareDir}\"";

        var exitCode = await RunAsync(Exe("initdb"),
            initDbArgs,
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
            max_connections = 150
            shared_buffers = 128MB
            log_destination = 'stderr'
            logging_collector = off
            """, ct);
    }

    private async Task EnsureManagedConfigurationAsync(CancellationToken ct)
    {
        var configPath = Path.Combine(DataDir, "postgresql.conf");
        if (!File.Exists(configPath))
            return;

        var desiredSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["port"] = _config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["listen_addresses"] = "'127.0.0.1'",
            ["max_connections"] = "150",
            ["shared_buffers"] = "128MB",
            ["log_destination"] = "'stderr'",
            ["logging_collector"] = "off",
            ["dynamic_library_path"] = QuotePostgresSettingValue(ToPostgresConfigPath(PgLibDir)),
            ["extension_control_path"] = QuotePostgresSettingValue(string.Join(Path.PathSeparator, new[] { ToPostgresConfigPath(PgShareDir), "$system" })),
        };

        var lines = (await File.ReadAllLinesAsync(configPath, ct)).ToList();
        var changed = false;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
                continue;

            foreach (var (key, value) in desiredSettings)
            {
                if (!IsSettingLine(trimmed, key))
                    continue;

                var nextLine = $"{key} = {value}";
                seen.Add(key);
                if (!string.Equals(line.Trim(), nextLine, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = nextLine;
                    changed = true;
                }
                break;
            }
        }

        var missingSettings = desiredSettings.Where(setting => !seen.Contains(setting.Key)).ToArray();
        if (missingSettings.Length > 0)
        {
            lines.Add("");
            lines.Add("# -- Cove managed --");
            lines.AddRange(missingSettings.Select(setting => $"{setting.Key} = {setting.Value}"));
            changed = true;
        }

        if (!changed)
            return;

        await File.WriteAllLinesAsync(configPath, lines, ct);
        _logger.LogInformation("Updated managed PostgreSQL configuration at {ConfigPath}", configPath);
    }

    private static bool IsSettingLine(string trimmedLine, string settingName)
    {
        if (!trimmedLine.StartsWith(settingName, StringComparison.OrdinalIgnoreCase))
            return false;

        return trimmedLine.Length == settingName.Length
            || char.IsWhiteSpace(trimmedLine[settingName.Length])
            || trimmedLine[settingName.Length] == '=';
    }

    private static string QuotePostgresSettingValue(string value)
        => "'" + value.Replace("'", "''") + "'";

    private static string ToPostgresConfigPath(string path)
        => Path.GetFullPath(path).Replace('\\', '/');

    private async Task PgCtlAsync(string args, CancellationToken ct)
    {
        var exitCode = await RunAsync(Exe("pg_ctl"), args, BinDir, ct);
        if (exitCode != 0)
        {
            var lastLines = await ReadLogTailAsync(20, ct);
            throw new InvalidOperationException(
                $"pg_ctl failed (exit code {exitCode}). Last log lines:\n{lastLines}");
        }
    }

    private async Task<string> ReadLogTailAsync(int lineCount, CancellationToken ct)
    {
        if (!File.Exists(LogFile)) return "(no log file)";

        try
        {
            await using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var logContent = await reader.ReadToEndAsync(ct);
            return string.Join('\n', logContent.Split('\n').TakeLast(lineCount));
        }
        catch (IOException ex)
        {
            return $"(log unavailable: {ex.Message})";
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
        catch (Exception ex)
        {
            // pg_ctl stop failed. The recorded postmaster may still be alive (e.g. cove was killed
            // before it could shut PostgreSQL down). Force-kill it by PID first so we don't abandon a
            // live process, THEN remove the now-stale pid file.
            _logger.LogWarning(ex, "Failed to stop stale PostgreSQL instance; force-stopping by PID then clearing {PidFile}", pidFile);
            TryKillPostmasterByPidFile();
            try
            {
                if (File.Exists(pidFile))
                {
                    File.Delete(pidFile);
                    _logger.LogInformation("Deleted stale PostgreSQL pid file {PidFile}", pidFile);
                }
            }
            catch (Exception deleteEx)
            {
                _logger.LogWarning(deleteEx, "Failed to delete stale PostgreSQL pid file {PidFile}", pidFile);
            }
        }
    }

    private async Task WaitForReadyAsync(CancellationToken ct)
    {
        for (int i = 0; i < 240; i++)
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

        var lastLines = await ReadLogTailAsync(30, ct);
        throw new TimeoutException(
            $"PostgreSQL did not become ready within 120 seconds. Log:\n{lastLines}");
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
            var vectorExitCode = await RunAsync(Exe("psql"),
                $"-h 127.0.0.1 -p {_config.Port} -U postgres -d {_config.Database} -c \"CREATE EXTENSION IF NOT EXISTS vector\"",
                BinDir, ct);
            if (vectorExitCode != 0)
                throw new InvalidOperationException(BuildPgvectorCreateExtensionFailureMessage(_config.Database));
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
            throw new InvalidOperationException(BuildPgvectorCreateExtensionFailureMessage(_config.Database));
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
            CreateNoWindow = true,
        };

        ApplyPostgresProcessEnvironment(psi);

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

        ApplyPostgresProcessEnvironment(psi);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {exe}");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        await stderrTask;
        return (proc.ExitCode, stdout);
    }

    private async Task<string> ResolveManagedPgLibDirAsync(CancellationToken ct)
    {
        var pgConfigDir = await PgConfigPathAsync("--pkglibdir", ct);
        if (!string.IsNullOrWhiteSpace(pgConfigDir) && IsPathUnderCoveDir(pgConfigDir))
            return pgConfigDir;

        return PgLibDir;
    }

    private async Task<string> ResolveManagedPgShareDirAsync(CancellationToken ct)
    {
        var pgConfigDir = await PgConfigPathAsync("--sharedir", ct);
        if (!string.IsNullOrWhiteSpace(pgConfigDir) && IsPathUnderCoveDir(pgConfigDir))
            return pgConfigDir;

        return PgShareDir;
    }

    private async Task<IReadOnlyList<string>> PgLibDirsAsync(CancellationToken ct)
    {
        var candidates = new List<string> { PgLibDir };
        var pgConfigDir = await PgConfigPathAsync("--pkglibdir", ct);
        if (!string.IsNullOrWhiteSpace(pgConfigDir))
            candidates.Add(pgConfigDir);

        return DistinctPaths(candidates);
    }

    private async Task<IReadOnlyList<string>> PgShareDirsAsync(CancellationToken ct)
    {
        var candidates = new List<string> { PgShareDir };
        var pgConfigDir = await PgConfigPathAsync("--sharedir", ct);
        if (!string.IsNullOrWhiteSpace(pgConfigDir))
            candidates.Add(pgConfigDir);

        return DistinctPaths(candidates);
    }

    private static IReadOnlyList<string> DistinctPaths(IEnumerable<string> paths)
    {
        var comparer = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            var fullPath = Path.GetFullPath(path);
            if (seen.Add(fullPath))
                result.Add(fullPath);
        }

        return result;
    }

    private bool IsPathUnderCoveDir(string path)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var root = Path.GetFullPath(CoveDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, comparison);
    }

    private async Task<string?> ExtractEmbeddedPgvectorPayloadAsync(CancellationToken ct)
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(PostgresManagerService).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Select(resourceName => new { Original = resourceName, Normalized = NormalizeResourceName(resourceName) })
            .Where(resource => resource.Normalized.StartsWith(EmbeddedPgvectorResourcePrefix, StringComparison.Ordinal))
            .ToArray();
        if (resourceNames.Length == 0)
            return null;

        var ridSegment = $"runtimes/{CurrentRuntimeId()}/";
        var matchingResources = resourceNames
            .Where(resource => resource.Normalized.Contains(ridSegment, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matchingResources.Length == 0)
            matchingResources = resourceNames;

        if (Directory.Exists(EmbeddedPgvectorExtractDir)
            && FindPgvectorLibrary(EmbeddedPgvectorExtractDir) != null
            && FindPgvectorControlFile(EmbeddedPgvectorExtractDir) != null)
        {
            return EmbeddedPgvectorExtractDir;
        }

        if (Directory.Exists(EmbeddedPgvectorExtractDir))
            Directory.Delete(EmbeddedPgvectorExtractDir, recursive: true);
        Directory.CreateDirectory(EmbeddedPgvectorExtractDir);

        var extractRoot = Path.GetFullPath(EmbeddedPgvectorExtractDir);
        foreach (var resource in matchingResources)
        {
            var relativePath = resource.Normalized[EmbeddedPgvectorResourcePrefix.Length..];
            if (string.IsNullOrWhiteSpace(relativePath))
                continue;

            var targetPath = Path.GetFullPath(Path.Combine(new[] { EmbeddedPgvectorExtractDir }.Concat(relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray()));
            if (!IsPathUnderRoot(targetPath, extractRoot))
                throw new InvalidOperationException($"Embedded pgvector resource path escaped extraction root: {resource.Original}");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await using var resourceStream = assembly.GetManifestResourceStream(resource.Original)
                ?? throw new InvalidOperationException($"Embedded pgvector resource '{resource.Original}' could not be opened.");
            await using var fileStream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
            await resourceStream.CopyToAsync(fileStream, ct);
        }

        if (FindPgvectorLibrary(EmbeddedPgvectorExtractDir) != null && FindPgvectorControlFile(EmbeddedPgvectorExtractDir) != null)
            return EmbeddedPgvectorExtractDir;

        throw new InvalidOperationException("Embedded pgvector payload was present but did not contain vector library and control files after extraction.");
    }

    private static string NormalizeResourceName(string resourceName)
        => resourceName.Replace('\\', '/');

    private static bool IsPathUnderRoot(string path, string root)
    {
        var comparison = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, comparison);
    }

    private void ApplyPostgresProcessEnvironment(ProcessStartInfo psi)
    {
        PrependEnvironmentPath(psi, "PATH", BinDir);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            PrependEnvironmentPath(psi, "LD_LIBRARY_PATH", PgLibDir);
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            PrependEnvironmentPath(psi, "DYLD_LIBRARY_PATH", PgLibDir);
    }

    private static void PrependEnvironmentPath(ProcessStartInfo psi, string variableName, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var existing = psi.Environment.TryGetValue(variableName, out var value) ? value : string.Empty;
        psi.Environment[variableName] = string.IsNullOrWhiteSpace(existing)
            ? path
            : $"{path}{Path.PathSeparator}{existing}";
    }
}
