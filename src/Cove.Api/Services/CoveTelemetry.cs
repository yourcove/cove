using Cove.Core.Common;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Cove.Api.Services;

/// <summary>
/// Opt-in OTLP export. Logs go through the Serilog pipeline (so the runtime level switch and
/// overrides apply); traces and metrics use the OpenTelemetry SDK. Everything is off unless
/// <c>Cove:Telemetry:{Logs,Traces,Metrics}:Enabled</c> is set.
/// </summary>
public static class CoveTelemetry
{
    public const string SectionName = "Cove:Telemetry";
    public const string DefaultServiceName = "cove";

    private const string EndpointVariable = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string ProtocolVariable = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string ServiceNameVariable = "OTEL_SERVICE_NAME";
    private const string ResourceAttributesVariable = "OTEL_RESOURCE_ATTRIBUTES";
    private const string SdkDisabledVariable = "OTEL_SDK_DISABLED";
    private const string TimeoutVariable = "OTEL_EXPORTER_OTLP_TIMEOUT";
    private const string HttpProtobufDefaultEndpoint = "http://localhost:4318";
    private static readonly TimeSpan DefaultExportTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownFlushTimeout = TimeSpan.FromSeconds(2);
    private static LogExportTimeoutHandler? logExportHandler;

    public sealed record Settings(bool Logs, bool Traces, bool Metrics, string ServiceName, string ServiceInstanceId)
    {
        public bool AnyEnabled => Logs || Traces || Metrics;
    }

    /// <summary>
    /// Resolves the settings, layers them onto <paramref name="configuration"/> as the standard
    /// OTEL_* keys, and registers the trace and metric providers for the enabled signals.
    /// </summary>
    public static Settings Configure(IConfigurationManager configuration, IServiceCollection services)
    {
        var settings = Resolve(configuration);
        if (!settings.AnyEnabled)
            return settings;

        configuration.AddInMemoryCollection(StandardVariableOverrides(configuration, settings));
        services.AddCoveTelemetry(settings);
        return settings;
    }

    public static Settings Resolve(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        // OTEL_SDK_DISABLED is the standard kill switch; honor it for logs too, not only the SDK.
        var disabled = string.Equals(NullIfBlank(configuration[SdkDisabledVariable]), "true", StringComparison.OrdinalIgnoreCase);
        var serviceName = NullIfBlank(section["ServiceName"])
            ?? NullIfBlank(configuration[ServiceNameVariable])
            ?? ServiceNameFromResourceAttributes(configuration[ResourceAttributesVariable])
            ?? DefaultServiceName;
        return new Settings(
            !disabled && Enabled(section, "Logs"),
            !disabled && Enabled(section, "Traces"),
            !disabled && Enabled(section, "Metrics"),
            serviceName,
            Guid.NewGuid().ToString());
    }

    // A mistyped switch must not stop Cove from starting; leave the signal off and say why.
    private static bool Enabled(IConfigurationSection section, string signal)
    {
        var value = NullIfBlank(section[$"{signal}:Enabled"]);
        if (value is null)
            return false;
        if (bool.TryParse(value, out var enabled))
            return enabled;

        Log.Warning(
            "Ignoring {Setting}={Value}: expected true or false. {Signal} export stays off",
            $"{SectionName}:{signal}:Enabled", value, signal);
        return false;
    }

    /// <summary>
    /// Cove's own keys expressed as the standard OTEL_* variables. Both the OpenTelemetry SDK and
    /// the Serilog sink read those from configuration, so layering these on top gives one
    /// precedence for every signal: Cove:Telemetry, then OTEL_*, then the OTLP default.
    /// </summary>
    public static Dictionary<string, string?> StandardVariableOverrides(IConfiguration configuration, Settings settings)
    {
        var overrides = new Dictionary<string, string?> { [ServiceNameVariable] = settings.ServiceName };
        if (NullIfBlank(configuration[$"{SectionName}:Endpoint"]) is { } endpoint)
            overrides[EndpointVariable] = endpoint;
        else if (NullIfBlank(configuration[EndpointVariable]) is null
            && string.Equals(NullIfBlank(configuration[ProtocolVariable]), "http/protobuf", StringComparison.Ordinal))
            // The SDK defaults to port 4318 for http/protobuf; the Serilog sink would stay on 4317.
            overrides[EndpointVariable] = HttpProtobufDefaultEndpoint;
        return overrides;
    }

    public static IServiceCollection AddCoveTelemetry(this IServiceCollection services, Settings settings)
    {
        if (!settings.Traces && !settings.Metrics)
            return services;

        var otel = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                settings.ServiceName,
                serviceVersion: CoveVersion.Display,
                autoGenerateServiceInstanceId: false,
                serviceInstanceId: settings.ServiceInstanceId));

        if (settings.Traces)
        {
            otel.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(options =>
                    // Container healthchecks poll this every 30s; a span per poll is noise.
                    options.Filter = context => !context.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql()
                .AddOtlpExporter());
        }

        if (settings.Metrics)
        {
            otel.WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddOtlpExporter());
        }

        return services;
    }

    /// <summary>
    /// The host is never disposed on the normal shutdown path, and the SDK's hosted service does
    /// not flush on stop, so pending spans and metrics must be flushed explicitly.
    /// </summary>
    public static void Shutdown(IServiceProvider services)
    {
        var timeout = (int)ShutdownFlushTimeout.TotalMilliseconds;
        services.GetService<TracerProvider>()?.Shutdown(timeout);
        services.GetService<MeterProvider>()?.Shutdown(timeout);
    }

    /// <summary>
    /// Call just before the Serilog pipeline is closed. The log sink drains its queue on close with
    /// no deadline of its own, so a collector that accepts connections but never answers would
    /// otherwise hold the process open for minutes. Log exports get the same grace period as the
    /// other signals and are cancelled after it.
    /// </summary>
    public static void BoundLogFlush() => logExportHandler?.CancelAfter(ShutdownFlushTimeout);

    public static LoggerConfiguration WriteToCoveTelemetry(
        this LoggerConfiguration logger,
        Settings settings,
        IConfiguration configuration)
    {
        if (!settings.Logs)
            return logger;

        // One handler for the process: the logger is built once, and BoundLogFlush must reach it.
        var handler = logExportHandler ??= new LogExportTimeoutHandler(ExportTimeout(configuration));
        return logger.WriteTo.OpenTelemetry(
            options =>
            {
                options.HttpMessageHandler = handler;
                options.ResourceAttributes["service.version"] = CoveVersion.Display;
                options.ResourceAttributes["service.instance.id"] = settings.ServiceInstanceId;
                options.OnBeginSuppressInstrumentation = SuppressInstrumentationScope.Begin;
            },
            name => configuration[name]);
    }

    /// <summary>OTEL_EXPORTER_OTLP_TIMEOUT in milliseconds, as the SDK reads it; 10 seconds when unset or invalid.</summary>
    internal static TimeSpan ExportTimeout(IConfiguration configuration) =>
        int.TryParse(NullIfBlank(configuration[TimeoutVariable]), out var milliseconds) && milliseconds > 0
            ? TimeSpan.FromMilliseconds(milliseconds)
            : DefaultExportTimeout;

    /// <summary>
    /// The Serilog sink has no export timeout, so its requests would otherwise run to HttpClient's
    /// 100 second default. This gives each request a deadline and lets shutdown cancel them all.
    /// The response body is read here so the deadline covers it too, not only the headers.
    /// </summary>
    internal sealed class LogExportTimeoutHandler(TimeSpan timeout, HttpMessageHandler? innerHandler = null)
        : DelegatingHandler(innerHandler ?? new SocketsHttpHandler())
    {
        private readonly CancellationTokenSource shutdown = new();

        /// <summary>Cancels in-flight and later requests once <paramref name="grace"/> has passed.</summary>
        public void CancelAfter(TimeSpan grace) => shutdown.CancelAfter(grace);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
            deadline.CancelAfter(timeout);
            var response = await base.SendAsync(request, deadline.Token);
            try
            {
                await response.Content.LoadIntoBufferAsync(deadline.Token);
                return response;
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
    }

    private static string? ServiceNameFromResourceAttributes(string? attributes)
    {
        if (string.IsNullOrWhiteSpace(attributes))
            return null;

        foreach (var pair in attributes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = pair.IndexOf('=');
            if (separator > 0 && pair[..separator].Trim() == "service.name")
                return NullIfBlank(Uri.UnescapeDataString(pair[(separator + 1)..]));
        }

        return null;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
