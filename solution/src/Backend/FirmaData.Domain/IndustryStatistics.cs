namespace FirmaData.Domain;

// R4: industry statistics = number of workplaces, jobs, total employment (full-time
// equivalents), wage sum -- all counted across the whole industry, end of November, per
// Statbank's ERHV1 methodology, not the individual company (plan fase 8, F6). Individual fields
// are nullable because Statbank reports suppressed/missing values as ".." -- distinct from an
// actual zero.
public sealed record IndustryStatistics(
    IndustryCode IndustryCode,
    StatisticsYear Year,
    long? WorkplacesEndNovember,
    long? JobsEndNovember,
    long? FullTimeEquivalents,
    decimal? WageSumMillionDkk);
