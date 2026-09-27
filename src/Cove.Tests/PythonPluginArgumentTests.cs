using Cove.Api.Controllers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Cove.Tests;

/// <summary>
/// A Python plugin's entry point can be any <c>*.py</c> file in its directory, so its name is not under
/// Cove's control. It must reach the interpreter as exactly one argument, unchanged.
/// </summary>
public class PythonPluginArgumentTests
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task RunPythonScript_PassesScriptPathAsOneArgument()
    {
        using var python = new ArgvRecordingExecutable(name: "python3");
        var root = Path.Combine(Path.GetTempPath(), $"cove-python-plugin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var script = Path.Combine(root, "task\" -c \"x.py");
            await File.WriteAllTextAsync(script, "", TestContext.Current.CancellationToken);

            await PluginsController.RunPythonScriptAsync(
                python.ExecutablePath, script, "task", args: null, NullLogger.Instance, TestContext.Current.CancellationToken);

            var argv = Assert.Single(python.Invocations);
            Assert.Equal([script], argv);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
