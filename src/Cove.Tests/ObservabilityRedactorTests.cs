using Cove.Api.Services;
using Serilog.Events;
using Serilog.Parsing;

namespace Cove.Tests;

public sealed class ObservabilityRedactorTests
{
    [Fact]
    public void RedactJson_RemovesSensitiveFieldsAndCredentialsFromNestedValues()
    {
        const string apiToken = "cove_pat_0123456789abcdef0123456789abcdef_private-token";
        const string shareToken = "cove_share_fedcba9876543210fedcba9876543210_share-secret";
        var input = $$"""{"password":"private password","nested":{"access_token":"{{apiToken}}"},"message":"share={{shareToken}} Bearer header.payload.signature","safe":"visible"}""";

        var result = ObservabilityRedactor.RedactJson(input);

        Assert.DoesNotContain("private password", result);
        Assert.DoesNotContain(apiToken, result);
        Assert.DoesNotContain(shareToken, result);
        Assert.DoesNotContain("header.payload.signature", result);
        Assert.Contains("visible", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void RedactText_RemovesLabeledAndOpaqueCredentials()
    {
        const string token = "cove_pat_0123456789abcdef0123456789abcdef_private-token";
        var input = $"https://example.invalid/path?access_token=opaque&share_password=private-value password=\"multi word password\" token:secret-value Authorization: Bearer abc+/def== {token}";

        var result = ObservabilityRedactor.RedactText(input);

        Assert.DoesNotContain("multi word password", result);
        Assert.DoesNotContain("opaque", result);
        Assert.DoesNotContain("private-value", result);
        Assert.DoesNotContain("secret-value", result);
        Assert.DoesNotContain("abc+/def==", result);
        Assert.DoesNotContain(token, result);
    }

    [Fact]
    public void RedactJson_RedactsRootStringWithoutThrowing()
    {
        var result = ObservabilityRedactor.RedactJson("\"password=hunter2\"");

        Assert.DoesNotContain("hunter2", result);
        Assert.Contains("[REDACTED]", result);
    }

    [Fact]
    public void SignalRLogSink_RedactsBeforeBufferingOrBroadcasting()
    {
        const string marker = "observability-redaction-marker";
        const string token = "cove_share_0123456789abcdef0123456789abcdef_private-token";
        var template = new MessageTemplateParser().Parse($"{marker} password=hunter2 {token}");
        var logEvent = new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Warning, null, template, []);

        new SignalRLogSink().Emit(logEvent);

        var entry = Assert.Single(
            SignalRLogSink.GetRecentLogs(),
            item => item.Message.Contains(marker, StringComparison.Ordinal));
        Assert.Contains(marker, entry.Message);
        Assert.Contains("[REDACTED]", entry.Message);
        Assert.DoesNotContain("hunter2", entry.Message);
        Assert.DoesNotContain(token, entry.Message);
    }
}
