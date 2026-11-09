using Cove.Api.Updates;

namespace Cove.Tests;

/// <summary>
/// The swap, journal, rollback, and revert state machine, run against a temporary app folder whose
/// "executables" are shell scripts. The relauncher starts and stops real processes; only the health
/// probe is replaced (it reads the version file each fake Cove writes instead of calling HTTP).
/// </summary>
public sealed class UpdateSwapRelaunchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cove-update-swap-" + Guid.NewGuid().ToString("N"));
    private readonly List<int> _started = [];

    public UpdateSwapRelaunchTests()
    {
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(Path.Combine(_root, "updates"));
    }

    private string AppDirectory => Path.Combine(_root, "app");
    private string JournalPath => Path.Combine(_root, "updates", "pending.json");
    private string VersionFile => Path.Combine(_root, "running-version");
    private string OutputLog => Path.Combine(_root, "output.log");
    private string CovePath => Path.Combine(AppDirectory, "Cove");
    private string ManagerPath => Path.Combine(AppDirectory, "Cove.InstanceManager");

    public void Dispose()
    {
        foreach (var pid in _started)
            UpdateProcessLauncher.Stop(pid, TimeSpan.FromSeconds(1));
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Successful_update_keeps_the_previous_binary_for_revert()
    {
        SkipUnlessUnix();
        InstallCurrent("1.0.0");
        var swapper = Swapper();
        swapper.Install(Stage(GoodCove("2.0.0"), Manager("2.0.0")));
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0");

        var exitCode = await new UpdateRelauncher(Host()).RunAsync(JournalPath, waitPid: ExitedPid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(UpdateJournalStates.Succeeded, ReadJournal().State);
        Assert.Equal("2.0.0", File.ReadAllText(VersionFile).Trim());
        Assert.Contains("2.0.0", File.ReadAllText(CovePath));
        Assert.Contains("1.0.0", File.ReadAllText(CovePath + ".old"));
        Assert.Contains("manager 2.0.0", File.ReadAllText(ManagerPath));
        Assert.Contains("manager 1.0.0", File.ReadAllText(ManagerPath + ".old"));
        Assert.False(Directory.Exists(swapper.StagingDirectory));
    }

    [Fact]
    public async Task New_version_that_exits_is_rolled_back_and_the_old_version_restarted()
    {
        SkipUnlessUnix();
        InstallCurrent("1.0.0");
        Swapper().Install(Stage(CrashingCove("2.0.0"), Manager("2.0.0")));
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0");

        var exitCode = await new UpdateRelauncher(Host()).RunAsync(JournalPath, waitPid: ExitedPid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        var journal = ReadJournal();
        Assert.Equal(UpdateJournalStates.RolledBack, journal.State);
        Assert.Contains("exited during startup", journal.Error);
        Assert.Contains("database exploded", journal.LogTail);
        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
        Assert.Contains("2.0.0", File.ReadAllText(CovePath + ".failed"));
        Assert.Contains("manager 1.0.0", File.ReadAllText(ManagerPath));
        Assert.Equal("1.0.0", File.ReadAllText(VersionFile).Trim());
        var failed = Assert.Single(UpdateJson.Read<UpdateCheckState>(new UpdatePaths(_root).StatePath)!.FailedVersions);
        Assert.Equal(("2.0.0", UpdateFailureOutcomes.RolledBack), (failed.Version, failed.Outcome));
    }

    [Fact]
    public void Failed_versions_are_remembered_alongside_the_last_check()
    {
        var paths = new UpdatePaths(_root);
        UpdateJson.Write(paths.StatePath, new UpdateCheckState { CheckedVersion = "1.0.0", LastError = "offline" });

        UpdateFailureMemory.Remember(paths.StatePath, "2.0.0", UpdateFailureOutcomes.RolledBack, "exited during startup");
        UpdateFailureMemory.Remember(paths.StatePath, "2.1.0", UpdateFailureOutcomes.Reverted, null);
        UpdateFailureMemory.Remember(paths.StatePath, "2.0.0", UpdateFailureOutcomes.Failed, "again");

        var state = UpdateJson.Read<UpdateCheckState>(paths.StatePath)!;
        Assert.Equal("offline", state.LastError);
        Assert.Equal(["2.1.0", "2.0.0"], state.FailedVersions.Select(entry => entry.Version));
        Assert.Equal(UpdateFailureOutcomes.Failed, UpdateFailureMemory.Find(state, "2.0.0")!.Outcome);
        Assert.Null(UpdateFailureMemory.Find(state, "3.0.0"));
    }

    [Fact]
    public void Log_tail_ignores_what_the_previous_version_wrote_before_the_restart()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var daily = Path.Combine(logs, "cove-20261022.log");
        File.WriteAllText(daily, "1.0.0 shutting down\nChecksum mismatch from an earlier attempt\n");
        var console = Path.Combine(logs, "update-console-2.0.0-abcdef12.log");
        var tail = new UpdateLogTail();

        tail.Mark(logs, console);
        Assert.Null(tail.Read(console));

        File.AppendAllText(daily, "\u001b[31mFATAL\u001b[0m simulated crash\n");
        var read = tail.Read(console);
        Assert.Equal("FATAL simulated crash", read);
    }

    [Fact]
    public void Log_tail_prefers_the_console_output_of_the_attempt()
    {
        var logs = Path.Combine(_root, "logs");
        Directory.CreateDirectory(logs);
        var console = Path.Combine(logs, "update-console-2.0.0-abcdef12.log");
        File.WriteAllText(console, "left over from an earlier attempt");
        var tail = new UpdateLogTail();

        tail.Mark(logs, console);
        File.WriteAllText(Path.Combine(logs, "cove-20261022-101500.log"), "Instance Manager start log");
        File.WriteAllText(console, "Unhandled exception: simulated crash");

        Assert.Equal("Unhandled exception: simulated crash", tail.Read(console));
    }

    [Fact]
    public async Task New_version_that_never_becomes_healthy_is_stopped_and_rolled_back()
    {
        SkipUnlessUnix();
        InstallCurrent("1.0.0");
        Swapper().Install(Stage(HangingCove(), Manager("2.0.0")));
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0");

        var exitCode = await new UpdateRelauncher(Host(healthTimeout: TimeSpan.FromSeconds(2))).RunAsync(JournalPath, waitPid: ExitedPid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("did not report itself running", ReadJournal().Error);
        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
        Assert.All(_started.Take(1), pid => Assert.False(UpdateProcessLauncher.IsAlive(pid)));
    }

    [Fact]
    public async Task Revert_restores_the_previous_binary_and_discards_the_reverted_one()
    {
        SkipUnlessUnix();
        InstallCurrent("1.0.0");
        var swapper = Swapper();
        swapper.Install(Stage(GoodCove("2.0.0"), Manager("2.0.0")));

        swapper.Revert();
        WriteJournal(UpdateJournalKinds.Revert, "2.0.0", "1.0.0");
        var exitCode = await new UpdateRelauncher(Host()).RunAsync(JournalPath, waitPid: ExitedPid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
        Assert.False(File.Exists(CovePath + ".reverted"));
        Assert.False(File.Exists(CovePath + ".old"));
        Assert.Equal("1.0.0", File.ReadAllText(VersionFile).Trim());
    }

    [Fact]
    public async Task Failed_revert_goes_back_to_the_updated_version()
    {
        SkipUnlessUnix();
        File.WriteAllText(CovePath, CrashingCove("1.0.0"));
        MakeExecutable(CovePath);
        File.WriteAllText(ManagerPath, Manager("1.0.0"));
        var swapper = Swapper();
        swapper.Install(Stage(GoodCove("2.0.0"), Manager("2.0.0")));

        swapper.Revert();
        WriteJournal(UpdateJournalKinds.Revert, "2.0.0", "1.0.0");
        var exitCode = await new UpdateRelauncher(Host()).RunAsync(JournalPath, waitPid: ExitedPid(), ct: TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
        Assert.Contains("2.0.0", File.ReadAllText(CovePath));
        Assert.Contains("1.0.0", File.ReadAllText(CovePath + ".old"));
        Assert.Equal("2.0.0", File.ReadAllText(VersionFile).Trim());
    }

    [Fact]
    public async Task Relauncher_waits_for_the_old_process_to_exit()
    {
        SkipUnlessUnix();
        InstallCurrent("1.0.0");
        Swapper().Install(Stage(GoodCove("2.0.0"), Manager("2.0.0")));
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0");
        var oldProcess = UpdateProcessLauncher.StartDetached("/bin/sleep", ["1"], AppDirectory, OutputLog, visibleWindow: false);
        var started = DateTime.UtcNow;

        var exitCode = await new UpdateRelauncher(Host()).RunAsync(JournalPath, oldProcess, TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.True(DateTime.UtcNow - started >= TimeSpan.FromMilliseconds(800));
    }

    [Fact]
    public void Install_failure_part_way_restores_the_live_binaries()
    {
        InstallCurrent("1.0.0");
        var staging = Path.Combine(AppDirectory, UpdateFileSwapper.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        var newCove = Path.Combine(staging, "Cove.new");
        File.WriteAllText(newCove, "new");

        Assert.ThrowsAny<IOException>(() => Swapper().Install(new ExtractedUpdate(newCove, Path.Combine(staging, "missing.new"))));

        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
        Assert.Contains("manager 1.0.0", File.ReadAllText(ManagerPath));
        Assert.False(File.Exists(CovePath + ".old"));
        Assert.Equal("new", File.ReadAllText(newCove));
    }

    [Fact]
    public void Next_install_replaces_an_older_previous_binary()
    {
        InstallCurrent("1.0.0");
        var swapper = Swapper();
        swapper.Install(Stage("v2", "m2"));
        swapper.PrepareForInstall();
        swapper.Install(Stage("v3", "m3"));

        Assert.Equal("v3", File.ReadAllText(CovePath));
        Assert.Equal("v2", File.ReadAllText(CovePath + ".old"));
    }

    [Fact]
    public void Rollback_without_a_previous_binary_changes_nothing()
    {
        InstallCurrent("1.0.0");

        Assert.Throws<FileNotFoundException>(() => Swapper().RollBack());
        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
    }

    [Fact]
    public void Unsupervised_boots_are_counted_and_repeated_failure_rolls_back()
    {
        InstallCurrent("1.0.0");
        Swapper().Install(Stage("2.0.0 binary", "m2"));
        var paths = new UpdatePaths(_root);
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0", supervised: false);

        for (var boot = 1; boot <= UpdateCommandLine.MaxUnsupervisedBootAttempts; boot++)
        {
            Assert.Null(UpdateCommandLine.CheckUnsupervisedBoot(paths, "2.0.0"));
            Assert.Equal(boot, ReadJournal().BootAttempts);
        }

        Assert.Equal(UpdateCommandLine.RestartExitCode, UpdateCommandLine.CheckUnsupervisedBoot(paths, "2.0.0"));
        Assert.Equal(UpdateJournalStates.RolledBack, ReadJournal().State);
        Assert.Contains("1.0.0", File.ReadAllText(CovePath));
        Assert.Equal("2.0.0 binary", File.ReadAllText(CovePath + ".failed"));
    }

    [Fact]
    public void Boot_check_ignores_supervised_and_other_versions()
    {
        var paths = new UpdatePaths(_root);
        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0", supervised: true);
        Assert.Null(UpdateCommandLine.CheckUnsupervisedBoot(paths, "2.0.0"));
        Assert.Equal(0, ReadJournal().BootAttempts);

        WriteJournal(UpdateJournalKinds.Update, "1.0.0", "2.0.0", supervised: false);
        Assert.Null(UpdateCommandLine.CheckUnsupervisedBoot(paths, "1.0.0"));
        Assert.Equal(0, ReadJournal().BootAttempts);
    }

    [Fact]
    public void Cleanup_keeps_previous_binary_only_when_asked()
    {
        InstallCurrent("1.0.0");
        var swapper = Swapper();
        swapper.Install(Stage("v2", "m2"));
        File.WriteAllText(CovePath + ".failed", "x");
        File.WriteAllText(CovePath + ".20260101000000.stale", "x");

        swapper.CleanUp(keepPrevious: true);
        Assert.True(File.Exists(CovePath + ".old"));
        Assert.False(File.Exists(CovePath + ".failed"));
        Assert.False(File.Exists(CovePath + ".20260101000000.stale"));

        swapper.CleanUp(keepPrevious: false);
        Assert.False(File.Exists(CovePath + ".old"));
        Assert.Equal("v2", File.ReadAllText(CovePath));
    }

    private static void SkipUnlessUnix()
        => Assert.SkipWhen(OperatingSystem.IsWindows(), "The fake executables are shell scripts.");

    private UpdateFileSwapper Swapper() => new(AppDirectory, "Cove", "Cove.InstanceManager", attempts: 2, initialDelay: TimeSpan.FromMilliseconds(10));

    private void InstallCurrent(string version)
    {
        File.WriteAllText(CovePath, GoodCove(version));
        MakeExecutable(CovePath);
        File.WriteAllText(ManagerPath, Manager(version));
    }

    private ExtractedUpdate Stage(string cove, string manager)
    {
        var staging = Path.Combine(AppDirectory, UpdateFileSwapper.StagingDirectoryName);
        Directory.CreateDirectory(staging);
        var update = new ExtractedUpdate(Path.Combine(staging, "Cove.new"), Path.Combine(staging, "Cove.InstanceManager.new"));
        File.WriteAllText(update.CovePath, cove);
        File.WriteAllText(update.ManagerPath, manager);
        MakeExecutable(update.CovePath);
        return update;
    }

    private string GoodCove(string version) => $"#!/bin/sh\necho 'Cove {version} starting'\nprintf '%s' '{version}' > '{VersionFile}'\nexec sleep 30\n";

    private static string CrashingCove(string version) => $"#!/bin/sh\necho 'Cove {version}: database exploded' >&2\nexit 1\n";

    private static string HangingCove() => "#!/bin/sh\nexec sleep 30\n";

    private static string Manager(string version) => $"manager {version}";

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void WriteJournal(string kind, string from, string to, bool supervised = true)
        => UpdateJson.Write(JournalPath, new UpdateJournal
        {
            Kind = kind,
            State = UpdateJournalStates.Restarting,
            FromVersion = from,
            ToVersion = to,
            AppDirectory = AppDirectory,
            CoveFileName = "Cove",
            ManagerFileName = "Cove.InstanceManager",
            LaunchMode = LaunchModes.Direct,
            Supervised = supervised,
        });

    private UpdateJournal ReadJournal() => UpdateJson.Read<UpdateJournal>(JournalPath)!;

    private int ExitedPid()
    {
        var pid = UpdateProcessLauncher.StartDetached("/bin/true", [], AppDirectory, OutputLog, visibleWindow: false);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (UpdateProcessLauncher.IsAlive(pid) && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        return pid;
    }

    private UpdateRelaunchHost Host(TimeSpan? healthTimeout = null) => new()
    {
        IsAlive = UpdateProcessLauncher.IsAlive,
        Stop = pid => UpdateProcessLauncher.Stop(pid, TimeSpan.FromSeconds(2)),
        Start = journal =>
        {
            File.Delete(VersionFile);
            var pid = UpdateProcessLauncher.StartDetached(Path.Combine(journal.AppDirectory, journal.CoveFileName), [], journal.AppDirectory, OutputLog, visibleWindow: false);
            _started.Add(pid);
            return pid;
        },
        GetRunningVersion = (_, _) => Task.FromResult(File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null),
        WaitForPortFree = (_, _) => Task.CompletedTask,
        ReadLogTail = _ => File.Exists(OutputLog) ? File.ReadAllText(OutputLog) : null,
        Log = _ => { },
        HealthTimeout = healthTimeout ?? TimeSpan.FromSeconds(20),
        PollInterval = TimeSpan.FromMilliseconds(100),
        ExitWaitTimeout = TimeSpan.FromSeconds(10),
    };
}
