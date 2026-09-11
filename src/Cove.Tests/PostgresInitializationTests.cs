using System.Reflection;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cove.Tests;

public sealed class PostgresInitializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove init test {Guid.NewGuid():N}");
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private PostgresManagerService Manager => new(Options.Create(new PostgresConfig { DataPath = _root }),
        NullLogger<PostgresManagerService>.Instance);

    private string Bootstrap(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "postgres.bki"), "bootstrap fixture");
        return path;
    }

    private async Task<string> Resolve()
    {
        var method = typeof(PostgresManagerService).GetMethod("ResolveBootstrapShareDirAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<string>)method.Invoke(Manager, [TestContext.Current.CancellationToken])!;
    }

    [Theory]
    [InlineData("pgsql/share")]
    [InlineData("pgsql/share/postgresql")]
    public async Task ResolvesPortableLayoutsWithSpaces(string relative)
    {
        var expected = Bootstrap(relative);
        Assert.Equal(expected, await Resolve());
    }

    [Fact]
    public async Task ExtensionOnlyDirectoryDoesNotMaskNestedBootstrap()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pgsql/share/extension"));
        File.WriteAllText(Path.Combine(_root, "pgsql/share/extension/vector.control"), "extension fixture");
        var expected = Bootstrap("pgsql/share/postgresql");
        Assert.Equal(expected, await Resolve());
    }

    [Fact]
    public async Task MissingBootstrapReportsCandidatesWithoutCreatingDatabase()
    {
        Directory.CreateDirectory(Path.Combine(_root, "pgsql/share/extension"));
        var method = typeof(PostgresManagerService).GetMethod("InitDbAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)method.Invoke(Manager, [TestContext.Current.CancellationToken])!);
        Assert.Contains("postgres.bki", error.Message);
        Assert.Contains(Path.Combine(_root, "pgsql/share"), error.Message);
        Assert.Contains(Path.Combine(_root, "pgsql/share/postgresql"), error.Message);
        Assert.False(Directory.Exists(Path.Combine(_root, "pgdata")));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task PrefersSelectedInstallationsPgConfigIncludingSystemPath()
    {
        var systemShare = Bootstrap("system installation/share");
        Bootstrap("pgsql/share");
        Script("pg_config", $"printf '%s\\n' '{systemShare}'");
        Assert.Equal(systemShare, await Resolve());
    }

    [Theory(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    [InlineData("exit 1")]
    [InlineData("printf '/missing/bootstrap\\n'")]
    [InlineData("printf 'relative/path\\n'")]
    public async Task InvalidPgConfigFallsBackToPortableBootstrap(string script)
    {
        Script("pg_config", script);
        var expected = Bootstrap("pgsql/share/postgresql");
        Assert.Equal(expected, await Resolve());
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task InitFailureCapturesBothStreamsAndUsesSeparateArguments()
    {
        var share = Bootstrap("pgsql/share/postgresql");
        Script("initdb", """
            printf 'arg:%s\n' "$@"
            i=0
            while [ "$i" -lt 4096 ]; do
              printf 'stdout diagnostics padding padding padding padding\n'
              printf 'stderr diagnostics padding padding padding padding\n' >&2
              i=$((i + 1))
            done
            printf 'final initdb error\n' >&2
            exit 17
            """);
        var method = typeof(PostgresManagerService).GetMethod("InitDbAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await (Task)method.Invoke(Manager, [timeout.Token])!);
        var logPath = Path.Combine(_root, "pg-initdb.log");
        Assert.Contains("17", error.Message);
        Assert.Contains(logPath, error.Message);
        var log = await File.ReadAllTextAsync(logPath, TestContext.Current.CancellationToken);
        Assert.Contains($"arg:{share}\n", log);
        Assert.Contains($"arg:{Path.Combine(_root, "pgdata")}\n", log);
        Assert.Contains("stdout diagnostics", log);
        Assert.Contains("stderr diagnostics", log);
        Assert.Contains("final initdb error", log);
        Assert.False(File.Exists(Path.Combine(_root, "pgdata/pg_hba.conf")));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task SuccessfulInitializationWritesConfigurationAndLog()
    {
        Bootstrap("pgsql/share/postgresql");
        Script("initdb", "printf 'initialization complete\\n'");
        var method = typeof(PostgresManagerService).GetMethod("InitDbAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(Manager, [TestContext.Current.CancellationToken])!;
        Assert.Contains("initialization complete", await File.ReadAllTextAsync(Path.Combine(_root, "pg-initdb.log"), TestContext.Current.CancellationToken));
        Assert.True(File.Exists(Path.Combine(_root, "pgdata/pg_hba.conf")));
        Assert.Contains("listen_addresses", await File.ReadAllTextAsync(Path.Combine(_root, "pgdata/postgresql.conf"), TestContext.Current.CancellationToken));
    }

    private async Task<string?> SystemCandidate(string path)
    {
        var method = typeof(PostgresManagerService).GetMethod("ResolveSystemPgCtlAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return await (Task<string?>)method.Invoke(Manager, [path, TestContext.Current.CancellationToken])!;
    }

    private void ServerTools(bool includeServer = true, string version = "18.4")
    {
        foreach (var name in new[] { "pg_ctl", "initdb", "pg_isready", "psql", "createdb" })
            Script(name, $"printf '{name} (PostgreSQL) {version}\\n'");
        if (includeServer) Script("postgres", $"printf 'postgres (PostgreSQL) {version}\\n'");
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task RejectsClientOnlyInstallation()
    {
        ServerTools(includeServer: false);
        Assert.Null(await SystemCandidate(Path.Combine(_root, "pgsql/bin/pg_ctl")));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task ResolvesCompleteServerFromExecutableSymlink()
    {
        ServerTools();
        var exposedBin = Path.Combine(_root, "homebrew bin");
        Directory.CreateDirectory(exposedBin);
        var physical = Path.Combine(_root, "pgsql/bin/pg_ctl");
        var exposed = Path.Combine(exposedBin, "pg_ctl");
        File.CreateSymbolicLink(exposed, physical);
        Assert.Equal(physical, await SystemCandidate(exposed));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task RejectsClientSymlinkEvenWithUnrelatedServerBesideIt()
    {
        ServerTools(includeServer: false);
        var exposedBin = Path.Combine(_root, "homebrew bin");
        Directory.CreateDirectory(exposedBin);
        var exposed = Path.Combine(exposedBin, "pg_ctl");
        File.CreateSymbolicLink(exposed, Path.Combine(_root, "pgsql/bin/pg_ctl"));
        File.WriteAllText(Path.Combine(exposedBin, "postgres"), "unrelated server");
        Assert.Null(await SystemCandidate(exposed));
    }

    [Theory(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    [InlineData("17.6")]
    [InlineData("19.0")]
    public async Task RejectsWrongServerMajor(string version)
    {
        ServerTools(version: version);
        Assert.Null(await SystemCandidate(Path.Combine(_root, "pgsql/bin/pg_ctl")));
    }

    [Theory(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepairsClientOnlyCachedLinkOnlyBeforeDatabaseInitialization(bool initialized)
    {
        ServerTools(includeServer: false);
        var bin = Path.Combine(_root, "pgsql/bin");
        var clientBin = Path.Combine(_root, "client bin");
        Directory.Move(bin, clientBin);
        Directory.CreateSymbolicLink(bin, clientBin);
        var data = Path.Combine(_root, "pgdata");
        Directory.CreateDirectory(data);
        var marker = Path.Combine(data, "keep-me");
        File.WriteAllText(marker, "preserve database files");
        if (initialized) File.WriteAllText(Path.Combine(data, "PG_VERSION"), "18");
        var method = typeof(PostgresManagerService).GetMethod("ValidateLinkedSystemPostgresAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        async Task Validate() => await (Task)method.Invoke(Manager, [TestContext.Current.CancellationToken])!;
        if (initialized)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(Validate);
            Assert.NotNull(new DirectoryInfo(bin).LinkTarget);
        }
        else
        {
            await Validate();
            Assert.Null(new DirectoryInfo(bin).LinkTarget);
        }
        Assert.True(File.Exists(Path.Combine(clientBin, "pg_ctl")));
        Assert.Equal("preserve database files", File.ReadAllText(marker));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task RejectsServerMajorMismatchEvenWhenPgCtlMatches()
    {
        ServerTools();
        Script("postgres", "printf 'postgres (PostgreSQL) 17.6\\n'");
        Assert.Null(await SystemCandidate(Path.Combine(_root, "pgsql/bin/pg_ctl")));
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task ValidCachedServerLinkIsPreserved()
    {
        ServerTools();
        var bin = Path.Combine(_root, "pgsql/bin");
        var serverBin = Path.Combine(_root, "server bin");
        Directory.Move(bin, serverBin);
        Directory.CreateSymbolicLink(bin, serverBin);
        var method = typeof(PostgresManagerService).GetMethod("ValidateLinkedSystemPostgresAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)method.Invoke(Manager, [TestContext.Current.CancellationToken])!;
        Assert.Equal(serverBin, new DirectoryInfo(bin).LinkTarget);
        Assert.True(File.Exists(Path.Combine(serverBin, "postgres")));
    }

    private void Script(string name, string body)
    {
        var bin = Path.Combine(_root, "pgsql/bin");
        Directory.CreateDirectory(bin);
        var path = Path.Combine(bin, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
