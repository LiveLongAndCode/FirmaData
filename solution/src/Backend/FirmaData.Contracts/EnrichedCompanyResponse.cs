namespace FirmaData.Contracts;

// R2: master data for a given year enriched with industry statistics in one response.
// StatisticsStatus is the serialized name of Domain's EnrichmentStatus ("Ok" |
// "NotAvailableForYear" | "IndustryCodeNotSupported" | "SourceUnavailable") -- a string here, not
// the enum itself, since Contracts must not depend on Domain (plan section 2.1).
//
// RetrievedAtUtc is a now-snapshot of when *this response* was assembled -- it is not the
// vintage of the underlying data. Company master data is always current as of this instant;
// IndustryStatistics reflects whatever `year` was requested (or resolved), which can be well in
// the past. The two follow different clocks entirely (plan fase 8, F6).
public sealed record EnrichedCompanyResponse(
    CompanyDto Company,
    IndustryStatisticsDto? IndustryStatistics,
    string StatisticsStatus,
    DateTimeOffset RetrievedAtUtc,
    SourcesDto Sources);
