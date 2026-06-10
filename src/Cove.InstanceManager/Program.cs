using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Cove.Core.Interfaces;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

var options = ManagerOptions.Parse(args);
if (options.ShowHelp)
{
    ManagerOptions.PrintHelp();
    return;
}

var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
var manager = await InstanceManagerService.CreateAsync(options);

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
});
builder.Services.AddSingleton(manager);
builder.Services.AddSingleton(new ManagerAccess(accessToken));
builder.WebHost.UseUrls($"http://{options.Host}:{options.Port}");

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path == "/health")
    {
        await next();
        return;
    }

    var access = context.RequestServices.GetRequiredService<ManagerAccess>();
    var queryToken = context.Request.Query["token"].ToString();
    var headerToken = context.Request.Headers["X-Cove-Manager-Token"].ToString();
    var cookieToken = context.Request.Cookies[ManagerAccess.CookieName];
    var supplied = !string.IsNullOrWhiteSpace(queryToken)
        ? queryToken
        : !string.IsNullOrWhiteSpace(headerToken)
            ? headerToken
            : cookieToken;

    if (!string.Equals(supplied, access.Token, StringComparison.Ordinal))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Cove Instance Manager token required.");
        return;
    }

    if (!string.IsNullOrWhiteSpace(queryToken))
    {
        context.Response.Cookies.Append(ManagerAccess.CookieName, queryToken, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = false,
        });
    }

    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/", () => Results.Content(ManagerPage.Html, "text/html"));
app.MapGet("/api/instances", async (InstanceManagerService managerService) => Results.Ok(await managerService.ListAsync()));
app.MapPost("/api/instances", async (CreateInstanceRequest request, InstanceManagerService managerService) => Results.Ok(await managerService.CreateAsync(request)));
app.MapPut("/api/instances/{id}", async (string id, UpdateInstanceRequest request, InstanceManagerService managerService) => Results.Ok(await managerService.UpdateAsync(id, request)));
app.MapPost("/api/instances/{id}/start", async (string id, InstanceManagerService managerService) => Results.Ok(await managerService.StartAsync(id)));
app.MapPost("/api/instances/{id}/stop", async (string id, InstanceManagerService managerService) => Results.Ok(await managerService.StopAsync(id)));
app.MapPost("/api/instances/{id}/open", (string id, InstanceManagerService managerService) =>
{
    managerService.Open(id);
    return Results.Ok(new { ok = true });
});
app.MapPost("/api/instances/{id}/console", (string id, InstanceManagerService managerService) =>
{
    managerService.OpenConsole(id);
    return Results.Ok(new { ok = true });
});
app.MapDelete("/api/instances/{id}", async (string id, bool deleteData, InstanceManagerService managerService) => Results.Ok(await managerService.RemoveAsync(id, deleteData)));
app.MapGet("/api/instances/{id}/logs", (string id, int? tail, InstanceManagerService managerService) => Results.Ok(managerService.GetLogs(id, tail ?? 120)));

await app.StartAsync();

var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
var managerUrl = ResolveBrowserUrl(addresses.FirstOrDefault(), options.Host, accessToken);
Console.WriteLine($"Cove Instance Manager: {managerUrl}");
Console.WriteLine($"Registry: {manager.RegistryPath}");

using var trayProcess = StartWindowsTrayIcon(managerUrl, accessToken);

if (!options.NoBrowser)
    OpenUrl(managerUrl);

try
{
    await app.WaitForShutdownAsync();
}
finally
{
    StopProcess(trayProcess);
}

static string ResolveBrowserUrl(string? boundAddress, string host, string token)
{
    var address = string.IsNullOrWhiteSpace(boundAddress)
        ? $"http://{host}:0"
        : boundAddress;

    if (address.Contains("0.0.0.0", StringComparison.Ordinal))
        address = address.Replace("0.0.0.0", "127.0.0.1", StringComparison.Ordinal);
    if (address.Contains("[::]", StringComparison.Ordinal))
        address = address.Replace("[::]", "127.0.0.1", StringComparison.Ordinal);

    return $"{address.TrimEnd('/')}/?token={token}";
}

static void OpenUrl(string url)
{
    try
    {
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not open browser: {ex.Message}");
    }
}

static Process? StartWindowsTrayIcon(string managerUrl, string accessToken)
{
    if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        return null;

    try
    {
        var managerRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove", ".manager");
        Directory.CreateDirectory(managerRoot);
        var scriptPath = Path.Combine(managerRoot, "manager-tray.ps1");
        var apiBaseUrl = managerUrl.Split('?')[0].TrimEnd('/');
        File.WriteAllLines(scriptPath,
        [
            "Add-Type -AssemblyName System.Windows.Forms",
            "Add-Type -AssemblyName System.Drawing",
            $"$managerUrl = {PsLiteral(managerUrl)}",
            $"$apiBaseUrl = {PsLiteral(apiBaseUrl)}",
            $"$token = {PsLiteral(accessToken)}",
            "$notify = New-Object System.Windows.Forms.NotifyIcon",
            "$notify.Icon = [System.Drawing.SystemIcons]::Application",
            "$notify.Text = 'Cove Instance Manager'",
            "$notify.Visible = $true",
            "$menu = New-Object System.Windows.Forms.ContextMenuStrip",
            "$openItem = $menu.Items.Add('Open Instance Manager')",
            "$consoleItem = $menu.Items.Add('Open Default Logs Console')",
            "$exitItem = $menu.Items.Add('Exit Tray Icon')",
            "$openItem.add_Click({ Start-Process $managerUrl })",
            "$consoleItem.add_Click({",
            "  try {",
            "    Invoke-RestMethod -Method Post -Uri ($apiBaseUrl + '/api/instances/default/console') -Headers @{ 'X-Cove-Manager-Token' = $token } | Out-Null",
            "  } catch {",
            "    [System.Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Cove Instance Manager') | Out-Null",
            "  }",
            "})",
            "$exitItem.add_Click({ $notify.Visible = $false; [System.Windows.Forms.Application]::Exit() })",
            "$notify.add_DoubleClick({ Start-Process $managerUrl })",
            "$notify.ContextMenuStrip = $menu",
            "[System.Windows.Forms.Application]::Run()",
            "$notify.Dispose()",
        ]);

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        return Process.Start(startInfo);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Could not start tray icon: {ex.Message}");
        return null;
    }
}

static void StopProcess(Process? process)
{
    if (process == null)
        return;
    try
    {
        if (!process.HasExited)
            process.Kill(entireProcessTree: true);
    }
    catch
    {
    }
}

static string PsLiteral(string value) => "'" + value.Replace("'", "''") + "'";

internal sealed record ManagerAccess(string Token)
{
    public const string CookieName = "cove-manager-token";
}

internal sealed class ManagerOptions
{
    public string Host { get; private init; } = "127.0.0.1";
    public int Port { get; private init; }
    public string? CoveExecutablePath { get; private init; }
    public bool NoBrowser { get; private init; }
    public bool ShowHelp { get; private init; }

    public static ManagerOptions Parse(string[] args)
    {
        if (args.Any(arg => arg is "--help" or "-h" or "help"))
            return new ManagerOptions { ShowHelp = true };

        return new ManagerOptions
        {
            Host = HasFlag(args, "--lan") ? "0.0.0.0" : GetOption(args, "--host") ?? "127.0.0.1",
            Port = GetIntOption(args, "--port") ?? 0,
            CoveExecutablePath = GetOption(args, "--cove-exe") ?? Environment.GetEnvironmentVariable("COVE_MANAGER_COVE_EXE"),
            NoBrowser = HasFlag(args, "--no-browser"),
        };
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Cove Instance Manager");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  Cove.InstanceManager [--port <port>] [--host <host>] [--lan] [--no-browser] [--cove-exe <path>]");
        Console.WriteLine();
        Console.WriteLine("Defaults to a random localhost port and opens a tokenized local URL in the browser.");
    }

    private static string? GetOption(string[] args, string name)
    {
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (arg.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(name.Length + 1)..];
            if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1];
        }

        return null;
    }

    private static int? GetIntOption(string[] args, string name) => int.TryParse(GetOption(args, name), out var value) ? value : null;

    private static bool HasFlag(string[] args, string name) => args.Any(arg => string.Equals(arg, name, StringComparison.OrdinalIgnoreCase));
}

internal sealed class InstanceManagerService
{
    private const string RegistryFileName = "cove-instances.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HttpClient ReadinessClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromMilliseconds(750),
    };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string? _configuredCoveExecutablePath;
    private CoveInstanceRegistry _registry;

    private InstanceManagerService(CoveInstanceRegistry registry, string? configuredCoveExecutablePath)
    {
        _registry = registry;
        _configuredCoveExecutablePath = configuredCoveExecutablePath;
    }

    public string RegistryPath => Path.Combine(ManagerRoot, RegistryFileName);

    private static string ManagerRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cove");

    public static async Task<InstanceManagerService> CreateAsync(ManagerOptions options)
    {
        Directory.CreateDirectory(ManagerRoot);
        var path = Path.Combine(ManagerRoot, RegistryFileName);
        CoveInstanceRegistry registry;

        if (File.Exists(path))
        {
            await using var stream = File.OpenRead(path);
            registry = await JsonSerializer.DeserializeAsync<CoveInstanceRegistry>(stream, JsonOptions) ?? new CoveInstanceRegistry();
        }
        else
        {
            registry = new CoveInstanceRegistry();
        }

        var service = new InstanceManagerService(registry, options.CoveExecutablePath);
        if (service.EnsureDefaultInstance())
            await service.SaveAsync();

        return service;
    }

    public async Task<IReadOnlyList<InstanceDto>> ListAsync()
    {
        List<CoveInstanceRecord> snapshot;
        await _gate.WaitAsync();
        try
        {
            snapshot = _registry.Instances
                .OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        finally
        {
            _gate.Release();
        }

        var results = new List<InstanceDto>(snapshot.Count);
        foreach (var instance in snapshot)
            results.Add(await ToDtoAsync(instance));
        return results;
    }

    public async Task<InstanceDto> CreateAsync(CreateInstanceRequest request)
    {
        CoveInstanceRecord instance;
        await _gate.WaitAsync();
        try
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                throw new BadHttpRequestException("Name is required.");

            if (ResolveInstance(name, quiet: true) != null)
                throw new BadHttpRequestException($"An instance named '{name}' already exists.");

            var reserved = _registry.Instances
                .SelectMany(instance => new[] { instance.Port, instance.ManagedPostgres ? instance.PostgresPort : 0 })
                .Where(port => port > 0)
                .ToHashSet();

            var port = request.Port ?? FindFreePort(5073, reserved);
            reserved.Add(port);
            var managedPostgres = request.ManagedPostgres ?? true;
            var postgresPort = managedPostgres ? request.PostgresPort ?? FindFreePort(5433, reserved) : 0;
            var slug = MakeSlug(name);
            var id = slug;
            for (var suffix = 2; _registry.Instances.Any(instance => string.Equals(instance.Id, id, StringComparison.OrdinalIgnoreCase)); suffix++)
                id = $"{slug}-{suffix}";

            var homePath = NormalizePath(string.IsNullOrWhiteSpace(request.HomePath)
                ? Path.Combine(ManagerRoot, "instances", slug)
                : request.HomePath);
            Directory.CreateDirectory(homePath);

            instance = new CoveInstanceRecord
            {
                Id = id,
                Name = name,
                HomePath = homePath,
                Port = port,
                ManagedPostgres = managedPostgres,
                PostgresPort = postgresPort,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };

            _registry.Instances.Add(instance);
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await ToDtoAsync(instance);
    }

    public async Task<InstanceDto> UpdateAsync(string id, UpdateInstanceRequest request)
    {
        CoveInstanceRecord instance;
        await _gate.WaitAsync();
        try
        {
            instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");

            if (request.Name is not null)
            {
                var trimmedName = request.Name.Trim();
                if (string.IsNullOrWhiteSpace(trimmedName))
                    throw new BadHttpRequestException("Name cannot be empty.");
                var existing = _registry.Instances.FirstOrDefault(i =>
                    !string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Name, trimmedName, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                    throw new BadHttpRequestException($"An instance named '{trimmedName}' already exists.");
                instance.Name = trimmedName;
            }

            if (request.HomePath is not null)
                instance.HomePath = NormalizePath(request.HomePath);

            if (request.Port.HasValue)
            {
                if (request.Port.Value is < 1 or > 65535)
                    throw new BadHttpRequestException("Port must be between 1 and 65535.");
                instance.Port = request.Port.Value;
            }

            if (request.ManagedPostgres.HasValue)
                instance.ManagedPostgres = request.ManagedPostgres.Value;

            if (request.PostgresPort.HasValue)
                instance.PostgresPort = request.PostgresPort.Value;

            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await ToDtoAsync(instance);
    }

    public async Task<InstanceDto> StartAsync(string id)
    {
        CoveInstanceRecord instance;
        await _gate.WaitAsync();
        try
        {
            instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
            if (IsRunning(instance))
                return await ToDtoAsync(instance);

            if (!IsPortAvailable(instance.Port))
                throw new BadHttpRequestException($"Port {instance.Port} is already in use.");

            var launch = ResolveCoveLaunch();
            var logPath = CreateLogPath(instance, "log");
            var errorLogPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? CreateLogPath(instance, "error.log") : logPath;
            var pid = StartDetached(instance, launch, logPath, errorLogPath);

            instance.LastProcessId = pid;
            instance.LogPath = logPath;
            instance.ErrorLogPath = errorLogPath == logPath ? null : errorLogPath;
            instance.LastStartedAt = DateTimeOffset.UtcNow;
            instance.LastStoppedAt = null;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await ToDtoAsync(instance);
    }

    public async Task<InstanceDto> StopAsync(string id)
    {
        CoveInstanceRecord instance;
        await _gate.WaitAsync();
        try
        {
            instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
            if (instance.LastProcessId.HasValue)
            {
                try
                {
                    using var process = Process.GetProcessById(instance.LastProcessId.Value);
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        process.WaitForExit(10000);
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            // The instance's managed PostgreSQL postmaster is started detached (via pg_ctl), so it is
            // NOT part of cove's process tree and survives the Kill above. Stop it explicitly here,
            // otherwise every start/stop cycle leaks an orphaned "postgres" process.
            StopInstancePostgres(instance);

            instance.LastProcessId = null;
            instance.LastStoppedAt = DateTimeOffset.UtcNow;
            instance.UpdatedAt = DateTimeOffset.UtcNow;
            await SaveAsync();
        }
        finally
        {
            _gate.Release();
        }

        return await ToDtoAsync(instance);
    }

    // Stops an instance's managed PostgreSQL. Prefers a clean `pg_ctl stop` (flushes WAL); falls back
    // to force-killing the postmaster by the PID recorded in postmaster.pid. Best-effort.
    private static void StopInstancePostgres(CoveInstanceRecord instance)
    {
        if (!instance.ManagedPostgres || string.IsNullOrWhiteSpace(instance.HomePath))
            return;

        try
        {
            var dataDir = Path.Combine(instance.HomePath, "pgdata");
            var pidFile = Path.Combine(dataDir, "postmaster.pid");
            if (!File.Exists(pidFile))
                return;

            var pgCtl = Path.Combine(instance.HomePath, "pgsql", "bin",
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "pg_ctl.exe" : "pg_ctl");

            if (File.Exists(pgCtl))
            {
                try
                {
                    using var stop = Process.Start(new ProcessStartInfo(pgCtl, $"stop -D \"{dataDir}\" -m fast -w -t 30")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = Path.GetDirectoryName(pgCtl)!,
                    });
                    if (stop != null && stop.WaitForExit(35000) && stop.ExitCode == 0)
                        return;
                }
                catch
                {
                    // Fall through to PID kill.
                }
            }

            KillPostmasterByPidFile(pidFile);
        }
        catch
        {
            // Best-effort cleanup; never let a stop fail because of postgres teardown.
        }
    }

    private static void KillPostmasterByPidFile(string pidFile)
    {
        try
        {
            var firstLine = File.ReadLines(pidFile).FirstOrDefault();
            if (!int.TryParse(firstLine?.Trim(), out var pid) || pid <= 0)
                return;

            using var postmaster = Process.GetProcessById(pid);
            // Guard against a recycled PID: only kill if it is actually a postgres process.
            if (!postmaster.HasExited && postmaster.ProcessName.Contains("postgres", StringComparison.OrdinalIgnoreCase))
            {
                postmaster.Kill(entireProcessTree: true);
                postmaster.WaitForExit(10000);
            }
        }
        catch
        {
            // Process already gone, PID reused by something inaccessible, or no permission — ignore.
        }
    }

    public void Open(string id)
    {
        var instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
        Process.Start(new ProcessStartInfo($"http://127.0.0.1:{instance.Port}") { UseShellExecute = true });
    }

    public void OpenConsole(string id)
    {
        var instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            OpenConsoleWindows(instance);
        else
            OpenConsoleUnix(instance);
    }

    public async Task<object> RemoveAsync(string id, bool deleteData)
    {
        await _gate.WaitAsync();
        try
        {
            var instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
            if (IsRunning(instance))
                throw new BadHttpRequestException("Stop the instance before removing it.");
            if (deleteData && string.Equals(instance.Id, "default", StringComparison.OrdinalIgnoreCase))
                throw new BadHttpRequestException("Refusing to delete data for the default instance.");

            _registry.Instances.Remove(instance);
            if (deleteData && Directory.Exists(instance.HomePath))
                Directory.Delete(instance.HomePath, recursive: true);

            await SaveAsync();
            return new { ok = true };
        }
        finally
        {
            _gate.Release();
        }
    }

    public LogsDto GetLogs(string id, int tail)
    {
        var instance = ResolveInstance(id) ?? throw new BadHttpRequestException("Instance not found.");
        var lines = new List<string>();
        var clampedTail = Math.Clamp(tail, 1, 1000);
        if (!string.IsNullOrWhiteSpace(instance.LogPath) && File.Exists(instance.LogPath))
            lines.AddRange(ReadTailLines(instance.LogPath, clampedTail, "stdout"));
        if (!string.IsNullOrWhiteSpace(instance.ErrorLogPath) && File.Exists(instance.ErrorLogPath))
        {
            var errorLines = ReadTailLines(instance.ErrorLogPath, clampedTail, "stderr");
            if (errorLines.Count > 0)
            {
                lines.Add("--- stderr ---");
                lines.AddRange(errorLines);
            }
        }

        return new LogsDto(instance.LogPath, instance.ErrorLogPath, lines);
    }

    private static List<string> ReadTailLines(string path, int tail, string label)
    {
        try
        {
            var buffer = new Queue<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                buffer.Enqueue(line);
                while (buffer.Count > tail)
                    buffer.Dequeue();
            }

            return [.. buffer];
        }
        catch (IOException ex)
        {
            return [$"[{label} unavailable: {ex.Message}]"];
        }
        catch (UnauthorizedAccessException ex)
        {
            return [$"[{label} unavailable: {ex.Message}]"];
        }
    }

    private bool EnsureDefaultInstance()
    {
        if (_registry.Instances.Any(instance => string.Equals(instance.Id, "default", StringComparison.OrdinalIgnoreCase)))
            return false;

        _registry.Instances.Add(new CoveInstanceRecord
        {
            Id = "default",
            Name = "Default",
            HomePath = Path.GetFullPath(ManagerRoot),
            Port = 5073,
            ManagedPostgres = true,
            PostgresPort = 5433,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return true;
    }

    private CoveInstanceRecord? ResolveInstance(string idOrName, bool quiet = false)
    {
        var instance = _registry.Instances.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, idOrName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.Name, idOrName, StringComparison.OrdinalIgnoreCase));
        if (instance == null && !quiet)
            throw new BadHttpRequestException("Instance not found.");
        return instance;
    }

    private async Task<InstanceDto> ToDtoAsync(CoveInstanceRecord instance)
    {
        var status = await GetStatusAsync(instance);
        return new InstanceDto(
            instance.Id,
            instance.Name,
            instance.HomePath,
            instance.Port,
            instance.ManagedPostgres,
            instance.PostgresPort,
            string.Equals(status, "running", StringComparison.Ordinal),
            status,
            instance.LastProcessId,
            instance.LogPath,
            instance.ErrorLogPath,
            $"http://127.0.0.1:{instance.Port}",
            instance.LastStartedAt,
            instance.LastStoppedAt);
    }

    private async Task<string> GetStatusAsync(CoveInstanceRecord instance)
    {
        if (!IsRunning(instance))
            return "stopped";

        return await IsReadyAsync(instance.Port) ? "running" : "starting";
    }

    private static async Task<bool> IsReadyAsync(int port)
    {
        try
        {
            using var response = await ReadinessClient.GetAsync($"http://127.0.0.1:{port}/health", HttpCompletionOption.ResponseHeadersRead);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException)
        {
            return false;
        }
    }

    private static bool IsRunning(CoveInstanceRecord instance)
    {
        if (!instance.LastProcessId.HasValue)
            return false;
        try
        {
            using var process = Process.GetProcessById(instance.LastProcessId.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task SaveAsync()
    {
        Directory.CreateDirectory(ManagerRoot);
        _registry.Instances = _registry.Instances.OrderBy(instance => instance.Name, StringComparer.OrdinalIgnoreCase).ToList();
        await using var stream = File.Create(RegistryPath);
        await JsonSerializer.SerializeAsync(stream, _registry, JsonOptions);
    }

    private LaunchInvocation ResolveCoveLaunch()
    {
        var candidates = GetLaunchCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate))
                continue;

            if (string.Equals(Path.GetExtension(candidate), ".dll", StringComparison.OrdinalIgnoreCase))
                return new LaunchInvocation(FindDotnet(), [candidate], Path.GetDirectoryName(candidate)!);

            return new LaunchInvocation(candidate, [], Path.GetDirectoryName(candidate)!);
        }

        throw new InvalidOperationException(
            "Could not find the Cove executable next to the manager. Start with --cove-exe <path> to point at Cove. Searched: "
            + string.Join(", ", candidates));
    }

    private IEnumerable<string> GetLaunchCandidates()
    {
        if (!string.IsNullOrWhiteSpace(_configuredCoveExecutablePath))
            yield return NormalizePath(_configuredCoveExecutablePath);

        foreach (var baseDir in GetLaunchSearchDirectories())
        {
            // Current executable name is "Cove"; "Cove.Api" is kept as a fallback for
            // older builds/packages produced before the rename.
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                yield return Path.Combine(baseDir, "Cove.exe");
                yield return Path.Combine(baseDir, "Cove.Api.exe");
            }
            else
            {
                yield return Path.Combine(baseDir, "Cove");
                yield return Path.Combine(baseDir, "Cove.Api");
            }

            yield return Path.Combine(baseDir, "Cove.dll");
            yield return Path.Combine(baseDir, "Cove.Api.dll");
        }

        var configuration = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Cove.Api", "bin", configuration, "net10.0", "Cove.dll"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Cove.Api", "bin", configuration, "net10.0", "Cove.Api.dll"));
    }

    private static IEnumerable<string> GetLaunchSearchDirectories()
    {
        if (!string.IsNullOrWhiteSpace(Environment.ProcessPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var processDir = Path.GetDirectoryName(Environment.ProcessPath);
            if (!string.IsNullOrWhiteSpace(processDir))
                yield return processDir;
        }

        var entryLocation = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryLocation))
        {
            var entryDir = Path.GetDirectoryName(entryLocation);
            if (!string.IsNullOrWhiteSpace(entryDir))
                yield return entryDir;
        }

        yield return AppContext.BaseDirectory;
    }

    private static string FindDotnet() => Environment.ProcessPath is { } path
        && string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? path
            : "dotnet";

    private static int StartDetached(CoveInstanceRecord instance, LaunchInvocation launch, string logPath, string errorLogPath)
    {
        Directory.CreateDirectory(Path.Combine(instance.HomePath, ".manager"));
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.AppendAllText(logPath, $"[{DateTimeOffset.Now:u}] Starting Cove on http://127.0.0.1:{instance.Port}{Environment.NewLine}");

        var pidFile = Path.Combine(instance.HomePath, ".manager", "cove.pid");
        if (File.Exists(pidFile))
            File.Delete(pidFile);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            StartDetachedWindows(instance, launch, logPath, errorLogPath, pidFile);
        else
            StartDetachedUnix(instance, launch, logPath, pidFile);

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid))
                return pid;
            Thread.Sleep(100);
        }

        throw new InvalidOperationException("Cove process did not report a process id after launch.");
    }

    private static void StartDetachedWindows(CoveInstanceRecord instance, LaunchInvocation launch, string logPath, string errorLogPath, string pidFile)
    {
        var scriptPath = Path.Combine(instance.HomePath, ".manager", "start-cove.ps1");
        var argumentClause = launch.Arguments.Count == 0
            ? string.Empty
            : " -ArgumentList @(" + string.Join(", ", launch.Arguments.Select(PsQuote)) + ")";
        var scriptLines = new List<string>
        {
            "$ErrorActionPreference = 'Stop'",
            $"$pidPath = {PsQuote(pidFile)}",
            $"  $env:{CoveDefaultPaths.DataRootEnvironmentVariable} = {PsQuote(instance.HomePath)}",
            $"  $env:Cove__Port = {PsQuote(instance.Port.ToString())}",
            $"  $env:Cove__Postgres__Managed = {PsQuote(instance.ManagedPostgres.ToString().ToLowerInvariant())}",
            $"  $env:Cove__Postgres__Port = {PsQuote(instance.PostgresPort.ToString())}",
            $"$p = Start-Process -FilePath {PsQuote(launch.FileName)}{argumentClause} -WorkingDirectory {PsQuote(launch.WorkingDirectory)} -RedirectStandardOutput {PsQuote(logPath)} -RedirectStandardError {PsQuote(errorLogPath)} -WindowStyle Hidden -PassThru",
            "$p.Id | Set-Content -Path $pidPath -Encoding ascii",
        };
        File.WriteAllLines(scriptPath, scriptLines);

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = launch.WorkingDirectory,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start PowerShell launcher.");
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (File.Exists(pidFile))
                return;
            if (process.HasExited)
                throw new InvalidOperationException($"PowerShell launcher exited with code {process.ExitCode}.");
            Thread.Sleep(100);
        }
    }

    private static void OpenConsoleWindows(CoveInstanceRecord instance)
    {
        Directory.CreateDirectory(Path.Combine(instance.HomePath, ".manager"));
        var scriptPath = Path.Combine(instance.HomePath, ".manager", "show-cove-logs.ps1");
        File.WriteAllLines(scriptPath,
        [
            $"$logPath = {PsQuote(instance.LogPath ?? string.Empty)}",
            $"$errorLogPath = {PsQuote(instance.ErrorLogPath ?? string.Empty)}",
            $"$title = {PsQuote($"Cove Logs - {instance.Name}")}",
            "try { $Host.UI.RawUI.WindowTitle = $title } catch {}",
            "[Console]::Title = $title",
            $"Write-Host {PsQuote($"Cove logs for {instance.Name}")}",
            "if ($logPath) { Write-Host $logPath }",
            "if ($errorLogPath) { Write-Host $errorLogPath }",
            "$paths = @()",
            "if ($logPath -and (Test-Path $logPath)) { $paths += $logPath }",
            "if ($errorLogPath -and (Test-Path $errorLogPath)) { $paths += $errorLogPath }",
            "if ($paths.Count -eq 0) {",
            "  Write-Host 'No log files exist for this instance yet.' -ForegroundColor Yellow",
            "  Read-Host 'Press Enter to close' | Out-Null",
            "  exit 0",
            "}",
            "Write-Host ''",
            "Write-Host 'Tailing logs. Press Ctrl+C to stop.' -ForegroundColor Cyan",
            "Get-Content -Path $paths -Tail 200 -Wait",
        ]);

        var startInfo = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        Process.Start(startInfo);
    }

    [UnsupportedOSPlatform("windows")]
    private static void OpenConsoleUnix(CoveInstanceRecord instance)
    {
        Directory.CreateDirectory(Path.Combine(instance.HomePath, ".manager"));
        var scriptPath = Path.Combine(instance.HomePath, ".manager", "show-cove-logs.sh");
        var paths = new[] { instance.LogPath, instance.ErrorLogPath }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToArray();
        var pathArgs = paths.Length == 0 ? ShQuote(Path.Combine(instance.HomePath, "logs")) : string.Join(" ", paths.Select(ShQuote));
        File.WriteAllLines(scriptPath,
        [
            "#!/bin/sh",
            $"printf '%s\\n' {ShQuote($"Cove logs for {instance.Name}")}",
            $"tail -n 200 -f {pathArgs}",
        ]);
        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", $"-a Terminal {ShQuote(scriptPath)}");
            return;
        }

        foreach (var terminal in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
        {
            try
            {
                Process.Start(terminal, terminal == "gnome-terminal" ? $"-- {ShQuote(scriptPath)}" : $"-e {ShQuote(scriptPath)}");
                return;
            }
            catch
            {
            }
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void StartDetachedUnix(CoveInstanceRecord instance, LaunchInvocation launch, string logPath, string pidFile)
    {
        var scriptPath = Path.Combine(instance.HomePath, ".manager", "start-cove.sh");
        var command = string.Join(" ", new[] { ShQuote(launch.FileName) }.Concat(launch.Arguments.Select(ShQuote)));
        File.WriteAllLines(scriptPath,
        [
            "#!/bin/sh",
            $"export {CoveDefaultPaths.DataRootEnvironmentVariable}={ShQuote(instance.HomePath)}",
            $"export Cove__Port={ShQuote(instance.Port.ToString())}",
            $"export Cove__Postgres__Managed={ShQuote(instance.ManagedPostgres.ToString().ToLowerInvariant())}",
            $"export Cove__Postgres__Port={ShQuote(instance.PostgresPort.ToString())}",
            $"cd {ShQuote(launch.WorkingDirectory)} || exit 1",
            $"nohup {command} >> {ShQuote(logPath)} 2>&1 &",
            $"echo $! > {ShQuote(pidFile)}",
        ]);

        File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        startInfo.ArgumentList.Add(scriptPath);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start shell launcher.");
        process.WaitForExit(15000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Shell launcher exited with code {process.ExitCode}.");
    }

    private static string CreateLogPath(CoveInstanceRecord instance, string suffix)
    {
        var logDir = Path.Combine(instance.HomePath, "logs");
        Directory.CreateDirectory(logDir);
        return Path.Combine(logDir, $"cove-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.{suffix}");
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static int FindFreePort(int preferred, ISet<int> reserved)
    {
        for (var port = preferred; port < preferred + 1000; port++)
        {
            if (!reserved.Contains(port) && IsPortAvailable(port))
                return port;
        }

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string NormalizePath(string path) => Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));

    private static string MakeSlug(string value)
    {
        var chars = value.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(slug) ? $"instance-{Guid.NewGuid():N}"[..17] : slug;
    }

    private static string PsQuote(string value) => "'" + value.Replace("'", "''") + "'";

    private static string ShQuote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private sealed record LaunchInvocation(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory);
}

internal sealed record CreateInstanceRequest(string? Name, string? HomePath, int? Port, bool? ManagedPostgres, int? PostgresPort);
internal sealed record UpdateInstanceRequest(string? Name, string? HomePath, int? Port, bool? ManagedPostgres, int? PostgresPort);

internal sealed record InstanceDto(
    string Id,
    string Name,
    string HomePath,
    int Port,
    bool ManagedPostgres,
    int PostgresPort,
    bool Running,
    string Status,
    int? ProcessId,
    string? LogPath,
    string? ErrorLogPath,
    string Url,
    DateTimeOffset? LastStartedAt,
    DateTimeOffset? LastStoppedAt);

internal sealed record LogsDto(string? LogPath, string? ErrorLogPath, IReadOnlyList<string> Lines);

internal sealed class CoveInstanceRegistry
{
    public List<CoveInstanceRecord> Instances { get; set; } = [];
}

internal sealed class CoveInstanceRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string HomePath { get; set; } = string.Empty;
    public int Port { get; set; }
    public bool ManagedPostgres { get; set; } = true;
    public int PostgresPort { get; set; }
    public int? LastProcessId { get; set; }
    public string? LogPath { get; set; }
    public string? ErrorLogPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastStartedAt { get; set; }
    public DateTimeOffset? LastStoppedAt { get; set; }
}

internal static class ManagerPage
{
    public const string Html = """
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>Cove Instance Manager</title>
  <style>
    :root { color-scheme: dark; --bg: #111315; --panel: #181b1f; --line: #2b3036; --text: #e8eaed; --muted: #a2aab3; --accent: #ff4d16; --green: #6ee7b7; }
    * { box-sizing: border-box; }
    body { margin: 0; background: var(--bg); color: var(--text); font: 14px/1.45 system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif; }
    main { max-width: 1120px; margin: 0 auto; padding: 28px 20px 48px; }
    header { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin-bottom: 20px; }
    h1 { margin: 0; font-size: 24px; letter-spacing: 0; }
    button, input { font: inherit; }
    button { border: 1px solid var(--line); border-radius: 8px; background: #20242a; color: var(--text); padding: 8px 12px; cursor: pointer; }
    button:hover { border-color: var(--accent); }
    button.primary { border-color: var(--accent); background: var(--accent); color: white; }
    button.danger { color: #ffb4b4; }
    input { width: 100%; border: 1px solid var(--line); border-radius: 8px; background: #121417; color: var(--text); padding: 9px 10px; outline: none; }
    input:focus { border-color: var(--accent); }
    label { display: grid; gap: 5px; color: var(--muted); font-size: 12px; text-transform: uppercase; }
    form { display: grid; grid-template-columns: minmax(12rem, 1fr) minmax(18rem, 2fr) 7rem 7rem auto; gap: 12px; align-items: end; background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 14px; margin-bottom: 16px; }
    .toolbar { display: flex; gap: 8px; align-items: center; }
    .list { display: grid; gap: 10px; }
    .instance { display: grid; grid-template-columns: 1fr auto; gap: 14px; align-items: center; background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 14px; }
    .name { font-weight: 700; font-size: 16px; }
    .meta { display: flex; flex-wrap: wrap; gap: 10px; color: var(--muted); margin-top: 5px; }
    .pill { display: inline-flex; align-items: center; gap: 6px; border: 1px solid var(--line); border-radius: 999px; padding: 2px 8px; }
    .dot { width: 8px; height: 8px; border-radius: 99px; background: #6b7280; }
    .running .dot { background: var(--green); }
    .starting .dot { background: #fbbf24; }
    .actions { display: flex; flex-wrap: wrap; gap: 8px; justify-content: flex-end; }
    .logs { white-space: pre-wrap; overflow: auto; max-height: 320px; margin-top: 14px; padding: 12px; border: 1px solid var(--line); border-radius: 8px; background: #090a0c; color: #d4d8dd; }
    .error { color: #ffb4b4; min-height: 20px; margin-bottom: 10px; }
    .modal-overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.6); display: flex; align-items: center; justify-content: center; z-index: 100; }
    .modal-overlay[hidden] { display: none; }
    .modal { background: var(--panel); border: 1px solid var(--line); border-radius: 12px; padding: 24px; min-width: 380px; max-width: 90vw; display: grid; gap: 14px; }
    .modal h2 { margin: 0; font-size: 18px; }
    .modal .buttons { display: flex; gap: 8px; justify-content: flex-end; }
    .modal form { all: unset; display: grid; gap: 14px; }
    .modal form label { display: grid; gap: 5px; color: var(--muted); font-size: 12px; text-transform: uppercase; }
    .modal form input { width: 100%; border: 1px solid var(--line); border-radius: 8px; background: #121417; color: var(--text); padding: 9px 10px; outline: none; }
    .modal form input:focus { border-color: var(--accent); }
    @media (max-width: 820px) { form, .instance { grid-template-columns: 1fr; } .actions, header { justify-content: flex-start; } }
  </style>
</head>
<body>
  <main>
    <header>
      <h1>Cove Instance Manager</h1>
      <div class="toolbar"><button id="refresh">Refresh</button></div>
    </header>
    <div id="error" class="error"></div>
    <form id="create-form">
      <label>Name<input id="name" required placeholder="Sandbox" /></label>
      <label>Home path<input id="home" placeholder="Auto" /></label>
      <label>App port<input id="port" type="number" min="1" max="65535" placeholder="Auto" /></label>
      <label>PG port<input id="pg-port" type="number" min="1" max="65535" placeholder="Auto" /></label>
      <button class="primary" type="submit">Create</button>
    </form>
    <div id="edit-modal" class="modal-overlay" hidden>
      <div class="modal">
        <h2>Edit Instance</h2>
        <form id="edit-form">
          <label>Name<input id="edit-name" required /></label>
          <label>Home path<input id="edit-home" /></label>
          <label>App port<input id="edit-port" type="number" min="1" max="65535" /></label>
          <label>PG port<input id="edit-pg-port" type="number" min="1" max="65535" /></label>
          <div class="buttons">
            <button type="button" id="edit-cancel">Cancel</button>
            <button class="primary" type="submit">Save</button>
          </div>
        </form>
      </div>
    </div>
    <section id="instances" class="list"></section>
    <pre id="logs" class="logs" hidden></pre>
  </main>
  <script>
    const $ = (id) => document.getElementById(id);
    const error = $('error');
    const list = $('instances');
    const logs = $('logs');

    async function request(path, options = {}) {
      error.textContent = '';
      const response = await fetch(path, { ...options, headers: { 'content-type': 'application/json', ...(options.headers || {}) } });
      if (!response.ok) throw new Error(await response.text());
      return response.json();
    }

    function text(value) { return value == null || value === '' ? '-' : value; }

    async function load() {
      try {
        const instances = await request('/api/instances');
        list.innerHTML = instances.map(renderInstance).join('');
      } catch (err) {
        error.textContent = err.message;
      }
    }

    function renderInstance(instance) {
            const status = instance.status || (instance.running ? 'running' : 'stopped');
            const isRunning = status === 'running';
            const isStarting = status === 'starting';
            const statusLabel = isRunning ? 'Running' : isStarting ? 'Starting' : 'Stopped';
    return `<article class="instance ${status}">
        <div>
          <div class="name">${escapeHtml(instance.name)}</div>
          <div class="meta">
                        <span class="pill"><span class="dot"></span>${statusLabel}</span>
            <span>${escapeHtml(instance.url)}</span>
            <span>PG ${instance.managedPostgres ? instance.postgresPort : 'external'}</span>
            <span>${escapeHtml(instance.homePath)}</span>
          </div>
        </div>
        <div class="actions">
                    ${status === 'stopped' ? `<button class="primary" data-action="start" data-id="${instance.id}">Start</button>` : `<button data-action="stop" data-id="${instance.id}">Stop</button>`}
                    <button data-action="open" data-id="${instance.id}" ${isRunning ? '' : 'disabled'}>Open</button>
          <button data-action="logs" data-id="${instance.id}">Logs</button>
          <button data-action="console" data-id="${instance.id}">Console</button>
          <button data-action="edit" data-id="${instance.id}">Edit</button>
          ${instance.id === 'default' ? '' : `<button class="danger" data-action="remove" data-id="${instance.id}">Remove</button>`}
        </div>
      </article>`;
    }

    function escapeHtml(value) {
      return String(value).replace(/[&<>'"]/g, (ch) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[ch]));
    }

    let editingId = null;
    const editModal = $('edit-modal');
    const editForm = $('edit-form');

    function showEdit(instance) {
      editingId = instance.id;
      $('edit-name').value = instance.name;
      $('edit-home').value = instance.homePath;
      $('edit-port').value = instance.port;
      $('edit-pg-port').value = instance.managedPostgres ? instance.postgresPort : '';
      editModal.hidden = false;
      $('edit-name').focus();
    }

    $('edit-cancel').addEventListener('click', () => { editModal.hidden = true; editingId = null; });

    editForm.addEventListener('submit', async (event) => {
      event.preventDefault();
      try {
        await request(`/api/instances/${editingId}`, {
          method: 'PUT',
          body: JSON.stringify({
            name: $('edit-name').value || null,
            homePath: $('edit-home').value || null,
            port: $('edit-port').value ? Number($('edit-port').value) : null,
            postgresPort: $('edit-pg-port').value ? Number($('edit-pg-port').value) : null,
          }),
        });
        editModal.hidden = true;
        editingId = null;
        await load();
      } catch (err) { error.textContent = err.message; }
    });

    editModal.addEventListener('click', (event) => {
      if (event.target === editModal) { editModal.hidden = true; editingId = null; }
    });

    $('create-form').addEventListener('submit', async (event) => {
      event.preventDefault();
      try {
        await request('/api/instances', {
          method: 'POST',
          body: JSON.stringify({
            name: $('name').value,
            homePath: $('home').value || null,
            port: $('port').value ? Number($('port').value) : null,
            postgresPort: $('pg-port').value ? Number($('pg-port').value) : null,
          }),
        });
        event.target.reset();
        await load();
      } catch (err) { error.textContent = err.message; }
    });

    list.addEventListener('click', async (event) => {
      const button = event.target.closest('button[data-action]');
      if (!button) return;
      const action = button.dataset.action;
      const id = button.dataset.id;
      try {
        if (action === 'start') await request(`/api/instances/${id}/start`, { method: 'POST' });
        if (action === 'stop') await request(`/api/instances/${id}/stop`, { method: 'POST' });
        if (action === 'open') await request(`/api/instances/${id}/open`, { method: 'POST' });
        if (action === 'console') await request(`/api/instances/${id}/console`, { method: 'POST' });
        if (action === 'edit') {
          const instances = await request('/api/instances');
          const instance = instances.find(i => i.id === id);
          if (instance) showEdit(instance);
          return;
        }
        if (action === 'remove' && confirm('Remove this instance from the manager?')) await request(`/api/instances/${id}?deleteData=false`, { method: 'DELETE' });
        if (action === 'logs') {
          const result = await request(`/api/instances/${id}/logs?tail=160`);
          logs.hidden = false;
          logs.textContent = [text(result.logPath), text(result.errorLogPath), '', ...(result.lines || [])].join('\n');
        }
        await load();
      } catch (err) { error.textContent = err.message; }
    });

    $('refresh').addEventListener('click', load);
    load();
    setInterval(load, 5000);
  </script>
</body>
</html>
""";
}