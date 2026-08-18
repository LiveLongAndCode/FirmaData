namespace FirmaData.Contracts;

// R2: master data for a given year enriched with industry statistics in one response.
// StatisticsStatus is the serialized name of Domain's EnrichmentStatus ("Ok" |
// "NotAvailableForYear" | "SourceUnavailable") -- a string here, not the enum itself, since
// Contracts must not depend on Domain (plan section 2.1).
public sealed record EnrichedCompanyResponse(
    CompanyDto Company,
    IndustryStatisticsDto? IndustryStatistics,
    string StatisticsStatus,
    DateTimeOffset RetrievedAt,
    SourcesDto Sources);
