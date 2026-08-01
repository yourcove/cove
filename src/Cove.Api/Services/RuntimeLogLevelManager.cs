using Serilog.Core;
using Serilog.Events;

namespace Cove.Api.Services;

public sealed class RuntimeLogLevelManager : IDisposable
{
    private static readonly TimeSpan DefaultTraceDuration = TimeSpan.FromMinutes(15);
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _traceDuration;
    private readonly Action<LogEventLevel>? _traceExpired;
    private CancellationTokenSource? _traceSessionCancellation;
    private long _sessionVersion;
    private LogEventLevel _persistedLevel;
    private DateTimeOffset? _traceExpiresAt;

    public RuntimeLogLevelManager(
        LogEventLevel configuredLevel,
        TimeSpan? traceDuration = null,
        TimeProvider? timeProvider = null,
        Action<LogEventLevel>? traceExpired = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _traceDuration = traceDuration ?? DefaultTraceDuration;
        _traceExpired = traceExpired;

        // Trace is deliberately temporary. Treat a legacy persisted Trace value as Info
        // so upgrading cannot leave detailed, potentially sensitive logging enabled forever.
        _persistedLevel = configuredLevel == LogEventLevel.Verbose
            ? LogEventLevel.Information
            : configuredLevel;
        LevelSwitch = new LoggingLevelSwitch(_persistedLevel);
    }

    public LoggingLevelSwitch LevelSwitch { get; }

    public RuntimeLogLevelState GetState()
    {
        lock (_gate)
        {
            return new RuntimeLogLevelState(
                LevelSwitch.MinimumLevel,
                _persistedLevel,
                _traceExpiresAt);
        }
    }

    public RuntimeLogLevelState StartTemporaryTrace()
    {
        CancellationTokenSource cancellation;
        long version;

        lock (_gate)
        {
            CancelTraceSessionLocked();
            cancellation = new CancellationTokenSource();
            _traceSessionCancellation = cancellation;
            version = ++_sessionVersion;
            LevelSwitch.MinimumLevel = LogEventLevel.Verbose;
            _traceExpiresAt = _timeProvider.GetUtcNow().Add(_traceDuration);
        }

        _ = ExpireTraceSessionAsync(version, cancellation.Token);
        return GetState();
    }

    public RuntimeLogLevelState SetPersistentLevel(LogEventLevel level)
    {
        if (level == LogEventLevel.Verbose)
            return StartTemporaryTrace();

        lock (_gate)
        {
            CancelTraceSessionLocked();
            _sessionVersion++;
            _persistedLevel = level;
            LevelSwitch.MinimumLevel = level;
            _traceExpiresAt = null;
            return new RuntimeLogLevelState(level, level, null);
        }
    }

    private async Task ExpireTraceSessionAsync(long version, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_traceDuration, _timeProvider, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return;
        }

        lock (_gate)
        {
            if (version != _sessionVersion)
                return;

            try
            {
                // Emit while the switch is still at Verbose so the closing marker is
                // retained even when the restored level is Warning or higher.
                _traceExpired?.Invoke(_persistedLevel);
            }
            finally
            {
                LevelSwitch.MinimumLevel = _persistedLevel;
                _traceExpiresAt = null;
                _traceSessionCancellation?.Dispose();
                _traceSessionCancellation = null;
            }
        }
    }

    private void CancelTraceSessionLocked()
    {
        _traceSessionCancellation?.Cancel();
        _traceSessionCancellation?.Dispose();
        _traceSessionCancellation = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            CancelTraceSessionLocked();
            _sessionVersion++;
        }
    }
}

public sealed record RuntimeLogLevelState(
    LogEventLevel EffectiveLevel,
    LogEventLevel PersistedLevel,
    DateTimeOffset? TraceExpiresAt);
