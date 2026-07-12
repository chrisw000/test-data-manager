using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Tdm.Core.Diagnostics;

namespace Tdm.Observability;

/// <summary>
/// Wires the "Tdm" ActivitySource and Meter to OTLP exporters. Endpoint/protocol/headers come
/// from the standard OTEL_EXPORTER_OTLP_* environment variables (handoff §11). Dispose flushes.
/// </summary>
public sealed class OtelBootstrap : IDisposable
{
    private readonly TracerProvider? _tracerProvider;
    private readonly MeterProvider? _meterProvider;

    private OtelBootstrap(TracerProvider? tracerProvider, MeterProvider? meterProvider)
    {
        _tracerProvider = tracerProvider;
        _meterProvider = meterProvider;
    }

    /// <summary>No-op when no OTLP endpoint is configured — instrumentation stays dormant.</summary>
    public static OtelBootstrap Start(string runName)
    {
        var endpointConfigured =
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_TRACES_ENDPOINT")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_METRICS_ENDPOINT"));
        if (!endpointConfigured) return new OtelBootstrap(null, null);

        var resource = ResourceBuilder.CreateDefault()
            .AddService("tdm", serviceInstanceId: runName);

        var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(resource)
            .AddSource(TdmDiagnostics.SourceName)
            .AddOtlpExporter()
            .Build();

        var meterProvider = Sdk.CreateMeterProviderBuilder()
            .SetResourceBuilder(resource)
            .AddMeter(TdmDiagnostics.SourceName)
            .AddOtlpExporter()
            .Build();

        return new OtelBootstrap(tracerProvider, meterProvider);
    }

    public void Dispose()
    {
        _tracerProvider?.ForceFlush();
        _meterProvider?.ForceFlush();
        _tracerProvider?.Dispose();
        _meterProvider?.Dispose();
    }
}
