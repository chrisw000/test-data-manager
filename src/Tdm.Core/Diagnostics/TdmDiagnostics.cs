using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Tdm.Core.Diagnostics;

/// <summary>
/// OTEL instrumentation points (handoff §11). Core only defines the sources;
/// Tdm.Observability wires exporters. Span hierarchy: run → feature → scenario → step
/// → {resolve|generate|override|persist}.
/// </summary>
public static class TdmDiagnostics
{
    public const string SourceName = "Tdm";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    public static readonly Counter<long> EntitiesCreated = Meter.CreateCounter<long>("tdm.entities.created");
    public static readonly Counter<long> EntitiesUpdated = Meter.CreateCounter<long>("tdm.entities.updated");
    public static readonly Counter<long> EntitiesDeleted = Meter.CreateCounter<long>("tdm.entities.deleted");
    public static readonly Counter<long> EntitiesFailed = Meter.CreateCounter<long>("tdm.entities.failed");

    public static readonly Histogram<double> StepDuration = Meter.CreateHistogram<double>("tdm.step.duration", unit: "ms");
    public static readonly Histogram<double> PersistDuration = Meter.CreateHistogram<double>("tdm.persist.duration", unit: "ms");
}
