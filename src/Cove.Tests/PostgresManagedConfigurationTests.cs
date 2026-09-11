using System.Reflection;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cove.Tests;

public sealed class PostgresManagedConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove user's home {Guid.NewGuid():N}");

    [Theory]
    [InlineData("")]
    [InlineData("#dynamic_library_path = '$libdir'\n")]
    [InlineData("dynamic_library_path = '/previous/managed/lib'\n")]
    public async Task PreservesStandardLibraryLookupAndRepairsExistingConfiguration(string original)
    {
        var data = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(data);
        var configPath = Path.Combine(data, "postgresql.conf");
        var versionPath = Path.Combine(data, "PG_VERSION");
        await File.WriteAllTextAsync(configPath, original + "work_mem = '8MB'\n", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(versionPath, "18", TestContext.Current.CancellationToken);
        var manager = new PostgresManagerService(Options.Create(new PostgresConfig { DataPath = _root }),
            NullLogger<PostgresManagerService>.Instance);
        var method = typeof(PostgresManagerService).GetMethod("EnsureManagedConfigurationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        await (Task)method.Invoke(manager, [TestContext.Current.CancellationToken])!;

        var updated = await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken);
        var managedLib = Path.Combine(_root, "pgsql", "lib").Replace('\\', '/').Replace("'", "''");
        var librarySetting = Assert.Single(updated.Split('\n'), line => line.StartsWith("dynamic_library_path ="));
        Assert.Equal($"dynamic_library_path = '{managedLib}{Path.PathSeparator}$libdir'", librarySetting.TrimEnd('\r'));
        Assert.Contains("work_mem = '8MB'", updated);
        Assert.Equal("18", await File.ReadAllTextAsync(versionPath, TestContext.Current.CancellationToken));

        await (Task)method.Invoke(manager, [TestContext.Current.CancellationToken])!;
        Assert.Equal(updated, await File.ReadAllTextAsync(configPath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
