// EnrichmentMetrics uses one process-wide static Meter ("FirmaData"). EnrichmentMetricsTests
// listens on that meter globally via MeterListener, so if another test class in this assembly
// (CompanyEnrichmentServiceTests) calls CompanyEnrichmentService concurrently -- the xUnit
// default across separate test classes/collections -- its measurements leak into the listener
// and break the Single(...) lookups intermittently. Serializing the whole assembly is cheap here
// (well under a second) and removes that cross-talk entirely.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
