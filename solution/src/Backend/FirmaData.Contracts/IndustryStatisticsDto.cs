namespace FirmaData.Contracts;

// R4: industry statistics = number of companies (workplaces), employees, total employment
// (full-time equivalents), wage sum. Nullable fields mirror Statbank's ".." suppressed-value
// marker (FirmaData.Statbank) -- "we don't know" is not "there were none".
public sealed record IndustryStatisticsDto(
    string IndustryCode,
    int Year,
    long? Workplaces,
    long? Employees,
    long? FullTimeEquivalents,
    decimal? WageSumMillionDkk);
