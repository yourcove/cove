using Serilog.Events;
using Serilog.Parsing;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class SignalRLogSinkTests
{
    [Fact]
    public void Emit_PreservesCategoryAndCorrelationProperties()
    {
        var message = $"trace-sink-test-{Guid.NewGuid():N}";
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Verbose,
            exception: null,
            new MessageTemplateParser().Parse(message),
            [
                new LogEventProperty("SourceContext", new ScalarValue("Cove.Api.Services.ScanService")),
                new LogEventProperty("JobId", new ScalarValue("job-123")),
                new LogEventProperty("JobType", new ScalarValue("scan")),
                new LogEventProperty("OperationId", new ScalarValue("operation-456")),
            ]);

        new SignalRLogSink().Emit(logEvent);

        var entry = Assert.Single(
            SignalRLogSink.GetRecentLogs(),
            candidate => candidate.Message == message);
        Assert.Equal(
            "Cove.Api.Services.ScanService",
            entry.GetType().GetProperty("Category")?.GetValue(entry));
        Assert.Equal("job-123", entry.GetType().GetProperty("JobId")?.GetValue(entry));
        Assert.Equal("scan", entry.GetType().GetProperty("JobType")?.GetValue(entry));
        Assert.Equal(
            "operation-456",
            entry.GetType().GetProperty("OperationId")?.GetValue(entry));
    }
}
