using System.Diagnostics;
using System.Reflection;
using Cove.Core.Interfaces;
using Cove.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Cove.Tests;

/// <summary>
/// Managed PostgreSQL runs pg_ctl, psql and createdb with paths under the configured data path and with
/// the configured database name. Each must reach the child process as exactly one argument, unchanged,
/// and a cancelled start-up must not leave the child running.
/// </summary>
public sealed class PostgresProcessArgumentTests : IDisposable
{
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cove \"pg\" test {Guid.NewGuid():N}");
    private readonly List<ArgvRecordingExecutable> _recorders = [];

    private string BinDir => Path.Combine(_root, "pgsql", "bin");
    private string DataDir => Path.Combine(_root, "pgdata");

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task PgCtlStart_PassesDataDirectoryLogFileAndPortAsSeparateArguments()
    {
        var pgCtl = Recorder("pg_ctl");

        await Invoke(Manager(), "StartPostgresAsync");

        var argv = Assert.Single(pgCtl.Invocations);
        Assert.Equal(["start", "-D", DataDir, "-l", Path.Combine(_root, "pg.log"), "-w", "-t", "300", "-o", "-p 5433"], argv);
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task PgCtlStopOfStaleInstance_PassesDataDirectoryAsOneArgument()
    {
        var pgCtl = Recorder("pg_ctl");
        Directory.CreateDirectory(DataDir);
        await File.WriteAllTextAsync(Path.Combine(DataDir, "postmaster.pid"), "", TestContext.Current.CancellationToken);

        await Invoke(Manager(), "StopStaleInstanceAsync");

        var argv = Assert.Single(pgCtl.Invocations);
        Assert.Equal(["stop", "-D", DataDir, "-m", "fast"], argv);
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task EnsureDatabase_PassesDatabaseNameAsOneArgumentAndQuotesItInSql()
    {
        const string database = "cove db's";
        var psql = Recorder("psql");
        var createdb = Recorder("createdb");

        await Invoke(Manager(database), "EnsureDatabaseAsync");

        string[] connection = ["-h", "127.0.0.1", "-p", "5433", "-U", "postgres"];
        Assert.Equal(
            [
                [.. connection, "-d", "postgres", "-tAc", "SELECT 1 FROM pg_database WHERE datname='cove db''s'"],
                [.. connection, "-d", database, "-c", "CREATE EXTENSION IF NOT EXISTS vector"],
            ],
            psql.Invocations);
        Assert.Equal([[.. connection, database]], createdb.Invocations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("-cove")]
    [InlineData("dbname=cove host=elsewhere")]
    [InlineData("postgresql://elsewhere/cove")]
    public void DatabaseNamesThatPostgresToolsWouldReinterpretAreRejected(string database)
    {
        var error = Assert.Throws<InvalidOperationException>(() => PostgresManagerService.ValidateDatabaseName(database));
        Assert.Contains("Cove:Postgres:Database", error.Message);
    }

    [Theory]
    [InlineData("cove")]
    [InlineData("cove db's")]
    [InlineData("cove-test_2")]
    public void OrdinaryDatabaseNamesAreAccepted(string database)
        => PostgresManagerService.ValidateDatabaseName(database);

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task CancellingPgCtl_KillsTheChildProcess()
    {
        var pidFile = Path.Combine(_root, "pg_ctl.pid");
        Script("pg_ctl", $"printf '%s' $$ > '{pidFile.Replace("'", "'\\''")}'\nexec sleep 60");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var start = Invoke(Manager(), "StartPostgresAsync", cts.Token);
        var pid = await WaitForPidAsync(pidFile);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.True(await HasExitedAsync(pid), $"pg_ctl stand-in (PID {pid}) was still running after cancellation");
    }

    [Fact(Skip = "Requires Unix shell fixtures", SkipUnless = nameof(IsUnix))]
    public async Task TimedOutProcess_IsKilledAndReported()
    {
        var pidFile = Path.Combine(_root, "slow.pid");
        var slow = Script("slow", $"printf '%s' $$ > '{pidFile.Replace("'", "'\\''")}'\nexec sleep 60");
        var method = typeof(PostgresManagerService).GetMethod("RunAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var run = (Task<int>)method.Invoke(Manager(), [slow, Array.Empty<string>(), _root, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken])!;

        var error = await Assert.ThrowsAsync<TimeoutException>(() => run);
        Assert.Contains("slow", error.Message);
        Assert.True(await HasExitedAsync(int.Parse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken))));
    }

    [Fact(Skip = "Requires Unix file modes", SkipUnless = nameof(IsUnix))]
    public void MakeExecutable_AddsExecuteBitsToEveryFileBelowTheDirectory()
    {
        if (OperatingSystem.IsWindows())
            return;

        var nested = Path.Combine(BinDir, "nested");
        Directory.CreateDirectory(nested);
        var files = new[] { Path.Combine(BinDir, "pg_ctl"), Path.Combine(nested, "helper") };
        foreach (var file in files)
        {
            File.WriteAllText(file, "");
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }

        PostgresManagerService.MakeExecutable(BinDir);

        foreach (var file in files)
            Assert.Equal(
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead
                    | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute,
                File.GetUnixFileMode(file));
    }

    public void Dispose()
    {
        foreach (var recorder in _recorders)
            recorder.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private PostgresManagerService Manager(string database = "cove")
        => new(Options.Create(new PostgresConfig { DataPath = _root, Database = database }), NullLogger<PostgresManagerService>.Instance);

    private static Task Invoke(PostgresManagerService manager, string methodName, CancellationToken? ct = null)
    {
        var method = typeof(PostgresManagerService).GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (Task)method.Invoke(manager, [ct ?? TestContext.Current.CancellationToken])!;
    }

    /// <summary>Installs an argv recorder as <paramref name="name"/> in the managed bin directory.</summary>
    private ArgvRecordingExecutable Recorder(string name)
    {
        var recorder = new ArgvRecordingExecutable(name: name);
        _recorders.Add(recorder);
        Directory.CreateDirectory(BinDir);
        File.CreateSymbolicLink(Path.Combine(BinDir, name), recorder.ExecutablePath);
        return recorder;
    }

    private string Script(string name, string body)
    {
        Directory.CreateDirectory(BinDir);
        var path = Path.Combine(BinDir, name);
        File.WriteAllText(path, $"#!/bin/sh\n{body}\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private static async Task<int> WaitForPidAsync(string pidFile)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(pidFile) && int.TryParse(await File.ReadAllTextAsync(pidFile, TestContext.Current.CancellationToken), out var pid))
                return pid;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        throw new TimeoutException($"The stand-in never wrote {pidFile}");
    }

    private static async Task<bool> HasExitedAsync(int pid)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
        return false;
    }
}
