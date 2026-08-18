using System.Diagnostics.Metrics;

namespace FirmaData.Statbank;

// firmadata.cache.hits / .misses (plan section 6.2/7.2), tagged by which cache -- so the
// dashboard can show a hit ratio per cache once Phase 7 wires up the OTel exporter. Just the
// instruments live here; nothing in this phase depends on an exporter being configured.
internal static class CacheMetrics
{
    private static readonly Meter Meter = new("FirmaData");
    private static readonly Counter<long> HitCounter = Meter.CreateCounter<long>("firmadata.cache.hits");
    private static readonly Counter<long> MissCounter = Meter.CreateCounter<long>("firmadata.cache.misses");

    public static void RecordHit(string cache) => HitCounter.Add(1, new KeyValuePair<string, object?>("cache", cache));

    public static void RecordMiss(string cache) => MissCounter.Add(1, new KeyValuePair<string, object?>("cache", cache));
}
