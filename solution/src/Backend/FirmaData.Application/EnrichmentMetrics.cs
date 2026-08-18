using System.Diagnostics.Metrics;

namespace FirmaData.Application;

// firmadata.enrichment.duration / .degraded (plan section 7.2), emitted from
// CompanyEnrichmentService -- the one place that knows both "which kind of lookup" (lookup=
// cvr|name) and "did the result actually get degraded, and why" (reason=NotAvailableForYear|
// SourceUnavailable).
internal static class EnrichmentMetrics
{
    private static readonly Meter Meter = new("FirmaData");
    private static readonly Histogram<double> DurationHistogram = Meter.CreateHistogram<double>("firmadata.enrichment.duration", "s");
    private static readonly Counter<long> DegradedCounter = Meter.CreateCounter<long>("firmadata.enrichment.degraded");

    public static void RecordDuration(string lookup, double seconds) =>
        DurationHistogram.Record(seconds, new KeyValuePair<string, object?>("lookup", lookup));

    public static void RecordDegraded(string reason) =>
        DegradedCounter.Add(1, new KeyValuePair<string, object?>("reason", reason));
}
