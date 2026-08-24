using Serilog.Events;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class RuntimeLogLevelManagerTests
{
    [Fact]
    public async Task TemporaryTrace_RevertsToPersistedLevel()
    {
        var manager = new RuntimeLogLevelManager(
            LogEventLevel.Debug,
            traceDuration: TimeSpan.FromMilliseconds(25));

        var trace = manager.StartTemporaryTrace();

        Assert.Equal(LogEventLevel.Verbose, trace.EffectiveLevel);
        Assert.Equal(LogEventLevel.Debug, trace.PersistedLevel);
        Assert.NotNull(trace.TraceExpiresAt);

        await Task.Delay(200, TestContext.Current.CancellationToken);

        var reverted = manager.GetState();
        Assert.Equal(LogEventLevel.Debug, reverted.EffectiveLevel);
        Assert.Null(reverted.TraceExpiresAt);
    }

    [Fact]
    public async Task PersistentLevelChange_CancelsTemporaryTraceReversion()
    {
        var manager = new RuntimeLogLevelManager(
            LogEventLevel.Debug,
            traceDuration: TimeSpan.FromMilliseconds(25));
        manager.StartTemporaryTrace();

        var changed = manager.SetPersistentLevel(LogEventLevel.Warning);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(LogEventLevel.Warning, changed.EffectiveLevel);
        Assert.Equal(LogEventLevel.Warning, manager.GetState().EffectiveLevel);
        Assert.Null(manager.GetState().TraceExpiresAt);
    }

    [Fact]
    public void LegacyPersistedTrace_StartsAtInformation()
    {
        var manager = new RuntimeLogLevelManager(LogEventLevel.Verbose);

        var state = manager.GetState();

        Assert.Equal(LogEventLevel.Information, state.EffectiveLevel);
        Assert.Equal(LogEventLevel.Information, state.PersistedLevel);
        Assert.Null(state.TraceExpiresAt);
    }

    [Fact]
    public async Task TemporaryTrace_NotifiesWhenPersistedLevelIsRestored()
    {
        var expired = new TaskCompletionSource<LogEventLevel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RuntimeLogLevelManager(
            LogEventLevel.Warning,
            traceDuration: TimeSpan.FromMilliseconds(25),
            traceExpired: level => expired.SetResult(level));

        manager.StartTemporaryTrace();

        var restoredLevel = await expired.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(LogEventLevel.Warning, restoredLevel);
        Assert.Equal(LogEventLevel.Warning, manager.GetState().EffectiveLevel);
    }
}
