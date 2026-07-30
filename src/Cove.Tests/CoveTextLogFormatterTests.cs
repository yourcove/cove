using Serilog.Events;
using Serilog.Parsing;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class CoveTextLogFormatterTests
{
    [Fact]
    public void Format_PreservesCategoryAndCorrelationContext()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.Parse("2026-07-30T12:34:56Z"),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("Processed {Count} files at {Path}"),
            [
                new LogEventProperty("Count", new ScalarValue(3)),
                new LogEventProperty("Path", new ScalarValue("/library/example.mp4")),
                new LogEventProperty("SourceContext", new ScalarValue("Cove.Api.Services.ScanService")),
                new LogEventProperty("JobId", new ScalarValue("job-123")),
                new LogEventProperty("JobType", new ScalarValue("scan")),
                new LogEventProperty("OperationId", new ScalarValue("operation-456")),
            ]);
        using var output = new StringWriter();

        CoveTextLogFormatter.Instance.Format(logEvent, output);

        var rendered = output.ToString();
        Assert.StartsWith(
            "[2026-07-30 12:34:56.000 +00:00 INF] [Cove.Api.Services.ScanService] [job=job-123/scan] [operation=operation-456] ",
            rendered);
        Assert.Contains("[Cove.Api.Services.ScanService]", rendered);
        Assert.Contains("[job=job-123/scan]", rendered);
        Assert.Contains("[operation=operation-456]", rendered);
        Assert.Contains("Processed 3 files at /library/example.mp4", rendered);
    }

    [Fact]
    public void Format_OmitsUnavailableCorrelationContext()
    {
        var logEvent = new LogEvent(
            DateTimeOffset.Parse("2026-07-30T12:34:56Z"),
            LogEventLevel.Information,
            exception: null,
            new MessageTemplateParser().Parse("Application started"),
            []);
        using var output = new StringWriter();

        CoveTextLogFormatter.Instance.Format(logEvent, output);

        var rendered = output.ToString();
        Assert.Contains("[Cove]", rendered);
        Assert.DoesNotContain("[job=", rendered);
        Assert.DoesNotContain("[operation=", rendered);
    }

    [Fact]
    public void Format_PreservesExceptionDetailsAndSanitizesContextNewlines()
    {
        var exception = new InvalidOperationException(
            "outer failure",
            new ArgumentException("inner failure"));
        var logEvent = new LogEvent(
            DateTimeOffset.Parse("2026-07-30T12:34:56Z"),
            LogEventLevel.Error,
            exception,
            new MessageTemplateParser().Parse("Request failed"),
            [
                new LogEventProperty("SourceContext", new ScalarValue("Cove.Api\nInjected")),
                new LogEventProperty("OperationId", new ScalarValue("operation\r\ninjected")),
            ]);
        using var output = new StringWriter();

        CoveTextLogFormatter.Instance.Format(logEvent, output);

        var rendered = output.ToString();
        Assert.StartsWith(
            "[2026-07-30 12:34:56.000 +00:00 ERR] [Cove.Api Injected] [operation=operation  injected] Request failed",
            rendered);
        Assert.Contains("System.InvalidOperationException: outer failure", rendered);
        Assert.Contains("System.ArgumentException: inner failure", rendered);
    }
}
