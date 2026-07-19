using Microsoft.AspNetCore.Mvc;
using Cove.Api.Services;
using Cove.Core.Auth;
using Cove.Core.DTOs;
using Cove.Core.Interfaces;
using Cove.Plugins;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Cove.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[RequiresPermission(Permissions.ExtensionsRead)]
public class PluginsController(
    ExtensionManager extensionManager,
    IJobService jobService,
    CoveConfiguration config,
    ConfigService configService,
    ILogger<PluginsController> logger) : ControllerBase
{
    [HttpGet]
    public ActionResult<List<PluginDto>> ListPlugins()
    {
        var manifest = extensionManager.GetAggregatedManifest();
        var plugins = extensionManager.Extensions
            .Select(ext => extensionManager.ExecuteExtensionMetadata(ext, () => new PluginDto(
                    ext.Id,
                    ext.Name,
                    ext.Description ?? string.Empty,
                    ext.Version,
                    !config.DisabledPlugins.Contains(ext.Id),
                    GetPluginTasksCore(ext))))
            .ToList();

        // Also scan for Python plugins
        foreach (var pluginDir in GetPluginDirectories())
        {
            var pyPlugins = DiscoverPythonPlugins(pluginDir);
            plugins.AddRange(pyPlugins);
        }

        return Ok(plugins);
    }

    [HttpGet("tasks")]
    public ActionResult<List<PluginTaskDto>> ListTasks()
    {
        var tasks = new List<PluginTaskDto>();
        foreach (var ext in extensionManager.Extensions)
            tasks.AddRange(GetPluginTasks(ext));

        // Python plugin tasks
        foreach (var pluginDir in GetPluginDirectories())
        {
            foreach (var plugin in DiscoverPythonPlugins(pluginDir))
                tasks.AddRange(plugin.Tasks);
        }

        return Ok(tasks);
    }

    [HttpPost("run-task")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public ActionResult<object> RunTask([FromBody] RunPluginTaskDto dto)
    {
        if (!IsSafePluginId(dto.PluginId))
            return BadRequest("Invalid plugin id.");

        // Check if it's a Python plugin
        foreach (var pluginDir in GetPluginDirectories())
        {
            if (!TryResolvePluginDirectory(pluginDir, dto.PluginId, out var scriptDir))
                continue;

            var configPath = Path.Combine(scriptDir, "plugin.yml");
            if (!System.IO.File.Exists(configPath))
                configPath = Path.Combine(scriptDir, "plugin.yaml");
            if (!System.IO.File.Exists(configPath)) continue;

            var jobId = jobService.Enqueue($"plugin:{dto.PluginId}", $"Running {dto.PluginId}/{dto.TaskName}", async (progress, ct) =>
            {
                progress.Report(0, $"Starting plugin task {dto.TaskName}...");
                var entryPoint = FindPythonEntryPoint(scriptDir);

                if (entryPoint != null)
                {
                    await RunPythonPluginAsync(entryPoint, dto.TaskName, dto.Args, ct);
                }
                else
                {
                    logger.LogWarning("No entry point found for plugin {PluginId}", dto.PluginId);
                }

                progress.Report(1, "Done");
            }, exclusive: false);

            return Ok(new { jobId });
        }

        // Check .NET extensions
        var ext = extensionManager.GetExtension(dto.PluginId);
        if (ext == null) return NotFound($"Plugin '{dto.PluginId}' not found");

        if (ext is IJobExtension jobExt)
        {
            var execution = extensionManager.CaptureExtensionExecution(jobExt);
            var jobMetadata = extensionManager.ExecuteExtension(
                execution,
                () => (
                    Job: jobExt.Jobs.FirstOrDefault(j => j.Id == dto.TaskName),
                    ExtensionName: ext.Name));
            var jobDef = jobMetadata.Job;
            if (jobDef == null)
                return NotFound($"Task '{dto.TaskName}' was not found for plugin '{dto.PluginId}'.");
            if (!jobDef.ShowInTaskList)
                return NotFound($"Task '{dto.TaskName}' is not exposed on the extension tasks page.");

            var label = jobDef?.Name ?? dto.TaskName;
            var netJobId = jobService.Enqueue($"plugin:{dto.PluginId}", $"{jobMetadata.ExtensionName}: {label}", async (progress, ct) =>
            {
                var adapter = new PluginJobProgressAdapter(progress);
                await extensionManager.ExecuteExtensionAsync(
                    execution,
                    () => jobExt.RunJobAsync(dto.TaskName, dto.Args != null ? dto.Args : null, adapter, ct));
            }, exclusive: false);
            return Ok(new { jobId = netJobId });
        }

        return BadRequest($"Plugin '{dto.PluginId}' does not expose runnable tasks.");
    }

    [HttpPost("settings")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> UpdateSettings([FromBody] PluginSettingsDto dto)
    {
        foreach (var (pluginId, enabled) in dto.EnabledMap)
        {
            if (enabled)
            {
                await extensionManager.EnableExtensionAsync(pluginId);
                config.DisabledPlugins.Remove(pluginId);
            }
            else
            {
                await extensionManager.DisableExtensionAsync(pluginId);
                config.DisabledPlugins.Add(pluginId);
            }
        }

        await configService.SaveCurrentConfigAsync();
        return Ok();
    }

    [HttpGet("{pluginId}/config")]
    public ActionResult<Dictionary<string, object?>> GetPluginConfig(string pluginId)
    {
        if (config.PluginConfigurations.TryGetValue(pluginId, out var cfg))
            return Ok(cfg);
        return Ok(new Dictionary<string, object?>());
    }

    [HttpPost("{pluginId}/config")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> SetPluginConfig(string pluginId, [FromBody] Dictionary<string, object?> values)
    {
        config.PluginConfigurations[pluginId] = values;
        await configService.SaveCurrentConfigAsync();
        return Ok();
    }

    [HttpPost("reload")]
    [RequiresPermission(Permissions.ExtensionsConfigure)]
    public async Task<IActionResult> ReloadPlugins()
    {
        await extensionManager.InitializeAllAsync(HttpContext.RequestServices);
        return Ok(new { message = "Plugins reloaded" });
    }

    // ===== Helpers =====

    private List<string> GetPluginDirectories()
    {
        var dirs = new List<string>();
        var defaultDir = CoveDefaultPaths.GetDataSubdirectory("plugins");
        dirs.Add(defaultDir);

        if (config.ExtensionPaths?.Count > 0)
            dirs.AddRange(config.ExtensionPaths);

        return dirs;
    }

    public static bool IsSafePluginId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." || Path.IsPathRooted(id))
            return false;
        if (id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('/') || id.Contains('\\'))
            return false;

        return id.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
    }

    public static bool TryResolvePluginDirectory(string pluginRoot, string pluginId, out string pluginDirectory)
    {
        pluginDirectory = string.Empty;
        if (!IsSafePluginId(pluginId))
            return false;

        try
        {
            var root = Path.GetFullPath(pluginRoot);
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, pluginId));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!candidate.StartsWith(rootWithSeparator, comparison))
                return false;

            pluginDirectory = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private List<PluginTaskDto> GetPluginTasks(IExtension ext)
        => extensionManager.ExecuteExtensionMetadata(ext, () => GetPluginTasksCore(ext));

    private static List<PluginTaskDto> GetPluginTasksCore(IExtension ext)
    {
        // If the extension defines typed jobs, expose those
        if (ext is IJobExtension jobExt && jobExt.Jobs.Count > 0)
            return jobExt.Jobs
                .Where(j => j.ShowInTaskList)
                .Select(j => new PluginTaskDto(j.Id, j.Name))
                .ToList();

        return [];
    }

    private List<PluginDto> DiscoverPythonPlugins(string pluginDir)
    {
        var plugins = new List<PluginDto>();
        if (!Directory.Exists(pluginDir)) return plugins;

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        foreach (var dir in Directory.GetDirectories(pluginDir))
        {
            var configPath = Path.Combine(dir, "plugin.yml");
            if (!System.IO.File.Exists(configPath))
                configPath = Path.Combine(dir, "plugin.yaml");
            if (!System.IO.File.Exists(configPath)) continue;

            var id = Path.GetFileName(dir);
            var entryPoint = FindPythonEntryPoint(dir);
            if (entryPoint == null) continue;

            var name = id;
            var description = $"Python plugin: {id}";
            var version = "0.0.1";
            string? url = null;
            var tasks = new List<PluginTaskDto>();
            var settingsSchema = new List<PluginSettingSchemaDto>();

            try
            {
                var yamlText = System.IO.File.ReadAllText(configPath);
                var parsed = deserializer.Deserialize<Dictionary<string, object?>>(yamlText);
                if (parsed != null)
                {
                    if (parsed.TryGetValue("name", out var n) && n is string ns) name = ns;
                    if (parsed.TryGetValue("description", out var d) && d is string ds) description = ds;
                    if (parsed.TryGetValue("version", out var v) && v != null) version = v.ToString()!;
                    if (parsed.TryGetValue("url", out var u) && u is string us) url = us;

                    // Parse tasks
                    if (parsed.TryGetValue("exec", out var exec) && exec is Dictionary<object, object> execDict)
                    {
                        tasks.Clear();
                        if (execDict.TryGetValue("tasks", out var tasksObj) && tasksObj is List<object> tasksList)
                        {
                            foreach (var t in tasksList)
                            {
                                if (t is Dictionary<object, object> td)
                                {
                                    var taskName = td.TryGetValue("name", out var tn) ? tn?.ToString() ?? "run" : "run";
                                    var taskDesc = td.TryGetValue("description", out var tdesc) ? tdesc?.ToString() ?? "" : "";
                                    tasks.Add(new PluginTaskDto(taskName, taskDesc));
                                }
                            }
                        }
                    }

                    // Parse settings schema
                    if (parsed.TryGetValue("settings", out var settings) && settings is Dictionary<object, object> settingsDict)
                    {
                        foreach (var (key, val) in settingsDict)
                        {
                            var settingName = key.ToString()!;
                            var settingType = "STRING";
                            string? displayName = null;
                            string? settingDescription = null;

                            if (val is Dictionary<object, object> sd)
                            {
                                if (sd.TryGetValue("type", out var st)) settingType = st?.ToString()?.ToUpperInvariant() ?? "STRING";
                                if (sd.TryGetValue("displayName", out var dn)) displayName = dn?.ToString();
                                if (sd.TryGetValue("description", out var sdesc)) settingDescription = sdesc?.ToString();
                            }

                            settingsSchema.Add(new PluginSettingSchemaDto(settingName, settingType, displayName, settingDescription));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse plugin.yml for {PluginId}", id);
            }

            plugins.Add(new PluginDto(
                id,
                name,
                description,
                version,
                !config.DisabledPlugins.Contains(id),
                tasks,
                settingsSchema.Count > 0 ? settingsSchema : null,
                url
            ));
        }

        return plugins;
    }

    private static string? FindPythonEntryPoint(string dir)
    {
        var mainPy = Path.Combine(dir, "main.py");
        if (System.IO.File.Exists(mainPy)) return mainPy;

        var initPy = Path.Combine(dir, "__init__.py");
        if (System.IO.File.Exists(initPy)) return initPy;

        return Directory.GetFiles(dir, "*.py").FirstOrDefault();
    }

    private async Task RunPythonPluginAsync(string scriptPath, string taskName, Dictionary<string, string>? args, CancellationToken ct)
    {
        var pythonPath = FindPython();
        if (pythonPath == null)
        {
            logger.LogError("Python not found in PATH");
            return;
        }

        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = pythonPath,
            Arguments = $"\"{scriptPath}\"",
            WorkingDirectory = Path.GetDirectoryName(scriptPath),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // Pass task name and args as environment variables
        process.StartInfo.EnvironmentVariables["COVE_TASK"] = taskName;
        if (args != null)
        {
            foreach (var (key, value) in args)
                process.StartInfo.EnvironmentVariables[$"COVE_ARG_{key.ToUpperInvariant()}"] = value;
        }

        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);
        var error = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            logger.LogWarning("Python plugin {Script} exited with code {Code}: {Error}", scriptPath, process.ExitCode, error);
        else if (!string.IsNullOrEmpty(output))
            logger.LogDebug("Python plugin {Script} output: {Output}", scriptPath, output.TrimEnd());
    }

    private static string? FindPython()
    {
        var names = new[] { "python3", "python" };
        foreach (var name in names)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                var exts = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" };
                foreach (var ext in exts)
                {
                    var fullPath = Path.Combine(dir, name + ext);
                    if (System.IO.File.Exists(fullPath)) return fullPath;
                }
            }
        }
        return null;
    }
}

internal sealed class PluginJobProgressAdapter(Cove.Core.Interfaces.IJobProgress inner) : Cove.Plugins.IJobProgress
{
    public void Report(double percent, string? message = null) => inner.Report(percent, message);
}
