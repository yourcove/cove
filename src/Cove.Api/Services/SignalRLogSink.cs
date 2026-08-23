using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using Serilog.Core;
using Serilog.Events;
using Cove.Api.Hubs;

namespace Cove.Api.Services;

public class SignalRLogSink : ILogEventSink
{
    private static IHubContext<LogHub>? _hubContext;
    private static readonly ConcurrentQueue<LogEntry> _recentLogs = new();
    private const int MaxLogs = 2_000;

    public static void SetHubContext(IHubContext<LogHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public static IReadOnlyList<LogEntry> GetRecentLogs()
    {
        return [.. _recentLogs];
    }

    public void Emit(LogEvent logEvent)
    {
        var entry = new LogEntry
        {
            Timestamp = logEvent.Timestamp.UtcDateTime.ToString("o"),
            Level = logEvent.Level.ToString(),
            Message = logEvent.RenderMessage(),
            Exception = logEvent.Exception?.ToString(),
            Category = ReadScalarString(logEvent, "SourceContext"),
            JobId = ReadScalarString(logEvent, "JobId"),
            JobType = ReadScalarString(logEvent, "JobType"),
            OperationId = ReadScalarString(logEvent, "OperationId"),
        };

        _recentLogs.Enqueue(entry);
        while (_recentLogs.Count > MaxLogs)
            _recentLogs.TryDequeue(out _);

        if (_hubContext != null)
        {
            // Fire-and-forget: don't await, avoid blocking the logging pipeline
            _ = _hubContext.Clients.All.SendAsync("LogReceived", entry);
        }
    }

    private static string? ReadScalarString(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var value)
            || value is not ScalarValue { Value: not null } scalar)
        {
            return null;
        }

        return scalar.Value.ToString();
    }
}

public record LogEntry
{
    public string Timestamp { get; init; } = "";
    public string Level { get; init; } = "";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }
    public string? Category { get; init; }
    public string? JobId { get; init; }
    public string? JobType { get; init; }
    public string? OperationId { get; init; }
}
