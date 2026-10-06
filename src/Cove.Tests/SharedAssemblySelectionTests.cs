using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.Json;
using Cove.Plugins;
using Microsoft.Extensions.Configuration;

namespace Cove.Tests;

/// <summary>
/// Shared assemblies load into the default context, which never unloads. Each test therefore uses an
/// assembly name of its own so the process-wide preferred copy and loaded assembly start empty.
/// </summary>
public sealed class SharedAssemblySelectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove-shared-assembly-{Guid.NewGuid():N}");
    private readonly string _extensionsDir;
    private readonly string _assemblyName = $"Cove.Tests.SharedContract{Guid.NewGuid():N}";

    public SharedAssemblySelectionTests()
    {
        _extensionsDir = Path.Combine(_root, "extensions");
        Directory.CreateDirectory(_extensionsDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Preload_prefers_the_highest_assembly_version_over_the_newest_file()
    {
        var newerContractDir = WriteExtension("contract.owner", new Version(2, 0, 0, 0));
        File.SetLastWriteTimeUtc(Path.Combine(newerContractDir, $"{_assemblyName}.dll"), DateTime.UtcNow.AddDays(-1));
        var olderBundledDir = WriteExtension("contract.bundler", new Version(1, 0, 0, 0));

        var changes = ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [newerContractDir, olderBundledDir]);

        Assert.Empty(changes);
        Assert.Equal(new Version(2, 0, 0, 0), LoadedSharedAssembly().GetName().Version);
    }

    [Fact]
    public void Preload_reports_a_newer_installed_copy_without_replacing_the_loaded_one()
    {
        var ownerDir = WriteExtension("contract.owner", new Version(1, 0, 0, 0));
        Assert.Empty(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir]));

        var sourceDir = WriteExtension("contract.source", new Version(2, 0, 0, 0));
        var change = Assert.Single(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir, sourceDir]));

        Assert.Equal(_assemblyName, change.AssemblyName);
        Assert.Equal(new Version(1, 0, 0, 0), change.LoadedVersion);
        Assert.Equal(new Version(2, 0, 0, 0), change.InstalledVersion);
        Assert.Equal(new Version(1, 0, 0, 0), LoadedSharedAssembly().GetName().Version);

        // The loaded copy stays until restart, so a later discovery still reports the difference.
        Assert.Single(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir, sourceDir]));
    }

    [Fact]
    public void Preload_reports_a_rebuilt_copy_with_the_same_version()
    {
        var ownerDir = WriteExtension("contract.owner", new Version(1, 3, 0, 0));
        Assert.Empty(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir]));

        EmitSharedAssembly(Path.Combine(ownerDir, $"{_assemblyName}.dll"), new Version(1, 3, 0, 0));
        var change = Assert.Single(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir]));

        Assert.Equal(new Version(1, 3, 0, 0), change.LoadedVersion);
        Assert.Equal(new Version(1, 3, 0, 0), change.InstalledVersion);
    }

    [Fact]
    public void Preload_does_not_report_an_older_installed_copy()
    {
        var ownerDir = WriteExtension("contract.owner", new Version(2, 0, 0, 0));
        Assert.Empty(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir]));

        var bundlerDir = WriteExtension("contract.bundler", new Version(1, 0, 0, 0));

        Assert.Empty(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [ownerDir, bundlerDir]));
    }

    [Fact]
    public void Discovery_marks_extensions_sharing_a_changed_assembly_as_restart_required()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.Combine(_root, "data"),
            CoveVersion = "1.0.0",
        });
        WriteExtension("contract.owner", new Version(1, 0, 0, 0));
        WriteExtension("unrelated.extension", version: null);

        manager.DiscoverExtensions(_extensionsDir);
        Assert.Null(manager.GetRestartRequiredReason("contract.owner"));

        WriteExtension("contract.source", new Version(2, 0, 0, 0));
        manager.DiscoverExtensions(_extensionsDir);

        Assert.Contains(_assemblyName, manager.GetRestartRequiredReason("contract.owner"));
        Assert.Contains(_assemblyName, manager.GetRestartRequiredReason("contract.source"));
        Assert.Null(manager.GetRestartRequiredReason("unrelated.extension"));

        // Removing the newer copy leaves only the loaded one installed, so no restart is needed any more.
        Directory.Delete(Path.Combine(_extensionsDir, "contract.source"), recursive: true);
        manager.DiscoverExtensions(_extensionsDir);

        Assert.Null(manager.GetRestartRequiredReason("contract.owner"));
    }

    [Fact]
    public void Discovery_ignores_blank_shared_assembly_entries()
    {
        var manager = new ExtensionManager(new ExtensionContext
        {
            Configuration = new ConfigurationBuilder().Build(),
            DataDirectory = Path.Combine(_root, "data"),
            CoveVersion = "1.0.0",
        });
        var directory = Path.Combine(_extensionsDir, "blank.entries");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "extension.json"),
            """{ "id": "blank.entries", "name": "Blank Entries", "version": "1.0.0", "entryDll": "Missing.Entry.dll", "sharedAssemblies": [null, " "] }""");
        var nullListDirectory = Path.Combine(_extensionsDir, "null.list");
        Directory.CreateDirectory(nullListDirectory);
        File.WriteAllText(
            Path.Combine(nullListDirectory, "extension.json"),
            """{ "id": "null.list", "name": "Null List", "version": "1.0.0", "entryDll": "Missing.Entry.dll", "sharedAssemblies": null }""");

        manager.DiscoverExtensions(_extensionsDir);

        Assert.NotNull(manager.GetManifestFile("blank.entries"));
        Assert.NotNull(manager.GetManifestFile("null.list"));
    }

    [Fact]
    public void Preload_does_not_report_an_assembly_the_host_already_supplies()
    {
        // Cove.Tests is loaded by the host (here, the test runner), so its shared copy is never the one used.
        var hostAssemblyName = typeof(SharedAssemblySelectionTests).Assembly.GetName();
        var directory = Path.Combine(_extensionsDir, "host.bundler");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = "host.bundler",
            Name = "Host Bundler",
            Version = "1.0.0",
            SharedAssemblies = [hostAssemblyName.Name!],
        }));
        EmitAssembly(Path.Combine(directory, $"{hostAssemblyName.Name}.dll"), hostAssemblyName.Name!, new Version(99, 0, 0, 0));

        Assert.Empty(ExtensionLoadContext.PreloadSharedAssemblies(_extensionsDir, [directory]));
    }

    private Assembly LoadedSharedAssembly() =>
        Assert.Single(AssemblyLoadContext.Default.Assemblies, assembly => assembly.GetName().Name == _assemblyName);

    /// <summary>
    /// Writes an extension directory whose manifest shares the test contract. A null version writes an
    /// extension that neither shares nor ships it. The entry DLL is absent so discovery loads no runtime code.
    /// </summary>
    private string WriteExtension(string id, Version? version)
    {
        var directory = Path.Combine(_extensionsDir, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(new ExtensionManifestFile
        {
            Id = id,
            Name = id,
            Version = "1.0.0",
            EntryDll = "Missing.Entry.dll",
            SharedAssemblies = version == null ? [] : [_assemblyName],
        }));
        if (version != null)
            EmitSharedAssembly(Path.Combine(directory, $"{_assemblyName}.dll"), version);
        return directory;
    }

    private void EmitSharedAssembly(string path, Version version) => EmitAssembly(path, _assemblyName, version);

    private static void EmitAssembly(string path, string name, Version version)
    {
        var assembly = new PersistedAssemblyBuilder(new AssemblyName(name) { Version = version }, typeof(object).Assembly);
        var module = assembly.DefineDynamicModule(name);
        module.DefineType($"{name}.Marker", TypeAttributes.Public | TypeAttributes.Class).CreateType();
        assembly.Save(path);
    }
}
