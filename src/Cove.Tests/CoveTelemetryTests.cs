using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Cove.Api.Services;

namespace Cove.Tests;

public sealed class CoveTelemetryTests
{
    private const string ShippedDefaults = """
        {
          "Cove": {
            "Telemetry": {
              "Logs": { "Enabled": false },
              "Traces": { "Enabled": false },
              "Metrics": { "Enabled": false },
              "Endpoint": null,
              "ServiceName": null
            }
          }
        }
        """;

    // Mirrors the host: shipped appsettings defaults first, then environment-style overrides.
    private static ConfigurationManager Configuration(params (string Key, string? Value)[] environment)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ShippedDefaults)));
        configuration.AddInMemoryCollection(environment.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        return configuration;
    }

    [Fact]
    public void Configure_WithShippedDefaults_RegistersNothingAndLeavesConfigurationAlone()
    {
        var configuration = Configuration();
        var services = new ServiceCollection();
        var sourcesBefore = configuration.Sources.Count;

        var settings = CoveTelemetry.Configure(configuration, services);

        Assert.False(settings.AnyEnabled);
        Assert.Empty(services);
        Assert.Equal(sourcesBefore, configuration.Sources.Count);
        Assert.Null(configuration["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public void Configure_EnablesSignalsIndependently()
    {
        var configuration = Configuration(("Cove:Telemetry:Traces:Enabled", "true"));
        var services = new ServiceCollection();

        var settings = CoveTelemetry.Configure(configuration, services);

        Assert.False(settings.Logs);
        Assert.True(settings.Traces);
        Assert.False(settings.Metrics);
        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<TracerProvider>());
        Assert.Null(provider.GetService<MeterProvider>());
    }

    [Fact]
    public void Configure_StandardKillSwitchDisablesEverySignal()
    {
        var configuration = Configuration(
            ("OTEL_SDK_DISABLED", "true"),
            ("Cove:Telemetry:Logs:Enabled", "true"),
            ("Cove:Telemetry:Traces:Enabled", "true"));
        var services = new ServiceCollection();

        Assert.False(CoveTelemetry.Configure(configuration, services).AnyEnabled);
        Assert.Empty(services);
    }

    [Fact]
    public void Configure_CoveEndpointWinsOverStandardVariable()
    {
        var configuration = Configuration(
            ("Cove:Telemetry:Logs:Enabled", "true"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"),
            ("Cove:Telemetry:Endpoint", "http://cove-collector:4318"));

        CoveTelemetry.Configure(configuration, new ServiceCollection());

        Assert.Equal("http://cove-collector:4318", configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Fact]
    public void Configure_KeepsStandardEndpointWhenCoveEndpointIsUnset()
    {
        var configuration = Configuration(
            ("Cove:Telemetry:Logs:Enabled", "true"),
            ("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"));

        CoveTelemetry.Configure(configuration, new ServiceCollection());

        Assert.Equal("http://collector:4317", configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        Assert.Equal(CoveTelemetry.DefaultServiceName, configuration["OTEL_SERVICE_NAME"]);
    }

    [Fact]
    public void Configure_HttpProtobufWithoutEndpointUsesTheHttpPortForEverySignal()
    {
        var configuration = Configuration(
            ("Cove:Telemetry:Logs:Enabled", "true"),
            ("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf"));

        CoveTelemetry.Configure(configuration, new ServiceCollection());

        Assert.Equal("http://localhost:4318", configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
    }

    [Theory]
    [InlineData("from-cove", "from-otel", "service.name=from-attributes", "from-cove")]
    [InlineData(" ", "from-otel", "service.name=from-attributes", "from-otel")]
    [InlineData(null, null, "deployment.environment=home, service.name=from%20attributes", "from attributes")]
    [InlineData(null, null, "deployment.environment=home", CoveTelemetry.DefaultServiceName)]
    public void Resolve_ServiceNameFollowsCoveThenStandardPrecedence(
        string? coveName, string? otelName, string resourceAttributes, string expected)
    {
        var settings = CoveTelemetry.Resolve(Configuration(
            ("Cove:Telemetry:ServiceName", coveName),
            ("OTEL_SERVICE_NAME", otelName),
            ("OTEL_RESOURCE_ATTRIBUTES", resourceAttributes)));

        Assert.Equal(expected, settings.ServiceName);
    }

    [Fact]
    public void Resolve_IgnoresAnEnabledValueThatIsNotABoolean()
    {
        var settings = CoveTelemetry.Resolve(Configuration(
            ("Cove:Telemetry:Logs:Enabled", "yes"),
            ("Cove:Telemetry:Traces:Enabled", " TRUE ")));

        Assert.False(settings.Logs);
        Assert.True(settings.Traces);
    }

    [Fact]
    public async Task LogExportTimeoutHandler_CancelsAResponseBodyThatStalls()
    {
        using var handler = new CoveTelemetry.LogExportTimeoutHandler(TimeSpan.FromMilliseconds(50), new StallingBodyHandler());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("http://collector.invalid/v1/logs", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null, 10000)]
    [InlineData("2500", 2500)]
    [InlineData("0", 10000)]
    [InlineData("soon", 10000)]
    public void ExportTimeout_FollowsStandardVariableAndFallsBackToTenSeconds(string? configured, int expectedMilliseconds)
    {
        var timeout = CoveTelemetry.ExportTimeout(Configuration(("OTEL_EXPORTER_OTLP_TIMEOUT", configured)));

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), timeout);
    }

    [Fact]
    public async Task LogExportTimeoutHandler_CancelsARequestTheCollectorNeverAnswers()
    {
        using var handler = new CoveTelemetry.LogExportTimeoutHandler(TimeSpan.FromMilliseconds(50), new NeverAnsweringHandler());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("http://collector.invalid/v1/logs", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LogExportTimeoutHandler_CancelAfterEndsInFlightAndLaterRequests()
    {
        using var handler = new CoveTelemetry.LogExportTimeoutHandler(Timeout.InfiniteTimeSpan, new NeverAnsweringHandler());
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var inFlight = client.GetAsync("http://collector.invalid/v1/logs", TestContext.Current.CancellationToken);

        handler.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inFlight);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetAsync("http://collector.invalid/v1/logs", TestContext.Current.CancellationToken));
    }

    private sealed class StallingBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage { Content = new StreamContent(new StallingStream()) });

        private sealed class StallingStream : Stream
        {
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
        }
    }

    private sealed class NeverAnsweringHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
