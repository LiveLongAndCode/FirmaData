namespace FirmaData.Domain;

// R2: master data enriched with industry statistics in one response. A failed enrichment is a
// representable, non-exceptional result -- StatisticsStatus records why Statistics may be null
// instead of the caller having to infer it, which is what makes graceful degradation (see the
// resilience design in FirmaData.Application) expressible without throwing.
public sealed record EnrichedCompany(
    Company Company,
    IndustryStatistics? Statistics,
    EnrichmentStatus StatisticsStatus);
