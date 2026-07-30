using System.Globalization;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Cove.Api.Services;

/// <summary>
/// Human-readable console and file output that retains structured category and
/// correlation properties without adding empty context labels to every event.
/// </summary>
public sealed class CoveTextLogFormatter : ITextFormatter
{
    private static readonly MessageTemplateTextFormatter MessageFormatter =
        new("{Message:lj}", CultureInfo.InvariantCulture);

    public static CoveTextLogFormatter Instance { get; } = new();

    private CoveTextLogFormatter()
    {
    }

    public void Format(LogEvent logEvent, TextWriter output)
    {
        output.Write('[');
        output.Write(logEvent.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
        output.Write(' ');
        output.Write(ToLevelCode(logEvent.Level));
        output.Write("] [");
        output.Write(ReadScalarString(logEvent, "SourceContext") ?? "Cove");
        output.Write(']');

        var jobId = ReadScalarString(logEvent, "JobId");
        var jobType = ReadScalarString(logEvent, "JobType");
        if (jobId != null)
        {
            output.Write(" [job=");
            output.Write(jobId);
            if (jobType != null)
            {
                output.Write('/');
                output.Write(jobType);
            }
            output.Write(']');
        }

        var operationId = ReadScalarString(logEvent, "OperationId");
        if (operationId != null)
        {
            output.Write(" [operation=");
            output.Write(operationId);
            output.Write(']');
        }

        output.Write(' ');
        MessageFormatter.Format(logEvent, output);
        output.WriteLine();

        if (logEvent.Exception != null)
            output.WriteLine(logEvent.Exception);
    }

    private static string? ReadScalarString(LogEvent logEvent, string propertyName)
    {
        if (!logEvent.Properties.TryGetValue(propertyName, out var value)
            || value is not ScalarValue { Value: not null } scalar)
        {
            return null;
        }

        return scalar.Value.ToString()?
            .Replace('\r', ' ')
            .Replace('\n', ' ');
    }

    private static string ToLevelCode(LogEventLevel level) => level switch
    {
        LogEventLevel.Verbose => "TRC",
        LogEventLevel.Debug => "DBG",
        LogEventLevel.Information => "INF",
        LogEventLevel.Warning => "WRN",
        LogEventLevel.Error => "ERR",
        LogEventLevel.Fatal => "FTL",
        _ => level.ToString().ToUpperInvariant(),
    };
}
