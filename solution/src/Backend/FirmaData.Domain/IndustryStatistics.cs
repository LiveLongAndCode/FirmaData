namespace FirmaData.Domain;

// R4: industry statistics = number of companies (workplaces), employees, total employment
// (full-time equivalents), wage sum. Individual fields are nullable because Statbank reports
// suppressed/missing values as ".." -- distinct from an actual zero.
public sealed record IndustryStatistics(
    IndustryCode IndustryCode,
    StatisticsYear Year,
    long? Workplaces,
    long? Employees,
    long? FullTimeEquivalents,
    decimal? WageSumMillionDkk);
